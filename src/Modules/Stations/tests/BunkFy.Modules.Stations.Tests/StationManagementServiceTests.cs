namespace BunkFy.Modules.Stations.Tests;

using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using BunkFy.Modules.Stations.Application;
using BunkFy.Modules.Stations.Contracts;
using BunkFy.Modules.Stations.Domain;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.AccessControl;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Gma.Framework.Security;
using Gma.Modules.AccessControl.Contracts;
using Gma.Modules.Auth.Contracts;
using Gma.Modules.Organizations.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

public sealed class StationManagementServiceTests
{
    private const string Tenant = "aa000000-0000-0000-0000-000000000001";
    private const string Subject = "cc000000-0000-0000-0000-000000000003";
    private static readonly Guid Session = Guid.Parse("dd000000-0000-0000-0000-000000000004");
    private static readonly Guid Property = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Self_uses_real_recent_assurance_and_current_owner_facts_without_manager_permission()
    {
        var owners = new Owners { PermitManager = false };
        var primary = Create(owners);
        var result = await primary.SelfAsync(Principal(), Subject, Property);
        Assert.Equal(StationAdmissionState.Current, result.State);
        Assert.Equal(StationIssuerKind.Self, result.Issuer!.Kind);
        Assert.Equal(Session, result.Issuer.SessionId);
        Assert.Equal(0, owners.Decisions);
        Assert.Equal(2, owners.AccountReads);
        Assert.Equal(StationAdmissionState.Denied, (await primary.ManagerAsync(Principal(), null, true)).State);
        Assert.Equal(2, owners.Decisions);
        Assert.Equal(StationAdmissionState.Denied, (await primary.SelfAsync(Principal(), Guid.NewGuid().ToString("D"), Property)).State);
    }

    [Theory]
    [InlineData("missing-session")]
    [InlineData("duplicate-session")]
    [InlineData("empty-session")]
    [InlineData("invalid-session")]
    [InlineData("noncanonical-session")]
    [InlineData("duplicate-subject")]
    [InlineData("duplicate-acr")]
    [InlineData("duplicate-time")]
    [InlineData("future-time")]
    [InlineData("old-time")]
    [InlineData("weak-acr")]
    [InlineData("missing-time")]
    [InlineData("mixed-identities")]
    public async Task Ambiguous_missing_or_unassured_primary_is_denied_before_owner_access(string scenario)
    {
        var principal = Principal();
        var identity = Assert.IsType<ClaimsIdentity>(principal.Identity);
        switch (scenario)
        {
            case "missing-session":
                identity.RemoveClaim(identity.FindFirst("sid")!);
                break;
            case "duplicate-session":
                identity.AddClaim(new("sid", Session.ToString("D")));
                break;
            case "empty-session":
                Replace("sid", Guid.Empty.ToString("D"));
                break;
            case "invalid-session":
                Replace("sid", "not-a-session");
                break;
            case "noncanonical-session":
                Replace("sid", Session.ToString("D").ToUpperInvariant());
                break;
            case "duplicate-subject":
                identity.AddClaim(new("sub", Subject));
                break;
            case "duplicate-acr":
                identity.AddClaim(new("acr", "urn:gma:acr:mfa"));
                break;
            case "duplicate-time":
                identity.AddClaim(new("auth_time", Now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
                break;
            case "future-time":
                Replace("auth_time", Now.AddMinutes(5).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
                break;
            case "old-time":
                Replace("auth_time", Now.AddMinutes(-11).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
                break;
            case "weak-acr":
                Replace("acr", "urn:gma:acr:password");
                break;
            case "missing-time":
                identity.RemoveClaim(identity.FindFirst("auth_time")!);
                break;
            case "mixed-identities":
                principal.AddIdentity(new ClaimsIdentity([new Claim("sub", Subject)], "other"));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }
        var owners = new Owners();
        Assert.Equal(StationAdmissionState.Denied, (await Create(owners).ManagerAsync(principal, Property, true)).State);
        Assert.Equal(0, owners.AccountReads);
        void Replace(string type, string value)
        { identity.RemoveClaim(identity.FindFirst(type)!); identity.AddClaim(new(type, value)); }
    }

    [Fact]
    public async Task Manager_permission_is_requested_at_exact_property_or_tenant_root_and_list_needs_no_step_up()
    {
        var owners = new Owners();
        var primary = Create(owners);
        Assert.Equal(StationAdmissionState.Current, (await primary.ManagerAsync(Principal(), Property, true)).State);
        Assert.Equal(WorkspaceAccessScopes.CreateProperty(Tenant, Property), owners.LastTarget);
        Assert.Equal(StationAdmissionState.Current, (await primary.ManagerAsync(Principal(), null, true)).State);
        Assert.Equal(WorkspaceAccessScopes.Create(Tenant), owners.LastTarget);
        var old = Principal();
        var identity = (ClaimsIdentity)old.Identity!;
        identity.RemoveClaim(identity.FindFirst("auth_time")!);
        identity.RemoveClaim(identity.FindFirst("acr")!);
        Assert.Equal(StationAdmissionState.Current, (await primary.ManagerAsync(old, Property, false)).State);
    }

    [Fact]
    public async Task Issuer_signout_is_not_a_requirement_for_continued_session_but_current_authority_and_expiry_are()
    {
        var owners = new Owners();
        var primary = Create(owners);
        var setup = new StationSetupFacts(Guid.NewGuid(), Guid.NewGuid(), 0,
            new(StationActorKind.StationOnly, null), StationActorKind.StationOnly, Now.AddMinutes(5),
            StationIssuerKind.Manager, Subject, Now.AddMinutes(10));
        Assert.Equal(StationAdmissionState.Current, (await primary.RevalidateIssuerAsync(setup, Property)).State);
        owners.PermitManager = false;
        Assert.Equal(StationAdmissionState.Denied, (await primary.RevalidateIssuerAsync(setup, Property)).State);
        Assert.Equal(StationAdmissionState.StateChanged, (await primary.RevalidateIssuerAsync(setup with { AssuranceExpiresAtUtc = Now }, Property)).State);
        Assert.Equal(StationAdmissionState.StateChanged, (await primary.RevalidateIssuerAsync(setup with { IssuerKind = StationIssuerKind.Unknown }, Property)).State);
    }

    [Theory]
    [InlineData("account-change", StationAdmissionState.StateChanged)]
    [InlineData("old-before", StationAdmissionState.Unavailable)]
    [InlineData("revision-change", StationAdmissionState.StateChanged)]
    [InlineData("unavailable", StationAdmissionState.Unavailable)]
    [InlineData("inactive", StationAdmissionState.Denied)]
    [InlineData("session-revoked", StationAdmissionState.Denied)]
    [InlineData("session-changed", StationAdmissionState.StateChanged)]
    public async Task Owner_observation_is_bracketed_and_fail_closed(string scenario, StationAdmissionState expected)
    {
        var owners = new Owners { Scenario = scenario };
        Assert.Equal(expected, (await Create(owners).ManagerAsync(Principal(), Property, true)).State);
    }

    [Fact]
    public void Pairing_secret_is_never_serialized_or_formatted_and_original_session_is_nonsecret_receipt_provenance()
    {
        var receipt = new StationManagementReceipt(Guid.NewGuid(), Guid.NewGuid(), Property,
            Kind: StationOperationKind.Register, IssuerKind: StationSetupIssuerKind.Manager,
            IssuerSubjectId: Subject, IssuerSessionId: Session);
        var handoff = new StationPairingHandoff(new(StationManagementState.Applied, receipt), "synthetic-private-cookie");
        Assert.DoesNotContain("synthetic-private-cookie", JsonSerializer.Serialize(handoff), StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-private-cookie", handoff.ToString(), StringComparison.Ordinal);
        var domain = new StationOperationReceipt(Tenant, Guid.NewGuid(), StationMutationKind.Register, new string('a', 64),
            new(StationMutationOutcome.Applied, Management: new(receipt.StationId, receipt.BrowserSessionId, Property, null, null, 1)),
            Now, Subject, StationIssuerKind.Manager, Session);
        Assert.Equal(Session, domain.Result().Management!.IssuerSessionId);
        Assert.Equal(Subject, domain.Result().Management!.IssuerSubjectId);
        Assert.Throws<ArgumentException>(() => new StationOperationReceipt(Tenant, Guid.NewGuid(), StationMutationKind.Lock,
            new string('a', 64), new(StationMutationOutcome.Applied), Now, Subject, StationIssuerKind.Manager, Session));
    }

    private static ClaimsPrincipal Principal() => new(new ClaimsIdentity(
        [new("sub", Subject), new("sid", Session.ToString("D")), new("acr", "urn:gma:acr:mfa"),
            new("auth_time", Now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))], "trusted-primary-test"));
    private static StationPrimaryAdmission Create(Owners o) => new(o, o, o, o, o, o, o, o, o,
        Options.Create(new StationOptions { ManagementAuthScopeId = "global" }),
        new(new AuthenticationAssuranceRequirement(["urn:gma:acr:mfa", "urn:gma:acr:two-step"], TimeSpan.FromMinutes(10))), o);
    private sealed class Owners : IScopeContext, ISystemClock, IAuthMemberAdmissionReader, IAuthSessionAdmissionReader,
        IOrganizationMembershipReader, IOrganizationScopeLifecycle, IWorkspaceOperationalAdmissionPolicy,
        IWorkspaceTerminationFenceReader, IAccessAuthorizationService, IAccessControlSubjectAuthoritySnapshotReader
    {
        public bool IsEnabled => true;
        public string ScopeId => Tenant;
        public DateTimeOffset UtcNow => Now;
        public bool PermitManager { get; set; } = true;
        public string? Scenario { get; init; }
        public int AccountReads { get; private set; }
        public int Decisions { get; private set; }
        public int AuthorityReads { get; private set; }
        public int SessionReads { get; private set; }
        public AccessScope? LastTarget { get; private set; }
        public ValueTask<bool> IsActiveAsync(string scopeId, Guid memberId, Guid sessionId, CancellationToken cancellationToken = default)
        {
            Assert.Equal("global", scopeId);
            Assert.Equal(Guid.Parse(Subject), memberId);
            Assert.Equal(Session, sessionId);
            this.SessionReads++;
            return ValueTask.FromResult(this.Scenario != "session-revoked" && !(this.Scenario == "session-changed" && this.SessionReads == 2));
        }
        public ValueTask<AuthMemberAdmission?> FindActiveAsync(string scopeId, Guid memberId, CancellationToken cancellationToken = default)
        {
            Assert.Equal("global", scopeId);
            Assert.Equal(Guid.Parse(Subject), memberId);
            this.AccountReads++;
            if (this.Scenario == "unavailable")
            { throw new InvalidOperationException("Synthetic owner unavailable"); }
            return ValueTask.FromResult<AuthMemberAdmission?>(this.Scenario == "inactive" ||
                (this.Scenario == "account-change" && this.AccountReads == 2) ? null : new(null));
        }
        public Task<OrganizationMembershipSnapshotDto?> FindAsync(Guid organizationId, string subjectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<OrganizationMembershipSnapshotDto?>(new(organizationId, OrganizationStatus.Active,
                new(Guid.Empty, organizationId, subjectId, OrganizationMembershipRole.Owner, OrganizationMembershipStatus.Active, 1, Now, Now)));
        public Task<OrganizationScopeSnapshot> GetSnapshotAsync(Guid organizationId, CancellationToken cancellationToken) =>
            Task.FromResult(new OrganizationScopeSnapshot(OrganizationScopeStatus.Open, 1));
        public Task<OrganizationScopeExportPage> ExportAsync(OrganizationScopeExportRequest request, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task<OrganizationScopeDestroyResult> DestroyBatchAsync(OrganizationScopeDestroyRequest request, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public ValueTask<WorkspaceOperationalAdmissionDecision> EvaluateAsync(string tenantId, CancellationToken cancellationToken = default) => ValueTask.FromResult(WorkspaceOperationalAdmissionDecision.Allowed);
        public Task<WorkspaceTerminationFenceSnapshot?> GetCurrentAsync(CancellationToken cancellationToken = default) => Task.FromResult<WorkspaceTerminationFenceSnapshot?>(null);
        public Task<AccessDecision> AuthorizeAsync(AccessRequirement requirement, CancellationToken cancellationToken)
        {
            this.Decisions++;
            this.LastTarget = requirement.Scope;
            Assert.Equal(StationsPermissionCodes.Manage, requirement.Permission.Value);
            return Task.FromResult(this.PermitManager ? AccessDecision.Allowed() : AccessDecision.Denied("fixture-denied"));
        }
        public Task<AccessControlSubjectAuthoritySnapshot> ReadAsync(AccessControlSubjectAuthoritySnapshotRequest r, CancellationToken cancellationToken = default)
        {
            this.AuthorityReads++;
            return Task.FromResult(new AccessControlSubjectAuthoritySnapshot(AccessControlAuthoritySnapshotStatus.Stable, 2,
                r.SubjectKind, r.SubjectId, r.RootScope, r.ExpectedTargetScopes.ToArray(),
                this.Scenario == "revision-change" ? this.AuthorityReads : 1,
                this.Scenario == "old-before" && this.AuthorityReads == 1 ? Now.AddSeconds(-1) : Now,
                null, new string('a', 64), [], []));
        }
    }
}
