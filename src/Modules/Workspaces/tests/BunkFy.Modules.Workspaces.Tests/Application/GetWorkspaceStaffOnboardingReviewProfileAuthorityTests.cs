namespace BunkFy.Modules.Workspaces.Tests;

using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Application;
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
public sealed class GetWorkspaceStaffOnboardingReviewProfileAuthorityTests
{
    private const string TenantId =
        "71000000-0000-0000-0000-000000000001";
    private const string SubjectId = "subject:profile-reader";
    private static readonly Guid SourceId =
        Guid.Parse("71000000-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset Now =
        new(2026, 8, 11, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Exact_absent_application_is_disclosed_after_all_locks()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        List<string> calls = [];
        GetWorkspaceStaffOnboardingReviewQueryHandler handler = CreateHandler(
            application,
            new RecordingOperationLock(calls),
            new MutableOutcomeReader(calls),
            new InlineReadBoundary(calls));

        Result<WorkspaceStaffOnboardingDto> result = await handler.HandleAsync(
            Query(),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal("Applicant Profile", result.Value.DisplayName);
        Assert.Equal(
            ["tenant", "source", "application", "staff"],
            calls);
    }

    [Fact]
    public async Task Staff_commit_while_waiting_for_application_lock_cannot_leak_staging()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        List<string> calls = [];
        CoordinatedOperationLock operationLock = new(calls);
        MutableOutcomeReader outcomes = new(calls);
        GetWorkspaceStaffOnboardingReviewQueryHandler handler = CreateHandler(
            application,
            operationLock,
            outcomes,
            new InlineReadBoundary(calls));

        Task<Result<WorkspaceStaffOnboardingDto>> read = handler.HandleAsync(
            Query(),
            CancellationToken.None);
        await operationLock.ApplicationLockRequested.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        Assert.Equal(0, outcomes.ReadCount);

        outcomes.Outcome = new StaffWorkspaceOnboardingIdentityAnchorOutcome(
            application.Id,
            StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Unresolved,
            Guid.Parse("71000000-0000-0000-0000-000000000003"),
            StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Active,
            StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Exact,
            WorkspaceApplicationVersion: null,
            ResolutionDisposition: null,
            Guid.Parse("71000000-0000-0000-0000-000000000004"));
        operationLock.ReleaseApplicationLock.TrySetResult();

        Result<WorkspaceStaffOnboardingDto> result = await read.WaitAsync(
            TimeSpan.FromSeconds(5));

        Assert.True(result.IsFailure);
        Assert.Equal(
            WorkspaceStaffOnboardingApplicationErrors.ProfileReviewUnavailable,
            result.Error);
        Assert.Equal(1, outcomes.ReadCount);
        Assert.Equal("Applicant Profile", application.DisplayName);
    }

    [Fact]
    public async Task Completed_Staff_owned_application_has_no_manager_review_profile()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        Guid staffMemberId = Guid.NewGuid();
        Guid resolutionEventId = Guid.NewGuid();
        Assert.True(application.ObserveClaimAccepted(Guid.NewGuid(), 1, Now).IsSuccess);
        Assert.True(application.BindAcceptedMembership(Guid.NewGuid(), 1, Now).IsSuccess);
        Assert.True(application.MarkStaffReady(
            staffMemberId, resolutionEventId, Guid.NewGuid(), Now).IsSuccess);
        Assert.True(application.Complete(Now).IsSuccess);
        MutableOutcomeReader outcomes = new([])
        {
            Outcome = new(application.Id,
                StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Resolved,
                staffMemberId,
                StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Active,
                StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Exact,
                application.Version,
                StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition.CompletedRedacted,
                resolutionEventId)
        };
        GetWorkspaceStaffOnboardingReviewQueryHandler handler = CreateHandler(
            application, new RecordingOperationLock([]), outcomes,
            new InlineReadBoundary([]));

        Result<WorkspaceStaffOnboardingDto> result = await handler.HandleAsync(
            Query(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            WorkspaceStaffOnboardingApplicationErrors.ProfileReviewUnavailable,
            result.Error);
        Assert.Equal(0, outcomes.ReadCount);
        Assert.Equal(staffMemberId, application.StaffMemberId);
    }

    [Fact]
    public async Task Failed_Staff_owned_application_has_no_manager_review_profile()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        Guid staffMemberId = Guid.NewGuid();
        Guid resolutionEventId = Guid.NewGuid();
        Assert.True(application.ObserveClaimAccepted(Guid.NewGuid(), 1, Now).IsSuccess);
        Assert.True(application.BindAcceptedMembership(Guid.NewGuid(), 1, Now).IsSuccess);
        Assert.True(application.MarkStaffReady(
            staffMemberId, resolutionEventId, Guid.NewGuid(), Now).IsSuccess);
        Assert.True(application.Fail("Workspaces.AccessProvisioningFailed", Now).IsSuccess);
        MutableOutcomeReader outcomes = new([])
        {
            Outcome = new(application.Id,
                StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Unresolved,
                staffMemberId,
                StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Active,
                StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Exact,
                WorkspaceApplicationVersion: null,
                ResolutionDisposition: null,
                resolutionEventId)
        };
        GetWorkspaceStaffOnboardingReviewQueryHandler handler = CreateHandler(
            application, new RecordingOperationLock([]), outcomes,
            new InlineReadBoundary([]));

        Result<WorkspaceStaffOnboardingDto> result = await handler.HandleAsync(
            Query(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            WorkspaceStaffOnboardingApplicationErrors.ProfileReviewUnavailable,
            result.Error);
        Assert.Equal(0, outcomes.ReadCount);
        Assert.Equal("Workspaces.AccessProvisioningFailed", application.FailureCode);
    }

    [Fact]
    public async Task Uncertain_Staff_evidence_refuses_the_manager_review_profile()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        MutableOutcomeReader outcomes = new([])
        {
            Outcome = new(application.Id,
                StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Corrupt,
                Guid.NewGuid(),
                StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Unknown,
                StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Unknown,
                WorkspaceApplicationVersion: null,
                ResolutionDisposition: null,
                ResolutionEventId: null)
        };
        GetWorkspaceStaffOnboardingReviewQueryHandler handler = CreateHandler(
            application, new RecordingOperationLock([]), outcomes,
            new InlineReadBoundary([]));

        Result<WorkspaceStaffOnboardingDto> result = await handler.HandleAsync(
            Query(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            WorkspaceStaffOnboardingApplicationErrors.ProfileReviewUnavailable,
            result.Error);
        Assert.Equal(1, outcomes.ReadCount);
        Assert.Equal("Applicant Profile", application.DisplayName);
    }

    [Fact]
    public async Task Outcome_for_another_application_is_still_refused()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        MutableOutcomeReader outcomes = new([])
        {
            Outcome = new(Guid.NewGuid(),
                StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Corrupt,
                StaffMemberId: null,
                StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Unknown,
                StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Unknown,
                WorkspaceApplicationVersion: null,
                ResolutionDisposition: null)
        };
        GetWorkspaceStaffOnboardingReviewQueryHandler handler = CreateHandler(
            application, new RecordingOperationLock([]), outcomes,
            new InlineReadBoundary([]));

        InvalidOperationException rejected =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                handler.HandleAsync(Query(), CancellationToken.None));

        Assert.Equal("Staff identity-anchor outcome coordinates are invalid.",
            rejected.Message);
        Assert.Equal("Applicant Profile", application.DisplayName);
    }

    [Fact]
    public async Task Read_boundary_failure_does_not_touch_or_disclose_application()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        MutableOutcomeReader outcomes = new([]);
        GetWorkspaceStaffOnboardingReviewQueryHandler handler = CreateHandler(
            application,
            new RecordingOperationLock([]),
            outcomes,
            new FailingReadBoundary());

        Result<WorkspaceStaffOnboardingDto> result = await handler.HandleAsync(
            Query(),
            CancellationToken.None);

        Assert.Equal(
            WorkspaceOperationalAdmissionErrors.AdmissionUnavailable,
            result.Error);
        Assert.Equal(0, outcomes.ReadCount);
        Assert.Equal("Applicant Profile", application.DisplayName);
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("71000000-0000-0000-0000-000000000099")]
    public async Task Unknown_application_has_no_profile_or_Staff_read(string applicationId)
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        List<string> calls = [];
        MutableOutcomeReader outcomes = new(calls);
        GetWorkspaceStaffOnboardingReviewQueryHandler handler = CreateHandler(
            application, new RecordingOperationLock(calls), outcomes,
            new InlineReadBoundary(calls));

        Result<WorkspaceStaffOnboardingDto> result = await handler.HandleAsync(
            new(Guid.Parse(applicationId)), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(WorkspaceStaffOnboardingApplicationErrors.ApplicationNotFound,
            result.Error);
        Assert.Equal(0, outcomes.ReadCount);
        Assert.Equal(["tenant"], calls);
    }

    [Fact]
    public async Task Processing_restriction_has_no_profile_or_Staff_read()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        MutableOutcomeReader outcomes = new([]);
        GetWorkspaceStaffOnboardingReviewQueryHandler handler = CreateHandler(
            application, new RecordingOperationLock([]), outcomes,
            new InlineReadBoundary([]), operationalVisible: false);

        Result<WorkspaceStaffOnboardingDto> result = await handler.HandleAsync(
            Query(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(WorkspaceStaffOnboardingApplicationErrors.ProcessingRestricted,
            result.Error);
        Assert.Equal(0, outcomes.ReadCount);
        Assert.Equal("Applicant Profile", application.DisplayName);
    }

    [Fact]
    public async Task Legacy_list_preserves_preapproval_applicant_identity_and_page_metadata()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        Assert.True(application.ObserveClaimRequested(Guid.NewGuid(), 1, Now).IsSuccess);
        List<string> calls = [];
        InlineReadBoundary boundary = new(calls);
        MutableOutcomeReader outcomes = new(calls);
        ListActionableWorkspaceStaffOnboardingQueryHandler handler = CreateLegacyHandler(
            application, new RecordingOperationLock(calls), outcomes, boundary);

        Result<WorkspaceStaffOnboardingListResponse> result = await handler.HandleAsync(
            new(1, 25), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        WorkspaceStaffOnboardingDto applicant = Assert.Single(result.Value.Items);
        Assert.Equal("Applicant Profile", applicant.DisplayName);
        Assert.Equal("verified@example.test", applicant.VerifiedAccountEmail);
        Assert.Equal("Applicant Legal", applicant.LegalName);
        Assert.Equal("work@example.test", applicant.WorkEmail);
        Assert.Equal("+1 555 0100", applicant.WorkPhone);
        Assert.Equal("EMP-710", applicant.EmployeeNumber);
        Assert.Equal("Manager", applicant.JobTitle);
        Assert.Equal("Operations", applicant.Department);
        Assert.Equal(WorkspaceStaffOnboardingStatus.PendingApproval, applicant.Status);
        Assert.Equal((1, 25, false),
            (result.Value.Page, result.Value.PageSize, result.Value.HasMore));
        Assert.Equal(["tenant", "source", "application", "staff"], calls);
        Assert.Equal(0, boundary.ActiveReads);
    }

    [Fact]
    public async Task Legacy_list_refuses_entire_page_if_Staff_commits_before_local_version_changes()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        Assert.True(application.ObserveClaimRequested(Guid.NewGuid(), 1, Now).IsSuccess);
        long listedVersion = application.Version;
        CoordinatedOperationLock operationLock = new([]);
        MutableOutcomeReader outcomes = new([]);
        ListActionableWorkspaceStaffOnboardingQueryHandler handler = CreateLegacyHandler(
            application, operationLock, outcomes, new InlineReadBoundary([]));
        Task<Result<WorkspaceStaffOnboardingListResponse>> read = handler.HandleAsync(
            new(1, 25), CancellationToken.None);
        await operationLock.ApplicationLockRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        outcomes.Outcome = new(application.Id,
            StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Unresolved,
            Guid.NewGuid(), StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Active,
            StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Exact, null, null, Guid.NewGuid());
        operationLock.ReleaseApplicationLock.TrySetResult();

        Result<WorkspaceStaffOnboardingListResponse> result = await read.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.IsFailure);
        Assert.Equal(WorkspaceStaffOnboardingApplicationErrors.ProfileReviewUnavailable, result.Error);
        Assert.Equal(listedVersion, application.Version);
        Assert.Equal(1, outcomes.ReadCount);
    }

    [Fact]
    public async Task Legacy_list_omits_a_row_that_became_terminal_while_waiting()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        Guid claimId = Guid.NewGuid();
        Assert.True(application.ObserveClaimRequested(claimId, 1, Now).IsSuccess);
        CoordinatedOperationLock operationLock = new([]);
        MutableOutcomeReader outcomes = new([]);
        ListActionableWorkspaceStaffOnboardingQueryHandler handler = CreateLegacyHandler(
            application, operationLock, outcomes, new InlineReadBoundary([]));
        Task<Result<WorkspaceStaffOnboardingListResponse>> read = handler.HandleAsync(
            new(1, 25), CancellationToken.None);
        await operationLock.ApplicationLockRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(application.ObserveClaimRejected(claimId, 2, Now.AddSeconds(1)).IsSuccess);
        operationLock.ReleaseApplicationLock.TrySetResult();

        Result<WorkspaceStaffOnboardingListResponse> result = await read.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Empty(result.Value.Items);
        Assert.Equal(0, outcomes.ReadCount);
    }

    [Fact]
    public async Task Legacy_list_refuses_restricted_rows_without_disclosing_staging()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        Assert.True(application.ObserveClaimRequested(Guid.NewGuid(), 1, Now).IsSuccess);
        MutableOutcomeReader outcomes = new([]);
        ListActionableWorkspaceStaffOnboardingQueryHandler handler = CreateLegacyHandler(
            application, new RecordingOperationLock([]), outcomes,
            new InlineReadBoundary([]), operationalVisible: false);

        Result<WorkspaceStaffOnboardingListResponse> result = await handler.HandleAsync(
            new(1, 25), CancellationToken.None);

        Assert.Equal(WorkspaceStaffOnboardingApplicationErrors.ProcessingRestricted, result.Error);
        Assert.Equal(0, outcomes.ReadCount);
    }

    [Fact]
    public async Task Legacy_list_allows_safe_pre_Staff_failure_but_refuses_a_known_Staff_failure()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        Assert.True(application.Fail("Workspaces.EarlyFailure", Now).IsSuccess);
        MutableOutcomeReader outcomes = new([]);
        ListActionableWorkspaceStaffOnboardingQueryHandler handler = CreateLegacyHandler(
            application, new RecordingOperationLock([]), outcomes, new InlineReadBoundary([]));
        Result<WorkspaceStaffOnboardingListResponse> early = await handler.HandleAsync(
            new(1, 25), CancellationToken.None);
        Assert.True(early.IsSuccess, early.Error.Code);
        Assert.Equal("Applicant Profile", Assert.Single(early.Value.Items).DisplayName);
        Assert.True(application.ObserveClaimAccepted(Guid.NewGuid(), 1, Now).IsSuccess);
        Assert.True(application.BindAcceptedMembership(Guid.NewGuid(), 1, Now).IsSuccess);
        Assert.True(application.BeginProvisioning(Now).IsSuccess);
        Assert.True(application.MarkStaffReady(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now).IsSuccess);
        Assert.True(application.Fail("Workspaces.AccessProvisioningFailed", Now).IsSuccess);

        Result<WorkspaceStaffOnboardingListResponse> owned = await handler.HandleAsync(
            new(1, 25), CancellationToken.None);

        Assert.Equal(WorkspaceStaffOnboardingApplicationErrors.ProfileReviewUnavailable, owned.Error);
        Assert.Equal(1, outcomes.ReadCount);
    }

    [Fact]
    public async Task Legacy_page_reads_are_bounded_and_never_overlap_serialized_applicants()
    {
        WorkspaceStaffOnboarding[] applications = Enumerable.Range(0, 26)
            .Select(_ => CreateApplication(Guid.NewGuid())).ToArray();
        foreach (WorkspaceStaffOnboarding application in applications)
        {
            Assert.True(application.ObserveClaimRequested(Guid.NewGuid(), 1, Now).IsSuccess);
        }
        InlineReadBoundary boundary = new([]);
        MutableOutcomeReader outcomes = new([]);
        ListActionableWorkspaceStaffOnboardingQueryHandler handler = CreateLegacyHandler(
            applications[0], new RecordingOperationLock([]), outcomes, boundary, rows: applications);

        Result<WorkspaceStaffOnboardingListResponse> result = await handler.HandleAsync(
            new(1, 25), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(25, result.Value.Items.Count);
        Assert.True(result.Value.HasMore);
        Assert.Equal(25, outcomes.ReadCount);
        Assert.Equal(25, boundary.ReadCount);
        Assert.Equal(1, boundary.MaximumActiveReads);
        Assert.Equal(0, boundary.ActiveReads);
    }

    [Fact]
    public async Task Minimal_summary_page_does_not_fetch_any_applicant_profile()
    {
        WorkspaceStaffOnboarding application = CreateApplication();
        Assert.True(application.Fail("Workspaces.AnyFailure", Now).IsSuccess);
        ApplicationRepository applications = new(application, operationalVisible: false);
        ListActionableWorkspaceStaffOnboardingSummariesQueryHandler handler = new(applications);

        Result<WorkspaceStaffOnboardingSummaryListResponse> result = await handler.HandleAsync(
            new(1, 25), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        WorkspaceStaffOnboardingActionableSummaryDto summary = Assert.Single(result.Value.Items);
        Assert.Equal(application.Id, summary.ApplicationId);
        Assert.Equal(0, applications.ProfileReadCount);
    }

    private static ListActionableWorkspaceStaffOnboardingQueryHandler CreateLegacyHandler(
        WorkspaceStaffOnboarding application,
        IWorkspaceStaffOnboardingOperationLock operationLock,
        IStaffWorkspaceOnboardingIdentityAnchorOutcomeReader outcomes,
        IWorkspaceStaffOnboardingSerializedReadBoundary boundary,
        bool operationalVisible = true,
        IReadOnlyList<WorkspaceStaffOnboarding>? rows = null)
    {
        ApplicationRepository applications = new(application, operationalVisible, rows);
        return new(applications, new WorkspaceStaffOnboardingManagerReviewReader(
            WorkspaceStaffOnboardingMutationTestSupport.Create(applications, operationLock),
            boundary, outcomes,
            WorkspaceOperationalAdmissionTestSupport.Allowed(TenantId), new TestScopeContext()));
    }

    private static GetWorkspaceStaffOnboardingReviewQueryHandler CreateHandler(
        WorkspaceStaffOnboarding application,
        IWorkspaceStaffOnboardingOperationLock operationLock,
        IStaffWorkspaceOnboardingIdentityAnchorOutcomeReader outcomes,
        IWorkspaceStaffOnboardingSerializedReadBoundary boundary,
        bool operationalVisible = true)
    {
        ApplicationRepository applications = new(application, operationalVisible);
        return new(new WorkspaceStaffOnboardingManagerReviewReader(
            WorkspaceStaffOnboardingMutationTestSupport.Create(
                applications,
                operationLock),
            boundary,
            outcomes,
            WorkspaceOperationalAdmissionTestSupport.Allowed(TenantId),
            new TestScopeContext()));
    }

    private static GetWorkspaceStaffOnboardingReviewQuery Query() =>
        new(Guid.Parse("71000000-0000-0000-0000-000000000005"));

    private static WorkspaceStaffOnboarding CreateApplication(Guid? applicationId = null) =>
        WorkspaceStaffOnboarding.Create(
            applicationId ?? Guid.Parse("71000000-0000-0000-0000-000000000005"),
            TenantId,
            WorkspaceStaffOnboardingSource.EnrollmentLink,
            SourceId,
            SubjectId,
            "verified@example.test",
            "Applicant Profile",
            "Applicant Legal",
            "work@example.test",
            "+1 555 0100",
            "EMP-710",
            "Manager",
            "Operations",
            Now).Value;

    private sealed class InlineReadBoundary(List<string> calls)
        : IWorkspaceStaffOnboardingSerializedReadBoundary
    {
        public int ActiveReads { get; private set; }
        public int MaximumActiveReads { get; private set; }
        public int ReadCount { get; private set; }

        public async Task<Result<T>> RunAsync<T>(
            Func<CancellationToken, Task<Result<T>>> read,
            CancellationToken cancellationToken)
        {
            calls.Add("tenant");
            this.ActiveReads++;
            this.ReadCount++;
            this.MaximumActiveReads = Math.Max(this.MaximumActiveReads, this.ActiveReads);
            try
            {
                return await read(cancellationToken);
            }
            finally
            {
                this.ActiveReads--;
            }
        }
    }

    private sealed class FailingReadBoundary
        : IWorkspaceStaffOnboardingSerializedReadBoundary
    {
        public Task<Result<T>> RunAsync<T>(
            Func<CancellationToken, Task<Result<T>>> read,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result.Failure<T>(
                WorkspaceOperationalAdmissionErrors.AdmissionUnavailable));
    }

    private class RecordingOperationLock(List<string> calls)
        : IWorkspaceStaffOnboardingOperationLock
    {
        protected List<string> Calls { get; } = calls;

        public virtual Task AcquireSourceReadAsync(
            Guid sourceId,
            CancellationToken cancellationToken)
        {
            this.Calls.Add("source");
            return Task.CompletedTask;
        }

        public Task AcquireSourceWriteAsync(
            Guid sourceId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public virtual Task AcquireApplicantAsync(
            Guid sourceId,
            string subjectId,
            CancellationToken cancellationToken)
        {
            this.Calls.Add("applicant");
            return Task.CompletedTask;
        }

        public virtual Task<bool> TryAcquireAsync(
            Guid applicationId,
            CancellationToken cancellationToken)
        {
            this.Calls.Add("application");
            return Task.FromResult(true);
        }
    }

    private sealed class CoordinatedOperationLock(List<string> calls)
        : RecordingOperationLock(calls)
    {
        public TaskCompletionSource ApplicationLockRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseApplicationLock { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<bool> TryAcquireAsync(
            Guid applicationId,
            CancellationToken cancellationToken)
        {
            this.Calls.Add("application-wait");
            this.ApplicationLockRequested.TrySetResult();
            await this.ReleaseApplicationLock.Task.WaitAsync(cancellationToken);
            this.Calls.Add("application");
            return true;
        }
    }

    private sealed class MutableOutcomeReader(List<string> calls)
        : IStaffWorkspaceOnboardingIdentityAnchorOutcomeReader
    {
        public StaffWorkspaceOnboardingIdentityAnchorOutcome? Outcome
        {
            get;
            set;
        }

        public int ReadCount { get; private set; }

        public Task<IReadOnlyList<
            StaffWorkspaceOnboardingIdentityAnchorOutcome>> ReadAsync(
            IReadOnlyList<StaffWorkspaceOnboardingIdentityAnchorOutcomeRequest>
                requests,
            CancellationToken cancellationToken = default)
        {
            this.ReadCount++;
            calls.Add("staff");
            StaffWorkspaceOnboardingIdentityAnchorOutcomeRequest request =
                Assert.Single(requests);
            return Task.FromResult<IReadOnlyList<
                StaffWorkspaceOnboardingIdentityAnchorOutcome>>([
                    this.Outcome ??
                    StubStaffWorkspaceOnboardingIdentityAnchorOutcomeReader
                        .Absent(request)
                ]);
        }
    }

    private sealed class ApplicationRepository(
        WorkspaceStaffOnboarding application,
        bool operationalVisible,
        IReadOnlyList<WorkspaceStaffOnboarding>? rows = null)
        : IWorkspaceStaffOnboardingRepository
    {
        private IReadOnlyList<WorkspaceStaffOnboarding> Applications => rows ?? [application];
        public int ProfileReadCount { get; private set; }

        public Task<Guid?> FindIdBySourceAndSubjectAsync(
            WorkspaceStaffOnboardingSource sourceKind,
            Guid sourceId,
            string subjectId,
            CancellationToken cancellationToken) =>
            Task.FromResult<Guid?>(
                application.SourceKind == sourceKind &&
                application.SourceId == sourceId &&
                string.Equals(
                    application.SubjectId,
                    subjectId,
                    StringComparison.Ordinal)
                    ? application.Id
                    : null);

        public Task<WorkspaceStaffOnboarding?> GetOperationalAsync(
            Guid applicationId,
            CancellationToken cancellationToken)
        {
            this.ProfileReadCount++;
            return Task.FromResult(operationalVisible
                ? this.Applications.SingleOrDefault(row => row.Id == applicationId)
                : null);
        }

        public Task<WorkspaceStaffOnboardingCoordinate?> FindCoordinateAsync(
            Guid applicationId,
            CancellationToken cancellationToken)
        {
            WorkspaceStaffOnboarding? row = this.Applications.SingleOrDefault(row => row.Id == applicationId);
            return Task.FromResult<WorkspaceStaffOnboardingCoordinate?>(row is not null
                ? new(row.Id, row.SourceKind, row.SourceId)
                : null);
        }

        public Task<WorkspaceStaffOnboarding?> GetAsync(
            Guid applicationId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WorkspaceStaffOnboarding?>
            GetOperationalBySourceAndSubjectAsync(
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
            CancellationToken cancellationToken) => Task.FromResult(
                new WorkspaceStaffOnboardingSummaryListResponse(
                    this.Applications.Skip(page.SkipCount).Take(page.PageSize).Select(row =>
                        new WorkspaceStaffOnboardingActionableSummaryDto(
                            row.Id, WorkspaceStaffOnboardingSourceKind.EnrollmentLink,
                            row.SourceId, row.ClaimId, row.ClaimVersion,
                            (WorkspaceStaffOnboardingStatus)row.Status, row.Version,
                            row.FailureCode, row.CreatedAtUtc, row.LastChangedAtUtc,
                            row.StaffMemberId.HasValue)).ToArray(),
                    page.Page, page.PageSize, this.Applications.Count > page.SkipCount + page.PageSize));

        public Task ReloadAsync(
            WorkspaceStaffOnboarding candidate,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AddAsync(
            WorkspaceStaffOnboarding candidate,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string? ScopeId => TenantId;
    }
}
