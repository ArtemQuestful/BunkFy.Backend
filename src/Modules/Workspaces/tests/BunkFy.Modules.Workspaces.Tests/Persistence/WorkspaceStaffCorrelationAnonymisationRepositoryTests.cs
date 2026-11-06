namespace BunkFy.Modules.Workspaces.Tests.Persistence;

using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Domain.DataRights;
using BunkFy.Modules.Workspaces.Persistence;
using BunkFy.Modules.Workspaces.Persistence.Repositories;
using Gma.Framework.Results;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Xunit;

[Trait("Category", "Unit")]
public sealed class
    WorkspaceStaffCorrelationAnonymisationRepositoryTests
{
    private const string TenantId =
        "10000000-0000-0000-0000-000000000001";
    private const string SubjectId = "account-subject-a";
    private static readonly Guid StaffMemberId =
        Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now =
        new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Completed_departure_mapping_produces_stable_bounded_snapshot()
    {
        await using WorkspacesDbContext context = CreateContext();
        SeededState state = SeedEligibleState(context);
        await context.SaveChangesAsync();
        WorkspaceStaffCorrelationAnonymisationRepository repository =
            new(context);

        WorkspaceStaffCorrelationAnonymisationSnapshot resolved =
            await repository.ResolveAsync(
                TenantId,
                StaffMemberId,
                selectedStaffVersion: 7,
                SubjectId,
                CancellationToken.None);
        WorkspaceStaffCorrelationAnonymisationSnapshot read =
            await repository.ReadAsync(
                TenantId,
                state.Anchor.Id,
                state.Anchor.Version,
                CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffCorrelationAnonymisationSnapshotStatus
                .Eligible,
            resolved.Status);
        Assert.Equal(state.Anchor.Id, resolved.AnchorProcessId);
        Assert.Equal(state.Anchor.Version, resolved.AnchorProcessVersion);
        Assert.Equal(StaffMemberId, resolved.StaffMemberId);
        Assert.Equal(7, resolved.SelectedStaffVersion);
        Assert.Equal(SubjectId, resolved.SubjectId);
        Assert.Equal(1, resolved.OnboardingRecordCount);
        Assert.Equal(1, resolved.AccessProcessRecordCount);
        Assert.Equal(1, resolved.AccessPlanRecordCount);
        Assert.NotNull(resolved.StateSha256);
        Assert.Equal(64, resolved.StateSha256.Length);
        Assert.Equal(resolved, read);
    }

    [Fact]
    public async Task Person_linked_active_work_blocks_snapshot()
    {
        await using WorkspacesDbContext context = CreateContext();
        _ = SeedEligibleState(context);
        WorkspaceStaffAccessProcess active =
            WorkspaceStaffAccessProcess.Create(
                Guid.NewGuid(),
                TenantId,
                Guid.NewGuid(),
                "other-subject",
                WorkspaceStaffAccessTargetState.Suspended,
                targetStaffVersion: 2,
                new DateOnly(2026, 7, 30),
                SubjectId,
                [],
                Now).Value;
        context.StaffAccessProcesses.Add(active);
        await context.SaveChangesAsync();
        WorkspaceStaffCorrelationAnonymisationRepository repository =
            new(context);

        WorkspaceStaffCorrelationAnonymisationSnapshot result =
            await repository.ResolveAsync(
                TenantId,
                StaffMemberId,
                selectedStaffVersion: 7,
                SubjectId,
                CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffCorrelationAnonymisationSnapshotStatus
                .ActiveAccessProcess,
            result.Status);
    }

    [Fact]
    public async Task Existing_retention_proof_conflicts_with_data_rights_path()
    {
        await using WorkspacesDbContext context = CreateContext();
        _ = SeedEligibleState(context);
        context.StaffRetentionCorrelationReceipts.Add(
            WorkspaceStaffRetentionCorrelationReceipt.Create(
                Guid.NewGuid(),
                TenantId,
                Guid.NewGuid(),
                StaffMemberId,
                selectedStaffVersion: 7,
                onboardingRecordsScrubbed: 1,
                accessProcessRecordsScrubbed: 1,
                accessPlanRecordsScrubbed: 1,
                Now.AddDays(1)).Value);
        await context.SaveChangesAsync();
        WorkspaceStaffCorrelationAnonymisationRepository repository =
            new(context);

        WorkspaceStaffCorrelationAnonymisationSnapshot result =
            await repository.ResolveAsync(
                TenantId,
                StaffMemberId,
                selectedStaffVersion: 7,
                SubjectId,
                CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffCorrelationAnonymisationSnapshotStatus
                .Conflict,
            result.Status);
    }

    [Fact]
    public async Task Missing_subject_and_mapping_is_explicit_no_correlation()
    {
        await using WorkspacesDbContext context = CreateContext();
        WorkspaceStaffCorrelationAnonymisationRepository repository =
            new(context);

        WorkspaceStaffCorrelationAnonymisationSnapshot result =
            await repository.ResolveAsync(
                TenantId,
                StaffMemberId,
                selectedStaffVersion: 7,
                subjectId: null,
                CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffCorrelationAnonymisationSnapshotStatus
                .NoCorrelation,
            result.Status);
    }

    [Fact]
    public async Task Staff_linked_row_with_another_subject_fails_closed()
    {
        await using WorkspacesDbContext context = CreateContext();
        _ = SeedEligibleState(context);
        WorkspaceStaffOnboarding conflict =
            CreateCompletedOnboarding(
                "another-subject",
                Guid.NewGuid());
        context.StaffOnboardingApplications.Add(conflict);
        await context.SaveChangesAsync();
        WorkspaceStaffCorrelationAnonymisationRepository repository =
            new(context);

        WorkspaceStaffCorrelationAnonymisationSnapshot result =
            await repository.ResolveAsync(
                TenantId,
                StaffMemberId,
                selectedStaffVersion: 7,
                SubjectId,
                CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffCorrelationAnonymisationSnapshotStatus
                .Conflict,
            result.Status);
    }

    [Fact]
    public async Task Apply_scrubs_bounded_set_and_persists_exact_proof()
    {
        await using WorkspacesDbContext context = CreateContext();
        SeededState state = SeedEligibleState(context);
        await context.SaveChangesAsync();
        WorkspaceStaffCorrelationAnonymisationRepository repository =
            new(context);
        WorkspaceStaffCorrelationAnonymisationSnapshot selected =
            await repository.ReadAsync(
                TenantId,
                state.Anchor.Id,
                state.Anchor.Version,
                CancellationToken.None);
        Guid receiptId = Guid.Parse(
            "30000000-0000-0000-0000-000000000001");
        WorkspaceStaffCorrelationAnonymisationApplyRequest request =
            new(
                receiptId,
                TenantId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                ApprovalRevision: 4,
                OperationRevision: 5,
                selected,
                new string('a', 64),
                new string('b', 64),
                "user:privacy-executor",
                Now.AddMinutes(10));

        Result<WorkspaceStaffCorrelationAnonymisationReceipt>
            applied = await repository.ApplyAsync(
                request,
                CancellationToken.None);
        await context.SaveChangesAsync();

        Assert.True(applied.IsSuccess, applied.Error.Code);
        string pseudonym = $"anonymised:{receiptId:N}";
        Assert.Equal(pseudonym, state.Onboarding.SubjectId);
        Assert.Equal(pseudonym, state.Anchor.SubjectId);
        Assert.Equal(pseudonym, state.Anchor.RequestedBy);
        Assert.Equal(pseudonym, state.Plan.CreatedBySubjectId);
        Assert.Equal(1, applied.Value.OnboardingRecordsScrubbed);
        Assert.Equal(1, applied.Value.AccessProcessRecordsScrubbed);
        Assert.Equal(1, applied.Value.AccessPlanRecordsScrubbed);
        Assert.Equal(
            selected.AnchorProcessVersion + 1,
            applied.Value.ResultingAnchorVersion);
        Assert.True(applied.Value.HasValidCanonicalProof());
        WorkspaceStaffCorrelationAnonymisationTombstone
            tombstone = Assert.Single(
                context.StaffCorrelationAnonymisationTombstones);
        Assert.True(tombstone.Matches(applied.Value));
        Assert.DoesNotContain(
            context.StaffOnboardingApplications,
            record => record.SubjectId == SubjectId);
        Assert.DoesNotContain(
            context.StaffAccessProcesses,
            record =>
                record.SubjectId == SubjectId ||
                record.RequestedBy == SubjectId);
        Assert.DoesNotContain(
            context.StaffAccessPlans,
            record => record.CreatedBySubjectId == SubjectId);

        Result<WorkspaceStaffCorrelationAnonymisationReceipt>
            replay = await repository.ApplyAsync(
                request,
                CancellationToken.None);
        Assert.True(replay.IsSuccess);
        Assert.Same(applied.Value, replay.Value);

        Result<WorkspaceStaffCorrelationAnonymisationReceipt>
            conflictingReplay = await repository.ApplyAsync(
                request with { CaseId = Guid.NewGuid() },
                CancellationToken.None);
        Assert.Equal(
            WorkspaceStaffCorrelationAnonymisationApplicationErrors
                .IdempotencyConflict,
            conflictingReplay.Error);
    }

    [Fact]
    public async Task Restore_replays_scrub_from_original_state_once()
    {
        await using WorkspacesDbContext context = CreateContext();
        SeededState state = SeedEligibleState(context);
        await context.SaveChangesAsync();
        WorkspaceStaffCorrelationAnonymisationRepository repository =
            new(context);
        Guid ownerReceiptId = Guid.Parse(
            "30000000-0000-0000-0000-000000000001");
        WorkspaceStaffCorrelationAnonymisationRestoreRequest request =
            new(
                TenantId,
                Guid.Parse(
                    "40000000-0000-0000-0000-000000000001"),
                3,
                new string('d', 64),
                state.Anchor.Id,
                OwnerReceiptContractVersion: 1,
                ownerReceiptId,
                new string('a', 64),
                state.Anchor.Version + 1,
                Now.AddMinutes(10),
                Now.AddMinutes(20));

        Result<
            WorkspaceStaffCorrelationAnonymisationRestoreReceipt>
            restored = await repository.RestoreAsync(
                request,
                CancellationToken.None);
        await context.SaveChangesAsync();

        Assert.True(restored.IsSuccess, restored.Error.Code);
        Assert.Equal(request.TenantSequence, restored.Value.TenantSequence);
        Assert.Equal(
            request.LedgerEntrySha256,
            restored.Value.LedgerEntrySha256);
        Assert.Equal(
            request.OriginallyCompletedAtUtc,
            restored.Value.OriginallyCompletedAtUtc);
        string pseudonym = $"anonymised:{ownerReceiptId:N}";
        Assert.Equal(pseudonym, state.Onboarding.SubjectId);
        Assert.Equal(pseudonym, state.Anchor.SubjectId);
        Assert.Equal(pseudonym, state.Anchor.RequestedBy);
        Assert.Equal(pseudonym, state.Plan.CreatedBySubjectId);
        Assert.True(restored.Value.HasValidCanonicalProof());
        Assert.Equal(
            state.Anchor.Version,
            restored.Value.ResultingAnchorVersion);
        WorkspaceStaffCorrelationAnonymisationTombstone
            tombstone = Assert.Single(
                context.StaffCorrelationAnonymisationTombstones);
        Assert.True(tombstone.MatchesRestore(
            request.LedgerEntryId,
            request.OwnerReceiptContractVersion,
            request.OwnerReceiptId,
            request.OwnerReceiptSha256,
            request.ResultingAnchorVersion,
            request.OriginallyCompletedAtUtc));

        Result<
            WorkspaceStaffCorrelationAnonymisationRestoreReceipt>
            replay = await repository.RestoreAsync(
                request,
                CancellationToken.None);
        Assert.True(replay.IsSuccess);
        Assert.Same(restored.Value, replay.Value);
        Assert.Single(
            context.StaffCorrelationAnonymisationRestoreReceipts);

        Result<
            WorkspaceStaffCorrelationAnonymisationRestoreReceipt>
            conflictingLedger = await repository.RestoreAsync(
                request with
                {
                    TenantSequence = request.TenantSequence + 1
                },
                CancellationToken.None);
        Assert.Equal(
            WorkspaceStaffCorrelationAnonymisationApplicationErrors
                .RestoreProofConflict,
            conflictingLedger.Error);

        context.Entry(state.Plan)
            .Property(plan => plan.CreatedBySubjectId)
            .CurrentValue = "tampered-subject";
        await context.SaveChangesAsync();
        Result<
            WorkspaceStaffCorrelationAnonymisationRestoreReceipt>
            driftedReplay = await repository.RestoreAsync(
                request,
                CancellationToken.None);
        Assert.Equal(
            WorkspaceStaffCorrelationAnonymisationApplicationErrors
                .RestoreProofConflict,
            driftedReplay.Error);
    }

    private static SeededState SeedEligibleState(
        WorkspacesDbContext context)
    {
        Guid sourceId = Guid.NewGuid();
        WorkspaceStaffOnboarding onboarding =
            CreateCompletedOnboarding(
                SubjectId,
                sourceId);

        WorkspaceStaffAccessProcess anchor =
            WorkspaceStaffAccessProcess.Create(
                Guid.NewGuid(),
                TenantId,
                StaffMemberId,
                SubjectId,
                WorkspaceStaffAccessTargetState.Departed,
                targetStaffVersion: 7,
                new DateOnly(2026, 7, 30),
                SubjectId,
                [],
                Now.AddMinutes(4)).Value;
        Assert.True(anchor.MarkAwaitingStaffCommit(
            Now.AddMinutes(5)).IsSuccess);
        Assert.True(anchor.ObserveStaffCommit(
            Now.AddMinutes(6)).IsSuccess);

        WorkspaceStaffAccessPlan plan =
            WorkspaceStaffAccessPlan.Create(
                sourceId,
                TenantId,
                WorkspaceStaffOnboardingSource.Invitation,
                Guid.NewGuid(),
                "front-desk",
                [],
                SubjectId,
                Now).Value;
        Assert.True(plan.Activate(Now.AddMinutes(1)).IsSuccess);

        context.StaffOnboardingApplications.Add(onboarding);
        context.StaffAccessProcesses.Add(anchor);
        context.StaffAccessPlans.Add(plan);
        return new(onboarding, anchor, plan);
    }

    private static WorkspaceStaffOnboarding
        CreateCompletedOnboarding(
            string subjectId,
            Guid sourceId)
    {
        WorkspaceStaffOnboarding onboarding =
            WorkspaceStaffOnboarding.Create(
                Guid.NewGuid(),
                TenantId,
                WorkspaceStaffOnboardingSource.Invitation,
                sourceId,
                subjectId,
                "staff@example.test",
                "Staff member",
                legalName: null,
                workEmail: null,
                workPhone: null,
                employeeNumber: null,
                jobTitle: null,
                department: null,
                Now).Value;
        Assert.True(onboarding.ObserveInvitationAccepted(
            Now.AddMinutes(1)).IsSuccess);
        Assert.True(onboarding.MarkStaffReady(
            StaffMemberId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Now.AddMinutes(2)).IsSuccess);
        Assert.True(onboarding.Complete(
            Now.AddMinutes(3)).IsSuccess);
        return onboarding;
    }

    private static WorkspacesDbContext CreateContext()
    {
        DbContextOptions<WorkspacesDbContext> options =
            new DbContextOptionsBuilder<WorkspacesDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;
        return new(options, new TestScopeContext());
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => TenantId;
    }

    private sealed record SeededState(
        WorkspaceStaffOnboarding Onboarding,
        WorkspaceStaffAccessProcess Anchor,
        WorkspaceStaffAccessPlan Plan);
}
