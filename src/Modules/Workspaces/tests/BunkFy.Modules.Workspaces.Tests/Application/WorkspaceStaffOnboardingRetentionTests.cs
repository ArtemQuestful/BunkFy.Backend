namespace BunkFy.Modules.Workspaces.Tests;

using BunkFy.Modules.Retention.Contracts;
using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Application.Contributors;
using BunkFy.Modules.Workspaces.Application.Handlers;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Domain.DataRights;
using Gma.Framework.AccessControl;
using Gma.Framework.Cqrs;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Gma.Modules.AccessControl.Contracts;
using Gma.Modules.Organizations.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspaceStaffOnboardingRetentionTests
{
    [Fact]
    public void Defaults_preserve_a_bounded_inspection_window()
    {
        WorkspaceStaffOnboardingRetentionOptions options = new();

        ValidateOptionsResult validation =
            new WorkspaceStaffOnboardingRetentionOptionsValidator()
                .Validate(null, options);

        Assert.True(validation.Succeeded);
        Assert.Equal(TimeSpan.FromHours(2), options.GracePeriod);
        Assert.Equal(TimeSpan.FromHours(20), options.AuthorityWindow);
        Assert.Equal(TimeSpan.FromHours(1), options.Interval);
        Assert.Equal(50, options.BatchSize);
    }

    [Fact]
    public void Authority_window_must_leave_one_schedule_after_grace()
    {
        WorkspaceStaffOnboardingRetentionOptions options = new()
        {
            GracePeriodHours = 12,
            AuthorityWindowHours = 14,
            IntervalMinutes = 120
        };

        ValidateOptionsResult validation =
            new WorkspaceStaffOnboardingRetentionOptionsValidator()
                .Validate(null, options);

        Assert.True(validation.Failed);
        Assert.Contains(
            validation.Failures,
            failure => failure.Contains(
                "must exceed the grace period",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_claim_inside_authority_window_expires_and_redacts_staging()
    {
        WorkspaceStaffOnboarding application =
            WorkspaceStaffOnboardingTests.CreateApplication();
        WorkspaceStaffAccessPlan plan = CreatePlan(
            application,
            Now.AddHours(-3),
            active: true);
        FakeOnboardingRepository applications = new(application);
        FakeAccessPlanRepository plans = new(plan);
        FakeClaimInspector inspector = new(null);
        ReconcileWorkspaceStaffOnboardingRetentionCandidateCommandHandler handler =
            CreateHandler(applications, plans, inspector);

        Result<WorkspaceStaffOnboardingRetentionReconciliation> result =
            await handler.HandleAsync(
                Command(application.Id, application.Version),
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(
            WorkspaceStaffOnboardingRetentionOutcome.Expired,
            result.Value.Outcome);
        Assert.True(result.Value.Affected);
        Assert.Equal(1, inspector.CallCount);
        Assert.Equal(WorkspaceStaffOnboardingState.Expired, application.Status);
        Assert.Equal(WorkspaceStaffAccessPlanState.Expired, plan.Status);
        Assert.Null(application.VerifiedAccountEmail);
        Assert.Null(application.DisplayName);
        Assert.Null(application.WorkEmail);
    }

    [Fact]
    public async Task Missing_claim_after_authority_window_fails_closed_without_redaction()
    {
        WorkspaceStaffOnboarding application =
            WorkspaceStaffOnboardingTests.CreateApplication();
        WorkspaceStaffAccessPlan plan = CreatePlan(
            application,
            Now.AddHours(-21),
            active: true);
        FakeClaimInspector inspector = new(null);
        ReconcileWorkspaceStaffOnboardingRetentionCandidateCommandHandler handler =
            CreateHandler(
                new FakeOnboardingRepository(application),
                new FakeAccessPlanRepository(plan),
                inspector);

        Result<WorkspaceStaffOnboardingRetentionReconciliation> result =
            await handler.HandleAsync(
                Command(application.Id, application.Version),
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(
            WorkspaceStaffOnboardingRetentionOutcome.AuthorityLapsed,
            result.Value.Outcome);
        Assert.False(result.Value.Affected);
        Assert.Equal(WorkspaceStaffOnboardingState.Submitted, application.Status);
        Assert.Equal(WorkspaceStaffAccessPlanState.Active, plan.Status);
        Assert.NotNull(application.DisplayName);
    }

    [Fact]
    public async Task Pending_claim_is_bound_and_preserves_staging_and_plan()
    {
        WorkspaceStaffOnboarding application =
            WorkspaceStaffOnboardingTests.CreateApplication();
        WorkspaceStaffAccessPlan plan = CreatePlan(
            application,
            Now.AddHours(-3),
            active: true);
        OrganizationEnrollmentClaimDto claim = CreateClaim(
            application,
            OrganizationEnrollmentClaimStatus.Pending);
        ReconcileWorkspaceStaffOnboardingRetentionCandidateCommandHandler handler =
            CreateHandler(
                new FakeOnboardingRepository(application),
                new FakeAccessPlanRepository(plan),
                new FakeClaimInspector(claim));

        Result<WorkspaceStaffOnboardingRetentionReconciliation> result =
            await handler.HandleAsync(
                Command(application.Id, application.Version),
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(
            WorkspaceStaffOnboardingRetentionOutcome.ClaimPending,
            result.Value.Outcome);
        Assert.Equal(WorkspaceStaffOnboardingState.PendingApproval, application.Status);
        Assert.Equal(claim.ClaimId, application.ClaimId);
        Assert.Equal(WorkspaceStaffAccessPlanState.Active, plan.Status);
        Assert.NotNull(application.DisplayName);
    }

    [Fact]
    public async Task Retained_claim_with_non_active_plan_fails_closed()
    {
        WorkspaceStaffOnboarding application =
            WorkspaceStaffOnboardingTests.CreateApplication();
        WorkspaceStaffAccessPlan plan = CreatePlan(
            application,
            Now.AddHours(-3),
            active: false);
        FakeClaimInspector inspector = new(CreateClaim(
            application,
            OrganizationEnrollmentClaimStatus.Pending));
        ReconcileWorkspaceStaffOnboardingRetentionCandidateCommandHandler handler =
            CreateHandler(
                new FakeOnboardingRepository(application),
                new FakeAccessPlanRepository(plan),
                inspector);

        Result<WorkspaceStaffOnboardingRetentionReconciliation> result =
            await handler.HandleAsync(
                Command(application.Id, application.Version),
                CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            WorkspaceStaffOnboardingApplicationErrors.RetentionPlanInconsistent,
            result.Error);
        Assert.Equal(1, inspector.CallCount);
        Assert.Equal(WorkspaceStaffOnboardingState.Submitted, application.Status);
        Assert.Equal(WorkspaceStaffAccessPlanState.Prepared, plan.Status);
        Assert.Null(application.ClaimId);
        Assert.NotNull(application.DisplayName);
    }

    [Theory]
    [InlineData(
        OrganizationEnrollmentClaimStatus.Rejected,
        WorkspaceStaffOnboardingState.Rejected)]
    [InlineData(
        OrganizationEnrollmentClaimStatus.Expired,
        WorkspaceStaffOnboardingState.Expired)]
    [InlineData(
        OrganizationEnrollmentClaimStatus.Withdrawn,
        WorkspaceStaffOnboardingState.Withdrawn)]
    public async Task Terminal_claim_redacts_staging_and_finalizes_source_expired_plan(
        OrganizationEnrollmentClaimStatus claimStatus,
        WorkspaceStaffOnboardingState expectedState)
    {
        WorkspaceStaffOnboarding application =
            WorkspaceStaffOnboardingTests.CreateApplication();
        WorkspaceStaffAccessPlan plan = CreatePlan(
            application,
            Now.AddHours(-3),
            active: true);
        ReconcileWorkspaceStaffOnboardingRetentionCandidateCommandHandler handler =
            CreateHandler(
                new FakeOnboardingRepository(application),
                new FakeAccessPlanRepository(plan),
                new FakeClaimInspector(CreateClaim(application, claimStatus)));

        Result<WorkspaceStaffOnboardingRetentionReconciliation> result =
            await handler.HandleAsync(
                Command(application.Id, application.Version),
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(
            claimStatus switch
            {
                OrganizationEnrollmentClaimStatus.Rejected =>
                    WorkspaceStaffOnboardingRetentionOutcome.ClaimRejected,
                OrganizationEnrollmentClaimStatus.Expired =>
                    WorkspaceStaffOnboardingRetentionOutcome.ClaimExpired,
                OrganizationEnrollmentClaimStatus.Withdrawn =>
                    WorkspaceStaffOnboardingRetentionOutcome.ClaimWithdrawn,
                _ => throw new ArgumentOutOfRangeException(nameof(claimStatus))
            },
            result.Value.Outcome);
        Assert.Equal(expectedState, application.Status);
        Assert.Equal(WorkspaceStaffAccessPlanState.Expired, plan.Status);
        Assert.Null(application.DisplayName);
    }

    [Fact]
    public async Task Deferred_withdrawal_and_authoritative_expiry_fail_closed_and_retain_the_fact()
    {
        WorkspaceStaffOnboarding application =
            WorkspaceStaffOnboardingTests.CreateApplication();
        Guid claimId = Guid.NewGuid();
        Assert.True(application.ObserveClaimRequested(
            claimId,
            1,
            Now.AddHours(-4)).IsSuccess);
        WorkspaceStaffAccessPlan plan = CreatePlan(
            application,
            Now.AddHours(-3),
            active: true);
        OrganizationEnrollmentClaimDto authoritative = new(
            claimId,
            application.SourceId,
            WorkspaceStaffOnboardingTests.OrganizationId,
            application.SubjectId,
            OrganizationEnrollmentClaimStatus.Expired,
            MembershipId: null,
            Version: 2,
            CreatedAtUtc: Now.AddHours(-4),
            LastChangedAtUtc: Now.AddHours(-2));
        WorkspaceStaffDeferredClaimWithdrawal withdrawal =
            WorkspaceStaffDeferredClaimWithdrawal.Create(
                application.ScopeId,
                WorkspaceStaffOnboardingTests.OrganizationId,
                application.SourceId,
                claimId,
                2,
                Guid.NewGuid(),
                Now.AddHours(-2)).Value;
        FakeWorkspaceStaffDeferredClaimWithdrawalRepository deferred = new(withdrawal);
        ReconcileWorkspaceStaffOnboardingRetentionCandidateCommandHandler handler =
            CreateHandler(
                new FakeOnboardingRepository(application),
                new FakeAccessPlanRepository(plan),
                new FakeClaimInspector(authoritative),
                deferred);

        Result<WorkspaceStaffOnboardingRetentionReconciliation> result =
            await handler.HandleAsync(
                new(application.Id, application.Version),
                CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            WorkspaceStaffOnboardingApplicationErrors.RetentionClaimInconsistent,
            result.Error);
        Assert.Equal(WorkspaceStaffOnboardingState.PendingApproval, application.Status);
        Assert.NotNull(application.DisplayName);
        Assert.Same(withdrawal, Assert.Single(deferred.Items));
    }

    [Fact]
    public async Task Accepted_claim_enters_existing_recoverable_processing_path()
    {
        WorkspaceStaffOnboarding application =
            WorkspaceStaffOnboardingTests.CreateApplication();
        WorkspaceStaffAccessPlan plan = CreatePlan(
            application,
            Now.AddHours(-3),
            active: true);
        FakeOnboardingRepository applications = new(application);
        FakeAccessPlanRepository plans = new(plan);
        WorkspaceStaffAccessPlanPolicy planPolicy = new(
            new MissingProfileProvisioner(),
            null!,
            null!,
            null!);
        WorkspaceStaffOnboardingProcessor processor = new(
            null!,
            null!,
            new FakeRestrictionProjectionRepository(application),
            WorkspaceStaffOnboardingMutationTestSupport.Create(
                applications,
                new FakeOperationLock()),
            plans,
            planPolicy,
            null!,
            WorkspaceOperationalAdmissionTestSupport.Allowed(
                application.ScopeId),
            new FakeClock(),
            NullLogger<WorkspaceStaffOnboardingProcessor>.Instance);
        ReconcileWorkspaceStaffOnboardingRetentionCandidateCommandHandler handler = new(
            applications,
            plans,
            CreateRetentionCoordinator(),
            new FakeWorkspaceStaffDeferredClaimWithdrawalRepository(),
            WorkspaceStaffOnboardingMutationTestSupport.Create(
                applications,
                new FakeOperationLock()),
            new FakeClaimInspector(CreateClaim(
                application,
                OrganizationEnrollmentClaimStatus.Accepted)),
            processor,
            Options.Create(new WorkspaceStaffOnboardingRetentionOptions()),
            new FakeClock());

        Result<WorkspaceStaffOnboardingRetentionReconciliation> result =
            await handler.HandleAsync(
                Command(application.Id, application.Version),
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(
            WorkspaceStaffOnboardingRetentionOutcome.ClaimAcceptedRecoveryRequired,
            result.Value.Outcome);
        Assert.Equal(WorkspaceStaffOnboardingState.Failed, application.Status);
        Assert.Equal(WorkspaceStaffAccessPlanState.Active, plan.Status);
        Assert.NotNull(application.DisplayName);
    }

    [Fact]
    public async Task Stale_candidate_is_an_idempotent_no_op()
    {
        WorkspaceStaffOnboarding application =
            WorkspaceStaffOnboardingTests.CreateApplication();
        WorkspaceStaffAccessPlan plan = CreatePlan(
            application,
            Now.AddHours(-3),
            active: true);
        FakeClaimInspector inspector = new(null);
        ReconcileWorkspaceStaffOnboardingRetentionCandidateCommandHandler handler =
            CreateHandler(
                new FakeOnboardingRepository(application),
                new FakeAccessPlanRepository(plan),
                inspector);

        Result<WorkspaceStaffOnboardingRetentionReconciliation> result =
            await handler.HandleAsync(
                Command(application.Id, application.Version - 1),
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(
            WorkspaceStaffOnboardingRetentionOutcome.Unchanged,
            result.Value.Outcome);
        Assert.Equal(0, inspector.CallCount);
        Assert.Equal(WorkspaceStaffOnboardingState.Submitted, application.Status);
    }

    [Fact]
    public void Application_registration_adds_one_retention_contributor()
    {
        ServiceCollection services = new();
        IConfiguration configuration = new ConfigurationBuilder().Build();

        services.AddWorkspacesApplication(configuration, "global");
        services.AddWorkspacesApplication(configuration, "global");

        Assert.Single(
            services,
            descriptor =>
                descriptor.ServiceType ==
                    typeof(IRetentionExecutionContributor) &&
                descriptor.ImplementationType ==
                    typeof(WorkspaceStaffOnboardingRetentionContributor));
        Assert.Single(
            services,
            descriptor =>
                descriptor.ServiceType ==
                    typeof(IValidateOptions<
                        WorkspaceStaffOnboardingRetentionOptions>) &&
                descriptor.ImplementationType ==
                    typeof(WorkspaceStaffOnboardingRetentionOptionsValidator));
    }

    [Fact]
    public async Task Reconciliation_records_scan_and_mutation_on_the_owner_execution()
    {
        WorkspaceStaffOnboarding application =
            WorkspaceStaffOnboardingTests.CreateApplication();
        WorkspaceStaffAccessPlan plan = CreatePlan(
            application,
            Now.AddHours(-3),
            active: true);
        FakeOnboardingRepository applications = new(application);
        WorkspaceStaffOnboardingRetentionExecution execution =
            CreateRetentionExecution();
        WorkspaceStaffOnboardingRetentionExecutionCoordinator coordinator =
            new(
                new FakeRetentionExecutionLock(),
                new FakeRetentionExecutionRepository(execution),
                new TestScopeContext());
        ReconcileWorkspaceStaffOnboardingRetentionCandidateCommandHandler
            handler = new(
                applications,
                new FakeAccessPlanRepository(plan),
                coordinator,
                WorkspaceStaffOnboardingMutationTestSupport.Create(
                    applications,
                    new FakeOperationLock()),
                new FakeClaimInspector(null),
                null!,
                Options.Create(
                    new WorkspaceStaffOnboardingRetentionOptions()),
                new FakeClock());

        Result<WorkspaceStaffOnboardingRetentionReconciliation> result =
            await handler.HandleAsync(
                Command(application.Id, application.Version),
                CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.True(result.Value.Affected);
        Assert.Equal(1, execution.ScannedCount);
        Assert.Equal(1, execution.AffectedCount);
    }

    private static ReconcileWorkspaceStaffOnboardingRetentionCandidateCommandHandler
        CreateHandler(
            FakeOnboardingRepository applications,
            FakeAccessPlanRepository plans,
            FakeClaimInspector inspector,
            FakeWorkspaceStaffDeferredClaimWithdrawalRepository? deferred = null) =>
        new(
            applications,
            plans,
            CreateRetentionCoordinator(),
            deferred ?? new FakeWorkspaceStaffDeferredClaimWithdrawalRepository(),
            WorkspaceStaffOnboardingMutationTestSupport.Create(
                applications,
                new FakeOperationLock()),
            inspector,
            null!,
            Options.Create(new WorkspaceStaffOnboardingRetentionOptions()),
            new FakeClock());

    private static ReconcileWorkspaceStaffOnboardingRetentionCandidateCommand
        Command(Guid applicationId, long expectedVersion) =>
        new(RetentionExecutionId, 1, applicationId, expectedVersion);

    private static WorkspaceStaffOnboardingRetentionExecutionCoordinator
        CreateRetentionCoordinator() =>
        new(
            new FakeRetentionExecutionLock(),
            new FakeRetentionExecutionRepository(CreateRetentionExecution()),
            new TestScopeContext());

    private static WorkspaceStaffOnboardingRetentionExecution
        CreateRetentionExecution() =>
        WorkspaceStaffOnboardingRetentionExecution.Start(
            RetentionExecutionId,
            WorkspaceStaffOnboardingTests.OrganizationId.ToString("D"),
            WorkspaceStaffOnboardingRetentionCoordinates.DataClassKey,
            WorkspaceStaffOnboardingRetentionCoordinates.ExecutionPolicyVersion,
            attempt: 1,
            Now.AddMinutes(-1),
            Now.AddMinutes(9)).Value;

    private static WorkspaceStaffAccessPlan CreatePlan(
        WorkspaceStaffOnboarding application,
        DateTimeOffset sourceExpiredAtUtc,
        bool active)
    {
        WorkspaceStaffAccessPlan plan = WorkspaceStaffAccessPlan.Create(
            application.SourceId,
            application.ScopeId,
            application.SourceKind,
            Guid.NewGuid(),
            "front-desk",
            [],
            WorkspaceStaffOnboardingTests.SubjectId,
            Now.AddDays(-1)).Value;
        if (active)
        {
            Assert.True(plan.Activate(Now.AddDays(-1).AddSeconds(1)).IsSuccess);
        }

        Assert.True(plan.ObserveSourceExpired(sourceExpiredAtUtc, Now.AddHours(-2)).IsSuccess);
        return plan;
    }

    private static OrganizationEnrollmentClaimDto CreateClaim(
        WorkspaceStaffOnboarding application,
        OrganizationEnrollmentClaimStatus status) =>
        new(
            Guid.NewGuid(),
            application.SourceId,
            WorkspaceStaffOnboardingTests.OrganizationId,
            application.SubjectId,
            status,
            status == OrganizationEnrollmentClaimStatus.Accepted
                ? Guid.NewGuid()
                : null,
            1,
            Now.AddHours(-4),
            Now.AddHours(-3));

    private static readonly DateTimeOffset Now =
        WorkspaceStaffOnboardingTests.Now.AddDays(7);
    private static readonly Guid RetentionExecutionId =
        Guid.Parse("10000000-0000-0000-0000-000000000001");

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId =>
            WorkspaceStaffOnboardingTests.OrganizationId.ToString("D");
    }

    private sealed class FakeRetentionExecutionLock
        : IWorkspaceStaffOnboardingRetentionExecutionLock
    {
        public Task AcquireAsync(
            string tenantId,
            Guid executionId,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeRetentionExecutionRepository(
        WorkspaceStaffOnboardingRetentionExecution execution)
        : IWorkspaceStaffOnboardingRetentionExecutionRepository
    {
        public Task<WorkspaceStaffOnboardingRetentionExecution?> GetAsync(
            Guid executionId,
            CancellationToken cancellationToken) =>
            Task.FromResult<WorkspaceStaffOnboardingRetentionExecution?>(
                execution.Id == executionId ? execution : null);

        public Task AddAsync(
            WorkspaceStaffOnboardingRetentionExecution added,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class FakeClaimInspector(OrganizationEnrollmentClaimDto? claim)
        : IOrganizationEnrollmentClaimInspector
    {
        public int CallCount { get; private set; }

        public Task<OrganizationEnrollmentClaimDto?> FindAsync(
            Guid organizationId,
            Guid enrollmentLinkId,
            string subjectId,
            CancellationToken cancellationToken = default)
        {
            this.CallCount++;
            return Task.FromResult(claim);
        }
    }

    private sealed class FakeOnboardingRepository(params WorkspaceStaffOnboarding[] seed)
        : IWorkspaceStaffOnboardingRepository
    {
        private readonly List<WorkspaceStaffOnboarding> applications = [.. seed];

        public Task<WorkspaceStaffOnboardingCoordinate?> FindCoordinateAsync(
            Guid applicationId,
            CancellationToken cancellationToken)
        {
            WorkspaceStaffOnboarding? application = this.applications
                .SingleOrDefault(item => item.Id == applicationId);
            return Task.FromResult(application is null
                ? null
                : new WorkspaceStaffOnboardingCoordinate(
                    application.Id,
                    application.SourceKind,
                    application.SourceId));
        }

        public Task<WorkspaceStaffOnboarding?> GetAsync(
            Guid applicationId,
            CancellationToken cancellationToken) =>
            Task.FromResult(this.applications.SingleOrDefault(
                application => application.Id == applicationId));

        public Task<WorkspaceStaffOnboarding?> GetOperationalAsync(
            Guid applicationId,
            CancellationToken cancellationToken) =>
            this.GetAsync(applicationId, cancellationToken);

        public Task<WorkspaceStaffOnboarding?>
            GetOperationalBySourceAndSubjectAsync(
            WorkspaceStaffOnboardingSource sourceKind,
            Guid sourceId,
            string subjectId,
            CancellationToken cancellationToken) =>
            this.GetBySourceAndSubjectForLifecycleAsync(
                sourceKind,
                sourceId,
                subjectId,
                cancellationToken);

        public Task<WorkspaceStaffOnboarding?>
            GetBySourceAndSubjectForLifecycleAsync(
            WorkspaceStaffOnboardingSource sourceKind,
            Guid sourceId,
            string subjectId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(this.applications.SingleOrDefault(application =>
                application.SourceKind == sourceKind &&
                application.SourceId == sourceId &&
                string.Equals(
                    application.SubjectId,
                    subjectId,
                    StringComparison.Ordinal)));
        }

        public async Task<Guid?> FindIdBySourceAndSubjectAsync(
            WorkspaceStaffOnboardingSource sourceKind,
            Guid sourceId,
            string subjectId,
            CancellationToken cancellationToken) =>
            (await this.GetBySourceAndSubjectForLifecycleAsync(
                sourceKind,
                sourceId,
                subjectId,
                cancellationToken))?.Id;

        public Task<WorkspaceStaffOnboarding?> GetByClaimAsync(
            Guid claimId,
            CancellationToken cancellationToken) =>
            Task.FromResult(this.applications.SingleOrDefault(
                application => application.ClaimId == claimId));

        public Task<IReadOnlyList<WorkspaceStaffOnboarding>> ListActiveBySourceAsync(
            WorkspaceStaffOnboardingSource sourceKind,
            Guid sourceId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkspaceStaffOnboarding>>(
                this.applications.Where(application =>
                    application.SourceKind == sourceKind &&
                    application.SourceId == sourceId).ToArray());

        public Task<WorkspaceStaffOnboardingListResponse> ListActionableAsync(
            PageRequest page,
            CancellationToken cancellationToken) =>
            Task.FromResult(new WorkspaceStaffOnboardingListResponse(
                [],
                page.Page,
                page.PageSize,
                HasMore: false));

        public Task ReloadAsync(
            WorkspaceStaffOnboarding application,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task AddAsync(
            WorkspaceStaffOnboarding application,
            CancellationToken cancellationToken)
        {
            this.applications.Add(application);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRestrictionProjectionRepository(
        WorkspaceStaffOnboarding application)
        : IWorkspaceStaffOnboardingProcessingRestrictionProjectionRepository
    {
        private readonly WorkspaceStaffOnboardingProcessingRestrictionProjection
            projection =
                WorkspaceStaffOnboardingProcessingRestrictionProjection.Create(
                    application.ScopeId,
                    application.Id,
                    WorkspaceStaffOnboardingProcessingRestrictionContract
                        .CurrentVersion,
                    application.CreatedAtUtc).Value;

        public Task<
            WorkspaceStaffOnboardingProcessingRestrictionProjection?> GetAsync(
            Guid applicationId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                this.projection.ApplicationId == applicationId
                    ? this.projection
                    : null);

        public Task AddAsync(
            WorkspaceStaffOnboardingProcessingRestrictionProjection ignored,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class FakeOperationLock
        : IWorkspaceStaffOnboardingOperationLock
    {
        public Task AcquireSourceReadAsync(
            Guid sourceId,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AcquireSourceWriteAsync(
            Guid sourceId,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AcquireApplicantAsync(
            Guid sourceId,
            string subjectId,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> TryAcquireAsync(
            Guid applicationId,
            CancellationToken cancellationToken) =>
            Task.FromResult(true);
    }

    private sealed class FakeAccessPlanRepository(params WorkspaceStaffAccessPlan[] seed)
        : IWorkspaceStaffAccessPlanRepository
    {
        private readonly List<WorkspaceStaffAccessPlan> plans = [.. seed];

        public Task<WorkspaceStaffAccessPlan?> GetAsync(
            Guid sourceId,
            CancellationToken cancellationToken) =>
            Task.FromResult(this.plans.SingleOrDefault(plan => plan.Id == sourceId));

        public Task<IReadOnlyDictionary<Guid, WorkspaceStaffAccessPlan>> GetManyAsync(
            IReadOnlyCollection<Guid> sourceIds,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, WorkspaceStaffAccessPlan>>(
                this.plans
                    .Where(plan => sourceIds.Contains(plan.Id))
                    .ToDictionary(plan => plan.Id));

        public Task AddAsync(
            WorkspaceStaffAccessPlan plan,
            CancellationToken cancellationToken)
        {
            this.plans.Add(plan);
            return Task.CompletedTask;
        }
    }

    private sealed class MissingProfileProvisioner : IAccessProfileProvisioner
    {
        public Task<AccessProfileDto> EnsureProfileAsync(
            AccessScope ownerScope,
            AccessProfileDefinition definition,
            AccessSubject actor,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AccessProfileDto?> FindProfileByKeyAsync(
            AccessScope ownerScope,
            string key,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AccessProfileDto?>(null);

        public Task<AccessProfileAssignmentSet> GetSubjectAssignmentsAsync(
            AccessSubject subject,
            AccessScope ownerScope,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AccessProfileAssignmentReconciliation>
            ReconcileSubjectAssignmentsAsync(
                AccessSubject subject,
                AccessScope ownerScope,
                IReadOnlyCollection<Guid> profileIds,
                AccessSubject actor,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}

[Trait("Category", "Unit")]
public sealed class WorkspaceStaffOnboardingRetentionContributorTests
{
    [Fact]
    public async Task Contributor_processes_one_bounded_batch_and_reports_backlog()
    {
        WorkspaceStaffOnboardingRetentionCandidate[] candidates =
        [
            Candidate(1),
            Candidate(2),
            Candidate(3)
        ];
        FakeDispatcher dispatcher = new(
            candidates,
            WorkspaceStaffOnboardingRetentionOutcome.Expired,
            WorkspaceStaffOnboardingRetentionOutcome.Unchanged);
        WorkspaceStaffOnboardingRetentionContributor contributor = CreateContributor(
            dispatcher,
            new WorkspaceStaffOnboardingRetentionOptions { BatchSize = 2 });

        RetentionContributionResult result = await contributor.ExecuteAsync(
            Request(),
            CancellationToken.None);

        Assert.Equal(RetentionContributionStatus.Completed, result.Status);
        Assert.Equal(2, result.ScannedCount);
        Assert.Equal(1, result.AffectedCount);
        Assert.Equal(1, result.RemainingCount);
        Assert.Equal(
            WorkspaceStaffOnboardingRetentionCoordinates.BacklogOutcome,
            result.OutcomeCode);
        Assert.Equal(2, dispatcher.CallCount);
        Assert.Equal(3, dispatcher.MaximumCount);
    }

    [Fact]
    public async Task Contributor_surfaces_authority_lapse_as_a_failed_run()
    {
        WorkspaceStaffOnboardingRetentionContributor contributor = CreateContributor(
            new FakeDispatcher(
                [Candidate(1)],
                WorkspaceStaffOnboardingRetentionOutcome.AuthorityLapsed),
            new WorkspaceStaffOnboardingRetentionOptions());

        RetentionContributionResult result = await contributor.ExecuteAsync(
            Request(),
            CancellationToken.None);

        Assert.Equal(RetentionContributionStatus.Failed, result.Status);
        Assert.Equal(0, result.AffectedCount);
        Assert.Equal(1, result.RemainingCount);
        Assert.Equal(
            WorkspaceStaffOnboardingRetentionCoordinates.AuthorityLapsedOutcome,
            result.OutcomeCode);
        Assert.Equal(TimeSpan.FromHours(1), contributor.Schedule.Interval);
    }

    [Fact]
    public async Task Contributor_persists_only_the_candidates_reached_before_a_known_failure()
    {
        FakeDispatcher dispatcher = new(
            [Candidate(1), Candidate(2), Candidate(3)],
            WorkspaceStaffOnboardingRetentionOutcome.Expired)
        {
            ReconciliationFailure =
                WorkspaceStaffOnboardingApplicationErrors
                    .RetentionPlanInconsistent
        };
        WorkspaceStaffOnboardingRetentionContributor contributor =
            CreateContributor(
                dispatcher,
                new WorkspaceStaffOnboardingRetentionOptions
                {
                    BatchSize = 3
                });

        RetentionContributionResult result = await contributor.ExecuteAsync(
            Request(),
            CancellationToken.None);

        Assert.Equal(RetentionContributionStatus.Failed, result.Status);
        Assert.Equal(1, result.ScannedCount);
        Assert.Equal(0, result.AffectedCount);
        Assert.Equal(1, result.RemainingCount);
        Assert.Equal(
            WorkspaceStaffOnboardingRetentionCoordinates
                .ReconciliationFailedOutcome,
            result.OutcomeCode);
        Assert.Equal(1, dispatcher.CallCount);
    }

    private static WorkspaceStaffOnboardingRetentionContributor CreateContributor(
        FakeDispatcher dispatcher,
        WorkspaceStaffOnboardingRetentionOptions options) =>
        new(
            dispatcher,
            Options.Create(options),
            new FakeClock(),
            NullLogger<WorkspaceStaffOnboardingRetentionContributor>.Instance);

    private static WorkspaceStaffOnboardingRetentionCandidate Candidate(int suffix) =>
        new(
            Guid.Parse($"00000000-0000-0000-0000-{suffix:D12}"),
            suffix,
            Now.AddHours(-3));

    private static RetentionContributionRequest Request() =>
        new(
            RetentionExecutionContract.CurrentVersion,
            Guid.NewGuid(),
            WorkspaceStaffOnboardingTests.OrganizationId.ToString("D"),
            null,
            WorkspaceStaffOnboardingRetentionCoordinates.OwnerKey,
            WorkspaceStaffOnboardingRetentionCoordinates.DataClassKey,
            WorkspaceStaffOnboardingRetentionCoordinates.ExecutionPolicyVersion,
            1,
            Now.AddMinutes(-1),
            Now.AddMinutes(9));

    private static readonly DateTimeOffset Now =
        WorkspaceStaffOnboardingTests.Now.AddDays(7);

    private sealed class FakeClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class FakeDispatcher(
        IReadOnlyList<WorkspaceStaffOnboardingRetentionCandidate> candidates,
        params WorkspaceStaffOnboardingRetentionOutcome[] outcomes)
        : IRequestDispatcher
    {
        private readonly Queue<WorkspaceStaffOnboardingRetentionOutcome> outcomes =
            new(outcomes);

        public int CallCount { get; private set; }
        public int MaximumCount { get; private set; }
        public Error? ReconciliationFailure { get; init; }
        private int affectedCount;

        public Task<Result<TResponse>> SendAsync<TResponse>(
            ICommand<TResponse> command,
            CancellationToken cancellationToken = default)
        {
            if (command is
                    ReconcileWorkspaceStaffOnboardingRetentionCandidateCommand &&
                this.ReconciliationFailure is not null)
            {
                this.CallCount++;
                return Task.FromResult(
                    Result.Failure<TResponse>(
                        this.ReconciliationFailure));
            }

            object response = command switch
            {
                BeginWorkspaceStaffOnboardingRetentionExecutionCommand =>
                    new WorkspaceStaffOnboardingRetentionExecutionStart(
                        DispatchRequired: true,
                        ScannedCount: 0,
                        AffectedCount: 0,
                        CompletedResult: null),
                ListWorkspaceStaffOnboardingRetentionCandidatesCommand listed =>
                    this.List(listed),
                ReconcileWorkspaceStaffOnboardingRetentionCandidateCommand
                    reconciled => this.Reconcile(reconciled),
                CompleteWorkspaceStaffOnboardingRetentionExecutionCommand
                    completed => this.Complete(completed),
                _ => throw new InvalidOperationException(
                    $"Unexpected command {command.GetType().Name}.")
            };
            return Task.FromResult(Result.Success((TResponse)response));
        }

        public Task<Result<TResponse>> QueryAsync<TResponse>(
            IQuery<TResponse> query,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private WorkspaceStaffOnboardingRetentionCandidate[] List(
            ListWorkspaceStaffOnboardingRetentionCandidatesCommand command)
        {
            this.MaximumCount = command.MaximumCount;
            return candidates.Take(command.MaximumCount).ToArray();
        }

        private WorkspaceStaffOnboardingRetentionReconciliation Reconcile(
            ReconcileWorkspaceStaffOnboardingRetentionCandidateCommand command)
        {
            Assert.NotEqual(Guid.Empty, command.ExecutionId);
            Assert.Equal(1, command.Attempt);
            this.CallCount++;
            WorkspaceStaffOnboardingRetentionOutcome outcome =
                this.outcomes.Dequeue();
            bool affected = outcome is not (
                WorkspaceStaffOnboardingRetentionOutcome.Unchanged or
                WorkspaceStaffOnboardingRetentionOutcome.AuthorityLapsed);
            this.affectedCount += affected ? 1 : 0;
            return new(outcome, affected);
        }

        private RetentionContributionResult Complete(
            CompleteWorkspaceStaffOnboardingRetentionExecutionCommand command) =>
            new(
                RetentionExecutionContract.CurrentVersion,
                command.State ==
                    WorkspaceStaffOnboardingRetentionExecutionState.Completed
                        ? RetentionContributionStatus.Completed
                        : RetentionContributionStatus.Failed,
                command.ScannedCount,
                this.affectedCount,
                command.RemainingCount,
                command.OutcomeCode,
                command.CompletedAtUtc);
    }
}
