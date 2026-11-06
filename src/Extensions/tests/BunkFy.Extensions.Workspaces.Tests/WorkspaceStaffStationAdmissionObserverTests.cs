namespace BunkFy.Extensions.Workspaces.Tests;

using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.TimeZones;
using Gma.Framework.AccessControl;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Gma.Modules.AccessControl.Contracts;
using Gma.Modules.Auth.Contracts;
using Gma.Modules.Organizations.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using Status = BunkFy.Modules.Workspaces.Contracts.WorkspaceStaffStationObservationStatus;

[Trait("Category", "Unit")]
public sealed class WorkspaceStaffStationAdmissionObserverTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.Parse("aa000000-0000-0000-0000-000000000001");
    private static readonly Guid Property = Guid.Parse("bb000000-0000-0000-0000-000000000001");
    private static readonly Guid Member = Guid.Parse("cc000000-0000-0000-0000-000000000001");
    private static readonly Guid Account = Guid.Parse("dd000000-0000-0000-0000-000000000001");
    private static readonly string[] OutputProperties =
        ["CanonicalTimeZoneId", "CatalogVersion", "ObservedAtUtc", "PropertyLocalDate", "Reason", "Status", "TzdbVersion"];

    [Fact]
    public async Task Neutral_identity_rechecks_account_membership_and_owners_without_any_job_authority_read()
    {
        var f = new Fixture { Decision = AccessDecision.Denied("fixture.no-jobs"), ThrowAt = "access" };
        var result = await f.Create().ObserveAsync(f.ScopeId, Property, Member, WorkspaceStaffStationAction.StationIdentity);
        Assert.Equal(Status.LinkedAccountPrerequisitesObserved, result.Status);
        Assert.Equal(2, f.AccountReads);
        Assert.Equal(2, f.StaffReads);
        Assert.Equal(2, f.Calls.Count(x => x == "membership"));
        Assert.DoesNotContain("access", f.Calls);
        Assert.DoesNotContain("permission", f.Calls);
        Assert.Null(f.Requirement);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("membership")]
    [InlineData("workspace")]
    public async Task Neutral_identity_still_rejects_inactive_prerequisites(string missing)
    {
        var f = new Fixture();
        if (missing == "account")
        { f.ActiveAccount = false; }
        else if (missing == "membership")
        { f.Membership = null; }
        else
        { f.Workspace = WorkspaceOperationalAdmissionDecision.Restricted; }
        Assert.Equal(Status.PrerequisitesNotObserved,
            (await f.Create().ObserveAsync(f.ScopeId, Property, Member, WorkspaceStaffStationAction.StationIdentity)).Status);
        Assert.DoesNotContain("permission", f.Calls);
    }

    [Fact]
    public async Task Checkout_requests_only_exact_checkout_permission()
    {
        var f = new Fixture();
        Assert.Equal(Status.LinkedAccountPrerequisitesObserved,
            (await f.Create().ObserveAsync(f.ScopeId, Property, Member, WorkspaceStaffStationAction.ReservationCheckOut)).Status);
        Assert.Equal(ReservationsAdminPermissionCodes.CheckOut, f.Requirement!.Permission.Value);
        Assert.Equal(WorkspaceAccessScopes.CreateProperty(Tenant.ToString("D"), Property), f.Requirement.Scope);
        Assert.Equal(2, f.Calls.Count(x => x == "permission"));
    }

    [Theory]
    [InlineData(OrganizationMembershipRole.Owner)]
    [InlineData(OrganizationMembershipRole.Member)]
    public async Task Linked_owner_or_member_observes_only_exact_check_in_and_repeats_reads(OrganizationMembershipRole role)
    {
        var f = new Fixture();
        f.Membership = f.Membership! with { Membership = f.Membership.Membership! with { Role = role } };
        IWorkspaceStaffStationAdmissionObserver reader = f.Create();
        var result = await f.Read(reader);
        Assert.Equal(Status.LinkedAccountPrerequisitesObserved, result.Status);
        Assert.Equal(DateOnly.FromDateTime(Now.UtcDateTime), result.PropertyLocalDate);
        Assert.Equal("Etc/UTC", result.CanonicalTimeZoneId);
        Assert.Equal(TimeZoneCatalog.Default.CatalogVersion, result.CatalogVersion);
        Assert.Equal(TimeZoneCatalog.Default.TzdbVersion, result.TzdbVersion);
        Assert.Equal(OutputProperties, result.GetType().GetProperties().Select(p => p.Name).Order());
        Assert.Equal(2, f.AccountReads);
        Assert.Equal(2, f.StaffReads);
        Assert.Equal("auth-global", f.RequestedAuthScope);
        Assert.Equal(Account, f.RequestedAccount);
        Assert.Equal(ReservationsAdminPermissionCodes.CheckIn, f.Requirement!.Permission.Value);
        Assert.Equal(AccessSubject.User(Account.ToString("D")), f.Requirement.Subject);
        Assert.Equal(WorkspaceAccessScopes.CreateProperty(Tenant.ToString("D"), Property), f.Requirement.Scope);
        Assert.Equal("auth", f.Calls[2]);
        Assert.Equal("auth", f.Calls[^1]);
        await f.Read(reader);
        Assert.Equal(4, f.AccountReads);
        Assert.Equal(4, f.StaffReads);
    }

    [Theory]
    [InlineData("missing-staff", Status.PrerequisitesNotObserved)]
    [InlineData("missing-property", Status.PrerequisitesNotObserved)]
    [InlineData("suspended", Status.PrerequisitesNotObserved)]
    [InlineData("departed", Status.PrerequisitesNotObserved)]
    [InlineData("retired", Status.PrerequisitesNotObserved)]
    [InlineData("unassigned", Status.PrerequisitesNotObserved)]
    [InlineData("future", Status.PrerequisitesNotObserved)]
    [InlineData("restricted", Status.PrerequisitesNotObserved)]
    [InlineData("unlinked", Status.StationOnlyAccessUnsupported)]
    [InlineData("unknown-restriction", Status.Unavailable)]
    [InlineData("unsupported-restriction", Status.Unavailable)]
    [InlineData("malformed-link", Status.Unavailable)]
    [InlineData("ambiguous-link", Status.Unavailable)]
    [InlineData("ambiguous-assignment", Status.Unavailable)]
    [InlineData("unknown-property", Status.Unavailable)]
    [InlineData("wrong-property", Status.Unavailable)]
    [InlineData("wrong-scope", Status.Unavailable)]
    [InlineData("invalid-subject", Status.Unavailable)]
    [InlineData("invalid-zone", Status.Unavailable)]
    [InlineData("alias-zone", Status.Unavailable)]
    [InlineData("auth-inactive", Status.PrerequisitesNotObserved)]
    [InlineData("membership-missing", Status.PrerequisitesNotObserved)]
    [InlineData("membership-inactive", Status.PrerequisitesNotObserved)]
    [InlineData("organization-inactive", Status.PrerequisitesNotObserved)]
    [InlineData("scope-closed", Status.PrerequisitesNotObserved)]
    [InlineData("workspace-closed", Status.PrerequisitesNotObserved)]
    [InlineData("workspace-unavailable", Status.Unavailable)]
    [InlineData("permission-denied", Status.PrerequisitesNotObserved)]
    [InlineData("empty-denied", Status.PrerequisitesNotObserved)]
    [InlineData("access-closed", Status.PrerequisitesNotObserved)]
    [InlineData("access-stale", Status.ObservationStale)]
    [InlineData("access-unavailable", Status.Unavailable)]
    [InlineData("access-unsupported", Status.Unavailable)]
    [InlineData("expired", Status.PrerequisitesNotObserved)]
    public async Task Negative_unknown_and_station_only_results_are_distinct(string scenario, Status expected)
    {
        var f = new Fixture();
        switch (scenario)
        {
            case "missing-staff":
                f.Staff = null;
                break;
            case "missing-property":
                f.PropertyFacts = null;
                break;
            case "suspended":
                f.Staff = f.Staff! with { Status = StaffStatus.Suspended };
                break;
            case "departed":
                f.Staff = f.Staff! with { Status = StaffStatus.Departed };
                break;
            case "retired":
                f.PropertyFacts = f.PropertyFacts! with { Status = PropertyStatus.Retired };
                break;
            case "unassigned":
                f.Staff = f.Staff! with { AssignmentState = StaffStationAssignmentState.None, OpenAssignment = null };
                break;
            case "future":
                f.Staff = f.Staff! with { OpenAssignment = f.Staff.OpenAssignment! with { EffectiveFrom = new(2026, 9, 21) } };
                break;
            case "restricted":
                f.Staff = f.Staff! with { ProcessingRestriction = StaffProcessingRestrictionGateResult.Restricted(1, 1) };
                break;
            case "unlinked":
                f.Staff = f.Staff! with { AuthLinkState = StaffStationAuthLinkState.Unlinked, AuthSubjectId = null };
                break;
            case "unknown-restriction":
                f.Staff = f.Staff! with { ProcessingRestriction = StaffProcessingRestrictionGateResult.Unknown };
                break;
            case "unsupported-restriction":
                f.Staff = f.Staff! with { ProcessingRestriction = StaffProcessingRestrictionGateResult.Unsupported(99, 0) };
                break;
            case "malformed-link":
                f.Staff = f.Staff! with { AuthLinkState = StaffStationAuthLinkState.Malformed };
                break;
            case "ambiguous-link":
                f.Staff = f.Staff! with { AuthLinkState = StaffStationAuthLinkState.Ambiguous };
                break;
            case "ambiguous-assignment":
                f.Staff = f.Staff! with { AssignmentState = StaffStationAssignmentState.Ambiguous };
                break;
            case "unknown-property":
                f.PropertyFacts = f.PropertyFacts! with { Status = PropertyStatus.Unknown };
                break;
            case "wrong-property":
                f.Staff = f.Staff! with { PropertyId = Guid.NewGuid() };
                break;
            case "wrong-scope":
                f.PropertyFacts = f.PropertyFacts! with { ScopeId = Guid.NewGuid().ToString("D") };
                break;
            case "invalid-subject":
                f.Staff = f.Staff! with { AuthSubjectId = "not-an-auth-subject" };
                break;
            case "invalid-zone":
                f.PropertyFacts = f.PropertyFacts! with { TimeZoneId = "missing/zone" };
                break;
            case "alias-zone":
                f.PropertyFacts = f.PropertyFacts! with { TimeZoneId = "US/Eastern" };
                break;
            case "auth-inactive":
                f.ActiveAccount = false;
                break;
            case "membership-missing":
                f.Membership = f.Membership! with { Membership = null };
                break;
            case "membership-inactive":
                f.Membership = f.Membership! with { Membership = f.Membership.Membership! with { Status = OrganizationMembershipStatus.Suspended } };
                break;
            case "organization-inactive":
                f.Membership = f.Membership! with { OrganizationStatus = OrganizationStatus.Suspended };
                break;
            case "scope-closed":
                f.OrganizationScope = new(OrganizationScopeStatus.Closed, 1);
                break;
            case "workspace-closed":
                f.Workspace = WorkspaceOperationalAdmissionDecision.Restricted;
                break;
            case "workspace-unavailable":
                f.Workspace = WorkspaceOperationalAdmissionDecision.Unavailable;
                break;
            case "permission-denied":
                f.Decision = AccessDecision.Denied("fixture.denied");
                break;
            case "empty-denied":
                f.Access = f.Access with { RoleGrants = [] };
                f.Decision = AccessDecision.Denied("fixture.denied");
                break;
            case "access-closed":
                f.Access = f.Access with { Status = AccessControlAuthoritySnapshotStatus.Closed };
                break;
            case "access-stale":
                f.Access = f.Access with { Status = AccessControlAuthoritySnapshotStatus.Stale };
                break;
            case "access-unavailable":
                f.Access = f.Access with { Status = AccessControlAuthoritySnapshotStatus.Unavailable };
                break;
            case "access-unsupported":
                f.Access = f.Access with { ContractVersion = 99 };
                break;
            case "expired":
                f.Access = f.Access with { EarliestExpiryAtUtc = Now };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }
        Assert.Equal(expected, (await f.Read(f.Create())).Status);
        if (scenario == "unlinked")
        {
            Assert.Equal(0, f.AccountReads);
            Assert.Equal(2, f.StaffReads);
        }
    }

    [Theory]
    [InlineData("staff")]
    [InlineData("assignment")]
    [InlineData("restriction")]
    [InlineData("property")]
    [InlineData("membership")]
    [InlineData("organization")]
    [InlineData("access-permissions")]
    [InlineData("access-closed")]
    [InlineData("auth")]
    [InlineData("fence")]
    [InlineData("workspace")]
    [InlineData("midnight")]
    [InlineData("expiry")]
    public async Task Rereads_detect_changed_facts_even_without_owner_version_change(string scenario)
    {
        var f = new Fixture();
        f.OnSecond = source =>
        {
            switch (scenario, source)
            {
                case ("staff", "staff"):
                    f.Staff = f.Staff! with { Status = StaffStatus.Suspended };
                    break;
                case ("assignment", "staff"):
                    f.Staff = f.Staff! with { OpenAssignment = f.Staff.OpenAssignment! with { EffectiveFrom = new(2026, 9, 19) } };
                    break;
                case ("restriction", "staff"):
                    f.Staff = f.Staff! with { ProcessingRestriction = StaffProcessingRestrictionGateResult.Restricted(1, 0) };
                    break;
                case ("property", "property"):
                    f.PropertyFacts = f.PropertyFacts! with { TimeZoneId = "Europe/London" };
                    break;
                case ("membership", "membership"):
                    f.Membership = f.Membership! with { Membership = f.Membership.Membership! with { Role = OrganizationMembershipRole.Owner } };
                    break;
                case ("organization", "organization"):
                    f.OrganizationScope = new(OrganizationScopeStatus.Open, 2);
                    break;
                case ("access-permissions", "access"):
                    f.Access = f.Access with { RoleGrants = [f.Access.RoleGrants[0] with { Permissions = [] }] };
                    break;
                case ("access-closed", "access"):
                    f.Access = f.Access with { Status = AccessControlAuthoritySnapshotStatus.Closed };
                    break;
                case ("auth", "auth"):
                    f.ActiveAccount = false;
                    break;
                case ("fence", "fence"):
                    f.Fence = new(Guid.NewGuid(), Guid.NewGuid(), WorkspaceTerminationFenceState.Frozen, 1);
                    break;
                case ("workspace", "workspace"):
                    f.Workspace = WorkspaceOperationalAdmissionDecision.Restricted;
                    break;
                case ("midnight", "auth"):
                    f.Time = Now.AddDays(1);
                    break;
                case ("expiry", "auth"):
                    f.Time = Now.AddMinutes(2);
                    break;
                default:
                    break;
            }
        };
        if (scenario == "expiry")
        {
            f.Access = f.Access with { EarliestExpiryAtUtc = Now.AddMinutes(1) };
        }
        Assert.Equal(Status.ObservationStale, (await f.Read(f.Create())).Status);
    }

    [Fact]
    public async Task A_cached_first_authority_read_is_unavailable_even_when_second_read_is_fresh()
    {
        var f = new Fixture();
        f.Access = f.Access with { ObservedAtUtc = Now.AddSeconds(-1) };
        f.OnSecond = source =>
        {
            if (source == "access")
            {
                f.Access = f.Access with { ObservedAtUtc = Now };
            }
        };
        Assert.Equal(Status.Unavailable, (await f.Read(f.Create())).Status);
    }

    [Fact]
    public async Task Equivalent_reloaded_scope_instances_are_not_authority_drift()
    {
        var f = new Fixture();
        f.OnSecond = source =>
        {
            if (source == "access")
            {
                var scope = WorkspaceAccessScopes.CreateProperty(Tenant.ToString("D"), Property);
                f.Access = f.Access with
                {
                    RootScope = scope,
                    ExpectedTargetScopes = [scope],
                    RoleGrants = [f.Access.RoleGrants[0] with { Scope = scope }]
                };
            }
        };
        Assert.Equal(Status.LinkedAccountPrerequisitesObserved, (await f.Read(f.Create())).Status);
    }

    [Fact]
    public async Task Caller_cancellation_during_owner_reads_is_not_translated_to_unavailable()
    {
        using var cancel = new CancellationTokenSource();
        var f = new Fixture
        {
            OnSecond = source =>
            {
                if (source == "staff")
                {
                    cancel.Cancel();
                    cancel.Token.ThrowIfCancellationRequested();
                }
            }
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Create().ObserveAsync(Tenant.ToString("D"), Property, Member,
            WorkspaceStaffStationAction.ReservationCheckIn, cancel.Token));
    }

    [Theory]
    [InlineData("2026-09-20T03:59:59Z", 19, Status.PrerequisitesNotObserved)]
    [InlineData("2026-09-20T04:00:00Z", 20, Status.LinkedAccountPrerequisitesObserved)]
    [InlineData("2026-11-01T05:30:00Z", 1, Status.LinkedAccountPrerequisitesObserved)]
    [InlineData("2026-11-01T06:30:00Z", 1, Status.LinkedAccountPrerequisitesObserved)]
    public async Task Assignment_uses_pinned_property_date_at_midnight_and_DST(string instant, int day, Status expected)
    {
        var f = new Fixture { Time = DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture) };
        f.PropertyFacts = f.PropertyFacts! with { TimeZoneId = "America/New_York" };
        f.Access = f.Access with { ObservedAtUtc = f.Time };
        var result = await f.Read(f.Create());
        Assert.Equal(day, result.PropertyLocalDate!.Value.Day);
        Assert.Equal(expected, result.Status);
    }

    [Theory]
    [InlineData("staff")]
    [InlineData("property")]
    [InlineData("auth")]
    [InlineData("membership")]
    [InlineData("organization")]
    [InlineData("access")]
    [InlineData("permission")]
    [InlineData("workspace")]
    [InlineData("fence")]
    public async Task Owner_failures_return_only_bounded_unavailable(string owner)
    {
        var f = new Fixture { ThrowAt = owner };
        var result = await f.Read(f.Create());
        Assert.Equal(Status.Unavailable, result.Status);
        Assert.DoesNotContain("PRIVATE", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_request_is_rejected_before_owner_reads_and_cancellation_propagates()
    {
        var f = new Fixture();
        var reader = f.Create();
        Assert.Equal(Status.PrerequisitesNotObserved, (await reader.ObserveAsync(Tenant.ToString("D"), Property, Member, (WorkspaceStaffStationAction)99)).Status);
        Assert.Equal(Status.PrerequisitesNotObserved, (await reader.ObserveAsync(Guid.NewGuid().ToString("D"), Property, Member, WorkspaceStaffStationAction.ReservationCheckIn)).Status);
        f.IsEnabled = false;
        Assert.Equal(Status.PrerequisitesNotObserved, (await f.Read(reader)).Status);
        Assert.Empty(f.Calls);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ObserveAsync(Tenant.ToString("D"), Property, Member,
            WorkspaceStaffStationAction.ReservationCheckIn, cancel.Token));
    }

    [Fact]
    public async Task Actual_effective_service_preserves_deny_wins_even_with_stable_grants()
    {
        var f = new Fixture();
        ServiceCollection services = new();
        services.AddGmaAccessControl();
        services.AddSingleton<IAccessDecisionProvider>(new DecisionProvider(AccessDecision.Allowed()));
        services.AddSingleton<IAccessDecisionProvider>(new DecisionProvider(AccessDecision.Denied("fixture.revoked")));
        using var provider = services.BuildServiceProvider();
        var result = await f.Read(f.Create(provider.GetRequiredService<IAccessAuthorizationService>()));
        Assert.Equal(Status.PrerequisitesNotObserved, result.Status);
    }

    private sealed class DecisionProvider(AccessDecision decision) : IAccessDecisionProvider
    {
        public Task<AccessDecision> DecideAsync(AccessRequirement requirement, CancellationToken cancellationToken) => Task.FromResult(decision);
    }

    private sealed class Fixture : IScopeContext, ISystemClock, IStaffStationEligibilitySource, IPropertyStationEligibilitySource,
        IAuthMemberAdmissionReader, IOrganizationMembershipReader, IOrganizationScopeLifecycle, IAccessAuthorizationService,
        IAccessControlSubjectAuthoritySnapshotReader, IWorkspaceOperationalAdmissionPolicy, IWorkspaceTerminationFenceReader
    {
        private readonly Dictionary<string, int> counts = [];
        public bool IsEnabled { get; set; } = true;
        public string ScopeId => Tenant.ToString("D");
        public DateTimeOffset Time { get; set; } = Now;
        public DateTimeOffset UtcNow => this.Time;
        public StaffStationEligibilitySnapshot? Staff { get; set; } = new(Tenant.ToString("D"), Property, Member, StaffStatus.Active, 2,
            StaffStationAuthLinkState.Linked, Account.ToString("D"), StaffStationAssignmentState.Open,
            new(Guid.NewGuid(), Property, 2, new(2026, 9, 20)), StaffProcessingRestrictionGateResult.Allowed(1, 0));
        public PropertyStationEligibilitySnapshot? PropertyFacts { get; set; } = new(Tenant.ToString("D"), Property, PropertyStatus.Active, 1,
            PropertyProcessingStatus.Unconfigured, "Etc/UTC");
        public bool ActiveAccount { get; set; } = true;
        public OrganizationMembershipSnapshotDto? Membership { get; set; } = new(Tenant, OrganizationStatus.Active,
            new(Guid.NewGuid(), Tenant, Account.ToString("D"), OrganizationMembershipRole.Member, OrganizationMembershipStatus.Active, 1, Now, Now));
        public OrganizationScopeSnapshot OrganizationScope { get; set; } = new(OrganizationScopeStatus.Open, 1);
        public WorkspaceOperationalAdmissionDecision Workspace { get; set; } = WorkspaceOperationalAdmissionDecision.Allowed;
        public WorkspaceTerminationFenceSnapshot? Fence { get; set; }
        public AccessDecision Decision { get; set; } = AccessDecision.Allowed();
        public AccessControlSubjectAuthoritySnapshot Access { get; set; } = new(AccessControlAuthoritySnapshotStatus.Stable, 2,
            AccessSubjectKind.User, Account.ToString("D"), WorkspaceAccessScopes.CreateProperty(Tenant.ToString("D"), Property),
            [WorkspaceAccessScopes.CreateProperty(Tenant.ToString("D"), Property)], 1, Now, null, new string('a', 64),
            [new(Guid.NewGuid(), Guid.NewGuid(), "operator", [ReservationsAdminPermissionCodes.CheckIn],
                WorkspaceAccessScopes.CreateProperty(Tenant.ToString("D"), Property), null)], []);
        public string? ThrowAt { get; set; }
        public Action<string>? OnSecond { get; set; }
        public List<string> Calls { get; } = [];
        public int AccountReads => this.counts.GetValueOrDefault("auth");
        public int StaffReads => this.counts.GetValueOrDefault("staff");
        public string? RequestedAuthScope { get; private set; }
        public Guid RequestedAccount { get; private set; }
        public AccessRequirement? Requirement { get; private set; }
        public WorkspaceStaffStationAdmissionObserver Create(IAccessAuthorizationService? service = null) => new(this, this, this,
            this, this, this, service ?? this, this, this, this, Options.Create(new BunkFyWorkspacesOptions { GlobalAuthScopeId = "auth-global" }), this);
        public Task<WorkspaceStaffStationObservation> Read(IWorkspaceStaffStationAdmissionObserver reader) =>
            reader.ObserveAsync(this.ScopeId, Property, Member, WorkspaceStaffStationAction.ReservationCheckIn);
        private void Call(string name)
        {
            this.Calls.Add(name);
            this.counts[name] = this.counts.GetValueOrDefault(name) + 1;
            if (name == this.ThrowAt)
            {
                throw new InvalidOperationException("PRIVATE provider detail");
            }
            if (this.counts[name] % 2 == 0)
            {
                this.OnSecond?.Invoke(name);
            }
        }
        public Task<StaffStationEligibilitySnapshot?> FindAsync(string scopeId, Guid propertyId, Guid staffMemberId, CancellationToken cancellationToken = default)
        { this.Call("staff"); return Task.FromResult(this.Staff); }
        public Task<PropertyStationEligibilitySnapshot?> FindAsync(string scopeId, Guid propertyId, CancellationToken cancellationToken = default)
        { this.Call("property"); return Task.FromResult(this.PropertyFacts); }
        public ValueTask<AuthMemberAdmission?> FindActiveAsync(string scopeId, Guid memberId, CancellationToken cancellationToken = default)
        { this.Call("auth"); this.RequestedAuthScope = scopeId; this.RequestedAccount = memberId; return ValueTask.FromResult(this.ActiveAccount ? new AuthMemberAdmission("PRIVATE-email@example.test") : null); }
        public Task<OrganizationMembershipSnapshotDto?> FindAsync(Guid organizationId, string subjectId, CancellationToken cancellationToken = default)
        { this.Call("membership"); return Task.FromResult(this.Membership); }
        public Task<OrganizationScopeSnapshot> GetSnapshotAsync(Guid organizationId, CancellationToken cancellationToken)
        { this.Call("organization"); return Task.FromResult(this.OrganizationScope); }
        public Task<OrganizationScopeExportPage> ExportAsync(OrganizationScopeExportRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<OrganizationScopeDestroyResult> DestroyBatchAsync(OrganizationScopeDestroyRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AccessDecision> AuthorizeAsync(AccessRequirement requirement, CancellationToken cancellationToken)
        { this.Call("permission"); this.Requirement = requirement; return Task.FromResult(this.Decision); }
        public Task<AccessControlSubjectAuthoritySnapshot> ReadAsync(AccessControlSubjectAuthoritySnapshotRequest request, CancellationToken cancellationToken = default)
        { this.Call("access"); return Task.FromResult(this.Access); }
        public ValueTask<WorkspaceOperationalAdmissionDecision> EvaluateAsync(string tenantId, CancellationToken cancellationToken = default)
        { this.Call("workspace"); return ValueTask.FromResult(this.Workspace); }
        public Task<WorkspaceTerminationFenceSnapshot?> GetCurrentAsync(CancellationToken cancellationToken = default)
        { this.Call("fence"); return Task.FromResult(this.Fence); }
    }
}
