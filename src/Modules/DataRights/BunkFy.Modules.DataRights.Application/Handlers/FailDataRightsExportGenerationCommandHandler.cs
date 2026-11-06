namespace BunkFy.Modules.DataRights.Application.Handlers;

using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;

internal sealed class FailDataRightsExportGenerationCommandHandler(
    IDataRightsExportArtifactRepository artifacts,
    IDataRightsExportAuditSink audit,
    ISystemClock clock)
    : ICommandHandler<FailDataRightsExportGenerationCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(
        FailDataRightsExportGenerationCommand command,
        CancellationToken cancellationToken)
    {
        DataRightsExportArtifact? artifact = await artifacts.GetAsync(
            command.Scope,
            command.ArtifactId,
            cancellationToken).ConfigureAwait(false);
        if (artifact is null)
        {
            return Result.Failure<Unit>(
                DataRightsApplicationErrors.ExportArtifactNotFound);
        }

        if (artifact.CaseId != command.CaseId ||
            artifact.DecisionRevision != command.DecisionRevision)
        {
            return Result.Failure<Unit>(
                DataRightsApplicationErrors.ExportGenerationConflict);
        }

        bool alreadyFailed =
            artifact.State == DataRightsExportArtifactState.Failed;
        DateTimeOffset failedAtUtc = clock.UtcNow;
        Result transition = artifact.State == DataRightsExportArtifactState.Requested
            ? artifact.RejectGeneration(
                command.RunId,
                command.Attempt,
                "system:data-rights-export",
                command.FailureCode,
                failedAtUtc)
            : artifact.MarkFailed(
                command.RunId,
                command.Attempt,
                command.FailureCode,
                failedAtUtc);
        if (transition.IsFailure)
        {
            return Result.Failure<Unit>(transition.Error);
        }

        if (!alreadyFailed)
        {
            await audit.RecordAsync(
                DataRightsExportAuditFacts.Create(
                    artifact,
                    DataRightsExportAuditAction.GenerationFailed,
                    "system:data-rights-export",
                    command.FailureCode,
                    failedAtUtc),
                cancellationToken).ConfigureAwait(false);
        }

        return Result.Success(Unit.Value);
    }
}
