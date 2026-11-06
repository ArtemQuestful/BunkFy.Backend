namespace BunkFy.Modules.Workspaces.Tests.Application;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Contracts.Authorization;
using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Application.Handlers;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Persistence;
using BunkFy.Modules.Workspaces.Persistence.Repositories;
using BunkFy.Modules.Workspaces.Tests;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Xunit;

[Trait("Category", "Unit")]
public sealed class
    ApplyWorkspaceStaffCorrelationAnonymisationCommandHandlerTests
{
    private const string TenantId =
        "10000000-0000-0000-0000-000000000001";
    private const string SubjectId = "private-account-subject";
    private static readonly Guid StaffMemberId =
        Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid ReceiptId =
        Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now =
        new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Exact_approval_commits_and_replays_workspace_proof()
    {
        await using WorkspacesDbContext context = CreateContext();
        WorkspaceStaffAccessProcess anchor = SeedEligibleState(
            context);
        await context.SaveChangesAsync();
        WorkspaceStaffCorrelationAnonymisationRepository repository =
            new(context);
        WorkspaceStaffCorrelationAnonymisationSnapshot snapshot =
            await repository.ReadAsync(
                TenantId,
                anchor.Id,
                anchor.Version,
                CancellationToken.None);
        DataRightsApprovalEvidence evidence =
            CreateApprovalEvidence(snapshot);
        RecordingApprovalGate approvalGate = new(evidence);
        List<string> calls = [];
        RecordingWorkspaceCrossGraphMutationLock crossGraphLock =
            new(calls);
        RecordingOperationLock operationLock = new(calls);
        ApplyWorkspaceStaffCorrelationAnonymisationCommandHandler
            handler = new(
                repository,
                new
                    RecordingWorkspaceStaffOnboardingIdentityAnchorSubjectMutationFence(
                        calls: calls),
                crossGraphLock,
                WorkspaceStaffAccessMutationTestSupport.Create(
                    calls: calls),
                operationLock,
                approvalGate,
                new TestScopeContext(),
                new TestClock(),
                new FixedIdGenerator());
        ApplyWorkspaceStaffCorrelationAnonymisationCommand command =
            new(
                Guid.NewGuid(),
                Guid.NewGuid(),
                ApprovalRevision: 4,
                OperationRevision: 5,
                anchor.Id,
                anchor.Version,
                evidence,
                "user:privacy-executor");

        Result<WorkspaceStaffCorrelationAnonymisationReceiptDto>
            applied = await handler.HandleAsync(
                command,
                CancellationToken.None);
        await context.SaveChangesAsync();
        Result<WorkspaceStaffCorrelationAnonymisationReceiptDto>
            replay = await handler.HandleAsync(
                command,
                CancellationToken.None);

        Assert.True(applied.IsSuccess, applied.Error.Code);
        Assert.True(replay.IsSuccess, replay.Error.Code);
        Assert.Equal(applied.Value, replay.Value);
        Assert.Equal(ReceiptId, applied.Value.ReceiptId);
        Assert.Equal(
            anchor.Version,
            applied.Value.ResultingAnchorVersion);
        Assert.Equal(1, applied.Value.OnboardingRecordsScrubbed);
        Assert.Equal(1, applied.Value.AccessProcessRecordsScrubbed);
        Assert.Equal(1, applied.Value.AccessPlanRecordsScrubbed);
        Assert.Equal(1, approvalGate.CallCount);
        Assert.Equal(1, crossGraphLock.AcquireCount);
        Assert.Equal(1, operationLock.AcquireCount);
        Assert.Equal(
            [
                "tenant-exclusive",
                "staff-coordinate",
                "correlation-row",
                "identity-anchor-fence"
            ],
            calls);
        Assert.All(
            context.StaffAccessProcesses,
            process =>
            {
                Assert.NotEqual(SubjectId, process.SubjectId);
                Assert.NotEqual(SubjectId, process.RequestedBy);
            });
    }

    [Fact]
    public async Task Unresolved_identity_anchor_blocks_before_scrub()
    {
        await using WorkspacesDbContext context = CreateContext();
        WorkspaceStaffAccessProcess anchor = SeedEligibleState(context);
        await context.SaveChangesAsync();
        WorkspaceStaffCorrelationAnonymisationRepository repository =
            new(context);
        WorkspaceStaffCorrelationAnonymisationSnapshot snapshot =
            await repository.ReadAsync(
                TenantId,
                anchor.Id,
                anchor.Version,
                CancellationToken.None);
        DataRightsApprovalEvidence evidence =
            CreateApprovalEvidence(snapshot);
        List<string> calls = [];
        RecordingWorkspaceStaffOnboardingIdentityAnchorSubjectMutationFence
            fence = new(allowed: false, calls: calls);
        ApplyWorkspaceStaffCorrelationAnonymisationCommandHandler handler =
            new(
                repository,
                fence,
                new RecordingWorkspaceCrossGraphMutationLock(calls),
                WorkspaceStaffAccessMutationTestSupport.Create(calls: calls),
                new RecordingOperationLock(calls),
                new RecordingApprovalGate(evidence),
                new TestScopeContext(),
                new TestClock(),
                new FixedIdGenerator());

        Result<WorkspaceStaffCorrelationAnonymisationReceiptDto> result =
            await handler.HandleAsync(
                new(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    ApprovalRevision: 4,
                    OperationRevision: 5,
                    anchor.Id,
                    anchor.Version,
                    evidence,
                    "user:privacy-executor"),
                CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffCorrelationAnonymisationApplicationErrors
                .IdentityAnchorUnavailable,
            result.Error);
        Assert.Equal(1, fence.CallCount);
        Assert.Equal(TenantId, fence.TenantId);
        Assert.Equal(SubjectId, fence.SubjectId);
        Assert.Equal(
            [
                "tenant-exclusive",
                "staff-coordinate",
                "correlation-row",
                "identity-anchor-fence"
            ],
            calls);
        Assert.Equal(SubjectId, anchor.SubjectId);
        Assert.Empty(context.StaffCorrelationAnonymisationReceipts);
    }

    [Fact]
    public async Task Changed_workspace_binding_is_blocked_before_scrub()
    {
        await using WorkspacesDbContext context = CreateContext();
        WorkspaceStaffAccessProcess anchor = SeedEligibleState(
            context);
        await context.SaveChangesAsync();
        WorkspaceStaffCorrelationAnonymisationRepository repository =
            new(context);
        WorkspaceStaffCorrelationAnonymisationSnapshot snapshot =
            await repository.ReadAsync(
                TenantId,
                anchor.Id,
                anchor.Version,
                CancellationToken.None);
        DataRightsApprovalEvidence evidence =
            CreateApprovalEvidence(snapshot) with
            {
                StateBindings =
                [
                    .. CreateApprovalEvidence(snapshot)
                        .StateBindings!
                        .Where(binding =>
                            binding.Key !=
                                WorkspacesDataRightsCoordinates
                                    .StaffCorrelationStateBindingKey),
                    new(
                        WorkspacesDataRightsCoordinates
                            .StaffCorrelationStateBindingKey,
                        snapshot.AnchorProcessVersion!.Value,
                        new string('f', 64))
                ]
            };
        ApplyWorkspaceStaffCorrelationAnonymisationCommandHandler
            handler = new(
                repository,
                new
                    RecordingWorkspaceStaffOnboardingIdentityAnchorSubjectMutationFence(),
                new RecordingWorkspaceCrossGraphMutationLock(),
                WorkspaceStaffAccessMutationTestSupport.Create(),
                new RecordingOperationLock(),
                new RecordingApprovalGate(evidence),
                new TestScopeContext(),
                new TestClock(),
                new FixedIdGenerator());

        Result<WorkspaceStaffCorrelationAnonymisationReceiptDto>
            result = await handler.HandleAsync(
                new(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    ApprovalRevision: 4,
                    OperationRevision: 5,
                    anchor.Id,
                    anchor.Version,
                    evidence,
                    "user:privacy-executor"),
                CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffCorrelationAnonymisationApplicationErrors
                .StateChanged,
            result.Error);
        Assert.Equal(SubjectId, anchor.SubjectId);
        Assert.Empty(
            context.StaffCorrelationAnonymisationReceipts);
    }

    [Fact]
    public async Task Malformed_binding_payload_fails_closed_before_gate()
    {
        await using WorkspacesDbContext context = CreateContext();
        WorkspaceStaffCorrelationAnonymisationSnapshot snapshot = new(
            WorkspaceStaffCorrelationAnonymisationSnapshotStatus
                .Eligible,
            Guid.NewGuid(),
            AnchorProcessVersion: 4,
            StaffMemberId,
            SelectedStaffVersion: 7,
            SubjectId,
            new string('a', 64),
            OnboardingRecordCount: 1,
            AccessProcessRecordCount: 1,
            AccessPlanRecordCount: 1);
        DataRightsApprovalEvidence valid =
            CreateApprovalEvidence(snapshot);
        DataRightsApprovalEvidence malformed = valid with
        {
            StateBindings =
            [
                null!,
                .. valid.StateBindings!
            ]
        };
        RecordingApprovalGate approvalGate = new(valid);
        RecordingWorkspaceCrossGraphMutationLock crossGraphLock =
            new();
        RecordingOperationLock operationLock = new();
        ApplyWorkspaceStaffCorrelationAnonymisationCommandHandler
            handler = new(
                new WorkspaceStaffCorrelationAnonymisationRepository(
                    context),
                new
                    RecordingWorkspaceStaffOnboardingIdentityAnchorSubjectMutationFence(),
                crossGraphLock,
                WorkspaceStaffAccessMutationTestSupport.Create(),
                operationLock,
                approvalGate,
                new TestScopeContext(),
                new TestClock(),
                new FixedIdGenerator());

        Result<WorkspaceStaffCorrelationAnonymisationReceiptDto>
            result = await handler.HandleAsync(
                new(
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    ApprovalRevision: 4,
                    OperationRevision: 5,
                    snapshot.AnchorProcessId!.Value,
                    snapshot.AnchorProcessVersion!.Value,
                    malformed,
                    "user:privacy-executor"),
                CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffCorrelationAnonymisationApplicationErrors
                .RequestInvalid,
            result.Error);
        Assert.Equal(0, approvalGate.CallCount);
        Assert.Equal(0, crossGraphLock.AcquireCount);
        Assert.Equal(0, operationLock.AcquireCount);
    }

    private static WorkspaceStaffAccessProcess SeedEligibleState(
        WorkspacesDbContext context)
    {
        Guid sourceId = Guid.NewGuid();
        WorkspaceStaffOnboarding onboarding =
            WorkspaceStaffOnboarding.Create(
                Guid.NewGuid(),
                TenantId,
                WorkspaceStaffOnboardingSource.Invitation,
                sourceId,
                SubjectId,
                "staff@example.test",
                "Staff member",
                legalName: null,
                workEmail: null,
                workPhone: null,
                employeeNumber: null,
                jobTitle: null,
                department: null,
                Now.AddDays(-30)).Value;
        Assert.True(onboarding.ObserveInvitationAccepted(
            Now.AddDays(-30).AddMinutes(1)).IsSuccess);
        Assert.True(onboarding.MarkStaffReady(
            StaffMemberId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Now.AddDays(-30).AddMinutes(2)).IsSuccess);
        Assert.True(onboarding.Complete(
            Now.AddDays(-30).AddMinutes(3)).IsSuccess);

        WorkspaceStaffAccessProcess anchor =
            WorkspaceStaffAccessProcess.Create(
                Guid.NewGuid(),
                TenantId,
                StaffMemberId,
                SubjectId,
                WorkspaceStaffAccessTargetState.Departed,
                targetStaffVersion: 7,
                new DateOnly(2026, 7, 1),
                SubjectId,
                [],
                Now.AddDays(-1)).Value;
        Assert.True(anchor.MarkAwaitingStaffCommit(
            Now.AddDays(-1).AddMinutes(1)).IsSuccess);
        Assert.True(anchor.ObserveStaffCommit(
            Now.AddDays(-1).AddMinutes(2)).IsSuccess);

        WorkspaceStaffAccessPlan plan =
            WorkspaceStaffAccessPlan.Create(
                sourceId,
                TenantId,
                WorkspaceStaffOnboardingSource.Invitation,
                Guid.NewGuid(),
                "front-desk",
                [],
                SubjectId,
                Now.AddDays(-30)).Value;
        Assert.True(plan.Activate(
            Now.AddDays(-30).AddMinutes(1)).IsSuccess);

        context.AddRange(onboarding, anchor, plan);
        return anchor;
    }

    private static DataRightsApprovalEvidence CreateApprovalEvidence(
        WorkspaceStaffCorrelationAnonymisationSnapshot snapshot) =>
        new(
            SchemaVersion: 2,
            PropertyId: null,
            PropertyVersion: 0,
            OperatingCountryCode: "GB",
            PolicyId: "staff-test",
            PolicyVersion: 1,
            RetentionPolicyId: "staff-employment",
            RetentionPolicyVersion: 1,
            ContentSha256: new string('a', 64),
            PurposeCode: "staff-data-rights-anonymisation",
            Surface: "erasure",
            SourceProvenance: "authorized-workspace-operator",
            EvaluatedAtUtc: Now.AddMinutes(-1),
            RequiresDistinctExecutor: true,
            CaseType: DataRightsCaseType.StaffRights,
            ScopeKind: DataRightsExecutionScopeKind.Tenant,
            RetentionDataClass: "staff-employment",
            RetentionTrigger: "employment-ended",
            RetentionTriggeredAtUtc: Now.AddDays(-2_557),
            RetentionDeadlineUtc: Now.AddDays(-1),
            StateBindings:
            [
                new("staff.governance", 1, new string('b', 64)),
                new("staff.holds", 1, new string('c', 64)),
                new("staff.record", 7, new string('d', 64)),
                new("staff.restriction", 1, new string('e', 64)),
                new(
                    WorkspacesDataRightsCoordinates
                        .StaffCorrelationStateBindingKey,
                    snapshot.AnchorProcessVersion!.Value,
                    snapshot.StateSha256!)
            ],
            StateBindingsSha256: new string('f', 64));

    private static WorkspacesDbContext CreateContext()
    {
        DbContextOptions<WorkspacesDbContext> options =
            new DbContextOptionsBuilder<WorkspacesDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;
        return new(options, new TestScopeContext());
    }

    private sealed class RecordingApprovalGate(
        DataRightsApprovalEvidence evidence)
        : IDataRightsOperationApprovalGate
    {
        public int CallCount { get; private set; }

        public Task<DataRightsOperationApprovalResult> EvaluateAsync(
            DataRightsOperationApprovalRequest request,
            CancellationToken cancellationToken)
        {
            this.CallCount++;
            Assert.Equal(
                WorkspacesDataRightsCoordinates.Owner,
                request.OwnerKey);
            Assert.Equal(
                WorkspacesDataRightsCoordinates
                    .StaffAccessProcessRecordType,
                request.RecordType);
            Assert.Equal(
                DataRightsCaseType.StaffRights,
                request.CaseType);
            return Task.FromResult(
                DataRightsOperationApprovalResult
                    .ApprovedWithEvidence(evidence));
        }
    }

    private sealed class RecordingOperationLock(
        List<string>? calls = null)
        : IWorkspaceStaffCorrelationOperationLock
    {
        public int AcquireCount { get; private set; }

        public Task<bool> TryAcquireAsync(
            Guid anchorProcessId,
            CancellationToken cancellationToken)
        {
            this.AcquireCount++;
            calls?.Add("correlation-row");
            return Task.FromResult(true);
        }
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => TenantId;
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class FixedIdGenerator : IIdGenerator
    {
        public Guid NewId() => ReceiptId;
    }
}
