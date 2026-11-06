namespace BunkFy.Extensions.Workspaces.Tests;

using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.AccessControl;
using Gma.Framework.Permissions;
using Gma.Modules.AccessControl.Contracts;
using Gma.Modules.Organizations.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspaceOwnerMembershipAccessDecisionProviderTests
{
    private static readonly Guid OrganizationId = Guid.NewGuid();
    private static readonly string WorkspaceId = OrganizationId.ToString("D");
    private static readonly string SubjectId = Guid.NewGuid().ToString("D");

    [Fact]
    public async Task Ordinary_member_abstains_without_an_organizations_lookup()
    {
        StubRoles roles = new(hasOwner: false);
        StubMemberships memberships = new(CreateMembership());
        WorkspaceOwnerMembershipAccessDecisionProvider provider = CreateProvider(roles, memberships);

        AccessDecision decision = await provider.DecideAsync(
            CreateRequirement(),
            CancellationToken.None);

        Assert.True(decision.IsAbstain);
        Assert.Equal(1, roles.CheckCount);
        Assert.Equal(0, memberships.ReadCount);
    }

    [Fact]
    public async Task Active_organization_owner_abstains_so_persisted_grants_can_decide()
    {
        StubRoles roles = new(hasOwner: true);
        StubMemberships memberships = new(CreateMembership());
        WorkspaceOwnerMembershipAccessDecisionProvider provider = CreateProvider(roles, memberships);

        AccessDecision decision = await provider.DecideAsync(
            CreateRequirement(),
            CancellationToken.None);

        Assert.True(decision.IsAbstain);
        Assert.Equal(1, memberships.ReadCount);
    }

    [Theory]
    [InlineData(OrganizationMembershipRole.Member, OrganizationMembershipStatus.Active)]
    [InlineData(OrganizationMembershipRole.Owner, OrganizationMembershipStatus.Suspended)]
    [InlineData(OrganizationMembershipRole.Owner, OrganizationMembershipStatus.Removed)]
    public async Task Stale_owner_assignment_is_denied(
        OrganizationMembershipRole role,
        OrganizationMembershipStatus status)
    {
        StubRoles roles = new(hasOwner: true);
        StubMemberships memberships = new(CreateMembership(role, status));
        WorkspaceOwnerMembershipAccessDecisionProvider provider = CreateProvider(roles, memberships);

        AccessDecision decision = await provider.DecideAsync(
            CreateRequirement(),
            CancellationToken.None);

        Assert.True(decision.IsDenied);
        Assert.Equal("bunkfy.workspace.owner-membership-stale", decision.ReasonCode);
    }

    [Fact]
    public async Task Missing_membership_is_denied()
    {
        WorkspaceOwnerMembershipAccessDecisionProvider provider = CreateProvider(
            new StubRoles(hasOwner: true),
            new StubMemberships(null));

        AccessDecision decision = await provider.DecideAsync(
            CreateRequirement(),
            CancellationToken.None);

        Assert.True(decision.IsDenied);
        Assert.Equal("bunkfy.workspace.owner-membership-stale", decision.ReasonCode);
    }

    [Fact]
    public async Task Active_owner_membership_in_suspended_organization_is_denied()
    {
        WorkspaceOwnerMembershipAccessDecisionProvider provider = CreateProvider(
            new StubRoles(hasOwner: true),
            new StubMemberships(
                CreateMembership(),
                OrganizationStatus.Suspended));

        AccessDecision decision = await provider.DecideAsync(
            CreateRequirement(),
            CancellationToken.None);

        Assert.True(decision.IsDenied);
        Assert.Equal("bunkfy.workspace.owner-membership-stale", decision.ReasonCode);
    }

    [Fact]
    public async Task Membership_reader_failure_is_fail_closed()
    {
        StubMemberships memberships = new(CreateMembership()) { ThrowOnRead = true };
        WorkspaceOwnerMembershipAccessDecisionProvider provider = CreateProvider(
            new StubRoles(hasOwner: true),
            memberships);

        AccessDecision decision = await provider.DecideAsync(
            CreateRequirement(),
            CancellationToken.None);

        Assert.True(decision.IsDenied);
        Assert.Equal("bunkfy.workspace.owner-membership-unavailable", decision.ReasonCode);
    }

    [Fact]
    public async Task Invalid_workspace_coordinate_with_owner_assignment_is_denied_without_membership_read()
    {
        StubMemberships memberships = new(CreateMembership());
        WorkspaceOwnerMembershipAccessDecisionProvider provider = CreateProvider(
            new StubRoles(hasOwner: true),
            memberships);
        AccessRequirement requirement = new(
            AccessSubject.User(SubjectId),
            PermissionCode.Create("reservations.read"),
            WorkspaceAccessScopes.Create("not-a-guid"));

        AccessDecision decision = await provider.DecideAsync(
            requirement,
            CancellationToken.None);

        Assert.True(decision.IsDenied);
        Assert.Equal(0, memberships.ReadCount);
    }

    [Fact]
    public async Task Batch_reuses_one_owner_and_membership_check_per_subject_workspace()
    {
        StubRoles roles = new(hasOwner: true);
        StubMemberships memberships = new(CreateMembership());
        WorkspaceOwnerMembershipAccessDecisionProvider provider = CreateProvider(roles, memberships);
        AccessRequirement[] requirements =
        [
            CreateRequirement("reservations.read"),
            CreateRequirement("inventory.read"),
            CreateRequirement(
                "properties.read",
                WorkspaceAccessScopes.CreateProperty(WorkspaceId, Guid.NewGuid()))
        ];

        IReadOnlyList<AccessDecision> decisions = await provider.DecideManyAsync(
            requirements,
            CancellationToken.None);

        Assert.All(decisions, decision => Assert.True(decision.IsAbstain));
        Assert.Equal(1, roles.CheckCount);
        Assert.Equal(1, memberships.ReadCount);
    }

    [Fact]
    public async Task Global_or_non_user_requirements_abstain_without_storage_reads()
    {
        StubRoles roles = new(hasOwner: true);
        StubMemberships memberships = new(CreateMembership());
        WorkspaceOwnerMembershipAccessDecisionProvider provider = CreateProvider(roles, memberships);
        AccessRequirement[] requirements =
        [
            new(
                AccessSubject.User(SubjectId),
                PermissionCode.Create("reservations.read"),
                AccessScope.Global),
            new(
                AccessSubject.Service("worker-a"),
                PermissionCode.Create("reservations.read"),
                WorkspaceAccessScopes.Create(WorkspaceId))
        ];

        IReadOnlyList<AccessDecision> decisions = await provider.DecideManyAsync(
            requirements,
            CancellationToken.None);

        Assert.All(decisions, decision => Assert.True(decision.IsAbstain));
        Assert.Equal(0, roles.CheckCount);
        Assert.Equal(0, memberships.ReadCount);
    }

    private static WorkspaceOwnerMembershipAccessDecisionProvider CreateProvider(
        StubRoles roles,
        StubMemberships memberships) =>
        new(
            roles,
            memberships,
            NullLogger<WorkspaceOwnerMembershipAccessDecisionProvider>.Instance);

    private static AccessRequirement CreateRequirement(
        string permission = "reservations.read",
        AccessScope? scope = null) =>
        new(
            AccessSubject.User(SubjectId),
            PermissionCode.Create(permission),
            scope ?? WorkspaceAccessScopes.Create(WorkspaceId));

    private static OrganizationMembershipDto CreateMembership(
        OrganizationMembershipRole role = OrganizationMembershipRole.Owner,
        OrganizationMembershipStatus status = OrganizationMembershipStatus.Active) =>
        new(
            Guid.NewGuid(),
            OrganizationId,
            SubjectId,
            role,
            status,
            3,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);

    private sealed class StubMemberships(
        OrganizationMembershipDto? membership,
        OrganizationStatus organizationStatus = OrganizationStatus.Active)
        : IOrganizationMembershipReader
    {
        public int ReadCount { get; private set; }
        public bool ThrowOnRead { get; init; }

        public Task<OrganizationMembershipSnapshotDto?> FindAsync(
            Guid organizationId,
            string subjectId,
            CancellationToken cancellationToken = default)
        {
            this.ReadCount++;
            if (this.ThrowOnRead)
            {
                throw new InvalidOperationException("unavailable");
            }

            Assert.Equal(OrganizationId, organizationId);
            Assert.Equal(SubjectId, subjectId);
            return Task.FromResult<OrganizationMembershipSnapshotDto?>(new(
                OrganizationId,
                organizationStatus,
                membership));
        }
    }

    private sealed class StubRoles(bool hasOwner) : IAccessControlRoleProvisioner
    {
        public int CheckCount { get; private set; }

        public Task<bool> HasAssignmentAsync(
            AccessSubject subject,
            string roleName,
            AccessScope scope,
            CancellationToken cancellationToken = default)
        {
            this.CheckCount++;
            Assert.Equal(AccessSubject.User(SubjectId), subject);
            Assert.Equal(WorkspaceAccessRoles.Owner, roleName);
            Assert.Equal(WorkspaceAccessScopes.Create(scope.Segments[0].Value), scope);
            return Task.FromResult(hasOwner);
        }

        public Task EnsureRoleAsync(
            AccessControlRoleDefinition role,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task EnsureAssignmentAsync(
            AccessSubject subject,
            string roleName,
            AccessScope scope,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AccessControlAssignmentRemovalOutcome> RemoveAssignmentAsync(
            AccessSubject subject,
            string roleName,
            AccessScope scope,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AccessControlPage<AccessControlRoleAssignment>> ListAssignmentsAsync(
            string roleName,
            AccessScope scope,
            int page,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
