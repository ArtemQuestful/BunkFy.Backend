namespace BunkFy.Modules.DataRights.Application.Handlers;

using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;

internal sealed class BeginTenantTerminationExportFragmentDeletionCommandHandler(
    TenantTerminationMutationCoordinator mutations,
    ITenantTerminationExportFragmentRepository fragments,
    ITenantTerminationExportArtifactRepository artifacts,
    IDataRightsExportAuditSink audit,
    ISystemClock clock)
    : ICommandHandler<BeginTenantTerminationExportFragmentDeletionCommand,
        TenantTerminationExportObjectDeletionStart>
{
    private const string Actor =
        "system:tenant-termination-export-retention";

    public async Task<Result<TenantTerminationExportObjectDeletionStart>>
        HandleAsync(
            BeginTenantTerminationExportFragmentDeletionCommand command,
            CancellationToken cancellationToken)
    {
        TenantTerminationProcess? process = await mutations.AcquireProcessAsync(
            command.ProcessId,
            cancellationToken).ConfigureAwait(false);
        TenantTerminationExportFragment? fragment = await fragments.GetAsync(
            command.FragmentId,
            cancellationToken).ConfigureAwait(false);
        if (process is null ||
            fragment is null ||
            !Matches(process, fragment, command))
        {
            return Invalid();
        }

        if (fragment.State == TenantTerminationExportFragmentState.Deleted)
        {
            return Result.Success(
                new TenantTerminationExportObjectDeletionStart(
                    DeletionRequired: false,
                    fragment.Id));
        }

        DateTimeOffset nowUtc = clock.UtcNow;
        long versionBeforeExpiry = fragment.Version;
        Result expired = fragment.MarkExpired(nowUtc);
        if (expired.IsFailure)
        {
            return Result.Failure<
                TenantTerminationExportObjectDeletionStart>(expired.Error);
        }

        if (fragment.Version != versionBeforeExpiry)
        {
            await audit.RecordAsync(
                DataRightsExportAuditFacts.Create(
                    fragment,
                    DataRightsExportAuditAction.Expired,
                    Actor,
                    "expired",
                    nowUtc),
                cancellationToken).ConfigureAwait(false);
        }

        TenantTerminationExportArtifact? artifact =
            await artifacts.GetByProcessAsync(
                process.Id,
                fragment.ExportOperationRevision,
                cancellationToken).ConfigureAwait(false);
        bool usableArtifact = artifact is not null &&
            TenantTerminationExportArtifactCoordinator.IsAvailableForDownload(
                process,
                artifact,
                nowUtc);
        if (!usableArtifact &&
            process.Phase == TenantTerminationProcessPhase.Export &&
            process.Status == TenantTerminationProcessStatus.Running &&
            process.OperationRevision == fragment.ExportOperationRevision &&
            !process.HasCurrentExportConfirmation())
        {
            Result blocked = process.RecordBlocked(
                process.Phase,
                process.OperationRevision,
                TenantTerminationProcess.ExportFragmentExpiredOutcomeCode,
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

        long versionBeforeDeletion = fragment.Version;
        Result deleting = fragment.BeginDeletion(command.RunId, nowUtc);
        if (deleting.IsFailure)
        {
            return Result.Failure<
                TenantTerminationExportObjectDeletionStart>(deleting.Error);
        }

        if (fragment.Version != versionBeforeDeletion)
        {
            await audit.RecordAsync(
                DataRightsExportAuditFacts.Create(
                    fragment,
                    DataRightsExportAuditAction.DeletionStarted,
                    Actor,
                    "started",
                    nowUtc),
                cancellationToken).ConfigureAwait(false);
        }

        return Result.Success(new TenantTerminationExportObjectDeletionStart(
            DeletionRequired: true,
            fragment.Id));
    }

    private static bool Matches(
        TenantTerminationProcess process,
        TenantTerminationExportFragment fragment,
        BeginTenantTerminationExportFragmentDeletionCommand command) =>
        command.RunId != Guid.Empty &&
        command.ExpiresAtUtc != default &&
        process.Id == command.ProcessId &&
        fragment.Id == command.FragmentId &&
        fragment.ExportOperationRevision == command.ExportOperationRevision &&
        fragment.ExpiresAtUtc == command.ExpiresAtUtc &&
        TenantTerminationExportArtifactCoordinator.MatchesFrozenProcessProof(
            process,
            fragment);

    private static Result<TenantTerminationExportObjectDeletionStart>
        Invalid() =>
        Result.Failure<TenantTerminationExportObjectDeletionStart>(
            DataRightsApplicationErrors
                .TenantTerminationExecutionStateInvalid);
}
