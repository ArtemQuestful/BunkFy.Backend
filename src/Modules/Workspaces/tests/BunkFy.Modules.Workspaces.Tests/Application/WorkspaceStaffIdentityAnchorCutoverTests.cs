namespace BunkFy.Modules.Workspaces.Tests;

using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Application.Handlers;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Results;
using Gma.Framework.Scoping;
using Gma.Modules.Organizations.Contracts;
using System.Text.Json;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspaceStaffIdentityAnchorCutoverTests
{
    private static readonly Guid OrganizationId =
        Guid.Parse("1d68a3a4-e4eb-4bf6-b36b-b0b447965d02");
    private static readonly string TenantId = OrganizationId.ToString("D");
    private static readonly DateTimeOffset ReviewedAtUtc =
        new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly string EvidenceSha256 = new('a', 64);

    [Fact]
    public async Task Status_is_write_free_and_reports_separate_tenant_bound_source_and_state_digests()
    {
        Guid applicationId = Guid.NewGuid();
        FakeSources sources = new([Source(applicationId, Guid.NewGuid())]);
        FakeStaffCutover staff = new();
        FakeOrganizations organizations = new(OrganizationId);
        WorkspaceStaffIdentityAnchorCutoverCoordinator coordinator = Create(
            sources,
            staff,
            organizations);

        WorkspaceStaffIdentityAnchorCutoverStatus first =
            (await coordinator.GetStatusAsync(
                TenantId,
                ownerManifest: null,
                CancellationToken.None)).Value;
        staff.AnchoredSources.Add((
            StaffIdentityProvisioningAnchorSourceKind.WorkspaceOnboarding,
            applicationId));
        WorkspaceStaffIdentityAnchorCutoverStatus second =
            (await coordinator.GetStatusAsync(
                TenantId,
                ownerManifest: null,
                CancellationToken.None)).Value;

        Assert.Equal(0, staff.ApplyCount);
        Assert.Equal(first.SourceEvidenceSha256, second.SourceEvidenceSha256);
        Assert.NotEqual(first.AnchorStateSha256, second.AnchorStateSha256);
        Assert.Equal(1, first.SeedableWorkspaceCount);
        Assert.Equal(1, second.AlreadyAnchoredCount);

        Guid otherOrganizationId = Guid.NewGuid();
        WorkspaceStaffIdentityAnchorCutoverStatus otherTenant =
            (await Create(
                    new FakeSources([Source(applicationId, staff.LastTargetId)]),
                    new FakeStaffCutover(),
                    new FakeOrganizations(otherOrganizationId))
                .GetStatusAsync(
                    otherOrganizationId.ToString("D"),
                    ownerManifest: null,
                    CancellationToken.None)).Value;
        Assert.NotEqual(
            first.SourceEvidenceSha256,
            otherTenant.SourceEvidenceSha256);
        Assert.NotEqual(first.AnchorStateSha256, otherTenant.AnchorStateSha256);
    }

    [Fact]
    public async Task V3_digests_bind_subject_status_target_disposition_and_record_count()
    {
        Guid applicationId = Guid.NewGuid();
        Guid targetId = Guid.NewGuid();
        WorkspaceStaffIdentityAnchorSourceRecord baselineRecord = Source(
            applicationId,
            targetId);

        WorkspaceStaffIdentityAnchorCutoverStatus baseline =
            await ReadStatusAsync([baselineRecord], new FakeStaffCutover());
        WorkspaceStaffIdentityAnchorCutoverStatus subjectChanged =
            await ReadStatusAsync(
                [baselineRecord with { SubjectId = "subject:changed" }],
                new FakeStaffCutover());
        WorkspaceStaffIdentityAnchorCutoverStatus statusChanged =
            await ReadStatusAsync(
                [baselineRecord with
                {
                    Status = WorkspaceStaffOnboardingState.Rejected
                }],
                new FakeStaffCutover());
        WorkspaceStaffIdentityAnchorCutoverStatus targetChanged =
            await ReadStatusAsync(
                [baselineRecord with { StaffMemberId = Guid.NewGuid() }],
                new FakeStaffCutover());
        WorkspaceStaffIdentityAnchorCutoverStatus countChanged =
            await ReadStatusAsync(
                [baselineRecord, Source(Guid.NewGuid(), Guid.NewGuid())],
                new FakeStaffCutover());
        FakeStaffCutover anchoredStaff = new();
        anchoredStaff.AnchoredSources.Add((
            StaffIdentityProvisioningAnchorSourceKind.WorkspaceOnboarding,
            applicationId));
        WorkspaceStaffIdentityAnchorCutoverStatus dispositionChanged =
            await ReadStatusAsync([baselineRecord], anchoredStaff);
        Guid membershipId = Guid.NewGuid();
        WorkspaceStaffIdentityAnchorCutoverStatus membershipSubjectBaseline =
            (await Create(
                    new FakeSources([]),
                    new FakeStaffCutover(),
                    CurrentOwner(membershipId, "subject:owner-a", 1))
                .GetStatusAsync(
                    TenantId,
                    EmptyManifest(),
                    CancellationToken.None)).Value;
        WorkspaceStaffIdentityAnchorCutoverStatus membershipSubjectChanged =
            (await Create(
                    new FakeSources([]),
                    new FakeStaffCutover(),
                    CurrentOwner(membershipId, "subject:owner-b", 1))
                .GetStatusAsync(
                    TenantId,
                    EmptyManifest(),
                    CancellationToken.None)).Value;

        Assert.NotEqual(
            baseline.SourceEvidenceSha256,
            subjectChanged.SourceEvidenceSha256);
        Assert.NotEqual(
            baseline.AnchorStateSha256,
            subjectChanged.AnchorStateSha256);
        Assert.NotEqual(
            baseline.SourceEvidenceSha256,
            statusChanged.SourceEvidenceSha256);
        Assert.NotEqual(
            baseline.SourceEvidenceSha256,
            targetChanged.SourceEvidenceSha256);
        Assert.NotEqual(
            baseline.AnchorStateSha256,
            targetChanged.AnchorStateSha256);
        Assert.NotEqual(
            baseline.SourceEvidenceSha256,
            countChanged.SourceEvidenceSha256);
        Assert.NotEqual(
            baseline.AnchorStateSha256,
            countChanged.AnchorStateSha256);
        Assert.Equal(
            baseline.SourceEvidenceSha256,
            dispositionChanged.SourceEvidenceSha256);
        Assert.NotEqual(
            baseline.AnchorStateSha256,
            dispositionChanged.AnchorStateSha256);
        Assert.NotEqual(
            membershipSubjectBaseline.SourceEvidenceSha256,
            membershipSubjectChanged.SourceEvidenceSha256);
        Assert.NotEqual(
            membershipSubjectBaseline.AnchorStateSha256,
            membershipSubjectChanged.AnchorStateSha256);
    }

    [Fact]
    public async Task Prepare_fails_closed_when_the_streamed_source_digest_changes()
    {
        Guid applicationId = Guid.NewGuid();
        FakeSources sources = new([Source(applicationId, Guid.NewGuid())]);
        FakeStaffCutover staff = new();
        WorkspaceStaffIdentityAnchorOwnerManifest manifest = EmptyManifest();
        WorkspaceStaffIdentityAnchorCutoverCoordinator coordinator = Create(
            sources,
            staff,
            new FakeOrganizations(OrganizationId));
        WorkspaceStaffIdentityAnchorCutoverStatus status =
            (await coordinator.GetStatusAsync(
                TenantId,
                manifest,
                CancellationToken.None)).Value;
        sources.Records[0] = sources.Records[0] with
        {
            SubjectId = "subject:changed"
        };

        Result<WorkspaceStaffIdentityAnchorPreparedReconcile> result =
            await coordinator.PrepareReconcileAsync(
                TenantId,
                status.SourceEvidenceSha256,
                status.AnchorStateSha256,
                manifest,
                status.OwnerManifestSha256!,
                batchSize: 100,
                CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffIdentityAnchorCutoverErrors.SourceEvidenceChanged,
            result.Error);
        Assert.Equal(0, staff.ApplyCount);
    }

    [Fact]
    public async Task Reconcile_acquires_the_Workspaces_lock_and_requires_source_state_and_manifest_evidence()
    {
        List<string> calls = [];
        Guid applicationId = Guid.NewGuid();
        FakeSources sources = new(
            [Source(applicationId, Guid.NewGuid())],
            calls);
        FakeStaffCutover staff = new(calls);
        WorkspaceStaffIdentityAnchorOwnerManifest manifest = EmptyManifest();
        WorkspaceStaffIdentityAnchorCutoverCoordinator coordinator = Create(
            sources,
            staff,
            new FakeOrganizations(OrganizationId));
        WorkspaceStaffIdentityAnchorCutoverStatus status =
            (await coordinator.GetStatusAsync(
                TenantId,
                manifest,
                CancellationToken.None)).Value;
        calls.Clear();
        ReconcileWorkspaceStaffIdentityAnchorsCommandHandler handler = new(
            coordinator,
            new RecordingCutoverExecutionBoundary(calls),
            new RecordingWorkspaceCrossGraphMutationLock(calls),
            staff,
            new DelegateFreshStatusReader((prepared, token) => ReadFreshAsync(
                coordinator,
                staff,
                manifest,
                prepared,
                token)),
            new TestScopeContext());
        ReconcileWorkspaceStaffIdentityAnchorsCommand command = new(
            status.SourceEvidenceSha256,
            status.AnchorStateSha256,
            manifest,
            status.OwnerManifestSha256!,
            100);

        Result<WorkspaceStaffIdentityAnchorReconcileResult> first =
            await handler.HandleAsync(command, CancellationToken.None);

        Assert.True(first.IsSuccess, first.Error.Code);
        Assert.True(
            calls.IndexOf("boundary.released") < calls.IndexOf("apply"));
        Assert.True(
            calls.IndexOf("tenant-exclusive") <
            calls.IndexOf("boundary.released"));
        Assert.Equal(1, first.Value.AppliedCount);
        Assert.True(first.Value.Status!.IsReady);
    }

    [Fact]
    public async Task Reconcile_rejects_changed_anchor_state_before_any_write()
    {
        Guid applicationId = Guid.NewGuid();
        FakeStaffCutover staff = new();
        WorkspaceStaffIdentityAnchorOwnerManifest manifest = EmptyManifest();
        WorkspaceStaffIdentityAnchorCutoverCoordinator coordinator = Create(
            new FakeSources([Source(applicationId, Guid.NewGuid())]),
            staff,
            new FakeOrganizations(OrganizationId));
        WorkspaceStaffIdentityAnchorCutoverStatus status =
            (await coordinator.GetStatusAsync(
                TenantId,
                manifest,
                CancellationToken.None)).Value;
        staff.AnchoredSources.Add((
            StaffIdentityProvisioningAnchorSourceKind.WorkspaceOnboarding,
            applicationId));

        Result<WorkspaceStaffIdentityAnchorPreparedReconcile> result =
            await coordinator.PrepareReconcileAsync(
                TenantId,
                status.SourceEvidenceSha256,
                status.AnchorStateSha256,
                manifest,
                status.OwnerManifestSha256!,
                100,
                CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffIdentityAnchorCutoverErrors.AnchorStateChanged,
            result.Error);
        Assert.Equal(0, staff.ApplyCount);
    }

    [Fact]
    public async Task Reconcile_reports_unknown_and_reads_fresh_state_after_lost_apply_response()
    {
        Guid applicationId = Guid.NewGuid();
        FakeStaffCutover staff = new();
        FakeOrganizations organizations = new(OrganizationId);
        WorkspaceStaffIdentityAnchorOwnerManifest manifest = EmptyManifest();
        WorkspaceStaffIdentityAnchorCutoverCoordinator coordinator = Create(
            new FakeSources([Source(applicationId, Guid.NewGuid())]),
            staff,
            organizations);
        WorkspaceStaffIdentityAnchorCutoverStatus status =
            (await coordinator.GetStatusAsync(
                TenantId,
                manifest,
                CancellationToken.None)).Value;
        using CancellationTokenSource cancellationSource = new();
        staff.AfterApply = () =>
        {
            cancellationSource.Cancel();
            throw new OperationCanceledException(cancellationSource.Token);
        };
        DelegateFreshStatusReader fresh = new((prepared, token) =>
            ReadFreshAsync(
                coordinator,
                staff,
                manifest,
                prepared,
                token));
        ReconcileWorkspaceStaffIdentityAnchorsCommandHandler handler = new(
            coordinator,
            new RecordingCutoverExecutionBoundary([]),
            new RecordingWorkspaceCrossGraphMutationLock([]),
            staff,
            fresh,
            new TestScopeContext());

        Result<WorkspaceStaffIdentityAnchorReconcileResult> result =
            await handler.HandleAsync(
                new ReconcileWorkspaceStaffIdentityAnchorsCommand(
                status.SourceEvidenceSha256,
                status.AnchorStateSha256,
                manifest,
                status.OwnerManifestSha256!,
                100),
                cancellationSource.Token);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Null(result.Value.AppliedCount);
        Assert.True(result.Value.Status!.IsReady);
        Assert.Equal(
            WorkspaceStaffIdentityAnchorReconcileOutcome
                .ApplyOutcomeUnknown,
            result.Value.Outcome);
        Assert.True(result.Value.MustRerunStatus);
        Assert.Equal(1, staff.ApplyCount);
        Assert.Equal(1, fresh.ReadCount);
        Assert.False(fresh.LastCancellationToken.IsCancellationRequested);
        Assert.Contains(
            (StaffIdentityProvisioningAnchorSourceKind.WorkspaceOnboarding,
                applicationId),
            staff.AnchoredSources);
    }

    [Fact]
    public async Task Reconcile_does_not_apply_when_the_preflight_boundary_cannot_confirm_release()
    {
        Guid applicationId = Guid.NewGuid();
        FakeStaffCutover staff = new();
        WorkspaceStaffIdentityAnchorOwnerManifest manifest = EmptyManifest();
        WorkspaceStaffIdentityAnchorCutoverCoordinator coordinator = Create(
            new FakeSources([Source(applicationId, Guid.NewGuid())]),
            staff,
            new FakeOrganizations(OrganizationId));
        WorkspaceStaffIdentityAnchorCutoverStatus status =
            (await coordinator.GetStatusAsync(
                TenantId,
                manifest,
                CancellationToken.None)).Value;
        DelegateFreshStatusReader fresh = new((prepared, token) =>
            ReadFreshAsync(
                coordinator,
                staff,
                manifest,
                prepared,
                token));
        ReconcileWorkspaceStaffIdentityAnchorsCommandHandler handler = new(
            coordinator,
            new UncertainReleaseCutoverExecutionBoundary(),
            new RecordingWorkspaceCrossGraphMutationLock([]),
            staff,
            fresh,
            new TestScopeContext());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleAsync(
                new ReconcileWorkspaceStaffIdentityAnchorsCommand(
                    status.SourceEvidenceSha256,
                    status.AnchorStateSha256,
                    manifest,
                    status.OwnerManifestSha256!,
                    100),
                CancellationToken.None));

        Assert.Equal(0, staff.ApplyCount);
        Assert.Equal(0, fresh.ReadCount);
    }

    [Fact]
    public async Task Reconcile_treats_a_malformed_success_as_unknown_without_retrying()
    {
        Guid applicationId = Guid.NewGuid();
        FakeStaffCutover staff = new()
        {
            ApplyResultOverride = new(
                IsSuccess: true,
                AppliedCount: 2,
                AlreadyAnchoredCount: 0,
                ErrorCode: null)
        };
        WorkspaceStaffIdentityAnchorOwnerManifest manifest = EmptyManifest();
        WorkspaceStaffIdentityAnchorCutoverCoordinator coordinator = Create(
            new FakeSources([Source(applicationId, Guid.NewGuid())]),
            staff,
            new FakeOrganizations(OrganizationId));
        WorkspaceStaffIdentityAnchorCutoverStatus status =
            (await coordinator.GetStatusAsync(
                TenantId,
                manifest,
                CancellationToken.None)).Value;
        DelegateFreshStatusReader fresh = new((prepared, token) =>
            ReadFreshAsync(
                coordinator,
                staff,
                manifest,
                prepared,
                token));
        ReconcileWorkspaceStaffIdentityAnchorsCommandHandler handler = new(
            coordinator,
            new RecordingCutoverExecutionBoundary([]),
            new RecordingWorkspaceCrossGraphMutationLock([]),
            staff,
            fresh,
            new TestScopeContext());

        Result<WorkspaceStaffIdentityAnchorReconcileResult> result =
            await handler.HandleAsync(
                new ReconcileWorkspaceStaffIdentityAnchorsCommand(
                    status.SourceEvidenceSha256,
                    status.AnchorStateSha256,
                    manifest,
                    status.OwnerManifestSha256!,
                    100),
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(
            WorkspaceStaffIdentityAnchorReconcileOutcome.ApplyOutcomeUnknown,
            result.Value.Outcome);
        Assert.Null(result.Value.AppliedCount);
        Assert.True(result.Value.Status!.IsReady);
        Assert.Equal(1, staff.ApplyCount);
        Assert.Equal(1, fresh.ReadCount);
    }

    [Fact]
    public async Task Reconcile_keeps_a_known_apply_count_but_is_unknown_when_fresh_status_fails()
    {
        FakeStaffCutover staff = new();
        WorkspaceStaffIdentityAnchorOwnerManifest manifest = EmptyManifest();
        WorkspaceStaffIdentityAnchorCutoverCoordinator coordinator = Create(
            new FakeSources([Source(Guid.NewGuid(), Guid.NewGuid())]),
            staff,
            new FakeOrganizations(OrganizationId));
        WorkspaceStaffIdentityAnchorCutoverStatus status =
            (await coordinator.GetStatusAsync(
                TenantId,
                manifest,
                CancellationToken.None)).Value;
        DelegateFreshStatusReader fresh = new((_, _) => Task.FromResult(
            Result.Failure<WorkspaceStaffIdentityAnchorFreshVerification>(
                WorkspaceStaffIdentityAnchorCutoverErrors
                    .OrganizationsUnavailable)));
        ReconcileWorkspaceStaffIdentityAnchorsCommandHandler handler = new(
            coordinator,
            new RecordingCutoverExecutionBoundary([]),
            new RecordingWorkspaceCrossGraphMutationLock([]),
            staff,
            fresh,
            new TestScopeContext());

        Result<WorkspaceStaffIdentityAnchorReconcileResult> result =
            await handler.HandleAsync(
                new ReconcileWorkspaceStaffIdentityAnchorsCommand(
                    status.SourceEvidenceSha256,
                    status.AnchorStateSha256,
                    manifest,
                    status.OwnerManifestSha256!,
                    100),
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(1, result.Value.AppliedCount);
        Assert.Null(result.Value.Status);
        Assert.Equal(
            WorkspaceStaffIdentityAnchorReconcileOutcome.ApplyOutcomeUnknown,
            result.Value.Outcome);
        Assert.True(result.Value.MustRerunStatus);
        Assert.Equal(1, staff.ApplyCount);
        Assert.Equal(1, fresh.ReadCount);
    }

    [Fact]
    public async Task Reconcile_marks_source_drift_during_fresh_verification_unknown()
    {
        FakeSources sources = new([
            Source(Guid.NewGuid(), Guid.NewGuid())
        ]);
        FakeStaffCutover staff = new();
        WorkspaceStaffIdentityAnchorOwnerManifest manifest = EmptyManifest();
        WorkspaceStaffIdentityAnchorCutoverCoordinator coordinator = Create(
            sources,
            staff,
            new FakeOrganizations(OrganizationId));
        WorkspaceStaffIdentityAnchorCutoverStatus status =
            (await coordinator.GetStatusAsync(
                TenantId,
                manifest,
                CancellationToken.None)).Value;
        DelegateFreshStatusReader fresh = new(async (prepared, token) =>
        {
            sources.Records.Add(Source(Guid.NewGuid(), Guid.NewGuid()));
            return await ReadFreshAsync(
                coordinator,
                staff,
                manifest,
                prepared,
                token);
        });
        ReconcileWorkspaceStaffIdentityAnchorsCommandHandler handler = new(
            coordinator,
            new RecordingCutoverExecutionBoundary([]),
            new RecordingWorkspaceCrossGraphMutationLock([]),
            staff,
            fresh,
            new TestScopeContext());

        Result<WorkspaceStaffIdentityAnchorReconcileResult> result =
            await handler.HandleAsync(
                new ReconcileWorkspaceStaffIdentityAnchorsCommand(
                    status.SourceEvidenceSha256,
                    status.AnchorStateSha256,
                    manifest,
                    status.OwnerManifestSha256!,
                    100),
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(
            WorkspaceStaffIdentityAnchorReconcileOutcome.ApplyOutcomeUnknown,
            result.Value.Outcome);
        Assert.Equal(1, result.Value.AppliedCount);
        Assert.NotNull(result.Value.Status);
        Assert.NotEqual(
            result.Value.AcceptedSourceEvidenceSha256,
            result.Value.Status.SourceEvidenceSha256);
        Assert.Equal(1, staff.ApplyCount);
    }

    [Fact]
    public async Task Reconcile_requires_exact_batch_convergence_in_fresh_inspection()
    {
        FakeStaffCutover staff = new();
        WorkspaceStaffIdentityAnchorOwnerManifest manifest = EmptyManifest();
        WorkspaceStaffIdentityAnchorCutoverCoordinator coordinator = Create(
            new FakeSources([Source(Guid.NewGuid(), Guid.NewGuid())]),
            staff,
            new FakeOrganizations(OrganizationId));
        WorkspaceStaffIdentityAnchorCutoverStatus status =
            (await coordinator.GetStatusAsync(
                TenantId,
                manifest,
                CancellationToken.None)).Value;
        DelegateFreshStatusReader fresh = new(async (prepared, token) =>
        {
            Result<WorkspaceStaffIdentityAnchorCutoverStatus> rebuilt =
                await coordinator.GetStatusAsync(TenantId, manifest, token);
            return Result.Success(
                new WorkspaceStaffIdentityAnchorFreshVerification(
                    rebuilt.Value,
                    new StaffIdentityProvisioningAnchorInspection(
                        true,
                        prepared.Batch.Select(candidate => new
                            StaffIdentityProvisioningAnchorCandidateInspection(
                                candidate.SourceKind,
                                candidate.SourceId,
                                StaffIdentityProvisioningAnchorCutoverDisposition
                                    .SeedableFromWorkspace)).ToArray(),
                        null)));
        });
        ReconcileWorkspaceStaffIdentityAnchorsCommandHandler handler = new(
            coordinator,
            new RecordingCutoverExecutionBoundary([]),
            new RecordingWorkspaceCrossGraphMutationLock([]),
            staff,
            fresh,
            new TestScopeContext());

        Result<WorkspaceStaffIdentityAnchorReconcileResult> result =
            await handler.HandleAsync(
                new ReconcileWorkspaceStaffIdentityAnchorsCommand(
                    status.SourceEvidenceSha256,
                    status.AnchorStateSha256,
                    manifest,
                    status.OwnerManifestSha256!,
                    100),
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(
            WorkspaceStaffIdentityAnchorReconcileOutcome.ApplyOutcomeUnknown,
            result.Value.Outcome);
        Assert.True(result.Value.Status!.IsReady);
        Assert.Equal(1, staff.ApplyCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Status_rejects_malformed_or_source_incompatible_Staff_inspection(
        int mode)
    {
        FakeStaffCutover staff = new()
        {
            InspectionOverride = candidates => mode switch
            {
                0 => new(
                true,
                candidates.Select(candidate => new
                    StaffIdentityProvisioningAnchorCandidateInspection(
                        candidate.SourceKind,
                        candidate.SourceId,
                        StaffIdentityProvisioningAnchorCutoverDisposition
                            .SeedableFromReviewedOwnerMap)).ToArray(),
                null),
                1 => new(
                true,
                [
                    new(candidates[0].SourceKind, candidates[0].SourceId,
                        StaffIdentityProvisioningAnchorCutoverDisposition
                            .SeedableFromWorkspace),
                    new(candidates[0].SourceKind, candidates[0].SourceId,
                        StaffIdentityProvisioningAnchorCutoverDisposition
                            .SeedableFromWorkspace)
                ],
                null),
                2 => new(
                true,
                candidates.Select(candidate => new
                    StaffIdentityProvisioningAnchorCandidateInspection(
                        candidate.SourceKind,
                        candidate.SourceId,
                        StaffIdentityProvisioningAnchorCutoverDisposition
                            .SeedableFromWorkspace)).ToArray(),
                "unexpected-success-error"),
                3 => new(
                true,
                [
                    new(candidates[0].SourceKind, Guid.NewGuid(),
                        StaffIdentityProvisioningAnchorCutoverDisposition
                            .SeedableFromWorkspace),
                    new(candidates[1].SourceKind, candidates[1].SourceId,
                        StaffIdentityProvisioningAnchorCutoverDisposition
                            .SeedableFromWorkspace)
                ],
                    null),
                _ => new(
                    true,
                    candidates.Select(candidate => new
                        StaffIdentityProvisioningAnchorCandidateInspection(
                            candidate.SourceKind,
                            candidate.SourceId,
                            StaffIdentityProvisioningAnchorCutoverDisposition
                                .Unknown)).ToArray(),
                    null)
            }
        };
        WorkspaceStaffIdentityAnchorCutoverCoordinator coordinator = Create(
            new FakeSources([
                Source(Guid.NewGuid(), Guid.NewGuid()),
                Source(Guid.NewGuid(), Guid.NewGuid())
            ]),
            staff,
            new FakeOrganizations(OrganizationId));

        Result<WorkspaceStaffIdentityAnchorCutoverStatus> result =
            await coordinator.GetStatusAsync(
                TenantId,
                EmptyManifest(),
                CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffIdentityAnchorCutoverErrors.StaffUnavailable,
            result.Error);
        Assert.Equal(0, staff.ApplyCount);
    }

    [Fact]
    public async Task Reconcile_rejects_noncanonical_tenant_scope_before_boundary_or_lock()
    {
        List<string> calls = [];
        FakeStaffCutover staff = new(calls);
        WorkspaceStaffIdentityAnchorCutoverCoordinator coordinator = Create(
            new FakeSources([], calls),
            staff,
            new FakeOrganizations(OrganizationId));
        DelegateFreshStatusReader fresh = new((_, _) => throw new
            InvalidOperationException("Fresh status must not run."));
        ReconcileWorkspaceStaffIdentityAnchorsCommandHandler handler = new(
            coordinator,
            new RecordingCutoverExecutionBoundary(calls),
            new RecordingWorkspaceCrossGraphMutationLock(calls),
            staff,
            fresh,
            new TestScopeContext(TenantId.ToUpperInvariant()));

        Result<WorkspaceStaffIdentityAnchorReconcileResult> result =
            await handler.HandleAsync(
                new ReconcileWorkspaceStaffIdentityAnchorsCommand(
                    new string('a', 64),
                    new string('b', 64),
                    EmptyManifest(),
                    new string('c', 64),
                    100),
                CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffIdentityAnchorCutoverErrors.TenantRequired,
            result.Error);
        Assert.Empty(calls);
        Assert.Equal(0, staff.ApplyCount);
        Assert.Equal(0, fresh.ReadCount);
    }

    [Fact]
    public async Task Current_owner_universe_is_not_defined_by_an_empty_manifest()
    {
        Guid membershipId = Guid.NewGuid();
        FakeOrganizations organizations = new(OrganizationId);
        organizations.AddMembership(
            membershipId,
            "subject:owner",
            OrganizationMembershipRole.Owner,
            OrganizationMembershipStatus.Active,
            version: 7);
        WorkspaceStaffIdentityAnchorCutoverCoordinator coordinator = Create(
            new FakeSources([]),
            new FakeStaffCutover(),
            organizations);

        WorkspaceStaffIdentityAnchorCutoverStatus status =
            (await coordinator.GetStatusAsync(
                TenantId,
                EmptyManifest(),
                CancellationToken.None)).Value;

        Assert.Equal(1, status.AuthoritativeOwnerCount);
        Assert.Equal(1, status.AmbiguousCount);
        Assert.False(status.CanReconcile);
        Assert.Equal(1, organizations.MembershipExportCallCount);
    }

    [Fact]
    public async Task Existing_anchor_covers_a_current_owner_without_a_new_binding()
    {
        Guid membershipId = Guid.NewGuid();
        FakeOrganizations organizations = CurrentOwner(
            membershipId,
            "subject:owner",
            version: 4);
        FakeStaffCutover staff = new();
        staff.AnchoredSources.Add((
            StaffIdentityProvisioningAnchorSourceKind.OrganizationMembership,
            membershipId));

        WorkspaceStaffIdentityAnchorCutoverStatus status =
            (await Create(
                    new FakeSources([]),
                    staff,
                    organizations)
                .GetStatusAsync(
                    TenantId,
                    EmptyManifest(),
                    CancellationToken.None)).Value;

        Assert.True(status.CanReconcile);
        Assert.True(status.IsReady);
        Assert.Equal(1, status.AlreadyAnchoredCount);
    }

    [Fact]
    public async Task Reviewed_current_owner_binding_is_rechecked_and_passes_subject_only_to_Staff()
    {
        Guid membershipId = Guid.NewGuid();
        Guid staffMemberId = Guid.NewGuid();
        FakeOrganizations organizations = CurrentOwner(
            membershipId,
            "subject:owner",
            version: 7);
        FakeStaffCutover staff = new();
        WorkspaceStaffIdentityAnchorCutoverStatus status =
            (await Create(
                    new FakeSources([]),
                    staff,
                    organizations)
                .GetStatusAsync(
                    TenantId,
                    Manifest(CurrentBinding(
                        membershipId,
                        staffMemberId,
                        observedVersion: 7)),
                    CancellationToken.None)).Value;

        Assert.Equal(1, status.SeedableOwnerCount);
        Assert.True(status.CanReconcile);
        StaffIdentityProvisioningAnchorCandidate candidate =
            Assert.Single(staff.LastCandidates);
        Assert.Equal("subject:owner", candidate.ExpectedAuthSubjectId);
        Assert.DoesNotContain(
            "subject:owner",
            status.SourceEvidenceSha256,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Historical_binding_uses_external_attestation_and_does_not_require_current_Owner_or_Active_state()
    {
        Guid membershipId = Guid.NewGuid();
        Guid staffMemberId = Guid.NewGuid();
        FakeOrganizations organizations = new(OrganizationId);
        organizations.AddMembership(
            membershipId,
            "subject:former-owner",
            OrganizationMembershipRole.Member,
            OrganizationMembershipStatus.Removed,
            version: 9);
        WorkspaceStaffIdentityAnchorOwnerBinding binding = new(
            OrganizationId,
            TenantId,
            membershipId,
            staffMemberId,
            8,
            WorkspaceStaffIdentityAnchorOwnerBindingEvidenceKind
                .HistoricalOwnerExternalReview);

        WorkspaceStaffIdentityAnchorCutoverStatus status =
            (await Create(
                    new FakeSources([]),
                    new FakeStaffCutover(),
                    organizations)
                .GetStatusAsync(
                    TenantId,
                    Manifest(
                        binding,
                        WorkspaceStaffIdentityAnchorHistoricalEvidenceKind
                            .ReviewedHistoricalOwnerUniverse),
                    CancellationToken.None)).Value;

        Assert.Equal(0, status.AuthoritativeOwnerCount);
        Assert.Equal(1, status.HistoricalBindingCount);
        Assert.Equal(1, status.SeedableOwnerCount);
        Assert.True(status.CanReconcile);
    }

    [Fact]
    public async Task Stale_or_unretained_historical_binding_blocks_and_zero_data_attestation_cannot_claim_it()
    {
        WorkspaceStaffIdentityAnchorOwnerBinding historical = new(
            OrganizationId,
            TenantId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            WorkspaceStaffIdentityAnchorOwnerBindingEvidenceKind
                .HistoricalOwnerExternalReview);
        WorkspaceStaffIdentityAnchorCutoverCoordinator coordinator = Create(
            new FakeSources([]),
            new FakeStaffCutover(),
            new FakeOrganizations(OrganizationId));

        Result<WorkspaceStaffIdentityAnchorCutoverStatus> invalidZeroData =
            await coordinator.GetStatusAsync(
                TenantId,
                Manifest(
                    historical,
                    WorkspaceStaffIdentityAnchorHistoricalEvidenceKind
                        .ReviewedNoHistoricalOwnerSources),
                CancellationToken.None);
        WorkspaceStaffIdentityAnchorCutoverStatus stale =
            (await coordinator.GetStatusAsync(
                TenantId,
                Manifest(
                    historical,
                    WorkspaceStaffIdentityAnchorHistoricalEvidenceKind
                        .ReviewedHistoricalOwnerUniverse),
                CancellationToken.None)).Value;

        Assert.Equal(
            WorkspaceStaffIdentityAnchorCutoverErrors.OwnerManifestInvalid,
            invalidZeroData.Error);
        Assert.Equal(1, stale.ConflictCount);
        Assert.False(stale.CanReconcile);
    }

    [Fact]
    public async Task Owner_snapshot_revision_drift_aborts_the_plan_instead_of_using_a_stale_universe()
    {
        Guid membershipId = Guid.NewGuid();
        FakeOrganizations organizations = CurrentOwner(
            membershipId,
            "subject:owner",
            version: 3);
        organizations.ClosedOnSnapshotCall = 2;

        Result<WorkspaceStaffIdentityAnchorCutoverStatus> result =
            await Create(
                    new FakeSources([]),
                    new FakeStaffCutover(),
                    organizations)
                .GetStatusAsync(
                    TenantId,
                    EmptyManifest(),
                    CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffIdentityAnchorCutoverErrors
                .OrganizationsEvidenceChanged,
            result.Error);
    }

    [Fact]
    public async Task Noncanonical_Organizations_subject_fails_closed_without_Staff_inspection()
    {
        FakeOrganizations organizations = CurrentOwner(
            Guid.NewGuid(),
            " subject:owner ",
            version: 1);
        FakeStaffCutover staff = new();

        Result<WorkspaceStaffIdentityAnchorCutoverStatus> result =
            await Create(
                    new FakeSources([]),
                    staff,
                    organizations)
                .GetStatusAsync(
                    TenantId,
                    EmptyManifest(),
                    CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffIdentityAnchorCutoverErrors
                .OrganizationsUnavailable,
            result.Error);
        Assert.Empty(staff.LastCandidates);
    }

    [Fact]
    public async Task Reconcile_preflights_the_whole_tenant_and_writes_nothing_when_any_source_is_ambiguous()
    {
        FakeStaffCutover staff = new();
        WorkspaceStaffIdentityAnchorOwnerManifest manifest = EmptyManifest();
        WorkspaceStaffIdentityAnchorCutoverCoordinator coordinator = Create(
            new FakeSources([
                Source(Guid.NewGuid(), Guid.NewGuid()),
                Source(Guid.NewGuid(), null)
            ]),
            staff,
            new FakeOrganizations(OrganizationId));
        WorkspaceStaffIdentityAnchorCutoverStatus status =
            (await coordinator.GetStatusAsync(
                TenantId,
                manifest,
                CancellationToken.None)).Value;

        Result<WorkspaceStaffIdentityAnchorPreparedReconcile> result =
            await coordinator.PrepareReconcileAsync(
                TenantId,
                status.SourceEvidenceSha256,
                status.AnchorStateSha256,
                manifest,
                status.OwnerManifestSha256!,
                100,
                CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffIdentityAnchorCutoverErrors.Blocked,
            result.Error);
        Assert.Equal(0, staff.ApplyCount);
        Assert.Equal(1, status.AmbiguousCount);
    }

    [Fact]
    public async Task Status_returns_bounded_deterministic_privacy_minimal_issue_coordinates()
    {
        WorkspaceStaffIdentityAnchorSourceRecord[] sources = Enumerable
            .Range(
                1,
                WorkspaceStaffIdentityAnchorCutoverStatusLimits.MaximumIssues +
                    3)
            .Select(value => Source(new Guid(
                value,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0), null))
            .Reverse()
            .ToArray();

        WorkspaceStaffIdentityAnchorCutoverStatus status =
            (await Create(
                    new FakeSources(sources),
                    new FakeStaffCutover(),
                    new FakeOrganizations(OrganizationId))
                .GetStatusAsync(
                    TenantId,
                    EmptyManifest(),
                    CancellationToken.None)).Value;

        Assert.Equal(sources.Length, status.TotalIssueCount);
        Assert.True(status.HasMoreIssues);
        Assert.Equal(
            WorkspaceStaffIdentityAnchorCutoverStatusLimits.MaximumIssues,
            status.Issues.Count);
        Assert.Equal(
            status.Issues.OrderBy(issue => issue.SourceKind)
                .ThenBy(issue => issue.SourceId),
            status.Issues);
        Assert.All(status.Issues, issue =>
        {
            Assert.Equal(
                StaffIdentityProvisioningAnchorCutoverDisposition.Ambiguous,
                issue.Disposition);
            Assert.Null(issue.CandidateStaffMemberId);
            Assert.Null(issue.ObservedMembershipVersion);
        });
        string json = JsonSerializer.Serialize(status);
        Assert.DoesNotContain("subject", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("profile", json, StringComparison.OrdinalIgnoreCase);
    }

    private static WorkspaceStaffIdentityAnchorCutoverCoordinator Create(
        FakeSources sources,
        FakeStaffCutover staff,
        FakeOrganizations organizations) =>
        new(
            sources,
            staff,
            organizations);

    private static async Task<WorkspaceStaffIdentityAnchorCutoverStatus>
        ReadStatusAsync(
            IReadOnlyList<WorkspaceStaffIdentityAnchorSourceRecord> records,
            FakeStaffCutover staff) =>
        (await Create(
                new FakeSources(records),
                staff,
                new FakeOrganizations(OrganizationId))
            .GetStatusAsync(
                TenantId,
                ownerManifest: null,
                CancellationToken.None)).Value;

    private static async Task<
        Result<WorkspaceStaffIdentityAnchorFreshVerification>> ReadFreshAsync(
            WorkspaceStaffIdentityAnchorCutoverCoordinator coordinator,
            FakeStaffCutover staff,
            WorkspaceStaffIdentityAnchorOwnerManifest manifest,
            WorkspaceStaffIdentityAnchorPreparedReconcile prepared,
            CancellationToken cancellationToken)
    {
        Result<WorkspaceStaffIdentityAnchorCutoverStatus> status =
            await coordinator.GetStatusAsync(
                TenantId,
                manifest,
                cancellationToken);
        if (status.IsFailure)
        {
            return Result.Failure<
                WorkspaceStaffIdentityAnchorFreshVerification>(status.Error);
        }

        StaffIdentityProvisioningAnchorInspection inspection =
            await staff.InspectAsync(prepared.Batch, cancellationToken);
        return Result.Success(
            new WorkspaceStaffIdentityAnchorFreshVerification(
                status.Value,
                inspection));
    }

    private static WorkspaceStaffIdentityAnchorSourceRecord Source(
        Guid applicationId,
        Guid? staffMemberId) =>
        new(
            applicationId,
            staffMemberId,
            $"subject:{applicationId:N}",
            staffMemberId.HasValue
                ? WorkspaceStaffOnboardingState.Completed
                : WorkspaceStaffOnboardingState.Failed);

    private static WorkspaceStaffIdentityAnchorOwnerManifest EmptyManifest() =>
        new(
            1,
            TenantId,
            ReviewedAtUtc,
            new(
                WorkspaceStaffIdentityAnchorHistoricalEvidenceKind
                    .ReviewedNoHistoricalOwnerSources,
                EvidenceSha256),
            []);

    private static WorkspaceStaffIdentityAnchorOwnerManifest Manifest(
        WorkspaceStaffIdentityAnchorOwnerBinding binding,
        WorkspaceStaffIdentityAnchorHistoricalEvidenceKind evidenceKind =
            WorkspaceStaffIdentityAnchorHistoricalEvidenceKind
                .ReviewedNoHistoricalOwnerSources) =>
        new(
            1,
            TenantId,
            ReviewedAtUtc,
            new(evidenceKind, EvidenceSha256),
            [binding]);

    private static WorkspaceStaffIdentityAnchorOwnerBinding CurrentBinding(
        Guid membershipId,
        Guid staffMemberId,
        long observedVersion) =>
        new(
            OrganizationId,
            TenantId,
            membershipId,
            staffMemberId,
            observedVersion,
            WorkspaceStaffIdentityAnchorOwnerBindingEvidenceKind
                .CurrentActiveOwnerReview);

    private static FakeOrganizations CurrentOwner(
        Guid membershipId,
        string subjectId,
        long version)
    {
        FakeOrganizations organizations = new(OrganizationId);
        organizations.AddMembership(
            membershipId,
            subjectId,
            OrganizationMembershipRole.Owner,
            OrganizationMembershipStatus.Active,
            version);
        return organizations;
    }

    private sealed class FakeSources(
        IEnumerable<WorkspaceStaffIdentityAnchorSourceRecord> records,
        List<string>? calls = null)
        : IWorkspaceStaffIdentityAnchorCutoverSourceReader
    {
        public List<WorkspaceStaffIdentityAnchorSourceRecord> Records { get; } =
            [.. records];

        public Task<WorkspaceStaffIdentityAnchorSourcePage>
            ListRelevantPageAsync(
                Guid? afterApplicationId,
                int pageSize,
                CancellationToken cancellationToken)
        {
            calls?.Add("source");
            WorkspaceStaffIdentityAnchorSourceRecord[] loaded = this.Records
                .Where(record => !afterApplicationId.HasValue ||
                    record.ApplicationId.CompareTo(
                        afterApplicationId.Value) > 0)
                .OrderBy(record => record.ApplicationId)
                .Take(pageSize + 1)
                .ToArray();
            WorkspaceStaffIdentityAnchorSourceRecord[] selected = loaded
                .Take(pageSize)
                .ToArray();
            return Task.FromResult(new WorkspaceStaffIdentityAnchorSourcePage(
                selected,
                selected.Length == 0
                    ? afterApplicationId
                    : selected[^1].ApplicationId,
                loaded.Length > pageSize));
        }
    }

    private sealed class FakeStaffCutover(List<string>? calls = null)
        : IStaffIdentityProvisioningAnchorCutover
    {
        public HashSet<(StaffIdentityProvisioningAnchorSourceKind, Guid)>
            AnchoredSources
        { get; } = [];
        public int ApplyCount { get; private set; }
        public Action? AfterApply { get; set; }
        public StaffIdentityProvisioningAnchorApplyResult? ApplyResultOverride
        { get; set; }
        public Func<IReadOnlyList<StaffIdentityProvisioningAnchorCandidate>,
            StaffIdentityProvisioningAnchorInspection>? InspectionOverride
        { get; set; }
        public Guid? LastTargetId { get; private set; }
        public IReadOnlyList<StaffIdentityProvisioningAnchorCandidate>
            LastCandidates
        { get; private set; } = [];

        public Task<StaffIdentityProvisioningAnchorInspection> InspectAsync(
            IReadOnlyList<StaffIdentityProvisioningAnchorCandidate> candidates,
            CancellationToken cancellationToken = default)
        {
            calls?.Add("inspect");
            this.LastCandidates = candidates.ToArray();
            this.LastTargetId = candidates.Count == 0
                ? null
                : candidates[0].StaffMemberId;
            if (this.InspectionOverride is not null)
            {
                return Task.FromResult(this.InspectionOverride(candidates));
            }

            return Task.FromResult(new StaffIdentityProvisioningAnchorInspection(
                true,
                candidates.Select(candidate =>
                    new StaffIdentityProvisioningAnchorCandidateInspection(
                        candidate.SourceKind,
                        candidate.SourceId,
                        this.Disposition(candidate)))
                    .ToArray(),
                null));
        }

        public Task<StaffIdentityProvisioningAnchorApplyResult> ApplyAsync(
            IReadOnlyList<StaffIdentityProvisioningAnchorCandidate> candidates,
            CancellationToken cancellationToken = default)
        {
            this.ApplyCount++;
            calls?.Add("apply");
            int applied = candidates.Count(candidate =>
                this.AnchoredSources.Add(
                    (candidate.SourceKind, candidate.SourceId)));
            this.AfterApply?.Invoke();
            return Task.FromResult(
                this.ApplyResultOverride ??
                new StaffIdentityProvisioningAnchorApplyResult(
                    true,
                    applied,
                    candidates.Count - applied,
                    null));
        }

        private StaffIdentityProvisioningAnchorCutoverDisposition Disposition(
            StaffIdentityProvisioningAnchorCandidate candidate)
        {
            if (this.AnchoredSources.Contains(
                (candidate.SourceKind, candidate.SourceId)))
            {
                return StaffIdentityProvisioningAnchorCutoverDisposition
                    .AlreadyAnchored;
            }

            if (!candidate.StaffMemberId.HasValue)
            {
                return StaffIdentityProvisioningAnchorCutoverDisposition
                    .Ambiguous;
            }

            return candidate.SourceKind ==
                StaffIdentityProvisioningAnchorSourceKind.WorkspaceOnboarding
                ? StaffIdentityProvisioningAnchorCutoverDisposition
                    .SeedableFromWorkspace
                : StaffIdentityProvisioningAnchorCutoverDisposition
                    .SeedableFromReviewedOwnerMap;
        }
    }

    private sealed class FakeOrganizations(Guid organizationId)
        : IOrganizationScopeLifecycle
    {
        private readonly List<OrganizationScopeMembershipExportRecord>
            memberships = [];

        public long Revision { get; set; } = 1;
        public int SnapshotCallCount { get; private set; }
        public int MembershipExportCallCount { get; private set; }
        public int? ClosedOnSnapshotCall { get; set; }

        public void AddMembership(
            Guid membershipId,
            string subjectId,
            OrganizationMembershipRole role,
            OrganizationMembershipStatus status,
            long version) => this.memberships.Add(new(
                membershipId,
                organizationId,
                subjectId,
                role,
                status,
                version,
                "system:test",
                ReviewedAtUtc.AddDays(-1),
                "system:test",
                ReviewedAtUtc));

        public Task<OrganizationScopeSnapshot> GetSnapshotAsync(
            Guid requestedOrganizationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.SnapshotCallCount++;
            if (this.ClosedOnSnapshotCall == this.SnapshotCallCount)
            {
                return Task.FromResult(new OrganizationScopeSnapshot(
                    OrganizationScopeStatus.Closed,
                    this.Revision));
            }

            return Task.FromResult(requestedOrganizationId == organizationId
                ? new OrganizationScopeSnapshot(
                    OrganizationScopeStatus.Open,
                    this.Revision)
                : new OrganizationScopeSnapshot(
                    OrganizationScopeStatus.Missing,
                    0));
        }

        public Task<OrganizationScopeExportPage> ExportAsync(
            OrganizationScopeExportRequest request,
            CancellationToken cancellationToken)
        {
            if (request.OrganizationId != organizationId ||
                request.ExpectedRevision != this.Revision)
            {
                return Task.FromResult(new OrganizationScopeExportPage(
                    OrganizationScopeExportStatus.Stale,
                    this.Revision,
                    request.Store,
                    [],
                    null,
                    false));
            }

            if (request.Store == OrganizationScopeExportStore.Organization)
            {
                int activeOwners = this.memberships.Count(membership =>
                    membership.Role == OrganizationMembershipRole.Owner &&
                    membership.Status ==
                        OrganizationMembershipStatus.Active);
                return Task.FromResult(new OrganizationScopeExportPage(
                    OrganizationScopeExportStatus.Completed,
                    this.Revision,
                    request.Store,
                    [new OrganizationScopeOrganizationExportRecord(
                        organizationId,
                        "Test organization",
                        "test-organization",
                        OrganizationStatus.Active,
                        activeOwners,
                        1,
                        "system:test",
                        ReviewedAtUtc.AddDays(-1),
                        "system:test",
                        ReviewedAtUtc)],
                    "id:" + organizationId.ToString("D"),
                    false));
            }

            this.MembershipExportCallCount++;

            Guid? afterId = request.AfterCursor is null
                ? null
                : Guid.ParseExact(request.AfterCursor[3..], "D");
            OrganizationScopeMembershipExportRecord[] loaded =
                this.memberships
                    .Where(membership => !afterId.HasValue ||
                        membership.MembershipId.CompareTo(afterId.Value) > 0)
                    .OrderBy(membership => membership.MembershipId)
                    .Take(request.PageSize + 1)
                    .ToArray();
            OrganizationScopeMembershipExportRecord[] selected = loaded
                .Take(request.PageSize)
                .ToArray();
            return Task.FromResult(new OrganizationScopeExportPage(
                OrganizationScopeExportStatus.Completed,
                this.Revision,
                request.Store,
                selected,
                selected.Length == 0
                    ? request.AfterCursor
                    : "id:" + selected[^1].MembershipId.ToString("D"),
                loaded.Length > request.PageSize));
        }

        public Task<OrganizationScopeDestroyResult> DestroyBatchAsync(
            OrganizationScopeDestroyRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingWorkspaceCrossGraphMutationLock(
        List<string> calls) : IWorkspaceCrossGraphMutationLock
    {
        public Task AcquireAsync(CancellationToken cancellationToken)
        {
            calls.Add("tenant-exclusive");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingCutoverExecutionBoundary(List<string> calls)
        : IWorkspaceIdentityAnchorCutoverExecutionBoundary
    {
        public async Task<Result<T>> ExecuteAsync<T>(
            Func<CancellationToken, Task<Result<T>>> operation,
            CancellationToken cancellationToken)
        {
            calls.Add("boundary.begin");
            Result<T> result = await operation(cancellationToken);
            calls.Add("boundary.released");
            return result;
        }
    }

    private sealed class UncertainReleaseCutoverExecutionBoundary
        : IWorkspaceIdentityAnchorCutoverExecutionBoundary
    {
        public async Task<Result<T>> ExecuteAsync<T>(
            Func<CancellationToken, Task<Result<T>>> operation,
            CancellationToken cancellationToken)
        {
            _ = await operation(cancellationToken);
            throw new InvalidOperationException(
                "The preflight transaction release is uncertain.");
        }
    }

    private sealed class DelegateFreshStatusReader(
        Func<WorkspaceStaffIdentityAnchorPreparedReconcile, CancellationToken,
            Task<Result<WorkspaceStaffIdentityAnchorFreshVerification>>> read)
        : IWorkspaceStaffIdentityAnchorFreshStatusReader
    {
        public int ReadCount { get; private set; }
        public CancellationToken LastCancellationToken { get; private set; }

        public Task<Result<WorkspaceStaffIdentityAnchorFreshVerification>>
            ReadAsync(
            WorkspaceStaffIdentityAnchorPreparedReconcile prepared,
            CancellationToken cancellationToken)
        {
            this.ReadCount++;
            this.LastCancellationToken = cancellationToken;
            return read(prepared, cancellationToken);
        }
    }

    private sealed class TestScopeContext(string? scopeId = null) : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId { get; } = scopeId ?? TenantId;
    }
}
