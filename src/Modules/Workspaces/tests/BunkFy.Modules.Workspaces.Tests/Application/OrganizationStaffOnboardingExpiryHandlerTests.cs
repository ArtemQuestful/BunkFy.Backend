namespace BunkFy.Modules.Workspaces.Tests;

using BunkFy.Modules.Workspaces.Application.Handlers;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Pagination;
using Gma.Framework.Runtime.Time;
using Gma.Modules.Organizations.Contracts;
using Xunit;

[Trait("Category", "Unit")]
public sealed class OrganizationStaffOnboardingExpiryHandlerTests
{
    [Fact]
    public async Task Invitation_expiry_terminates_the_application_and_access_plan()
    {
        WorkspaceStaffOnboarding application = WorkspaceStaffOnboardingTests.CreateApplication(
            WorkspaceStaffOnboardingSource.Invitation);
        WorkspaceStaffAccessPlan plan = CreateActivePlan(
            WorkspaceStaffOnboardingSource.Invitation,
            application.SourceId);
        FakeOnboardingRepository applications = new(application);
        OrganizationInvitationExpiredStaffOnboardingHandler handler = new(
            applications,
            new FakeAccessPlanRepository(plan),
            WorkspaceStaffOnboardingMutationTestSupport.Create(applications),
            new FakeClock());

        await handler.HandleAsync(
            new OrganizationInvitationExpiredIntegrationEvent(
                Guid.NewGuid(),
                Now.AddMinutes(1),
                ScopeId,
                OrganizationId,
                application.SourceId,
                Now,
                2),
            CancellationToken.None);

        Assert.Equal(WorkspaceStaffOnboardingState.Expired, application.Status);
        Assert.Equal(WorkspaceStaffAccessPlanState.Expired, plan.Status);
        Assert.Equal(Now, plan.SourceExpiredAtUtc);
        Assert.Null(application.DisplayName);
        Assert.Null(application.WorkEmail);
    }

    [Fact]
    public async Task Claim_expiry_after_link_expiry_terminates_the_application_and_plan_once()
    {
        WorkspaceStaffOnboarding application = WorkspaceStaffOnboardingTests.CreateApplication();
        Guid claimId = Guid.NewGuid();
        Assert.True(application.ObserveClaimRequested(claimId, 1, Now).IsSuccess);
        WorkspaceStaffAccessPlan plan = CreateActivePlan(
            WorkspaceStaffOnboardingSource.EnrollmentLink,
            application.SourceId);
        FakeOnboardingRepository applications = new(application);
        FakeAccessPlanRepository plans = new(plan);
        FakeWorkspaceStaffDeferredClaimWithdrawalRepository deferred = new(
            WorkspaceStaffDeferredClaimWithdrawal.Create(
                ScopeId,
                OrganizationId,
                application.SourceId,
                Guid.NewGuid(),
                2,
                Guid.NewGuid(),
                Now).Value);
        OrganizationEnrollmentLinkExpiredStaffOnboardingHandler linkHandler = new(
            applications,
            plans,
            deferred,
            WorkspaceStaffOnboardingMutationTestSupport.Create(applications),
            new FakeClock());
        await linkHandler.HandleAsync(
            new OrganizationEnrollmentLinkExpiredIntegrationEvent(
                Guid.NewGuid(),
                Now.AddMinutes(1),
                ScopeId,
                OrganizationId,
                application.SourceId,
                Now,
                2),
            CancellationToken.None);
        Assert.Single(deferred.Items);

        OrganizationEnrollmentClaimExpiredStaffOnboardingHandler handler = new(
            applications,
            plans,
            deferred,
            WorkspaceStaffOnboardingMutationTestSupport.Create(applications),
            new FakeClock());
        OrganizationEnrollmentClaimExpiredIntegrationEvent integrationEvent = new(
            Guid.NewGuid(),
            Now.AddMinutes(1),
            ScopeId,
            OrganizationId,
            application.SourceId,
            claimId,
            Now,
            2);

        await handler.HandleAsync(integrationEvent, CancellationToken.None);
        long applicationVersion = application.Version;
        long planVersion = plan.Version;
        await handler.HandleAsync(integrationEvent, CancellationToken.None);

        Assert.Equal(WorkspaceStaffOnboardingState.Expired, application.Status);
        Assert.Equal(WorkspaceStaffAccessPlanState.Expired, plan.Status);
        Assert.Equal(Now, plan.SourceExpiredAtUtc);
        Assert.Equal(applicationVersion, application.Version);
        Assert.Equal(planVersion, plan.Version);
        Assert.Empty(deferred.Items);
        Assert.Null(application.VerifiedAccountEmail);
        Assert.Null(application.DisplayName);
    }

    [Fact]
    public async Task Claim_expiry_preserves_the_reusable_plan_while_its_link_is_active()
    {
        WorkspaceStaffOnboarding application = WorkspaceStaffOnboardingTests.CreateApplication();
        Guid claimId = Guid.NewGuid();
        Assert.True(application.ObserveClaimRequested(claimId, 1, Now).IsSuccess);
        WorkspaceStaffAccessPlan plan = CreateActivePlan(
            WorkspaceStaffOnboardingSource.EnrollmentLink,
            application.SourceId);
        FakeOnboardingRepository applications = new(application);
        OrganizationEnrollmentClaimExpiredStaffOnboardingHandler handler = new(
            applications,
            new FakeAccessPlanRepository(plan),
            new FakeWorkspaceStaffDeferredClaimWithdrawalRepository(),
            WorkspaceStaffOnboardingMutationTestSupport.Create(applications),
            new FakeClock());

        await handler.HandleAsync(
            new OrganizationEnrollmentClaimExpiredIntegrationEvent(
                Guid.NewGuid(),
                Now.AddMinutes(1),
                ScopeId,
                OrganizationId,
                application.SourceId,
                claimId,
                Now,
                2),
            CancellationToken.None);

        Assert.Equal(WorkspaceStaffOnboardingState.Expired, application.Status);
        Assert.Equal(WorkspaceStaffAccessPlanState.Active, plan.Status);
        Assert.Null(plan.SourceExpiredAtUtc);
    }

    [Fact]
    public async Task Claim_withdrawal_terminates_staging_once_and_preserves_a_reusable_plan()
    {
        WorkspaceStaffOnboarding application = WorkspaceStaffOnboardingTests.CreateApplication();
        Guid claimId = Guid.NewGuid();
        Assert.True(application.ObserveClaimRequested(claimId, 1, Now).IsSuccess);
        WorkspaceStaffAccessPlan plan = CreateActivePlan(
            WorkspaceStaffOnboardingSource.EnrollmentLink,
            application.SourceId);
        FakeOnboardingRepository applications = new(application);
        OrganizationEnrollmentClaimWithdrawnStaffOnboardingHandler handler = new(
            applications,
            new FakeAccessPlanRepository(plan),
            new FakeWorkspaceStaffDeferredClaimWithdrawalRepository(),
            WorkspaceStaffOnboardingMutationTestSupport.Create(applications),
            new FakeClock());
        OrganizationEnrollmentClaimWithdrawnIntegrationEvent integrationEvent = new(
            Guid.NewGuid(),
            Now.AddMinutes(1),
            ScopeId,
            OrganizationId,
            application.SourceId,
            claimId,
            2);

        await handler.HandleAsync(integrationEvent, CancellationToken.None);
        long applicationVersion = application.Version;
        await handler.HandleAsync(integrationEvent, CancellationToken.None);

        Assert.Equal(WorkspaceStaffOnboardingState.Withdrawn, application.Status);
        Assert.Equal(applicationVersion, application.Version);
        Assert.Equal(WorkspaceStaffAccessPlanState.Active, plan.Status);
        Assert.Null(plan.SourceExpiredAtUtc);
        Assert.Null(application.VerifiedAccountEmail);
        Assert.Null(application.DisplayName);
    }

    [Fact]
    public async Task Unowned_claim_withdrawal_without_product_state_is_acknowledged()
    {
        FakeOnboardingRepository applications = new();
        OrganizationEnrollmentClaimWithdrawnStaffOnboardingHandler handler = new(
            applications,
            new FakeAccessPlanRepository(),
            new FakeWorkspaceStaffDeferredClaimWithdrawalRepository(),
            WorkspaceStaffOnboardingMutationTestSupport.Create(applications),
            new FakeClock());

        await handler.HandleAsync(
            new OrganizationEnrollmentClaimWithdrawnIntegrationEvent(
                Guid.NewGuid(),
                Now.AddMinutes(1),
                ScopeId,
                OrganizationId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                2),
            CancellationToken.None);
    }

    [Fact]
    public async Task Withdrawal_before_requested_is_durable_exactly_replayable_and_timestamp_stable()
    {
        WorkspaceStaffOnboarding application = WorkspaceStaffOnboardingTests.CreateApplication();
        WorkspaceStaffAccessPlan plan = CreateActivePlan(
            WorkspaceStaffOnboardingSource.EnrollmentLink,
            application.SourceId);
        FakeOnboardingRepository applications = new(application);
        FakeWorkspaceStaffDeferredClaimWithdrawalRepository deferred = new();
        OrganizationEnrollmentClaimWithdrawnStaffOnboardingHandler handler = new(
            applications,
            new FakeAccessPlanRepository(plan),
            deferred,
            WorkspaceStaffOnboardingMutationTestSupport.Create(applications),
            new FakeClock());
        Guid claimId = Guid.NewGuid();
        Guid eventId = Guid.NewGuid();
        DateTimeOffset occurredAtUtc = Now.AddMinutes(1).AddTicks(1);
        OrganizationEnrollmentClaimWithdrawnIntegrationEvent integrationEvent = new(
            eventId,
            occurredAtUtc,
            ScopeId,
            OrganizationId,
            application.SourceId,
            claimId,
            2);

        await handler.HandleAsync(integrationEvent, CancellationToken.None);
        await handler.HandleAsync(integrationEvent, CancellationToken.None);

        WorkspaceStaffDeferredClaimWithdrawal persisted = Assert.Single(deferred.Items);
        Assert.Equal(Now.AddMinutes(1), persisted.OccurredAtUtc);
        Assert.True(persisted.Matches(
            ScopeId,
            OrganizationId,
            application.SourceId,
            claimId,
            2,
            eventId,
            occurredAtUtc));
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(
            new OrganizationEnrollmentClaimWithdrawnIntegrationEvent(
                Guid.NewGuid(),
                occurredAtUtc,
                ScopeId,
                OrganizationId,
                application.SourceId,
                claimId,
                2),
            CancellationToken.None));
        Assert.Single(deferred.Items);
    }

    [Fact]
    public async Task Late_withdrawal_does_not_recreate_state_for_a_terminal_plan()
    {
        Guid sourceId = Guid.NewGuid();
        WorkspaceStaffAccessPlan plan = CreateActivePlan(
            WorkspaceStaffOnboardingSource.EnrollmentLink,
            sourceId);
        Assert.True(plan.Supersede(Now.AddMinutes(1)).IsSuccess);
        FakeOnboardingRepository applications = new();
        FakeWorkspaceStaffDeferredClaimWithdrawalRepository deferred = new();
        OrganizationEnrollmentClaimWithdrawnStaffOnboardingHandler handler = new(
            applications,
            new FakeAccessPlanRepository(plan),
            deferred,
            WorkspaceStaffOnboardingMutationTestSupport.Create(applications),
            new FakeClock());

        await handler.HandleAsync(
            new OrganizationEnrollmentClaimWithdrawnIntegrationEvent(
                Guid.NewGuid(),
                Now.AddMinutes(2),
                ScopeId,
                OrganizationId,
                sourceId,
                Guid.NewGuid(),
                2),
            CancellationToken.None);

        Assert.Empty(deferred.Items);
    }

    [Fact]
    public async Task Terminal_plan_with_an_active_application_is_retried_as_an_invariant_breach()
    {
        WorkspaceStaffOnboarding application = WorkspaceStaffOnboardingTests.CreateApplication();
        WorkspaceStaffAccessPlan plan = CreateActivePlan(
            WorkspaceStaffOnboardingSource.EnrollmentLink,
            application.SourceId);
        Assert.True(plan.Supersede(Now.AddMinutes(1)).IsSuccess);
        FakeOnboardingRepository applications = new(application);
        OrganizationEnrollmentClaimWithdrawnStaffOnboardingHandler handler = new(
            applications,
            new FakeAccessPlanRepository(plan),
            new FakeWorkspaceStaffDeferredClaimWithdrawalRepository(),
            WorkspaceStaffOnboardingMutationTestSupport.Create(applications),
            new FakeClock());

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(
            new OrganizationEnrollmentClaimWithdrawnIntegrationEvent(
                Guid.NewGuid(),
                Now.AddMinutes(2),
                ScopeId,
                OrganizationId,
                application.SourceId,
                Guid.NewGuid(),
                2),
            CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Delayed_withdrawal_cleans_its_exact_fact_without_regressing_a_newer_terminal_state(
        bool expire)
    {
        WorkspaceStaffOnboarding application = WorkspaceStaffOnboardingTests.CreateApplication();
        Guid claimId = Guid.NewGuid();
        Assert.True(application.ObserveClaimRequested(claimId, 1, Now).IsSuccess);
        if (expire)
        {
            Assert.True(application.ObserveClaimExpired(
                claimId,
                2,
                Now.AddMinutes(1)).IsSuccess);
        }
        else
        {
            Assert.True(application.Supersede(Now.AddMinutes(1)).IsSuccess);
        }

        WorkspaceStaffOnboardingState expected = application.Status;
        long expectedVersion = application.Version;
        WorkspaceStaffAccessPlan plan = CreateActivePlan(
            WorkspaceStaffOnboardingSource.EnrollmentLink,
            application.SourceId);
        Guid eventId = Guid.NewGuid();
        DateTimeOffset occurredAtUtc = Now.AddMinutes(2);
        WorkspaceStaffDeferredClaimWithdrawal observed =
            WorkspaceStaffDeferredClaimWithdrawal.Create(
                ScopeId,
                OrganizationId,
                application.SourceId,
                claimId,
                3,
                eventId,
                occurredAtUtc).Value;
        FakeWorkspaceStaffDeferredClaimWithdrawalRepository deferred = new(observed);
        FakeOnboardingRepository applications = new(application);
        OrganizationEnrollmentClaimWithdrawnStaffOnboardingHandler handler = new(
            applications,
            new FakeAccessPlanRepository(plan),
            deferred,
            WorkspaceStaffOnboardingMutationTestSupport.Create(applications),
            new FakeClock());

        await handler.HandleAsync(
            new OrganizationEnrollmentClaimWithdrawnIntegrationEvent(
                eventId,
                occurredAtUtc,
                ScopeId,
                OrganizationId,
                application.SourceId,
                claimId,
                3),
            CancellationToken.None);

        Assert.Equal(expected, application.Status);
        Assert.Equal(expectedVersion, application.Version);
        Assert.Empty(deferred.Items);
    }

    [Fact]
    public async Task Unowned_claim_expiry_without_product_state_is_acknowledged()
    {
        FakeOnboardingRepository applications = new();
        OrganizationEnrollmentClaimExpiredStaffOnboardingHandler handler = new(
            applications,
            new FakeAccessPlanRepository(),
            new FakeWorkspaceStaffDeferredClaimWithdrawalRepository(),
            WorkspaceStaffOnboardingMutationTestSupport.Create(applications),
            new FakeClock());

        await handler.HandleAsync(
            new OrganizationEnrollmentClaimExpiredIntegrationEvent(
                Guid.NewGuid(),
                Now.AddMinutes(1),
                ScopeId,
                OrganizationId,
                Guid.NewGuid(),
                Guid.NewGuid(),
                Now,
                1),
            CancellationToken.None);
    }

    [Fact]
    public async Task Claim_withdrawal_with_unbound_application_and_missing_plan_is_retried()
    {
        WorkspaceStaffOnboarding application = WorkspaceStaffOnboardingTests.CreateApplication();
        FakeOnboardingRepository applications = new(application);
        OrganizationEnrollmentClaimWithdrawnStaffOnboardingHandler handler = new(
            applications,
            new FakeAccessPlanRepository(),
            new FakeWorkspaceStaffDeferredClaimWithdrawalRepository(),
            WorkspaceStaffOnboardingMutationTestSupport.Create(applications),
            new FakeClock());

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(
            new OrganizationEnrollmentClaimWithdrawnIntegrationEvent(
                Guid.NewGuid(),
                Now.AddMinutes(1),
                ScopeId,
                OrganizationId,
                application.SourceId,
                Guid.NewGuid(),
                2),
            CancellationToken.None));
    }

    [Fact]
    public async Task Claim_expiry_with_unbound_application_and_missing_plan_is_retried()
    {
        WorkspaceStaffOnboarding application = WorkspaceStaffOnboardingTests.CreateApplication();
        FakeOnboardingRepository applications = new(application);
        OrganizationEnrollmentClaimExpiredStaffOnboardingHandler handler = new(
            applications,
            new FakeAccessPlanRepository(),
            new FakeWorkspaceStaffDeferredClaimWithdrawalRepository(),
            WorkspaceStaffOnboardingMutationTestSupport.Create(applications),
            new FakeClock());

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(
            new OrganizationEnrollmentClaimExpiredIntegrationEvent(
                Guid.NewGuid(),
                Now.AddMinutes(1),
                ScopeId,
                OrganizationId,
                application.SourceId,
                Guid.NewGuid(),
                Now,
                1),
            CancellationToken.None));
    }

    [Fact]
    public async Task Link_expiry_preserves_a_plan_while_a_pending_claim_uses_it()
    {
        WorkspaceStaffOnboarding application = WorkspaceStaffOnboardingTests.CreateApplication();
        Assert.True(application.ObserveClaimRequested(Guid.NewGuid(), 1, Now).IsSuccess);
        WorkspaceStaffAccessPlan plan = CreateActivePlan(
            WorkspaceStaffOnboardingSource.EnrollmentLink,
            application.SourceId);
        FakeOnboardingRepository applications = new(application);
        OrganizationEnrollmentLinkExpiredStaffOnboardingHandler handler = new(
            applications,
            new FakeAccessPlanRepository(plan),
            new FakeWorkspaceStaffDeferredClaimWithdrawalRepository(),
            WorkspaceStaffOnboardingMutationTestSupport.Create(applications),
            new FakeClock());

        await handler.HandleAsync(
            new OrganizationEnrollmentLinkExpiredIntegrationEvent(
                Guid.NewGuid(),
                Now.AddMinutes(1),
                ScopeId,
                OrganizationId,
                application.SourceId,
                Now,
                2),
            CancellationToken.None);

        Assert.Equal(WorkspaceStaffOnboardingState.PendingApproval, application.Status);
        Assert.Equal(WorkspaceStaffAccessPlanState.Active, plan.Status);
        Assert.Equal(Now, plan.SourceExpiredAtUtc);
    }

    [Fact]
    public async Task Link_expiry_preserves_unbound_staging_for_an_out_of_order_claim_fact()
    {
        WorkspaceStaffOnboarding application = WorkspaceStaffOnboardingTests.CreateApplication();
        WorkspaceStaffAccessPlan plan = CreateActivePlan(
            WorkspaceStaffOnboardingSource.EnrollmentLink,
            application.SourceId);
        FakeOnboardingRepository applications = new(application);
        OrganizationEnrollmentLinkExpiredStaffOnboardingHandler handler = new(
            applications,
            new FakeAccessPlanRepository(plan),
            new FakeWorkspaceStaffDeferredClaimWithdrawalRepository(),
            WorkspaceStaffOnboardingMutationTestSupport.Create(applications),
            new FakeClock());

        await handler.HandleAsync(
            new OrganizationEnrollmentLinkExpiredIntegrationEvent(
                Guid.NewGuid(),
                Now.AddMinutes(1),
                ScopeId,
                OrganizationId,
                application.SourceId,
                Now,
                2),
            CancellationToken.None);
        Assert.True(application.ObserveClaimRequested(
            Guid.NewGuid(),
            1,
            Now.AddMinutes(2)).IsSuccess);

        Assert.Equal(WorkspaceStaffOnboardingState.PendingApproval, application.Status);
        Assert.Equal(WorkspaceStaffAccessPlanState.Active, plan.Status);
        Assert.Equal(Now, plan.SourceExpiredAtUtc);
    }

    [Fact]
    public async Task Link_expiry_terminates_a_plan_with_no_active_onboarding()
    {
        Guid sourceId = Guid.NewGuid();
        WorkspaceStaffAccessPlan plan = CreateActivePlan(
            WorkspaceStaffOnboardingSource.EnrollmentLink,
            sourceId);
        FakeOnboardingRepository applications = new();
        OrganizationEnrollmentLinkExpiredStaffOnboardingHandler handler = new(
            applications,
            new FakeAccessPlanRepository(plan),
            new FakeWorkspaceStaffDeferredClaimWithdrawalRepository(),
            WorkspaceStaffOnboardingMutationTestSupport.Create(applications),
            new FakeClock());

        await handler.HandleAsync(
            new OrganizationEnrollmentLinkExpiredIntegrationEvent(
                Guid.NewGuid(),
                Now.AddMinutes(1),
                ScopeId,
                OrganizationId,
                sourceId,
                Now,
                2),
            CancellationToken.None);

        Assert.Equal(WorkspaceStaffAccessPlanState.Expired, plan.Status);
        Assert.Equal(Now, plan.SourceExpiredAtUtc);
    }

    private static WorkspaceStaffAccessPlan CreateActivePlan(
        WorkspaceStaffOnboardingSource sourceKind,
        Guid sourceId)
    {
        WorkspaceStaffAccessPlan plan = WorkspaceStaffAccessPlan.Create(
            sourceId,
            ScopeId,
            sourceKind,
            Guid.NewGuid(),
            "front-desk",
            [],
            WorkspaceStaffOnboardingTests.SubjectId,
            Now).Value;
        Assert.True(plan.Activate(Now.AddSeconds(1)).IsSuccess);
        return plan;
    }

    private static readonly DateTimeOffset Now = WorkspaceStaffOnboardingTests.Now;
    private static readonly Guid OrganizationId = WorkspaceStaffOnboardingTests.OrganizationId;
    private static readonly string ScopeId = OrganizationId.ToString("D");

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
            Task.FromResult(this.applications.SingleOrDefault(item => item.Id == applicationId));

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
            return Task.FromResult(this.applications.SingleOrDefault(item =>
                item.SourceKind == sourceKind &&
                item.SourceId == sourceId &&
                string.Equals(item.SubjectId, subjectId, StringComparison.Ordinal)));
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
            Task.FromResult(this.applications.SingleOrDefault(item => item.ClaimId == claimId));

        public Task<IReadOnlyList<WorkspaceStaffOnboarding>> ListActiveBySourceAsync(
            WorkspaceStaffOnboardingSource sourceKind,
            Guid sourceId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<WorkspaceStaffOnboarding>>(this.applications
                .Where(item => item.SourceKind == sourceKind && item.SourceId == sourceId)
                .ToArray());

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

    private sealed class FakeAccessPlanRepository(params WorkspaceStaffAccessPlan[] seed)
        : IWorkspaceStaffAccessPlanRepository
    {
        private readonly List<WorkspaceStaffAccessPlan> plans = [.. seed];

        public Task<WorkspaceStaffAccessPlan?> GetAsync(
            Guid sourceId,
            CancellationToken cancellationToken) =>
            Task.FromResult(this.plans.SingleOrDefault(item => item.Id == sourceId));

        public Task<IReadOnlyDictionary<Guid, WorkspaceStaffAccessPlan>> GetManyAsync(
            IReadOnlyCollection<Guid> sourceIds,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, WorkspaceStaffAccessPlan>>(this.plans
                .Where(item => sourceIds.Contains(item.Id))
                .ToDictionary(item => item.Id));

        public Task AddAsync(
            WorkspaceStaffAccessPlan plan,
            CancellationToken cancellationToken)
        {
            this.plans.Add(plan);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now.AddMinutes(2);
    }
}
