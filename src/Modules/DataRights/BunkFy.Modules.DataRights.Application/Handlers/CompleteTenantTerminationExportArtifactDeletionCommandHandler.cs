namespace BunkFy.Modules.DataRights.Application.Handlers;

using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;

internal sealed class CompleteTenantTerminationExportArtifactDeletionCommandHandler(
    ITenantTerminationExportArtifactRepository artifacts,
    IDataRightsExportAuditSink audit,
    ISystemClock clock)
    : ICommandHandler<CompleteTenantTerminationExportArtifactDeletionCommand,
        Unit>
{
    private const string Actor =
        "system:tenant-termination-export-retention";

    public async Task<Result<Unit>> HandleAsync(
        CompleteTenantTerminationExportArtifactDeletionCommand command,
        CancellationToken cancellationToken)
    {
        TenantTerminationExportArtifact? artifact = await artifacts.GetAsync(
            command.ArtifactId,
            cancellationToken).ConfigureAwait(false);
        if (artifact is null ||
            artifact.ProcessId != command.ProcessId ||
            artifact.ExportOperationRevision !=
                command.ExportOperationRevision)
        {
            return Invalid();
        }

        bool alreadyDeleted =
            artifact.State == TenantTerminationExportArtifactState.Deleted;
        DateTimeOffset nowUtc = clock.UtcNow;
        Result deleted = artifact.MarkDeleted(command.RunId, nowUtc);
        if (deleted.IsFailure)
        {
            return Result.Failure<Unit>(deleted.Error);
        }

        if (!alreadyDeleted)
        {
            await audit.RecordAsync(
                DataRightsExportAuditFacts.Create(
                    artifact,
                    DataRightsExportAuditAction.Deleted,
                    Actor,
                    "deleted",
                    nowUtc),
                cancellationToken).ConfigureAwait(false);
        }

        return Result.Success(Unit.Value);
    }

    private static Result<Unit> Invalid() => Result.Failure<Unit>(
        DataRightsApplicationErrors.TenantTerminationExecutionStateInvalid);
}
