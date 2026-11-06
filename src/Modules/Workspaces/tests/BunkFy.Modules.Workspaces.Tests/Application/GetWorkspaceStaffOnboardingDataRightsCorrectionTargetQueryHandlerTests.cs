namespace BunkFy.Modules.Workspaces.Tests;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Application.Authorization;
using BunkFy.Modules.Workspaces.Application.Handlers;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Application.Queries;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Gma.Framework.Scoping;
using Xunit;

[Trait("Category", "Unit")]
public sealed class
    GetWorkspaceStaffOnboardingDataRightsCorrectionTargetQueryHandlerTests
{
    private const string TenantId =
        "10000000-0000-0000-0000-000000000001";
    private static readonly Guid ApplicationId =
        Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now =
        new(2026, 7, 30, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Approved_exact_absent_target_is_returned_under_lock()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        RecordingExecutionGate gate = new();
        List<string> calls = [];
        GetWorkspaceStaffOnboardingDataRightsCorrectionTargetQueryHandler
            handler = CreateHandler(application, gate, calls: calls);

        Result<WorkspaceStaffOnboardingDataRightsCorrectionTargetDto> result =
            await handler.HandleAsync(
                Query(application.Version),
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(ApplicationId, result.Value.ApplicationId);
        Assert.Equal(application.Version, result.Value.Version);
        Assert.Equal("Ada Operator", result.Value.DisplayName);
        Assert.Equal("ada@example.test", result.Value.WorkEmail);
        Assert.Equal(["tenant", "source", "application", "staff"], calls);
        Assert.DoesNotContain(
            result.Value.GetType().GetProperties(),
            property => property.Name is "SubjectId" or
                "VerifiedAccountEmail");
        Assert.Equal(
            WorkspacesDataRightsCoordinates
                .StaffOnboardingCorrectionFieldPolicyKey,
            gate.Request!.FieldPolicyKey);
    }

    [Fact]
    public async Task Reviewed_target_is_not_disclosed()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        Assert.True(application.ObserveClaimRequested(
            Guid.NewGuid(),
            claimVersion: 1,
            Now.AddMinutes(1)).IsSuccess);
        GetWorkspaceStaffOnboardingDataRightsCorrectionTargetQueryHandler
            handler = CreateHandler(application, new RecordingExecutionGate());

        Result<WorkspaceStaffOnboardingDataRightsCorrectionTargetDto> result =
            await handler.HandleAsync(
                Query(application.Version),
                CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffOnboardingApplicationErrors
                .CorrectionTargetUnavailable,
            result.Error);
    }

    [Fact]
    public async Task Stale_target_is_not_disclosed()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        GetWorkspaceStaffOnboardingDataRightsCorrectionTargetQueryHandler
            handler = CreateHandler(application, new RecordingExecutionGate());

        Result<WorkspaceStaffOnboardingDataRightsCorrectionTargetDto> result =
            await handler.HandleAsync(
                Query(application.Version + 1),
                CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffOnboardingApplicationErrors
                .CorrectionTargetUnavailable,
            result.Error);
    }

    [Theory]
    [InlineData(
        StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Unresolved,
        StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Active,
        StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Exact)]
    [InlineData(
        StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Absent,
        StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Active,
        StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Unknown)]
    public async Task Non_absent_or_malformed_absent_target_is_not_disclosed(
        StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus status,
        StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle lifecycle,
        StaffWorkspaceOnboardingIdentityAnchorSubjectMatch subjectMatch)
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        StaffWorkspaceOnboardingIdentityAnchorOutcome outcome = new(
            application.Id,
            status,
            status ==
                StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Unresolved
                    ? Guid.NewGuid()
                    : null,
            lifecycle,
            subjectMatch,
            WorkspaceApplicationVersion: null,
            ResolutionDisposition: null,
            ResolutionEventId: status ==
                StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Unresolved
                    ? Guid.NewGuid()
                    : null);
        GetWorkspaceStaffOnboardingDataRightsCorrectionTargetQueryHandler
            handler = CreateHandler(
                application,
                new RecordingExecutionGate(),
                outcome);

        Result<WorkspaceStaffOnboardingDataRightsCorrectionTargetDto> result =
            await handler.HandleAsync(
                Query(application.Version),
                CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffOnboardingApplicationErrors
                .CorrectionTargetUnavailable,
            result.Error);
    }

    [Fact]
    public async Task Denied_execution_does_not_enter_read_boundary()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        RecordingReadBoundary boundary = new([]);
        GetWorkspaceStaffOnboardingDataRightsCorrectionTargetQueryHandler
            handler = CreateHandler(
                application,
                new RecordingExecutionGate(allowed: false),
                boundary: boundary);

        Result<WorkspaceStaffOnboardingDataRightsCorrectionTargetDto> result =
            await handler.HandleAsync(
                Query(application.Version),
                CancellationToken.None);

        Assert.Equal(
            WorkspaceStaffOnboardingApplicationErrors
                .DataRightsApprovalRequired,
            result.Error);
        Assert.Equal(0, boundary.CallCount);
    }

    private static
        GetWorkspaceStaffOnboardingDataRightsCorrectionTargetQueryHandler
        CreateHandler(
            WorkspaceStaffOnboarding application,
            RecordingExecutionGate gate,
            StaffWorkspaceOnboardingIdentityAnchorOutcome? outcome = null,
            List<string>? calls = null,
            RecordingReadBoundary? boundary = null)
    {
        calls ??= [];
        ApplicationRepository applications = new(application);
        return new(
            WorkspaceStaffOnboardingMutationTestSupport.Create(
                applications,
                new RecordingOperationLock(calls)),
            boundary ?? new RecordingReadBoundary(calls),
            new StubStaffWorkspaceOnboardingIdentityAnchorOutcomeReader(
                request =>
                {
                    calls.Add("staff");
                    return outcome ??
                        StubStaffWorkspaceOnboardingIdentityAnchorOutcomeReader
                            .Absent(request);
                }),
            new WorkspaceStaffOnboardingDataRightsCorrectionAuthorizer(
                gate,
                new TestScopeContext()));
    }

    private static
        GetWorkspaceStaffOnboardingDataRightsCorrectionTargetQuery Query(
            long expectedVersion) =>
        new(
            Guid.Parse("30000000-0000-0000-0000-000000000001"),
            Guid.Parse("40000000-0000-0000-0000-000000000001"),
            ApprovalRevision: 5,
            ApplicationId,
            expectedVersion,
            "user:privacy-owner");

    private static WorkspaceStaffOnboarding CreateApplication() =>
        WorkspaceStaffOnboarding.Create(
            ApplicationId,
            TenantId,
            WorkspaceStaffOnboardingSource.EnrollmentLink,
            Guid.Parse("50000000-0000-0000-0000-000000000001"),
            "subject:applicant",
            "verified@example.test",
            "Ada Operator",
            "Ada Lovelace",
            "ada@example.test",
            "+1 555 0100",
            "EMP-100",
            "Manager",
            "Operations",
            Now).Value;

    private sealed class RecordingReadBoundary(List<string> calls)
        : IWorkspaceStaffOnboardingSerializedReadBoundary
    {
        public int CallCount { get; private set; }

        public Task<Result<T>> RunAsync<T>(
            Func<CancellationToken, Task<Result<T>>> read,
            CancellationToken cancellationToken)
        {
            this.CallCount++;
            calls.Add("tenant");
            return read(cancellationToken);
        }
    }

    private sealed class RecordingOperationLock(List<string> calls)
        : IWorkspaceStaffOnboardingOperationLock
    {
        public Task AcquireSourceReadAsync(
            Guid sourceId,
            CancellationToken cancellationToken)
        {
            calls.Add("source");
            return Task.CompletedTask;
        }

        public Task AcquireSourceWriteAsync(
            Guid sourceId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AcquireApplicantAsync(
            Guid sourceId,
            string subjectId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> TryAcquireAsync(
            Guid applicationId,
            CancellationToken cancellationToken)
        {
            calls.Add("application");
            return Task.FromResult(true);
        }
    }

    private sealed class ApplicationRepository(
        WorkspaceStaffOnboarding application)
        : IWorkspaceStaffOnboardingRepository
    {
        public Task<WorkspaceStaffOnboardingCoordinate?> FindCoordinateAsync(
            Guid applicationId,
            CancellationToken cancellationToken) =>
            Task.FromResult<WorkspaceStaffOnboardingCoordinate?>(
                application.Id == applicationId
                    ? new WorkspaceStaffOnboardingCoordinate(
                        application.Id,
                        application.SourceKind,
                        application.SourceId)
                    : null);

        public Task<WorkspaceStaffOnboarding?> GetAsync(
            Guid applicationId,
            CancellationToken cancellationToken) =>
            Task.FromResult<WorkspaceStaffOnboarding?>(
                application.Id == applicationId ? application : null);

        public Task<WorkspaceStaffOnboarding?> GetOperationalAsync(
            Guid applicationId,
            CancellationToken cancellationToken) =>
            this.GetAsync(applicationId, cancellationToken);

        public Task ReloadAsync(
            WorkspaceStaffOnboarding candidate,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<WorkspaceStaffOnboarding?>
            GetOperationalBySourceAndSubjectAsync(
                WorkspaceStaffOnboardingSource sourceKind,
                Guid sourceId,
                string subjectId,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Guid?> FindIdBySourceAndSubjectAsync(
            WorkspaceStaffOnboardingSource sourceKind,
            Guid sourceId,
            string subjectId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WorkspaceStaffOnboarding?> GetByClaimAsync(
            Guid claimId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<WorkspaceStaffOnboarding>>
            ListActiveBySourceAsync(
                WorkspaceStaffOnboardingSource sourceKind,
                Guid sourceId,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WorkspaceStaffOnboardingSummaryListResponse> ListActionableAsync(
            PageRequest page,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddAsync(
            WorkspaceStaffOnboarding candidate,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingExecutionGate(bool allowed = true)
        : IDataRightsCorrectionExecutionGate
    {
        public DataRightsCorrectionExecutionGateRequest? Request
        {
            get;
            private set;
        }

        public Task<DataRightsCorrectionExecutionGateResult> EvaluateAsync(
            DataRightsCorrectionExecutionGateRequest request,
            CancellationToken cancellationToken)
        {
            this.Request = request;
            return Task.FromResult(
                allowed
                    ? DataRightsCorrectionExecutionGateResult.Allowed(
                        Now.AddMinutes(5))
                    : DataRightsCorrectionExecutionGateResult.Denied(
                        DataRightsCorrectionExecutionDenial
                            .ExecutionNotFound));
        }
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string? ScopeId => TenantId;
    }
}
