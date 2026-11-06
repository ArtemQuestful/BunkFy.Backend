namespace BunkFy.Modules.DataRights.Application.Handlers;

using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Application.Tasks;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Entities;
using BunkFy.Modules.DataRights.Domain.Models;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;

internal sealed class BeginTenantTerminationExportFragmentGenerationCommandHandler(
    ITenantTerminationRepository repository,
    TenantTerminationMutationCoordinator mutations,
    ITenantTerminationExportFragmentRepository fragments,
    IDataRightsExportArtifactPolicy artifactPolicy,
    TenantTerminationPhasePlanner planner,
    ITenantTerminationExportRetentionScheduler retentionScheduler,
    ISystemClock clock)
    : ICommandHandler<
        BeginTenantTerminationExportFragmentGenerationCommand,
        TenantTerminationExportFragmentGenerationStart>
{
    public async Task<Result<TenantTerminationExportFragmentGenerationStart>>
        HandleAsync(
            BeginTenantTerminationExportFragmentGenerationCommand command,
            CancellationToken cancellationToken)
    {
        TenantTerminationProcess? process = await mutations.AcquireOwnerWorkAsync(
            command.ProcessId,
            command.WorkItemId,
            cancellationToken).ConfigureAwait(false);
        if (!IsCurrentExport(process, command))
        {
            return Invalid();
        }

        IReadOnlyList<TenantTerminationOwnerWorkItem> workItems =
            await repository.ListOwnerWorkItemsAsync(
                process!.Id,
                TenantTerminationOwnerPhase.Export,
                process.OperationRevision,
                cancellationToken).ConfigureAwait(false);
        Result<TenantTerminationValidatedPhase> validated =
            planner.ValidateWorkItems(process, workItems);
        if (validated.IsFailure)
        {
            return Result.Failure<
                TenantTerminationExportFragmentGenerationStart>(
                    validated.Error);
        }

        TenantTerminationOwnerWorkItem? workItem = validated.Value
            .WorkByOwner
            .GetValueOrDefault(command.OwnerKey?.Trim() ?? string.Empty);
        if (!IsCurrentWork(workItem, command))
        {
            return Invalid();
        }

        DateTimeOffset nowUtc = clock.UtcNow;
        DateTimeOffset deadlineUtc = workItem!.LastAttemptAtUtc.GetValueOrDefault()
            .Add(BeginTenantTerminationOwnerWorkCommandHandler.OwnerCallTimeout);
        if (nowUtc == default || nowUtc >= deadlineUtc)
        {
            return Invalid();
        }

        TenantTerminationExportFragment? fragment =
            await fragments.GetAsync(
                workItem.Id,
                cancellationToken).ConfigureAwait(false);
        if (fragment is null)
        {
            Result<TenantTerminationExportFragment> requested =
                TenantTerminationExportFragment.Request(
                    workItem.Id,
                    process.ScopeId,
                    process.Id,
                    process.CaseId,
                    process.ApprovalRevision,
                    process.FreezeOperationRevision!.Value,
                    process.OperationRevision,
                    process.TerminationEpoch,
                    workItem.IdempotencyKey,
                    workItem.OwnerKey,
                    workItem.OwnerContractVersion,
                    workItem.CatalogVersion,
                    workItem.CatalogSha256,
                    process.FrozenRevisionSha256!,
                    process.PolicyEvidenceSha256,
                    nowUtc,
                    artifactPolicy.ExpiresAt(nowUtc));
            if (requested.IsFailure)
            {
                return Result.Failure<
                    TenantTerminationExportFragmentGenerationStart>(
                        requested.Error);
            }

            fragment = requested.Value;
            await fragments.AddAsync(
                fragment,
                cancellationToken).ConfigureAwait(false);
        }
        else if (!Matches(process, workItem, fragment))
        {
            return Invalid();
        }

        await retentionScheduler.EnqueueFragmentCleanupAsync(
            process.ScopeId,
            process.Id,
            fragment.Id,
            fragment.ExportOperationRevision,
            fragment.ExpiresAtUtc,
            cancellationToken).ConfigureAwait(false);

        Result begun = fragment.BeginGeneration(
            command.TaskRunId,
            command.TaskAttempt,
            nowUtc);
        if (begun.IsFailure ||
            fragment.GenerationStartedAtUtc is not DateTimeOffset generatedAtUtc)
        {
            return begun.IsFailure
                ? Result.Failure<
                    TenantTerminationExportFragmentGenerationStart>(
                        begun.Error)
                : Invalid();
        }

        TenantTerminationExportFragmentAssemblyRequest assemblyRequest = new(
            process.ScopeId,
            process.Id,
            process.CaseId,
            process.ApprovalRevision,
            process.FreezeOperationRevision!.Value,
            process.OperationRevision,
            process.TerminationEpoch,
            process.WorkspaceFenceRevision!.Value,
            process.FrozenRevisionSha256!,
            process.PolicyEvidenceSha256,
            TenantTerminationCoordination.ExecutorActorId,
            process.FrozenAtUtc!.Value,
            generatedAtUtc,
            deadlineUtc,
            new TenantTerminationExportOwnerWork(
                workItem.OwnerKey,
                workItem.Id,
                workItem.IdempotencyKey,
                workItem.OwnerContractVersion,
                workItem.CatalogVersion,
                workItem.CatalogSha256),
            process.FrozenExportOwners.Select(Map).ToArray());
        return Result.Success(
            new TenantTerminationExportFragmentGenerationStart(
                fragment.Version,
                new TenantTerminationExportFragmentGenerationRequest(
                    assemblyRequest,
                    command.TaskRunId,
                    command.TaskAttempt,
                    fragment.ExpiresAtUtc)));
    }

    private static bool IsCurrentExport(
        TenantTerminationProcess? process,
        BeginTenantTerminationExportFragmentGenerationCommand command) =>
        process is not null &&
        command.WorkItemId != Guid.Empty &&
        command.OperationRevision == process.OperationRevision &&
        command.TaskRunId != Guid.Empty &&
        command.TaskAttempt > 0 &&
        command.ExpectedWorkItemVersion > 0 &&
        process.ExportRequested &&
        process.Phase == TenantTerminationProcessPhase.Export &&
        process.Status == TenantTerminationProcessStatus.Running &&
        process.FreezeOperationRevision is > 0 &&
        process.WorkspaceFenceRevision is > 0 &&
        !string.IsNullOrWhiteSpace(process.FrozenRevisionSha256) &&
        process.FrozenAtUtc.HasValue &&
        process.FrozenExportOwners.Count > 0;

    private static bool IsCurrentWork(
        TenantTerminationOwnerWorkItem? workItem,
        BeginTenantTerminationExportFragmentGenerationCommand command) =>
        workItem is not null &&
        workItem.Id == command.WorkItemId &&
        workItem.Version == command.ExpectedWorkItemVersion &&
        workItem.State == TenantTerminationOwnerWorkState.Processing &&
        workItem.TaskRunId == command.TaskRunId &&
        workItem.LastTaskAttempt == command.TaskAttempt &&
        workItem.LastAttemptAtUtc.HasValue;

    private static bool Matches(
        TenantTerminationProcess process,
        TenantTerminationOwnerWorkItem workItem,
        TenantTerminationExportFragment fragment) =>
        fragment.Id == workItem.Id &&
        string.Equals(fragment.ScopeId, process.ScopeId, StringComparison.Ordinal) &&
        fragment.ProcessId == process.Id &&
        fragment.CaseId == process.CaseId &&
        fragment.ApprovalRevision == process.ApprovalRevision &&
        fragment.FreezeOperationRevision == process.FreezeOperationRevision &&
        fragment.ExportOperationRevision == process.OperationRevision &&
        fragment.TerminationEpoch == process.TerminationEpoch &&
        fragment.IdempotencyKey == workItem.IdempotencyKey &&
        string.Equals(fragment.OwnerKey, workItem.OwnerKey, StringComparison.Ordinal) &&
        fragment.OwnerContractVersion == workItem.OwnerContractVersion &&
        fragment.CatalogVersion == workItem.CatalogVersion &&
        string.Equals(
            fragment.CatalogSha256,
            workItem.CatalogSha256,
            StringComparison.Ordinal) &&
        string.Equals(
            fragment.FrozenRevisionSha256,
            process.FrozenRevisionSha256,
            StringComparison.Ordinal) &&
        string.Equals(
            fragment.PolicyEvidenceSha256,
            process.PolicyEvidenceSha256,
            StringComparison.Ordinal);

    private static TenantTerminationExportOwnerCatalogEntry Map(
        TenantTerminationFrozenOwner owner) => new(
            owner.OwnerKey,
            owner.ContractVersion,
            owner.CatalogVersion,
            owner.CatalogSha256);

    private static Result<TenantTerminationExportFragmentGenerationStart>
        Invalid() =>
        Result.Failure<TenantTerminationExportFragmentGenerationStart>(
            DataRightsApplicationErrors
                .TenantTerminationExecutionStateInvalid);
}
