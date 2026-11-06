namespace BunkFy.Modules.DataRights.Application.Handlers;

using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Mapping;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;

internal sealed class ConfirmTenantTerminationExportCommandHandler(
    TenantTerminationMutationCoordinator mutations,
    ITenantTerminationExportArtifactRepository artifacts,
    ITenantTerminationCoordinationSignal coordinationSignal,
    ISystemClock clock)
    : ICommandHandler<ConfirmTenantTerminationExportCommand,
        TenantTerminationProcessDto>
{
    public async Task<Result<TenantTerminationProcessDto>> HandleAsync(
        ConfirmTenantTerminationExportCommand command,
        CancellationToken cancellationToken)
    {
        TenantTerminationProcess? process = await mutations.AcquireProcessAsync(
            command.ProcessId,
            cancellationToken).ConfigureAwait(false);
        TenantTerminationExportArtifact? artifact = await artifacts.GetAsync(
            command.ArtifactId,
            cancellationToken).ConfigureAwait(false);
        string actor = command.ActorId?.Trim() ?? string.Empty;
        DateTimeOffset nowUtc = clock.UtcNow;
        if (process is null ||
            artifact is null ||
            command.CaseId == Guid.Empty ||
            process.CaseId != command.CaseId ||
            artifact.CaseId != command.CaseId ||
            process.Version != command.ExpectedProcessVersion ||
            artifact.Version != command.ExpectedArtifactVersion ||
            process.Phase != TenantTerminationProcessPhase.Export ||
            process.Status != TenantTerminationProcessStatus.Running ||
            process.OperationRevision != command.ExportOperationRevision ||
            artifact.ExportOperationRevision !=
                command.ExportOperationRevision ||
            !string.Equals(
                artifact.FrozenRevisionSha256,
                command.FrozenRevisionSha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                artifact.FragmentSetSha256,
                command.FragmentSetSha256,
                StringComparison.Ordinal) ||
            actor.Length is 0 or > TenantTerminationProcess.ActorIdMaxLength ||
            string.Equals(
                actor,
                TenantTerminationCoordination.ExecutorActorId,
                StringComparison.Ordinal) ||
            nowUtc == default)
        {
            return Invalid();
        }

        Result confirmed = TenantTerminationExportArtifactCoordinator.Confirm(
            process,
            artifact,
            command.ExpectedProcessVersion,
            actor,
            nowUtc);
        if (confirmed.IsFailure)
        {
            return Result.Failure<TenantTerminationProcessDto>(
                DataRightsApplicationErrors
                    .TenantTerminationExportConfirmationInvalid);
        }

        if (!await coordinationSignal.EnqueueAsync(
                process,
                process.LastChangedAtUtc,
                cancellationToken).ConfigureAwait(false))
        {
            return Invalid();
        }

        return Result.Success(process.ToDto());
    }

    private static Result<TenantTerminationProcessDto> Invalid() =>
        Result.Failure<TenantTerminationProcessDto>(
            DataRightsApplicationErrors
                .TenantTerminationExportConfirmationInvalid);
}
