namespace BunkFy.Modules.Stations.Tests;

using System.Security.Claims;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Staff.Contracts;
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

/// <summary>Real management/admission orchestration with owner-boundary doubles; no HTTP or database proof.</summary>
public sealed class StationStaffSetupHintTests
{
    private const string Tenant = "aa000000-0000-0000-0000-000000000001";
    private const string Subject = "bb000000-0000-0000-0000-000000000002";
    private static readonly Guid Property = Guid.Parse("cc000000-0000-0000-0000-000000000003");
    private static readonly Guid Staff = Guid.Parse("dd000000-0000-0000-0000-000000000004");
    private static readonly Guid Session = Guid.Parse("ee000000-0000-0000-0000-000000000005");
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("station-only", true)]
    [InlineData("linked", false)]
    [InlineData("unavailable", false)]
    [InlineData("not-registered", false)]
    public async Task Staff_status_derives_setup_hint_from_current_registration_enrollment_only(string scenario, bool expected)
    {
        var owners = new Owners { Scenario = scenario };
        // A preexisting true projection must not survive a linked/unavailable/unregistered observation.
        StationStaffManagementItem stored = new(Staff, 4, scenario != "not-registered", 2,
            StationPinState.NotSet, false, true, null, StationSetupState.None, null,
            CanIssueStationOnlySetup: true);
        owners.Item = stored;

        var result = await Create(owners).StaffStatusAsync(Principal(), Property, Staff);

        Assert.Equal(StationManagementState.Applied, result.State);
        Assert.Equal(stored with { CanIssueStationOnlySetup = expected }, result.Item);
        Assert.Equal(1, owners.StatusReads);
        Assert.True(owners.StaffReads > 0);
        Assert.Equal(0, owners.LinkedActionReads);
        // No local grant is present even in the positive case: this hint is not an IssueSetup authorization.
        Assert.False(result.Item!.LocalGrantPresent);
    }

    [Fact]
    public async Task Missing_status_item_stays_absent_without_enrollment_lookup()
    {
        var owners = new Owners();
        var result = await Create(owners).StaffStatusAsync(Principal(), Property, Staff);
        Assert.Equal(StationManagementState.Applied, result.State);
        Assert.Null(result.Item);
        Assert.Equal(1, owners.StatusReads);
        Assert.Equal(0, owners.StaffReads);
    }

    [Fact]
    public async Task Manager_denial_does_not_read_status_or_staff_enrollment()
    {
        var owners = new Owners { PermitManager = false };
        var result = await Create(owners).StaffStatusAsync(Principal(), Property, Staff);
        Assert.Equal(StationManagementState.Denied, result.State);
        Assert.Null(result.Item);
        Assert.Equal(0, owners.StatusReads);
        Assert.Equal(0, owners.StaffReads);
    }

    private static ClaimsPrincipal Principal() => new(new ClaimsIdentity(
        [new("sub", Subject), new("sid", Session.ToString("D"))], "trusted-primary-test"));

    private static StationManagementService Create(Owners owners)
    {
        var options = Options.Create(new StationOptions { ManagementAuthScopeId = "global" });
        var primary = new StationPrimaryAdmission(owners, owners, owners, owners, owners, owners, owners,
            owners, owners, options, new(new AuthenticationAssuranceRequirement(
                ["urn:gma:acr:mfa", "urn:gma:acr:two-step"], TimeSpan.FromMinutes(10))), owners);
        var admission = new StationAdmissionCoordinator(owners, owners, owners, owners, owners, owners, owners, owners);
        return new(primary, admission, owners, owners, options, owners);
    }

    private sealed class Owners : IScopeContext, ISystemClock, IAuthMemberAdmissionReader, IAuthSessionAdmissionReader,
        IOrganizationMembershipReader, IOrganizationScopeLifecycle, IWorkspaceOperationalAdmissionPolicy,
        IWorkspaceTerminationFenceReader, IAccessAuthorizationService, IAccessControlSubjectAuthoritySnapshotReader,
        IStaffStationEligibilitySource, IPropertyStationEligibilitySource, IWorkspaceStaffStationAdmissionObserver,
        IStationManagementStore, IStationPinVerifier
    {
        public bool IsEnabled => true;
        public string ScopeId => Tenant;
        public DateTimeOffset UtcNow => Now;
        public string Scenario { get; init; } = "station-only";
        public bool PermitManager { get; init; } = true;
        public StationStaffManagementItem? Item { get; set; }
        public int StatusReads { get; private set; }
        public int StaffReads { get; private set; }
        public int LinkedActionReads { get; private set; }

        public ValueTask<bool> IsActiveAsync(string scopeId, Guid memberId, Guid sessionId, CancellationToken cancellationToken = default)
        {
            Assert.Equal("global", scopeId);
            Assert.Equal(Guid.Parse(Subject), memberId);
            Assert.Equal(Session, sessionId);
            return ValueTask.FromResult(true);
        }
        public ValueTask<AuthMemberAdmission?> FindActiveAsync(string scopeId, Guid memberId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<AuthMemberAdmission?>(new(null));
        public Task<OrganizationMembershipSnapshotDto?> FindAsync(Guid organizationId, string subjectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<OrganizationMembershipSnapshotDto?>(new(organizationId, OrganizationStatus.Active,
                new(Session, organizationId, subjectId, OrganizationMembershipRole.Owner, OrganizationMembershipStatus.Active, 1, Now, Now)));
        public Task<OrganizationScopeSnapshot> GetSnapshotAsync(Guid organizationId, CancellationToken cancellationToken) =>
            Task.FromResult(new OrganizationScopeSnapshot(OrganizationScopeStatus.Open, 1));
        public ValueTask<WorkspaceOperationalAdmissionDecision> EvaluateAsync(string tenantId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(WorkspaceOperationalAdmissionDecision.Allowed);
        public Task<WorkspaceTerminationFenceSnapshot?> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<WorkspaceTerminationFenceSnapshot?>(null);
        public Task<AccessDecision> AuthorizeAsync(AccessRequirement requirement, CancellationToken cancellationToken)
        {
            Assert.Equal(StationsPermissionCodes.Manage, requirement.Permission.Value);
            Assert.Equal(WorkspaceAccessScopes.CreateProperty(Tenant, Property), requirement.Scope);
            return Task.FromResult(this.PermitManager ? AccessDecision.Allowed() : AccessDecision.Denied("fixture-denied"));
        }
        public Task<AccessControlSubjectAuthoritySnapshot> ReadAsync(AccessControlSubjectAuthoritySnapshotRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AccessControlSubjectAuthoritySnapshot(AccessControlAuthoritySnapshotStatus.Stable, 2,
                request.SubjectKind, request.SubjectId, request.RootScope, request.ExpectedTargetScopes.ToArray(),
                1, Now, null, new string('a', 64), [], []));
        public Task<PropertyStationEligibilitySnapshot?> FindAsync(string scopeId, Guid propertyId, CancellationToken cancellationToken = default) =>
            Task.FromResult<PropertyStationEligibilitySnapshot?>(new(Tenant, Property, PropertyStatus.Active, 2,
                PropertyProcessingStatus.Enabled, "Europe/Helsinki", "Synthetic property"));
        public Task<StaffStationEligibilitySnapshot?> FindAsync(string scopeId, Guid propertyId, Guid staffMemberId, CancellationToken cancellationToken = default)
        {
            this.StaffReads++;
            Assert.Equal(Tenant, scopeId);
            Assert.Equal(Property, propertyId);
            Assert.Equal(Staff, staffMemberId);
            if (this.Scenario == "unavailable")
            { throw new InvalidOperationException("Synthetic Staff owner unavailable"); }
            bool linked = this.Scenario == "linked";
            return Task.FromResult<StaffStationEligibilitySnapshot?>(new(Tenant, Property, Staff, StaffStatus.Active, 4,
                linked ? StaffStationAuthLinkState.Linked : StaffStationAuthLinkState.Unlinked, linked ? Subject : null,
                StaffStationAssignmentState.Open, new(Session, Property, 2, new DateOnly(2026, 9, 19)),
                StaffProcessingRestrictionGateResult.Allowed(StaffProcessingRestrictionContract.CurrentVersion, 2)));
        }
        public Task<WorkspaceStaffStationObservation> ObserveAsync(string scopeId, Guid propertyId, Guid staffMemberId,
            WorkspaceStaffStationAction action, CancellationToken cancellationToken = default)
        { this.LinkedActionReads++; throw new InvalidOperationException("Status must not observe check-in action authority."); }
        public Task<StationStaffManagementItem?> StaffStatusAsync(Guid propertyId, Guid staffId, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            Assert.Equal(Property, propertyId);
            Assert.Equal(Staff, staffId);
            Assert.Equal(Now, now);
            this.StatusReads++;
            return Task.FromResult(this.Item);
        }

        // Every unrelated owner mutation/PIN operation fails loudly if the informational hint starts invoking it.
        public Task<StationPinMaterial?> CreateAsync(string pin, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<StationPinVerification> VerifyAsync(string pin, StationStaffCredential credential, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<StationOwnPinFacts?> OwnPinFactsAsync(Guid propertyId, Guid staffId, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<StationCoreResult?> OwnPinOutcomeAsync(Guid operationId, StationIssuer issuer, Guid propertyId, Guid staffId,
            long expectedRevision, StationEnrollmentBinding binding, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<StationCoreResult> SetOwnPinAsync(Guid operationId, StationIssuer issuer, Guid propertyId, Guid staffId,
            long expectedRevision, long registrationVersion, StationEnrollmentBinding binding, StationPinMaterial material,
            DateTimeOffset now, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<StationManagementWrite> ExecuteAsync(Guid operationId, StationIssuer issuer, StationManagementCommand command,
            DateTimeOffset now, string? newCredentialDigest = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<StationCoreResult?> ReadOutcomeAsync(Guid operationId, string issuerSubjectId, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<IReadOnlyList<StationListItem>> ListStationsAsync(Guid propertyId, int offset, int pageSize, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<StationListItem?> FindStationAsync(Guid stationId, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<IReadOnlyList<StationRegistrationFacts>> RegistrationsAsync(Guid propertyId, bool activeOnly, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<StationSetupFacts?> FindSetupAsync(Guid setupId, StationDeviceReference device, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<OrganizationScopeExportPage> ExportAsync(OrganizationScopeExportRequest request, CancellationToken cancellationToken) => throw new InvalidOperationException();
        public Task<OrganizationScopeDestroyResult> DestroyBatchAsync(OrganizationScopeDestroyRequest request, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }
}
