namespace BunkFy.Extensions.Workspaces.Tests;

using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.AccessControl;
using Gma.Modules.AccessControl.Contracts;
using Gma.Modules.Organizations.Contracts;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspaceOwnerRoleAssignmentPolicyTests
{
    private static readonly Guid OrganizationId = Guid.NewGuid();
    private static readonly string WorkspaceId = OrganizationId.ToString("D");
    private static readonly string SubjectId = Guid.NewGuid().ToString("D");

    [Fact]
    public async Task Non_owner_role_is_unaffected_but_owner_is_workspace_root_only()
    {
        StubMemberships memberships = new(null);
        WorkspaceOwnerRoleAssignmentPolicy policy = new(memberships);

        bool ordinaryRole = await policy.IsAllowedAsync(
            CreateContext("custom-role", WorkspaceAccessScopes.Create(WorkspaceId)),
            CancellationToken.None);
        bool globalOwner = await policy.IsAllowedAsync(
            CreateContext(WorkspaceAccessRoles.Owner, AccessScope.Global),
            CancellationToken.None);
        bool propertyOwner = await policy.IsAllowedAsync(
            CreateContext(
                WorkspaceAccessRoles.Owner,
                WorkspaceAccessScopes.CreateProperty(
                    WorkspaceId,
                    Guid.NewGuid())),
            CancellationToken.None);

        Assert.True(ordinaryRole);
        Assert.False(globalOwner);
        Assert.False(propertyOwner);
        Assert.Equal(0, memberships.ReadCount);
    }

    [Fact]
    public async Task Current_active_owner_can_be_repaired_through_generic_assignment_command()
    {
        StubMemberships memberships = new(CreateMembership(
            OrganizationMembershipRole.Owner,
            OrganizationMembershipStatus.Active));
        WorkspaceOwnerRoleAssignmentPolicy policy = new(memberships);

        bool allowed = await policy.IsAllowedAsync(
            CreateContext(WorkspaceAccessRoles.Owner, WorkspaceAccessScopes.Create(WorkspaceId)),
            CancellationToken.None);

        Assert.True(allowed);
        Assert.Equal(1, memberships.ReadCount);
    }

    [Theory]
    [InlineData(OrganizationMembershipRole.Member, OrganizationMembershipStatus.Active)]
    [InlineData(OrganizationMembershipRole.Owner, OrganizationMembershipStatus.Suspended)]
    [InlineData(OrganizationMembershipRole.Owner, OrganizationMembershipStatus.Removed)]
    public async Task Non_owner_membership_cannot_receive_owner_role(
        OrganizationMembershipRole role,
        OrganizationMembershipStatus status)
    {
        WorkspaceOwnerRoleAssignmentPolicy policy = new(
            new StubMemberships(CreateMembership(role, status)));

        bool allowed = await policy.IsAllowedAsync(
            CreateContext(WorkspaceAccessRoles.Owner, WorkspaceAccessScopes.Create(WorkspaceId)),
            CancellationToken.None);

        Assert.False(allowed);
    }

    [Fact]
    public async Task Missing_membership_cannot_receive_owner_role()
    {
        WorkspaceOwnerRoleAssignmentPolicy policy = new(new StubMemberships(null));

        bool allowed = await policy.IsAllowedAsync(
            CreateContext(WorkspaceAccessRoles.Owner, WorkspaceAccessScopes.Create(WorkspaceId)),
            CancellationToken.None);

        Assert.False(allowed);
    }

    [Fact]
    public async Task Owner_of_inactive_organization_cannot_receive_owner_role()
    {
        WorkspaceOwnerRoleAssignmentPolicy policy = new(new StubMemberships(
            CreateMembership(
                OrganizationMembershipRole.Owner,
                OrganizationMembershipStatus.Active),
            OrganizationStatus.Suspended));

        bool allowed = await policy.IsAllowedAsync(
            CreateContext(WorkspaceAccessRoles.Owner, WorkspaceAccessScopes.Create(WorkspaceId)),
            CancellationToken.None);

        Assert.False(allowed);
    }

    [Theory]
    [InlineData(AccessSubjectKind.Service, "service-a", "00000000-0000-0000-0000-000000000001")]
    [InlineData(AccessSubjectKind.User, "user-a", "not-a-guid")]
    public async Task Invalid_owner_target_coordinate_is_rejected_without_membership_read(
        AccessSubjectKind subjectKind,
        string subjectId,
        string workspaceId)
    {
        StubMemberships memberships = new(CreateMembership(
            OrganizationMembershipRole.Owner,
            OrganizationMembershipStatus.Active));
        WorkspaceOwnerRoleAssignmentPolicy policy = new(memberships);
        AccessRoleAssignmentPolicyContext context = new(
            subjectKind == AccessSubjectKind.Service
                ? AccessSubject.Service(subjectId)
                : AccessSubject.User(subjectId),
            WorkspaceAccessRoles.Owner,
            WorkspaceAccessScopes.Create(workspaceId),
            expiresAtUtc: null,
            WorkspaceAccessRoles.OwnerPermissions);

        bool allowed = await policy.IsAllowedAsync(context, CancellationToken.None);

        Assert.False(allowed);
        Assert.Equal(0, memberships.ReadCount);
    }

    private static AccessRoleAssignmentPolicyContext CreateContext(
        string roleName,
        AccessScope scope) =>
        new(
            AccessSubject.User(SubjectId),
            roleName,
            scope,
            expiresAtUtc: null,
            WorkspaceAccessRoles.OwnerPermissions);

    private static OrganizationMembershipDto CreateMembership(
        OrganizationMembershipRole role,
        OrganizationMembershipStatus status) =>
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

        public Task<OrganizationMembershipSnapshotDto?> FindAsync(
            Guid organizationId,
            string subjectId,
            CancellationToken cancellationToken = default)
        {
            this.ReadCount++;
            Assert.Equal(OrganizationId, organizationId);
            Assert.Equal(SubjectId, subjectId);
            return Task.FromResult<OrganizationMembershipSnapshotDto?>(new(
                OrganizationId,
                organizationStatus,
                membership));
        }
    }
}
