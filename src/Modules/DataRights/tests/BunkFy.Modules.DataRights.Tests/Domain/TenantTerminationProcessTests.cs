namespace BunkFy.Modules.DataRights.Tests.Domain;

using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Errors;
using BunkFy.Modules.DataRights.Domain.Models;
using Gma.Framework.Results;
using Xunit;

[Trait("Category", "Unit")]
public sealed class TenantTerminationProcessTests
{
    private const string TenantId = "tenant-a";
    private const string Approver = "owner:approver";
    private const string Executor = "owner:executor";
    private static readonly string Digest = new('a', 64);
    private static readonly string FragmentSetDigest = new('b', 64);
    private static readonly DateTimeOffset Now =
        new(2026, 7, 31, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Exporting_process_advances_monotonically_and_requires_distinct_destructive_executor()
    {
        TenantTerminationProcess process = Prepare(exportRequested: true);

        Assert.Equal(TenantTerminationProcessPhase.Freeze, process.Phase);
        Assert.Equal(TenantTerminationProcessStatus.Pending, process.Status);
        Assert.Equal(4, process.OperationRevision);

        Assert.True(process.BeginPhase(
            TenantTerminationProcessPhase.Freeze,
            process.Version,
            Approver,
            Now.AddMinutes(1)).IsSuccess);
        Assert.Equal(5, process.OperationRevision);
        Assert.True(CompleteFreeze(
            process,
            process.OperationRevision,
            process.Version,
            Approver,
            Now.AddMinutes(2)).IsSuccess);
        Assert.Equal(TenantTerminationProcessPhase.Export, process.Phase);

        Assert.True(process.BeginPhase(
            TenantTerminationProcessPhase.Export,
            process.Version,
            Approver,
            Now.AddMinutes(3)).IsSuccess);
        Assert.True(process.CompletePhase(
            TenantTerminationProcessPhase.Export,
            process.OperationRevision,
            process.Version,
            Approver,
            Now.AddMinutes(4)).IsFailure);
        Guid artifactId = Guid.NewGuid();
        Assert.True(process.ConfirmExport(
            process.OperationRevision,
            artifactId,
            artifactVersion: 3,
            Digest,
            FragmentSetDigest,
            process.Version,
            Approver,
            Now.AddMinutes(4)).IsSuccess);
        Assert.Equal(1, process.ExportConfirmationRevision);
        Assert.Equal(process.OperationRevision, process.ExportConfirmedOperationRevision);
        Assert.Equal(artifactId, process.ExportArtifactId);
        Assert.True(process.CompletePhase(
            TenantTerminationProcessPhase.Export,
            process.OperationRevision,
            process.Version,
            Approver,
            Now.AddMinutes(5)).IsSuccess);
        Assert.Equal(TenantTerminationProcessPhase.Destroy, process.Phase);

        Result denied = process.BeginPhase(
            TenantTerminationProcessPhase.Destroy,
            process.Version,
            Approver,
            Now.AddMinutes(6));
        Assert.True(denied.IsFailure);
        Assert.Equal(
            DataRightsDomainErrors.TenantTerminationExecutorInvalid,
            denied.Error);

        Assert.True(process.BeginPhase(
            TenantTerminationProcessPhase.Destroy,
            process.Version,
            Executor,
            Now.AddMinutes(6)).IsSuccess);
        Assert.True(process.CompletePhase(
            TenantTerminationProcessPhase.Destroy,
            process.OperationRevision,
            process.Version,
            Executor,
            Now.AddMinutes(7)).IsSuccess);
        Assert.Equal(TenantTerminationProcessPhase.Verify, process.Phase);
        Assert.Equal(
            process.OperationRevision,
            process.DestroyCompletedOperationRevision);
        Assert.Equal(Now.AddMinutes(7), process.DestroyedAtUtc);

        Assert.True(process.BeginPhase(
            TenantTerminationProcessPhase.Verify,
            process.Version,
            Executor,
            Now.AddMinutes(8)).IsSuccess);
        Assert.True(process.CompletePhase(
            TenantTerminationProcessPhase.Verify,
            process.OperationRevision,
            process.Version,
            Executor,
            Now.AddMinutes(9)).IsFailure);
        Assert.True(process.ConfirmVerification(
            process.OperationRevision,
            Guid.NewGuid(),
            terminalReceiptVersion: 1,
            Digest,
            process.Version,
            Executor,
            Now.AddMinutes(9)).IsSuccess);
        Assert.True(process.CompletePhase(
            TenantTerminationProcessPhase.Verify,
            process.OperationRevision,
            process.Version,
            Executor,
            Now.AddMinutes(10)).IsSuccess);
        Assert.Equal(TenantTerminationProcessPhase.Completed, process.Phase);
        Assert.Equal(TenantTerminationProcessStatus.Completed, process.Status);
    }

    [Fact]
    public void Blocked_transition_preserves_version_conflict()
    {
        TenantTerminationProcess process = Prepare(exportRequested: false);
        Assert.True(process.BeginPhase(
            TenantTerminationProcessPhase.Freeze,
            process.Version,
            Approver,
            Now.AddMinutes(1)).IsSuccess);

        Result conflict = process.RecordBlocked(
            TenantTerminationProcessPhase.Freeze,
            process.OperationRevision,
            "workspace.legal-hold",
            Now.AddDays(1),
            expectedVersion: process.Version - 1,
            Approver,
            Now.AddMinutes(2));

        Assert.True(conflict.IsFailure);
        Assert.Equal(DataRightsDomainErrors.VersionConflict, conflict.Error);
        Assert.Equal(TenantTerminationProcessStatus.Running, process.Status);
    }

    [Fact]
    public void Non_exporting_process_moves_from_freeze_to_destroy()
    {
        TenantTerminationProcess process = Prepare(exportRequested: false);

        Assert.True(process.BeginPhase(
            TenantTerminationProcessPhase.Freeze,
            process.Version,
            Approver,
            Now.AddMinutes(1)).IsSuccess);
        Assert.True(CompleteFreeze(
            process,
            process.OperationRevision,
            process.Version,
            Approver,
            Now.AddMinutes(2)).IsSuccess);

        Assert.Equal(TenantTerminationProcessPhase.Destroy, process.Phase);
        Assert.Equal(TenantTerminationProcessStatus.Pending, process.Status);
    }

    [Fact]
    public void Blocked_phase_retains_only_bounded_review_evidence_and_requeues_in_place()
    {
        TenantTerminationProcess process = Prepare(exportRequested: false);
        Assert.True(process.BeginPhase(
            TenantTerminationProcessPhase.Freeze,
            process.Version,
            Approver,
            Now.AddMinutes(1)).IsSuccess);

        DateTimeOffset reviewAt = Now.AddDays(1);
        Assert.True(process.RecordBlocked(
            TenantTerminationProcessPhase.Freeze,
            process.OperationRevision,
            "workspace.legal-hold",
            reviewAt,
            process.Version,
            Approver,
            Now.AddMinutes(2)).IsSuccess);
        Assert.Equal(TenantTerminationProcessStatus.Blocked, process.Status);
        Assert.Equal("workspace.legal-hold", process.OutcomeCode);
        Assert.Equal(reviewAt, process.HoldReviewAtUtc);

        Assert.True(process.Requeue(
            process.Version,
            Approver,
            Now.AddMinutes(3)).IsSuccess);
        Assert.Equal(TenantTerminationProcessStatus.Running, process.Status);
        Assert.Null(process.OutcomeCode);
        Assert.Null(process.HoldReviewAtUtc);
        Assert.Equal(TenantTerminationProcessPhase.Freeze, process.Phase);
    }

    [Fact]
    public void Blocked_phase_can_retain_an_unknown_review_date()
    {
        TenantTerminationProcess process = Prepare(exportRequested: false);
        Assert.True(process.BeginPhase(
            TenantTerminationProcessPhase.Freeze,
            process.Version,
            Approver,
            Now.AddMinutes(1)).IsSuccess);

        Assert.True(process.RecordBlocked(
            TenantTerminationProcessPhase.Freeze,
            process.OperationRevision,
            "workspace.legal-hold",
            holdReviewAtUtc: null,
            process.Version,
            Approver,
            Now.AddMinutes(2)).IsSuccess);
        Assert.Equal(TenantTerminationProcessStatus.Blocked, process.Status);
        Assert.Null(process.HoldReviewAtUtc);
    }

    [Fact]
    public void Exact_phase_delivery_replays_without_advancing_version()
    {
        TenantTerminationProcess process = Prepare(exportRequested: false);
        long prepareVersion = process.Version;
        DateTimeOffset startedAt = Now.AddMinutes(1);

        Assert.True(process.BeginPhase(
            TenantTerminationProcessPhase.Freeze,
            prepareVersion,
            Approver,
            startedAt).IsSuccess);
        long runningVersion = process.Version;
        long operationRevision = process.OperationRevision;
        Assert.True(process.BeginPhase(
            TenantTerminationProcessPhase.Freeze,
            prepareVersion,
            Approver,
            startedAt).IsSuccess);
        Assert.Equal(runningVersion, process.Version);
        Assert.Equal(operationRevision, process.OperationRevision);

        DateTimeOffset completedAt = Now.AddMinutes(2);
        Assert.True(CompleteFreeze(
            process,
            operationRevision,
            runningVersion,
            Approver,
            completedAt).IsSuccess);
        long completedVersion = process.Version;
        Assert.True(CompleteFreeze(
            process,
            operationRevision,
            runningVersion,
            Approver,
            completedAt).IsSuccess);
        Assert.Equal(completedVersion, process.Version);
    }

    [Fact]
    public void Retry_resumes_the_same_operation_without_reopening_the_phase()
    {
        TenantTerminationProcess process = Prepare(exportRequested: false);
        Assert.True(process.BeginPhase(
            TenantTerminationProcessPhase.Freeze,
            process.Version,
            Approver,
            Now.AddMinutes(1)).IsSuccess);
        long operationRevision = process.OperationRevision;
        Assert.True(process.RecordFailed(
            TenantTerminationProcessPhase.Freeze,
            operationRevision,
            "workspaces.retry-exhausted",
            process.Version,
            Approver,
            Now.AddMinutes(2)).IsSuccess);
        Assert.True(process.Requeue(
            process.Version,
            Approver,
            Now.AddMinutes(3)).IsSuccess);
        Result reopened = process.BeginPhase(
            TenantTerminationProcessPhase.Freeze,
            process.Version,
            Approver,
            Now.AddMinutes(4));

        Assert.True(reopened.IsFailure);
        Assert.Equal(
            DataRightsDomainErrors.TenantTerminationTransitionInvalid,
            reopened.Error);
        Assert.Equal(TenantTerminationProcessStatus.Running, process.Status);
        Assert.Equal(operationRevision, process.OperationRevision);
    }

    [Fact]
    public void Retried_export_clears_stale_confirmation_but_advances_its_revision()
    {
        TenantTerminationProcess process = Prepare(exportRequested: true);
        _ = process.BeginPhase(
            TenantTerminationProcessPhase.Freeze,
            process.Version,
            Approver,
            Now.AddMinutes(1));
        _ = CompleteFreeze(
            process,
            process.OperationRevision,
            process.Version,
            Approver,
            Now.AddMinutes(2));
        _ = process.BeginPhase(
            TenantTerminationProcessPhase.Export,
            process.Version,
            Approver,
            Now.AddMinutes(3));
        _ = process.ConfirmExport(
            process.OperationRevision,
            Guid.NewGuid(),
            artifactVersion: 3,
            Digest,
            FragmentSetDigest,
            process.Version,
            Approver,
            Now.AddMinutes(4));
        _ = process.RecordFailed(
            TenantTerminationProcessPhase.Export,
            process.OperationRevision,
            "export.confirmation-revoked",
            process.Version,
            Approver,
            Now.AddMinutes(5));
        long operationRevision = process.OperationRevision;
        _ = process.Requeue(
            process.Version,
            Approver,
            Now.AddMinutes(6));

        Assert.Equal(1, process.ExportConfirmationRevision);
        Assert.Equal(operationRevision, process.OperationRevision);
        Assert.Equal(TenantTerminationProcessStatus.Running, process.Status);
        Assert.Null(process.ExportConfirmedOperationRevision);
        Assert.Null(process.ExportArtifactId);
        Assert.True(process.CompletePhase(
            TenantTerminationProcessPhase.Export,
            process.OperationRevision,
            process.Version,
            Approver,
            Now.AddMinutes(8)).IsFailure);

        Assert.True(process.ConfirmExport(
            process.OperationRevision,
            Guid.NewGuid(),
            artifactVersion: 3,
            Digest,
            FragmentSetDigest,
            process.Version,
            Approver,
            Now.AddMinutes(8)).IsSuccess);
        Assert.Equal(2, process.ExportConfirmationRevision);
    }

    [Fact]
    public void Cancellation_requires_restore_completion_after_freeze()
    {
        TenantTerminationProcess process = Prepare(exportRequested: false);
        Assert.True(process.BeginPhase(
            TenantTerminationProcessPhase.Freeze,
            process.Version,
            Approver,
            Now.AddMinutes(1)).IsSuccess);
        Assert.True(CompleteFreeze(
            process,
            process.OperationRevision,
            process.Version,
            Approver,
            Now.AddMinutes(2)).IsSuccess);

        Assert.True(process.RequestCancellation(
            process.Version,
            Approver,
            Now.AddMinutes(3)).IsSuccess);
        Assert.Equal(TenantTerminationProcessPhase.Restore, process.Phase);
        Assert.Equal(TenantTerminationProcessStatus.Pending, process.Status);

        Assert.True(process.BeginPhase(
            TenantTerminationProcessPhase.Restore,
            process.Version,
            Approver,
            Now.AddMinutes(4)).IsSuccess);
        Assert.True(process.CompletePhase(
            TenantTerminationProcessPhase.Restore,
            process.OperationRevision,
            process.Version,
            Approver,
            Now.AddMinutes(5)).IsFailure);
        Assert.Equal(TenantTerminationProcessStatus.Running, process.Status);

        Assert.True(process.CompleteCancellation(
            process.OperationRevision,
            process.Version,
            Approver,
            Now.AddMinutes(5)).IsSuccess);
        Assert.Equal(TenantTerminationProcessPhase.Restore, process.Phase);
        Assert.Equal(TenantTerminationProcessStatus.Cancelled, process.Status);
    }

    [Fact]
    public void Cancellation_remains_available_while_export_is_running()
    {
        TenantTerminationProcess process = Prepare(exportRequested: true);
        Assert.True(process.BeginPhase(
            TenantTerminationProcessPhase.Freeze,
            process.Version,
            Approver,
            Now.AddMinutes(1)).IsSuccess);
        Assert.True(CompleteFreeze(
            process,
            process.OperationRevision,
            process.Version,
            Approver,
            Now.AddMinutes(2)).IsSuccess);
        Assert.True(process.BeginPhase(
            TenantTerminationProcessPhase.Export,
            process.Version,
            Executor,
            Now.AddMinutes(3)).IsSuccess);

        Result cancelled = process.RequestCancellation(
            process.Version,
            Approver,
            Now.AddMinutes(4));

        Assert.True(cancelled.IsSuccess);
        Assert.Equal(TenantTerminationProcessPhase.Restore, process.Phase);
        Assert.Equal(TenantTerminationProcessStatus.Pending, process.Status);
    }

    [Fact]
    public void Cancellation_is_denied_before_freeze_or_after_destruction_starts()
    {
        TenantTerminationProcess beforeFreeze = Prepare(exportRequested: false);
        Assert.True(beforeFreeze.RequestCancellation(
            beforeFreeze.Version,
            Approver,
            Now.AddMinutes(1)).IsFailure);

        TenantTerminationProcess destroying = Prepare(exportRequested: false);
        Assert.True(destroying.BeginPhase(
            TenantTerminationProcessPhase.Freeze,
            destroying.Version,
            Approver,
            Now.AddMinutes(1)).IsSuccess);
        Assert.True(CompleteFreeze(
            destroying,
            destroying.OperationRevision,
            destroying.Version,
            Approver,
            Now.AddMinutes(2)).IsSuccess);
        Assert.True(destroying.BeginPhase(
            TenantTerminationProcessPhase.Destroy,
            destroying.Version,
            Executor,
            Now.AddMinutes(3)).IsSuccess);

        Result denied = destroying.RequestCancellation(
            destroying.Version,
            Approver,
            Now.AddMinutes(4));

        Assert.True(denied.IsFailure);
        Assert.Equal(
            DataRightsDomainErrors.TenantTerminationTransitionInvalid,
            denied.Error);
        Assert.Equal(TenantTerminationProcessStatus.Running, destroying.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABC")]
    [InlineData("contains space")]
    public void Invalid_policy_or_actor_coordinates_fail_closed(
        string invalidDigestOrActor)
    {
        Result<TenantTerminationProcess> result =
            TenantTerminationProcess.Prepare(
                Guid.NewGuid(),
                TenantId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                approvalRevision: 4,
                Guid.NewGuid(),
                exportRequested: false,
                invalidDigestOrActor,
                Approver,
                Now,
                invalidDigestOrActor,
                Now);

        Assert.True(result.IsFailure);
    }

    [Fact]
    public void Freeze_completion_persists_a_canonical_immutable_checkpoint()
    {
        TenantTerminationProcess process = Prepare(exportRequested: true);
        Assert.True(process.BeginPhase(
            TenantTerminationProcessPhase.Freeze,
            process.Version,
            Approver,
            Now.AddMinutes(1)).IsSuccess);
        long runningVersion = process.Version;
        long operationRevision = process.OperationRevision;
        DateTimeOffset frozenAtUtc = Now.AddMinutes(2);
        TenantTerminationFrozenOwnerDescriptor[] reversed =
        [
            new("workspaces", 1, 3, new string('c', 64)),
            new("reservations", 1, 2, new string('b', 64))
        ];

        Assert.True(process.CompleteFreeze(
            operationRevision,
            workspaceFenceRevision: 7,
            Digest,
            reversed,
            runningVersion,
            Approver,
            frozenAtUtc).IsSuccess);

        Assert.Equal(operationRevision, process.FreezeOperationRevision);
        Assert.Equal(7, process.WorkspaceFenceRevision);
        Assert.Equal(Digest, process.FrozenRevisionSha256);
        Assert.Equal(Approver, process.FrozenBy);
        Assert.Equal(frozenAtUtc, process.FrozenAtUtc);
        Assert.Equal(
            ["reservations", "workspaces"],
            process.FrozenExportOwners
                .OrderBy(owner => owner.Ordinal)
                .Select(owner => owner.OwnerKey)
                .ToArray());
        Assert.Equal([1, 2], process.FrozenExportOwners
            .OrderBy(owner => owner.Ordinal)
            .Select(owner => owner.Ordinal)
            .ToArray());

        long completedVersion = process.Version;
        Assert.True(process.CompleteFreeze(
            operationRevision,
            workspaceFenceRevision: 7,
            Digest,
            reversed.Reverse().ToArray(),
            runningVersion,
            Approver,
            frozenAtUtc).IsSuccess);
        Assert.Equal(completedVersion, process.Version);
    }

    [Fact]
    public void Freeze_completion_rejects_duplicate_owners_and_direct_transition()
    {
        TenantTerminationProcess process = Prepare(exportRequested: false);
        Assert.True(process.BeginPhase(
            TenantTerminationProcessPhase.Freeze,
            process.Version,
            Approver,
            Now.AddMinutes(1)).IsSuccess);

        Result direct = process.CompletePhase(
            TenantTerminationProcessPhase.Freeze,
            process.OperationRevision,
            process.Version,
            Approver,
            Now.AddMinutes(2));
        Result duplicate = process.CompleteFreeze(
            process.OperationRevision,
            workspaceFenceRevision: 1,
            Digest,
            [
                new("workspaces", 1, 1, Digest),
                new("workspaces", 1, 1, Digest)
            ],
            process.Version,
            Approver,
            Now.AddMinutes(2));

        Assert.True(direct.IsFailure);
        Assert.Equal(
            DataRightsDomainErrors.TenantTerminationTransitionInvalid,
            direct.Error);
        Assert.True(duplicate.IsFailure);
        Assert.Equal(
            DataRightsDomainErrors.TenantTerminationFreezeCheckpointInvalid,
            duplicate.Error);
        Assert.Equal(TenantTerminationProcessPhase.Freeze, process.Phase);
    }

    [Fact]
    public void Export_confirmation_must_match_the_frozen_revision()
    {
        TenantTerminationProcess process = Prepare(exportRequested: true);
        Assert.True(process.BeginPhase(
            TenantTerminationProcessPhase.Freeze,
            process.Version,
            Approver,
            Now.AddMinutes(1)).IsSuccess);
        Assert.True(CompleteFreeze(
            process,
            process.OperationRevision,
            process.Version,
            Approver,
            Now.AddMinutes(2)).IsSuccess);
        Assert.True(process.BeginPhase(
            TenantTerminationProcessPhase.Export,
            process.Version,
            Approver,
            Now.AddMinutes(3)).IsSuccess);

        Result mismatch = process.ConfirmExport(
            process.OperationRevision,
            Guid.NewGuid(),
            artifactVersion: 1,
            new string('c', 64),
            FragmentSetDigest,
            process.Version,
            Approver,
            Now.AddMinutes(4));

        Assert.True(mismatch.IsFailure);
        Assert.Equal(
            DataRightsDomainErrors.TenantTerminationExportConfirmationInvalid,
            mismatch.Error);
        Assert.Null(process.ExportConfirmedOperationRevision);
    }

    private static Result CompleteFreeze(
        TenantTerminationProcess process,
        long operationRevision,
        long expectedVersion,
        string actorId,
        DateTimeOffset frozenAtUtc) =>
        process.CompleteFreeze(
            operationRevision,
            workspaceFenceRevision: 3,
            Digest,
            [new("workspaces", 1, 1, Digest)],
            expectedVersion,
            actorId,
            frozenAtUtc);

    private static TenantTerminationProcess Prepare(bool exportRequested) =>
        TenantTerminationProcess.Prepare(
            Guid.NewGuid(),
            TenantId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            approvalRevision: 4,
            Guid.NewGuid(),
            exportRequested,
            Digest,
            Approver,
            Now,
            "system:tenant-termination",
            Now).Value;
}
