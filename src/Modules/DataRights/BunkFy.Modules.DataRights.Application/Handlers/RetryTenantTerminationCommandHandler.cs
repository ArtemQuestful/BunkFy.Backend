namespace BunkFy.Modules.DataRights.Application.Handlers;

using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Mapping;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;

internal sealed class RetryTenantTerminationCommandHandler(
    ITenantTerminationRepository repository,
    TenantTerminationMutationCoordinator mutations,
    ITenantTerminationExportArtifactRepository artifacts,
    ITenantTerminationExportFragmentRepository fragments,
    TenantTerminationPhasePlanner planner,
    ITenantTerminationCoordinationSignal coordinationSignal,
    ISystemClock clock)
    : ICommandHandler<RetryTenantTerminationCommand,
        TenantTerminationProcessDto>
{
    public async Task<Result<TenantTerminationProcessDto>> HandleAsync(
        RetryTenantTerminationCommand command,
        CancellationToken cancellationToken)
    {
        TenantTerminationProcess? process = await mutations.AcquireProcessAsync(
            command.ProcessId,
            cancellationToken).ConfigureAwait(false);
        if (process is null)
        {
            return Result.Failure<TenantTerminationProcessDto>(
                DataRightsApplicationErrors
                    .TenantTerminationProcessNotFound);
        }

        if (process.Version != command.ExpectedProcessVersion)
        {
            return Invalid();
        }

        DateTimeOffset nowUtc = clock.UtcNow;
        if (await this.TryRestartExportAsync(
                process,
                command,
                nowUtc,
                cancellationToken).ConfigureAwait(false))
        {
            return Result.Success(process.ToDto());
        }

        if (process.Status is not TenantTerminationProcessStatus.Blocked and
                not TenantTerminationProcessStatus.Failed ||
            !TenantTerminationPhasePlanner.TryMapPhase(
                process.Phase,
                out _,
                out TenantTerminationOwnerPhase ownerPhase))
        {
            return Invalid();
        }

        IReadOnlyList<TenantTerminationOwnerWorkItem> workItems =
            await repository.ListOwnerWorkItemsAsync(
                process.Id,
                ownerPhase,
                process.OperationRevision,
                cancellationToken).ConfigureAwait(false);
        Result<TenantTerminationValidatedPhase> validated =
            planner.ValidateRecoverableWorkItems(process, workItems);
        if (validated.IsFailure)
        {
            return Result.Failure<TenantTerminationProcessDto>(
                validated.Error);
        }

        TenantTerminationOwnerWorkItem[] retryable = validated.Value
            .WorkByOwner.Values
            .Where(item => item.State is
                TenantTerminationOwnerWorkState.Blocked or
                TenantTerminationOwnerWorkState.Failed)
            .OrderBy(item => item.OwnerKey, StringComparer.Ordinal)
            .ToArray();
        if (retryable.Length == 0)
        {
            return Invalid();
        }

        foreach (TenantTerminationOwnerWorkItem workItem in retryable)
        {
            Result requeued = workItem.Requeue(workItem.Version, nowUtc);
            if (requeued.IsFailure)
            {
                return Result.Failure<TenantTerminationProcessDto>(
                    requeued.Error);
            }
        }

        Result resumed = process.Requeue(
            command.ExpectedProcessVersion,
            command.ActorId,
            nowUtc);
        if (resumed.IsFailure)
        {
            return Result.Failure<TenantTerminationProcessDto>(
                resumed.Error);
        }

        if (!await coordinationSignal.EnqueueAsync(
                process,
                nowUtc,
                cancellationToken).ConfigureAwait(false))
        {
            return Invalid();
        }

        return Result.Success(process.ToDto());
    }

    private async Task<bool> TryRestartExportAsync(
        TenantTerminationProcess process,
        RetryTenantTerminationCommand command,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        if (process.Phase != TenantTerminationProcessPhase.Export ||
            process.Status is not (
                TenantTerminationProcessStatus.Running or
                TenantTerminationProcessStatus.Blocked) ||
            process.HasCurrentExportConfirmation())
        {
            return false;
        }

        TenantTerminationExportArtifact? artifact =
            await artifacts.GetByProcessAsync(
                process.Id,
                process.OperationRevision,
                cancellationToken).ConfigureAwait(false);
        bool recoverableArtifact = artifact is not null &&
            TenantTerminationExportArtifactCoordinator.MatchesProcessProof(
                process,
                artifact) &&
            (artifact.State is
                TenantTerminationExportArtifactState.Failed or
                TenantTerminationExportArtifactState.Expired or
                TenantTerminationExportArtifactState.Deleting or
                TenantTerminationExportArtifactState.Deleted ||
             nowUtc >= artifact.ExpiresAtUtc);
        IReadOnlyList<TenantTerminationExportFragment> currentFragments =
            recoverableArtifact
                ? []
                : await fragments.ListAsync(
                    process.Id,
                    process.OperationRevision,
                    cancellationToken).ConfigureAwait(false);
        bool exactFragments = currentFragments.All(fragment =>
            TenantTerminationExportArtifactCoordinator.MatchesProcessProof(
                process,
                fragment));
        bool recoverableFragment =
            currentFragments.Count > 0 &&
            exactFragments &&
            currentFragments.Any(fragment =>
                fragment.State is
                    TenantTerminationExportFragmentState.Failed or
                    TenantTerminationExportFragmentState.Expired or
                    TenantTerminationExportFragmentState.Deleting or
                    TenantTerminationExportFragmentState.Deleted ||
                nowUtc >= fragment.ExpiresAtUtc);
        bool recoverableProcess =
            process.Status == TenantTerminationProcessStatus.Running ||
            string.Equals(
                process.OutcomeCode,
                TenantTerminationProcess.ExportArtifactExpiredOutcomeCode,
                StringComparison.Ordinal) ||
            string.Equals(
                process.OutcomeCode,
                TenantTerminationProcess.ExportFragmentExpiredOutcomeCode,
                StringComparison.Ordinal);
        if ((!recoverableArtifact && !recoverableFragment) ||
            !recoverableProcess)
        {
            return false;
        }

        Result restarted = process.RequestExportRegeneration(
            command.ExpectedProcessVersion,
            command.ActorId,
            nowUtc);
        if (restarted.IsFailure ||
            !await coordinationSignal.EnqueueAsync(
                process,
                nowUtc,
                cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        return true;
    }

    private static Result<TenantTerminationProcessDto> Invalid() =>
        Result.Failure<TenantTerminationProcessDto>(
            DataRightsApplicationErrors
                .TenantTerminationExecutionStateInvalid);
}
