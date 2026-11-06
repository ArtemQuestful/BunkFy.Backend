namespace BunkFy.Extensions.Workspaces.Tests;

using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Modules.Auth.Contracts;
using Gma.Modules.Organizations.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

[Trait("Category", "Unit")]
public sealed class OrganizationOwnerStaffBootstrapHandlerTests
{
    [Fact]
    public async Task Non_candidate_event_does_not_read_current_snapshot()
    {
        RecordingStaffIdentityBootstrapper staff = new();
        RecordingContactReader contacts = new();
        RecordingMembershipInspector memberships = new(_ => null);
        StubWorkspaceOperationalAdmissionPolicy admission = new();
        OrganizationOwnerStaffBootstrapHandler handler = CreateHandler(
            staff,
            contacts,
            memberships,
            admission);

        await handler.HandleAsync(
            CreateEvent(
                OrganizationMembershipRole.Member,
                OrganizationMembershipStatus.Active),
            CancellationToken.None);

        Assert.Empty(memberships.Calls);
        Assert.Empty(admission.TenantIds);
        Assert.Empty(staff.Requests);
        Assert.Equal(0, contacts.CallCount);
    }

    [Fact]
    public async Task Exact_current_owner_snapshot_provisions_stable_membership_source()
    {
        RecordingStaffIdentityBootstrapper staff = new();
        RecordingContactReader contacts = new();
        OrganizationMembershipChangedIntegrationEvent integrationEvent =
            CreateEvent(
                OrganizationMembershipRole.Owner,
                OrganizationMembershipStatus.Active);
        RecordingMembershipInspector memberships = new(call =>
            ActiveSnapshot(
                integrationEvent,
                membershipVersion: integrationEvent.MembershipVersion + 1,
                scopeRevision: 0));
        StubWorkspaceOperationalAdmissionPolicy admission = new();
        OrganizationOwnerStaffBootstrapHandler handler = CreateHandler(
            staff,
            contacts,
            memberships,
            admission);

        await handler.HandleAsync(integrationEvent, CancellationToken.None);

        MembershipInspectionCall call = Assert.Single(memberships.Calls);
        Assert.Equal(integrationEvent.OrganizationId, call.OrganizationId);
        Assert.Equal(integrationEvent.MembershipId, call.MembershipId);
        Assert.Equal(integrationEvent.SubjectId, call.SubjectId);
        Assert.Equal(integrationEvent.ScopeId, Assert.Single(admission.TenantIds));
        StaffIdentityBootstrapRequest request = Assert.Single(staff.Requests);
        Assert.Equal(integrationEvent.EventId, request.OperationId);
        Assert.Equal(integrationEvent.MembershipId, request.SourceId);
        Assert.Equal(integrationEvent.SubjectId, request.AuthSubjectId);
        Assert.Equal("owner@example.test", request.WorkEmail);
    }

    [Theory]
    [InlineData(OrganizationMembershipChange.Suspended, OrganizationMembershipStatus.Suspended)]
    [InlineData(OrganizationMembershipChange.Resumed, OrganizationMembershipStatus.Active)]
    [InlineData(OrganizationMembershipChange.Removed, OrganizationMembershipStatus.Removed)]
    public async Task Non_join_membership_changes_do_not_read_or_mutate(
        OrganizationMembershipChange change,
        OrganizationMembershipStatus status)
    {
        RecordingStaffIdentityBootstrapper staff = new();
        RecordingContactReader contacts = new();
        RecordingMembershipInspector memberships = new(_ => null);
        StubWorkspaceOperationalAdmissionPolicy admission = new();
        OrganizationOwnerStaffBootstrapHandler handler = CreateHandler(
            staff,
            contacts,
            memberships,
            admission);

        await handler.HandleAsync(
            CreateEvent(OrganizationMembershipRole.Owner, status, change),
            CancellationToken.None);

        Assert.Empty(memberships.Calls);
        Assert.Empty(admission.TenantIds);
        Assert.Empty(staff.Requests);
        Assert.Equal(0, contacts.CallCount);
    }

    [Fact]
    public async Task Non_canonical_scope_coordinate_retries_before_snapshot_read()
    {
        OrganizationMembershipChangedIntegrationEvent invalid = CreateEvent(
            OrganizationMembershipRole.Owner,
            OrganizationMembershipStatus.Active,
            canonicalScope: false);
        RecordingMembershipInspector memberships = new(_ => null);
        RecordingStaffIdentityBootstrapper staff = new();
        RecordingContactReader contacts = new();
        StubWorkspaceOperationalAdmissionPolicy admission = new();
        OrganizationOwnerStaffBootstrapHandler handler = CreateHandler(
            staff,
            contacts,
            memberships,
            admission);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleAsync(invalid, CancellationToken.None));

        Assert.Empty(memberships.Calls);
        Assert.Empty(admission.TenantIds);
        Assert.Empty(staff.Requests);
        Assert.Equal(0, contacts.CallCount);
    }

    [Fact]
    public async Task Null_or_older_snapshot_retries_without_downstream_reads()
    {
        OrganizationMembershipChangedIntegrationEvent integrationEvent =
            CreateEvent(
                OrganizationMembershipRole.Owner,
                OrganizationMembershipStatus.Active,
                membershipVersion: 4);
        foreach (OrganizationMembershipSnapshot? snapshot in new[]
                 {
                     null,
                     ActiveSnapshot(
                         integrationEvent,
                         membershipVersion: 3,
                         scopeRevision: 1)
                 })
        {
            RecordingStaffIdentityBootstrapper staff = new();
            RecordingContactReader contacts = new();
            RecordingMembershipInspector memberships = new(_ => snapshot);
            StubWorkspaceOperationalAdmissionPolicy admission = new();
            OrganizationOwnerStaffBootstrapHandler handler = CreateHandler(
                staff,
                contacts,
                memberships,
                admission);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                handler.HandleAsync(
                    integrationEvent,
                    CancellationToken.None));

            Assert.Single(memberships.Calls);
            Assert.Empty(admission.TenantIds);
            Assert.Empty(staff.Requests);
            Assert.Equal(0, contacts.CallCount);
        }
    }

    [Theory]
    [InlineData(OrganizationScopeStatus.Unknown, OrganizationStatus.Active, OrganizationMembershipRole.Owner, OrganizationMembershipStatus.Active, 1)]
    [InlineData(OrganizationScopeStatus.Invalid, OrganizationStatus.Active, OrganizationMembershipRole.Owner, OrganizationMembershipStatus.Active, 1)]
    [InlineData(OrganizationScopeStatus.Missing, OrganizationStatus.Active, OrganizationMembershipRole.Owner, OrganizationMembershipStatus.Active, 1)]
    [InlineData(OrganizationScopeStatus.Open, OrganizationStatus.Unknown, OrganizationMembershipRole.Owner, OrganizationMembershipStatus.Active, 1)]
    [InlineData(OrganizationScopeStatus.Open, OrganizationStatus.Active, OrganizationMembershipRole.Unknown, OrganizationMembershipStatus.Active, 1)]
    [InlineData(OrganizationScopeStatus.Open, OrganizationStatus.Active, OrganizationMembershipRole.Owner, OrganizationMembershipStatus.Unknown, 1)]
    [InlineData(OrganizationScopeStatus.Open, OrganizationStatus.Active, OrganizationMembershipRole.Owner, OrganizationMembershipStatus.Active, -1)]
    public async Task Unknown_or_invalid_snapshot_retries(
        OrganizationScopeStatus scopeStatus,
        OrganizationStatus organizationStatus,
        OrganizationMembershipRole role,
        OrganizationMembershipStatus membershipStatus,
        long scopeRevision)
    {
        OrganizationMembershipChangedIntegrationEvent integrationEvent =
            CreateEvent(
                OrganizationMembershipRole.Owner,
                OrganizationMembershipStatus.Active);
        OrganizationMembershipSnapshot snapshot = new(
            integrationEvent.OrganizationId,
            integrationEvent.MembershipId,
            organizationStatus,
            scopeStatus,
            scopeRevision,
            role,
            membershipStatus,
            integrationEvent.MembershipVersion);
        RecordingStaffIdentityBootstrapper staff = new();
        RecordingContactReader contacts = new();
        RecordingMembershipInspector memberships = new(_ => snapshot);
        StubWorkspaceOperationalAdmissionPolicy admission = new();
        OrganizationOwnerStaffBootstrapHandler handler = CreateHandler(
            staff,
            contacts,
            memberships,
            admission);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleAsync(integrationEvent, CancellationToken.None));

        Assert.Empty(admission.TenantIds);
        Assert.Empty(staff.Requests);
        Assert.Equal(0, contacts.CallCount);
    }

    [Theory]
    [InlineData(OrganizationScopeStatus.Closed, OrganizationStatus.Active, OrganizationMembershipRole.Owner, OrganizationMembershipStatus.Active)]
    [InlineData(OrganizationScopeStatus.Open, OrganizationStatus.Suspended, OrganizationMembershipRole.Owner, OrganizationMembershipStatus.Active)]
    [InlineData(OrganizationScopeStatus.Open, OrganizationStatus.Archived, OrganizationMembershipRole.Owner, OrganizationMembershipStatus.Active)]
    [InlineData(OrganizationScopeStatus.Open, OrganizationStatus.Active, OrganizationMembershipRole.Member, OrganizationMembershipStatus.Active)]
    [InlineData(OrganizationScopeStatus.Open, OrganizationStatus.Active, OrganizationMembershipRole.Owner, OrganizationMembershipStatus.Suspended)]
    [InlineData(OrganizationScopeStatus.Open, OrganizationStatus.Active, OrganizationMembershipRole.Owner, OrganizationMembershipStatus.Removed)]
    public async Task Current_terminal_or_non_owner_state_is_acknowledged(
        OrganizationScopeStatus scopeStatus,
        OrganizationStatus organizationStatus,
        OrganizationMembershipRole role,
        OrganizationMembershipStatus membershipStatus)
    {
        OrganizationMembershipChangedIntegrationEvent integrationEvent =
            CreateEvent(
                OrganizationMembershipRole.Owner,
                OrganizationMembershipStatus.Active);
        OrganizationMembershipSnapshot snapshot = new(
            integrationEvent.OrganizationId,
            integrationEvent.MembershipId,
            organizationStatus,
            scopeStatus,
            1,
            role,
            membershipStatus,
            integrationEvent.MembershipVersion);
        RecordingStaffIdentityBootstrapper staff = new();
        RecordingContactReader contacts = new();
        RecordingMembershipInspector memberships = new(_ => snapshot);
        StubWorkspaceOperationalAdmissionPolicy admission = new();
        OrganizationOwnerStaffBootstrapHandler handler = CreateHandler(
            staff,
            contacts,
            memberships,
            admission);

        await handler.HandleAsync(integrationEvent, CancellationToken.None);

        Assert.Single(memberships.Calls);
        Assert.Empty(admission.TenantIds);
        Assert.Empty(staff.Requests);
        Assert.Equal(0, contacts.CallCount);
    }

    [Fact]
    public async Task Restricted_workspace_retries_after_current_owner_admission()
    {
        OrganizationMembershipChangedIntegrationEvent integrationEvent =
            CreateEvent(
                OrganizationMembershipRole.Owner,
                OrganizationMembershipStatus.Active);
        RecordingMembershipInspector memberships = new(_ =>
            ActiveSnapshot(
                integrationEvent,
                integrationEvent.MembershipVersion,
                scopeRevision: 1));
        RecordingStaffIdentityBootstrapper staff = new();
        RecordingContactReader contacts = new();
        StubWorkspaceOperationalAdmissionPolicy admission = new(
            WorkspaceOperationalAdmissionOutcome.Restricted);
        OrganizationOwnerStaffBootstrapHandler handler = CreateHandler(
            staff,
            contacts,
            memberships,
            admission);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleAsync(integrationEvent, CancellationToken.None));

        Assert.Single(memberships.Calls);
        Assert.Single(admission.TenantIds);
        Assert.Empty(staff.Requests);
        Assert.Equal(0, contacts.CallCount);
    }

    [Fact]
    public async Task Mismatched_snapshot_coordinates_retry()
    {
        OrganizationMembershipChangedIntegrationEvent integrationEvent =
            CreateEvent(
                OrganizationMembershipRole.Owner,
                OrganizationMembershipStatus.Active);
        OrganizationMembershipSnapshot snapshot = ActiveSnapshot(
            integrationEvent,
            integrationEvent.MembershipVersion,
            scopeRevision: 1) with
        {
            MembershipId = Guid.NewGuid()
        };
        RecordingStaffIdentityBootstrapper staff = new();
        RecordingContactReader contacts = new();
        RecordingMembershipInspector memberships = new(_ => snapshot);
        StubWorkspaceOperationalAdmissionPolicy admission = new();
        OrganizationOwnerStaffBootstrapHandler handler = CreateHandler(
            staff,
            contacts,
            memberships,
            admission);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleAsync(integrationEvent, CancellationToken.None));

        Assert.Empty(admission.TenantIds);
        Assert.Empty(staff.Requests);
        Assert.Equal(0, contacts.CallCount);
    }

    private static OrganizationOwnerStaffBootstrapHandler CreateHandler(
        IStaffIdentityBootstrapper staff,
        IAuthMemberContactReader contacts,
        IOrganizationMembershipInspector memberships,
        IWorkspaceOperationalAdmissionPolicy admission) => new(
        staff,
        contacts,
        memberships,
        admission,
        Options.Create(new BunkFyWorkspacesOptions
        {
            GlobalAuthScopeId = "global"
        }));

    private static OrganizationMembershipChangedIntegrationEvent CreateEvent(
        OrganizationMembershipRole role,
        OrganizationMembershipStatus status,
        OrganizationMembershipChange change = OrganizationMembershipChange.Joined,
        long membershipVersion = 1,
        bool canonicalScope = true)
    {
        Guid organizationId = Guid.NewGuid();
        return new OrganizationMembershipChangedIntegrationEvent(
            Guid.NewGuid(),
            new DateTimeOffset(2026, 7, 21, 8, 0, 0, TimeSpan.Zero),
            organizationId.ToString(canonicalScope ? "D" : "B"),
            organizationId,
            Guid.NewGuid(),
            Guid.NewGuid().ToString("D"),
            change,
            role,
            status,
            membershipVersion);
    }

    private static OrganizationMembershipSnapshot ActiveSnapshot(
        OrganizationMembershipChangedIntegrationEvent integrationEvent,
        long membershipVersion,
        long scopeRevision) => new(
        integrationEvent.OrganizationId,
        integrationEvent.MembershipId,
        OrganizationStatus.Active,
        OrganizationScopeStatus.Open,
        scopeRevision,
        OrganizationMembershipRole.Owner,
        OrganizationMembershipStatus.Active,
        membershipVersion);

    private sealed class RecordingStaffIdentityBootstrapper
        : IStaffIdentityBootstrapper
    {
        public List<StaffIdentityBootstrapRequest> Requests { get; } = [];

        public Task<StaffIdentityBootstrapResult> BootstrapAsync(
            StaffIdentityBootstrapRequest request,
            CancellationToken cancellationToken)
        {
            this.Requests.Add(request);
            return Task.FromResult(new StaffIdentityBootstrapResult(true, null));
        }
    }

    private sealed class RecordingMembershipInspector(
        Func<MembershipInspectionCall, OrganizationMembershipSnapshot?> resolve)
        : IOrganizationMembershipInspector
    {
        public List<MembershipInspectionCall> Calls { get; } = [];

        public Task<OrganizationMembershipSnapshot?> FindAsync(
            Guid organizationId,
            Guid membershipId,
            string subjectId,
            CancellationToken cancellationToken = default)
        {
            MembershipInspectionCall call = new(
                organizationId,
                membershipId,
                subjectId);
            this.Calls.Add(call);
            return Task.FromResult(resolve(call));
        }
    }

    private sealed record MembershipInspectionCall(
        Guid OrganizationId,
        Guid MembershipId,
        string SubjectId);

    private sealed class RecordingContactReader : IAuthMemberContactReader
    {
        public int CallCount { get; private set; }

        public ValueTask<string?> GetPreferredVerifiedEmailAsync(
            string scopeId,
            Guid memberId,
            CancellationToken cancellationToken = default)
        {
            this.CallCount++;
            return ValueTask.FromResult<string?>("owner@example.test");
        }
    }
}
