namespace BunkFy.Modules.Workspaces.Tests;

using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Application.Handlers;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspaceStaffRetentionCorrelationHandlerTests
{
    private static readonly Guid ReceiptId =
        Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid ExecutionId =
        Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid StaffMemberId =
        Guid.Parse("30000000-0000-0000-0000-000000000001");
    private const string TenantId =
        "40000000-0000-0000-0000-000000000001";

    [Fact]
    public async Task Valid_request_is_normalized_before_persistence()
    {
        List<string> calls = [];
        FakeRepository repository = new(calls);
        RecordingWorkspaceCrossGraphMutationLock crossGraphLock =
            new(calls);
        RecordingAccessClosure accessClosure = new(
            calls,
            WorkspaceStaffAccessClosureResult.Complete("subject-a"));
        DateTimeOffset completedAt =
            new DateTimeOffset(
                2026,
                7,
                30,
                12,
                0,
                0,
                TimeSpan.FromHours(3))
                .AddTicks(7);
        ScrubWorkspaceStaffRetentionCorrelationCommandHandler handler =
            new(
                repository,
                new
                    RecordingWorkspaceStaffOnboardingIdentityAnchorSubjectMutationFence(
                        calls: calls),
                crossGraphLock,
                WorkspaceStaffAccessMutationTestSupport.Create(
                    calls: calls),
                accessClosure,
                new TestClock(completedAt),
                new TestIdGenerator(ReceiptId),
                new TestScopeContext(TenantId));

        Result<WorkspaceStaffRetentionCorrelationReceipt> result =
            await handler.HandleAsync(
                new ScrubWorkspaceStaffRetentionCorrelationCommand(
                    ExecutionId,
                    $" {TenantId} ",
                    StaffMemberId,
                    SelectedStaffVersion: 7),
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(1, crossGraphLock.AcquireCount);
        Assert.Equal(
            [
                "tenant-exclusive",
                "staff-coordinate",
                "access-closure",
                "identity-anchor-fence",
                "repository"
            ],
            calls);
        Assert.NotNull(repository.Request);
        Assert.Equal(TenantId, repository.Request.TenantId);
        Assert.Equal("subject-a", repository.Request.SubjectId);
        Assert.Equal(TimeSpan.Zero, repository.Request.CompletedAtUtc.Offset);
        Assert.Equal(0, repository.Request.CompletedAtUtc.Ticks % 10);
    }

    [Fact]
    public async Task Unresolved_identity_anchor_blocks_before_scrub()
    {
        List<string> calls = [];
        FakeRepository repository = new(calls);
        RecordingWorkspaceStaffOnboardingIdentityAnchorSubjectMutationFence
            fence = new(allowed: false, calls: calls);
        ScrubWorkspaceStaffRetentionCorrelationCommandHandler handler = new(
            repository,
            fence,
            new RecordingWorkspaceCrossGraphMutationLock(calls),
            WorkspaceStaffAccessMutationTestSupport.Create(calls: calls),
            new RecordingAccessClosure(
                calls,
                WorkspaceStaffAccessClosureResult.Complete("subject-a")),
            new TestClock(DateTimeOffset.UtcNow),
            new TestIdGenerator(ReceiptId),
            new TestScopeContext(TenantId));

        Result<WorkspaceStaffRetentionCorrelationReceipt> result =
            await handler.HandleAsync(
                new(
                    ExecutionId,
                    TenantId,
                    StaffMemberId,
                    SelectedStaffVersion: 7),
                CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffRetentionErrors.IdentityAnchorUnavailable,
            result.Error);
        Assert.Equal(1, fence.CallCount);
        Assert.Equal(TenantId, fence.TenantId);
        Assert.Equal("subject-a", fence.SubjectId);
        Assert.Equal(
            [
                "tenant-exclusive",
                "staff-coordinate",
                "access-closure",
                "identity-anchor-fence"
            ],
            calls);
        Assert.Null(repository.Request);
    }

    [Fact]
    public async Task Cross_scope_request_fails_before_persistence()
    {
        FakeRepository repository = new();
        RecordingWorkspaceCrossGraphMutationLock crossGraphLock =
            new();
        ScrubWorkspaceStaffRetentionCorrelationCommandHandler handler =
            new(
                repository,
                new
                    RecordingWorkspaceStaffOnboardingIdentityAnchorSubjectMutationFence(),
                crossGraphLock,
                WorkspaceStaffAccessMutationTestSupport.Create(),
                new RecordingAccessClosure(
                    result:
                        WorkspaceStaffAccessClosureResult.Complete(
                            "subject-a")),
                new TestClock(DateTimeOffset.UtcNow),
                new TestIdGenerator(ReceiptId),
                new TestScopeContext(
                    "50000000-0000-0000-0000-000000000001"));

        Result<WorkspaceStaffRetentionCorrelationReceipt> result =
            await handler.HandleAsync(
                new ScrubWorkspaceStaffRetentionCorrelationCommand(
                    ExecutionId,
                    TenantId,
                    StaffMemberId,
                    SelectedStaffVersion: 7),
                CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            WorkspaceStaffRetentionErrors.RequestInvalid,
            result.Error);
        Assert.Equal(0, crossGraphLock.AcquireCount);
        Assert.Null(repository.Request);
    }

    [Fact]
    public async Task Malformed_request_fails_before_persistence()
    {
        FakeRepository repository = new();
        RecordingWorkspaceCrossGraphMutationLock crossGraphLock =
            new();
        ScrubWorkspaceStaffRetentionCorrelationCommandHandler handler =
            new(
                repository,
                new
                    RecordingWorkspaceStaffOnboardingIdentityAnchorSubjectMutationFence(),
                crossGraphLock,
                WorkspaceStaffAccessMutationTestSupport.Create(),
                new RecordingAccessClosure(
                    result:
                        WorkspaceStaffAccessClosureResult.Complete(
                            "subject-a")),
                new TestClock(DateTimeOffset.UtcNow),
                new TestIdGenerator(ReceiptId),
                new TestScopeContext(TenantId));

        Result<WorkspaceStaffRetentionCorrelationReceipt> result =
            await handler.HandleAsync(
                new ScrubWorkspaceStaffRetentionCorrelationCommand(
                    ExecutionId: Guid.Empty,
                    TenantId,
                    StaffMemberId,
                    SelectedStaffVersion: 0),
                CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            WorkspaceStaffRetentionErrors.RequestInvalid,
            result.Error);
        Assert.Equal(0, crossGraphLock.AcquireCount);
        Assert.Null(repository.Request);
    }

    private sealed class RecordingAccessClosure(
        List<string>? calls = null,
        WorkspaceStaffAccessClosureResult? result = null)
        : IWorkspaceStaffRetentionAccessClosure
    {
        public Task<WorkspaceStaffAccessClosureResult> EnsureClosedAsync(
            string tenantId,
            Guid staffMemberId,
            long selectedStaffVersion,
            CancellationToken cancellationToken)
        {
            calls?.Add("access-closure");
            return Task.FromResult(
                result ??
                WorkspaceStaffAccessClosureResult.Complete(
                    subjectId: null));
        }
    }

    private sealed class FakeRepository(List<string>? calls = null)
        : IWorkspaceStaffRetentionCorrelationRepository
    {
        public WorkspaceStaffRetentionCorrelationScrubRequest? Request
        {
            get;
            private set;
        }

        public Task<WorkspaceStaffRetentionCorrelationReceipt?> GetAsync(
            Guid staffMemberId,
            long selectedStaffVersion,
            CancellationToken cancellationToken) =>
            Task.FromResult<
                WorkspaceStaffRetentionCorrelationReceipt?>(null);

        public Task<Result<WorkspaceStaffRetentionCorrelationReceipt>>
            ScrubAsync(
                WorkspaceStaffRetentionCorrelationScrubRequest request,
                CancellationToken cancellationToken)
        {
            calls?.Add("repository");
            this.Request = request;
            return Task.FromResult(
                WorkspaceStaffRetentionCorrelationReceipt.Create(
                    request.ReceiptId,
                    request.TenantId,
                    request.ExecutionId,
                    request.StaffMemberId,
                    request.SelectedStaffVersion,
                    onboardingRecordsScrubbed: 0,
                    accessProcessRecordsScrubbed: 0,
                    accessPlanRecordsScrubbed: 0,
                    request.CompletedAtUtc));
        }
    }

    private sealed class TestScopeContext(string scopeId)
        : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => scopeId;
    }

    private sealed class TestClock(DateTimeOffset utcNow) : ISystemClock
    {
        public DateTimeOffset UtcNow => utcNow;
    }

    private sealed class TestIdGenerator(Guid value) : IIdGenerator
    {
        public Guid NewId() => value;
    }
}
