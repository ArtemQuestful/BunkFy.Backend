namespace BunkFy.Modules.Workspaces.Tests;

using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.AccessControl;
using Gma.Framework.Pagination;
using Gma.Modules.AccessControl.Contracts;
using Gma.Modules.Organizations.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspaceOrganizationJoinSourceAuthorizationPolicyTests
{
    private static readonly Guid OrganizationId =
        Guid.Parse("1fa5e693-3996-4bb5-9755-d9abf26c8ca2");
    private static readonly DateTimeOffset Now =
        new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
    private const string SubjectId = "delegated-manager";

    [Fact]
    public async Task Active_plan_and_onboarding_authority_allow_exact_issue()
    {
        Guid sourceId = Guid.NewGuid();
        WorkspaceStaffAccessPlan plan = CreatePlan(
            sourceId,
            WorkspaceStaffOnboardingSource.Invitation,
            active: true);
        using TestContext test = CreateContext(
            WorkspaceOperationalAdmissionTestSupport.Allowed(ScopeId),
            AccessDecision.Allowed(),
            [plan]);

        OrganizationJoinSourceAuthorizationDecision decision =
            await test.Policy.EvaluateAsync(Context(
                OrganizationJoinSourceAuthorizationOperation.IssueInvitation,
                sourceId: sourceId));

        Assert.Equal(
            OrganizationJoinSourceAuthorizationDecision.Allowed,
            decision);
        Assert.Equal(OrganizationId, test.Scope.OrganizationId);
        Assert.Equal(1, test.Authorization.BatchCallCount);
        Assert.Equal(0, test.Authorization.SingleCallCount);
        Assert.Equal(2, test.Authorization.Requirements.Count);
        Assert.All(
            test.Authorization.Requirements,
            requirement => Assert.Equal(SubjectId, requirement.Subject.Id));
        Assert.Equal(
            [
                WorkspacesPermissionCodes.StaffOnboardingManage,
                AccessControlProfilePermissionCodes.Read
            ],
            test.Authorization.Requirements.Select(
                requirement => requirement.Permission.Value));
        Assert.All(
            test.Authorization.Requirements,
            requirement => Assert.Equal(
                WorkspaceAccessScopes.Create(ScopeId).Value,
                requirement.Scope.Value));
        Assert.Equal([sourceId], test.Plans.RequestedSourceIds);
    }

    [Fact]
    public async Task Permission_denial_stops_before_join_source_state()
    {
        using TestContext test = CreateContext(
            WorkspaceOperationalAdmissionTestSupport.Allowed(ScopeId),
            AccessDecision.Denied("not-assigned"));

        OrganizationJoinSourceAuthorizationDecision decision =
            await test.Policy.EvaluateAsync(Context(
                OrganizationJoinSourceAuthorizationOperation.IssueInvitation,
                sourceId: Guid.NewGuid()));

        Assert.Equal(
            OrganizationJoinSourceAuthorizationDecision.Denied,
            decision);
        Assert.Equal(1, test.Authorization.BatchCallCount);
        Assert.Equal(2, test.Authorization.Requirements.Count);
        Assert.Empty(test.Plans.RequestedSourceIds);
        Assert.Equal(0, test.Onboardings.GetByClaimCallCount);
    }

    [Fact]
    public async Task Incomplete_authorization_result_fails_closed()
    {
        using TestContext test = CreateContext(
            WorkspaceOperationalAdmissionTestSupport.Allowed(ScopeId),
            AccessDecision.Allowed(),
            incompleteAuthorizationResult: true);

        OrganizationJoinSourceAuthorizationDecision decision =
            await test.Policy.EvaluateAsync(Context(
                OrganizationJoinSourceAuthorizationOperation.ReadInvitations));

        Assert.Equal(
            OrganizationJoinSourceAuthorizationDecision.Denied,
            decision);
        Assert.Equal(1, test.Authorization.BatchCallCount);
        Assert.Empty(test.Plans.RequestedSourceIds);
    }

    [Theory]
    [InlineData(null, WorkspaceStaffOnboardingSource.Invitation, true)]
    [InlineData("prepared", WorkspaceStaffOnboardingSource.Invitation, false)]
    [InlineData("wrong-kind", WorkspaceStaffOnboardingSource.EnrollmentLink, true)]
    public async Task Issue_requires_exact_active_plan(
        string? sourceMode,
        WorkspaceStaffOnboardingSource sourceKind,
        bool active)
    {
        Guid? sourceId = sourceMode is null ? null : Guid.NewGuid();
        WorkspaceStaffAccessPlan[] plans = sourceId.HasValue
            ? [CreatePlan(sourceId.Value, sourceKind, active)]
            : [];
        using TestContext test = CreateContext(
            WorkspaceOperationalAdmissionTestSupport.Allowed(ScopeId),
            AccessDecision.Allowed(),
            plans);

        OrganizationJoinSourceAuthorizationDecision decision =
            await test.Policy.EvaluateAsync(Context(
                OrganizationJoinSourceAuthorizationOperation.IssueInvitation,
                sourceId: sourceId));

        Assert.Equal(
            OrganizationJoinSourceAuthorizationDecision.Denied,
            decision);
    }

    [Fact]
    public async Task Source_reads_allow_lists_and_require_a_matching_item_plan()
    {
        Guid sourceId = Guid.NewGuid();
        using TestContext test = CreateContext(
            WorkspaceOperationalAdmissionTestSupport.Allowed(ScopeId),
            AccessDecision.Allowed(),
            [CreatePlan(
                sourceId,
                WorkspaceStaffOnboardingSource.EnrollmentLink,
                active: false)]);

        OrganizationJoinSourceAuthorizationDecision listDecision =
            await test.Policy.EvaluateAsync(Context(
                OrganizationJoinSourceAuthorizationOperation.ReadEnrollmentLinks));
        OrganizationJoinSourceAuthorizationDecision itemDecision =
            await test.Policy.EvaluateAsync(Context(
                OrganizationJoinSourceAuthorizationOperation.ReadEnrollmentLinks,
                sourceId: sourceId));
        OrganizationJoinSourceAuthorizationDecision missingDecision =
            await test.Policy.EvaluateAsync(Context(
                OrganizationJoinSourceAuthorizationOperation.ReadEnrollmentLinks,
                sourceId: Guid.NewGuid()));

        Assert.Equal(
            OrganizationJoinSourceAuthorizationDecision.Allowed,
            listDecision);
        Assert.Equal(
            OrganizationJoinSourceAuthorizationDecision.Allowed,
            itemDecision);
        Assert.Equal(
            OrganizationJoinSourceAuthorizationDecision.Denied,
            missingDecision);
    }

    [Theory]
    [InlineData(OrganizationJoinSourceAuthorizationOperation.ReissueInvitation)]
    [InlineData(OrganizationJoinSourceAuthorizationOperation.RotateEnrollmentLink)]
    [InlineData(OrganizationJoinSourceAuthorizationOperation.ReadJoinRequests)]
    public async Task Generic_owner_only_operations_remain_not_applicable(
        OrganizationJoinSourceAuthorizationOperation operation)
    {
        using TestContext test = CreateContext(
            WorkspaceOperationalAdmissionTestSupport.Allowed(ScopeId),
            AccessDecision.Allowed());

        OrganizationJoinSourceAuthorizationDecision decision =
            await test.Policy.EvaluateAsync(Context(operation));

        Assert.Equal(
            OrganizationJoinSourceAuthorizationDecision.NotApplicable,
            decision);
    }

    [Fact]
    public async Task Resolve_requires_the_exact_pending_enrollment_claim()
    {
        Guid acceptedClaimId = Guid.NewGuid();
        Guid invitationClaimId = Guid.NewGuid();
        Guid submittedClaimId = Guid.NewGuid();
        WorkspaceStaffOnboarding accepted = CreateOnboarding(
            WorkspaceStaffOnboardingSource.EnrollmentLink,
            acceptedClaimId,
            pending: true);
        WorkspaceStaffOnboarding wrongKind = CreateOnboarding(
            WorkspaceStaffOnboardingSource.Invitation,
            invitationClaimId,
            pending: true);
        WorkspaceStaffOnboarding notPending = CreateOnboarding(
            WorkspaceStaffOnboardingSource.EnrollmentLink,
            submittedClaimId,
            pending: false);
        using TestContext test = CreateContext(
            WorkspaceOperationalAdmissionTestSupport.Allowed(ScopeId),
            AccessDecision.Allowed(),
            onboardings: new Dictionary<Guid, WorkspaceStaffOnboarding>
            {
                [acceptedClaimId] = accepted,
                [invitationClaimId] = wrongKind,
                [submittedClaimId] = notPending
            });

        OrganizationJoinSourceAuthorizationDecision acceptedDecision =
            await test.Policy.EvaluateAsync(Context(
                OrganizationJoinSourceAuthorizationOperation.ResolveJoinRequest,
                claimId: acceptedClaimId));
        OrganizationJoinSourceAuthorizationDecision wrongKindDecision =
            await test.Policy.EvaluateAsync(Context(
                OrganizationJoinSourceAuthorizationOperation.ResolveJoinRequest,
                claimId: invitationClaimId));
        OrganizationJoinSourceAuthorizationDecision notPendingDecision =
            await test.Policy.EvaluateAsync(Context(
                OrganizationJoinSourceAuthorizationOperation.ResolveJoinRequest,
                claimId: submittedClaimId));
        OrganizationJoinSourceAuthorizationDecision missingDecision =
            await test.Policy.EvaluateAsync(Context(
                OrganizationJoinSourceAuthorizationOperation.ResolveJoinRequest,
                claimId: Guid.NewGuid()));

        Assert.Equal(
            OrganizationJoinSourceAuthorizationDecision.Allowed,
            acceptedDecision);
        Assert.Equal(
            OrganizationJoinSourceAuthorizationDecision.Denied,
            wrongKindDecision);
        Assert.Equal(
            OrganizationJoinSourceAuthorizationDecision.Denied,
            notPendingDecision);
        Assert.Equal(
            OrganizationJoinSourceAuthorizationDecision.Denied,
            missingDecision);
    }

    [Theory]
    [InlineData(true, OrganizationJoinSourceAuthorizationDecision.Denied)]
    [InlineData(false, OrganizationJoinSourceAuthorizationDecision.Unavailable)]
    public async Task Non_operational_workspace_stops_before_permission_checks(
        bool restricted,
        OrganizationJoinSourceAuthorizationDecision expected)
    {
        WorkspaceOperationalAdmissionEvaluator admission = restricted
            ? WorkspaceOperationalAdmissionTestSupport.Restricted(ScopeId)
            : WorkspaceOperationalAdmissionTestSupport.Unavailable(ScopeId);
        using TestContext test = CreateContext(
            admission,
            AccessDecision.Allowed());

        OrganizationJoinSourceAuthorizationDecision decision =
            await test.Policy.EvaluateAsync(Context(
                OrganizationJoinSourceAuthorizationOperation.ReadInvitations));

        Assert.Equal(expected, decision);
        Assert.Empty(test.Authorization.Requirements);
    }

    private static string ScopeId => OrganizationId.ToString("D");

    private static OrganizationJoinSourceAuthorizationContext Context(
        OrganizationJoinSourceAuthorizationOperation operation,
        Guid? sourceId = null,
        Guid? claimId = null) => new(
            operation,
            OrganizationId,
            SubjectId,
            sourceId,
            claimId);

    private static TestContext CreateContext(
        WorkspaceOperationalAdmissionEvaluator admission,
        AccessDecision accessDecision,
        WorkspaceStaffAccessPlan[]? plans = null,
        IReadOnlyDictionary<Guid, WorkspaceStaffOnboarding>? onboardings = null,
        bool incompleteAuthorizationResult = false)
    {
        RecordingAuthorization authorization = new(
            accessDecision,
            incompleteAuthorizationResult);
        RecordingPlans planRepository = new(plans ?? []);
        RecordingOnboardings onboardingRepository = new(onboardings);
        ServiceCollection services = new();
        services.AddSingleton(admission);
        services.AddSingleton<IAccessAuthorizationService>(authorization);
        services.AddSingleton<IWorkspaceStaffAccessPlanRepository>(planRepository);
        services.AddSingleton<IWorkspaceStaffOnboardingRepository>(
            onboardingRepository);
        ServiceProvider provider = services.BuildServiceProvider();
        RecordingAuthoritativeScope scope = new(provider);
        WorkspaceOrganizationJoinSourceAuthorizationPolicy policy = new(scope);
        return new TestContext(
            provider,
            policy,
            scope,
            authorization,
            planRepository,
            onboardingRepository);
    }

    private static WorkspaceStaffAccessPlan CreatePlan(
        Guid sourceId,
        WorkspaceStaffOnboardingSource sourceKind,
        bool active)
    {
        WorkspaceStaffAccessPlan plan = WorkspaceStaffAccessPlan.Create(
            sourceId,
            ScopeId,
            sourceKind,
            Guid.NewGuid(),
            WorkspaceAccessProfileSeeds.FrontDeskKey,
            [],
            "workspace-owner",
            Now).Value;
        if (active)
        {
            plan.Activate(Now.AddMinutes(1));
        }

        return plan;
    }

    private static WorkspaceStaffOnboarding CreateOnboarding(
        WorkspaceStaffOnboardingSource sourceKind,
        Guid claimId,
        bool pending)
    {
        WorkspaceStaffOnboarding onboarding = WorkspaceStaffOnboarding.Create(
            Guid.NewGuid(),
            ScopeId,
            sourceKind,
            Guid.NewGuid(),
            "candidate",
            "candidate@example.test",
            "Candidate",
            null,
            null,
            null,
            null,
            null,
            null,
            Now).Value;
        if (pending)
        {
            onboarding.ObserveClaimRequested(
                claimId,
                claimVersion: 1,
                Now.AddMinutes(1));
        }

        return onboarding;
    }

    private sealed class TestContext(
        ServiceProvider provider,
        WorkspaceOrganizationJoinSourceAuthorizationPolicy policy,
        RecordingAuthoritativeScope scope,
        RecordingAuthorization authorization,
        RecordingPlans plans,
        RecordingOnboardings onboardings) : IDisposable
    {
        public WorkspaceOrganizationJoinSourceAuthorizationPolicy Policy => policy;
        public RecordingAuthoritativeScope Scope => scope;
        public RecordingAuthorization Authorization => authorization;
        public RecordingPlans Plans => plans;
        public RecordingOnboardings Onboardings => onboardings;

        public void Dispose() => provider.Dispose();
    }

    private sealed class RecordingAuthoritativeScope(
        IServiceProvider services) : IWorkspaceAuthoritativeScope
    {
        public Guid OrganizationId { get; private set; }

        public Task<TResult> RunAsync<TResult>(
            Guid organizationId,
            Func<IServiceProvider, Task<TResult>> operation)
        {
            this.OrganizationId = organizationId;
            return operation(services);
        }
    }

    private sealed class RecordingAuthorization(
        AccessDecision decision,
        bool incompleteResult)
        : IAccessAuthorizationService
    {
        public List<AccessRequirement> Requirements { get; } = [];
        public int SingleCallCount { get; private set; }
        public int BatchCallCount { get; private set; }

        public Task<AccessDecision> AuthorizeAsync(
            AccessRequirement requirement,
            CancellationToken cancellationToken)
        {
            this.SingleCallCount++;
            this.Requirements.Add(requirement);
            return Task.FromResult(decision);
        }

        public Task<IReadOnlyList<AccessDecision>> AuthorizeManyAsync(
            IReadOnlyList<AccessRequirement> requirements,
            CancellationToken cancellationToken)
        {
            this.BatchCallCount++;
            this.Requirements.AddRange(requirements);
            IReadOnlyList<AccessDecision> decisions = incompleteResult
                ? [decision]
                : requirements.Select(_ => decision).ToArray();
            return Task.FromResult(decisions);
        }
    }

    private sealed class RecordingPlans(
        params WorkspaceStaffAccessPlan[] seed)
        : IWorkspaceStaffAccessPlanRepository
    {
        private readonly IReadOnlyDictionary<Guid, WorkspaceStaffAccessPlan>
            plans = seed.ToDictionary(plan => plan.Id);

        public List<Guid> RequestedSourceIds { get; } = [];

        public Task<WorkspaceStaffAccessPlan?> GetAsync(
            Guid sourceId,
            CancellationToken cancellationToken)
        {
            this.RequestedSourceIds.Add(sourceId);
            return Task.FromResult(this.plans.GetValueOrDefault(sourceId));
        }

        public Task<IReadOnlyDictionary<Guid, WorkspaceStaffAccessPlan>>
            GetManyAsync(
                IReadOnlyCollection<Guid> sourceIds,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddAsync(
            WorkspaceStaffAccessPlan plan,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingOnboardings(
        IReadOnlyDictionary<Guid, WorkspaceStaffOnboarding>? seed)
        : IWorkspaceStaffOnboardingRepository
    {
        private readonly IReadOnlyDictionary<Guid, WorkspaceStaffOnboarding>
            onboardings = seed ??
                new Dictionary<Guid, WorkspaceStaffOnboarding>();

        public int GetByClaimCallCount { get; private set; }

        public Task<WorkspaceStaffOnboardingCoordinate?> FindCoordinateAsync(
            Guid applicationId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WorkspaceStaffOnboarding?> GetByClaimAsync(
            Guid claimId,
            CancellationToken cancellationToken)
        {
            this.GetByClaimCallCount++;
            return Task.FromResult(
                this.onboardings.GetValueOrDefault(claimId));
        }

        public Task<WorkspaceStaffOnboarding?> GetAsync(
            Guid applicationId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WorkspaceStaffOnboarding?> GetOperationalAsync(
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

        public Task<WorkspaceStaffOnboarding?>
            GetBySourceAndSubjectForLifecycleAsync(
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

        public Task ReloadAsync(
            WorkspaceStaffOnboarding application,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddAsync(
            WorkspaceStaffOnboarding application,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
