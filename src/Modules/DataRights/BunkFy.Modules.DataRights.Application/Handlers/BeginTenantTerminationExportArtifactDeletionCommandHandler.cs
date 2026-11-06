namespace BunkFy.Modules.DataRights.Application.Handlers;

using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;

internal sealed class BeginTenantTerminationExportArtifactDeletionCommandHandler(
    TenantTerminationMutationCoordinator mutations,
    ITenantTerminationExportArtifactRepository artifacts,
    IDataRightsExportAuditSink audit,
    ISystemClock clock)
    : ICommandHandler<BeginTenantTerminationExportArtifactDeletionCommand,
        TenantTerminationExportObjectDeletionStart>
{
    private const string Actor =
        "system:tenant-termination-export-retention";

    public async Task<Result<TenantTerminationExportObjectDeletionStart>>
        HandleAsync(
            BeginTenantTerminationExportArtifactDeletionCommand command,
            CancellationToken cancellationToken)
    {
        TenantTerminationProcess? process = await mutations.AcquireProcessAsync(
            command.ProcessId,
            cancellationToken).ConfigureAwait(false);
        TenantTerminationExportArtifact? artifact = await artifacts.GetAsync(
            command.ArtifactId,
            cancellationToken).ConfigureAwait(false);
        if (!Matches(process, artifact, command))
        {
            return Invalid();
        }

        if (artifact!.State == TenantTerminationExportArtifactState.Deleted)
        {
            return Result.Success(
                new TenantTerminationExportObjectDeletionStart(
                    DeletionRequired: false,
                    artifact.Id));
        }

        DateTimeOffset nowUtc = clock.UtcNow;
        long versionBeforeExpiry = artifact.Version;
        Result expired = artifact.MarkExpired(nowUtc);
        if (expired.IsFailure)
        {
            return Result.Failure<
                TenantTerminationExportObjectDeletionStart>(expired.Error);
        }

        if (artifact.Version != versionBeforeExpiry)
        {
            await audit.RecordAsync(
                DataRightsExportAuditFacts.Create(
                    artifact,
                    DataRightsExportAuditAction.Expired,
                    Actor,
                    "expired",
                    nowUtc),
                cancellationToken).ConfigureAwait(false);
        }

        if (process!.Phase == TenantTerminationProcessPhase.Export &&
            process.Status == TenantTerminationProcessStatus.Running &&
            process.OperationRevision == artifact.ExportOperationRevision &&
            !process.HasCurrentExportConfirmation())
        {
            Result blocked = process.RecordBlocked(
                process.Phase,
                process.OperationRevision,
                TenantTerminationProcess.ExportArtifactExpiredOutcomeCode,
                holdReviewAtUtc: null,
                process.Version,
                Actor,
                nowUtc);
            if (blocked.IsFailure)
            {
                return Result.Failure<
                    TenantTerminationExportObjectDeletionStart>(
                        blocked.Error);
            }
        }

        long versionBeforeDeletion = artifact.Version;
        Result deleting = artifact.BeginDeletion(command.RunId, nowUtc);
        if (deleting.IsFailure)
        {
            return Result.Failure<
                TenantTerminationExportObjectDeletionStart>(deleting.Error);
        }

        if (artifact.Version != versionBeforeDeletion)
        {
            await audit.RecordAsync(
                DataRightsExportAuditFacts.Create(
                    artifact,
                    DataRightsExportAuditAction.DeletionStarted,
                    Actor,
                    "started",
                    nowUtc),
                cancellationToken).ConfigureAwait(false);
        }

        return Result.Success(new TenantTerminationExportObjectDeletionStart(
            DeletionRequired: true,
            artifact.Id));
    }

    private static bool Matches(
        TenantTerminationProcess? process,
        TenantTerminationExportArtifact? artifact,
        BeginTenantTerminationExportArtifactDeletionCommand command) =>
        process is not null &&
        artifact is not null &&
        command.RunId != Guid.Empty &&
        command.ExpiresAtUtc != default &&
        process.Id == command.ProcessId &&
        artifact.Id == command.ArtifactId &&
        artifact.ProcessId == process.Id &&
        artifact.CaseId == process.CaseId &&
        artifact.ExportOperationRevision == command.ExportOperationRevision &&
        artifact.ExpiresAtUtc == command.ExpiresAtUtc &&
        TenantTerminationExportArtifactCoordinator.MatchesProcessProof(
            process,
            artifact);

    private static Result<TenantTerminationExportObjectDeletionStart>
        Invalid() =>
        Result.Failure<TenantTerminationExportObjectDeletionStart>(
            DataRightsApplicationErrors
                .TenantTerminationExecutionStateInvalid);
}
