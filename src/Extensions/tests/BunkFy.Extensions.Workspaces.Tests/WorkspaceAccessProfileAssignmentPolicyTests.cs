namespace BunkFy.Extensions.Workspaces.Tests;

using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.AccessControl;
using Gma.Modules.AccessControl.Contracts;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspaceAccessProfileAssignmentPolicyTests
{
    private static readonly AccessScope WorkspaceScope = WorkspaceAccessScopes.Create("workspace-a");
    private static readonly AccessSubject Owner = AccessSubject.User("owner-a");
    private static readonly AccessSubject Manager = AccessSubject.User("manager-a");
    private static readonly AccessSubject Target = AccessSubject.User("member-a");

    [Theory]
    [InlineData(WorkspaceAccessRoles.MembershipMarker)]
    [InlineData(WorkspaceAccessRoles.LegacyMember)]
    public async Task Trusted_provisioner_can_assign_profile_to_workspace_member(string membershipRole)
    {
        StubRoleProvisioner accessControl = new();
        accessControl.Assign(Target, membershipRole, WorkspaceScope);
        WorkspaceAccessProfileAssignmentPolicy policy = CreatePolicy(accessControl);

        bool allowed = await policy.IsAllowedAsync(
            CreateContext(AccessSubject.System(WorkspaceAccessActors.Provisioner)),
            CancellationToken.None);

        Assert.True(allowed);
    }

    [Fact]
    public async Task Owner_target_is_rejected_even_when_an_ordinary_membership_marker_exists()
    {
        StubRoleProvisioner accessControl = new();
        accessControl.Assign(Target, WorkspaceAccessRoles.Owner, WorkspaceScope);
        accessControl.Assign(Target, WorkspaceAccessRoles.MembershipMarker, WorkspaceScope);
        WorkspaceAccessProfileAssignmentPolicy policy = CreatePolicy(accessControl);

        bool allowed = await policy.IsAllowedAsync(
            CreateContext(AccessSubject.System(WorkspaceAccessActors.Provisioner)),
            CancellationToken.None);

        Assert.False(allowed);
        Assert.Equal(
            WorkspaceAccessRoles.Owner,
            Assert.Single(accessControl.Checks).Role);
    }

    [Fact]
    public async Task Untrusted_system_actor_is_rejected()
    {
        StubRoleProvisioner accessControl = new();
        accessControl.Assign(Target, WorkspaceAccessRoles.MembershipMarker, WorkspaceScope);
        WorkspaceAccessProfileAssignmentPolicy policy = CreatePolicy(accessControl);

        bool allowed = await policy.IsAllowedAsync(
            CreateContext(AccessSubject.System("other-provisioner")),
            CancellationToken.None);

        Assert.False(allowed);
    }

    [Fact]
    public async Task Workspace_owner_can_assign_profile_to_member()
    {
        StubRoleProvisioner accessControl = new();
        accessControl.Assign(Target, WorkspaceAccessRoles.MembershipMarker, WorkspaceScope);
        accessControl.Assign(Owner, WorkspaceAccessRoles.Owner, WorkspaceScope);
        StubAuthorizationService authorization = new(_ => AccessDecision.Denied("unexpected"));
        WorkspaceAccessProfileAssignmentPolicy policy = new(accessControl, authorization);

        bool allowed = await policy.IsAllowedAsync(CreateContext(Owner), CancellationToken.None);

        Assert.True(allowed);
        Assert.Empty(authorization.Requirements);
    }

    [Fact]
    public async Task Delegated_manager_must_hold_every_profile_permission_and_assignment_permission()
    {
        StubRoleProvisioner accessControl = new();
        accessControl.Assign(Target, WorkspaceAccessRoles.MembershipMarker, WorkspaceScope);
        StubAuthorizationService authorization = new(_ => AccessDecision.Allowed());
        WorkspaceAccessProfileAssignmentPolicy policy = new(accessControl, authorization);
        AccessScope propertyScope = WorkspaceAccessScopes.CreateProperty("workspace-a", Guid.NewGuid());

        bool allowed = await policy.IsAllowedAsync(
            CreateContext(
                Manager,
                propertyScope,
                ["reservations.read", "inventory.read"]),
            CancellationToken.None);

        Assert.True(allowed);
        Assert.Collection(
            authorization.Requirements,
            requirement => AssertRequirement(requirement, Manager, "reservations.read", propertyScope),
            requirement => AssertRequirement(requirement, Manager, "inventory.read", propertyScope),
            requirement => AssertRequirement(
                requirement,
                Manager,
                AccessControlProfilePermissionCodes.Assign,
                WorkspaceScope));
    }

    [Fact]
    public async Task A_denied_delegated_permission_rejects_assignment()
    {
        StubRoleProvisioner accessControl = new();
        accessControl.Assign(Target, WorkspaceAccessRoles.MembershipMarker, WorkspaceScope);
        StubAuthorizationService authorization = new(requirement =>
            string.Equals(requirement.Permission.Value, "inventory.read", StringComparison.Ordinal)
                ? AccessDecision.Denied("not-granted")
                : AccessDecision.Allowed());
        WorkspaceAccessProfileAssignmentPolicy policy = new(accessControl, authorization);

        bool allowed = await policy.IsAllowedAsync(
            CreateContext(Manager, permissions: ["reservations.read", "inventory.read"]),
            CancellationToken.None);

        Assert.False(allowed);
    }

    [Fact]
    public async Task Subject_without_workspace_membership_is_rejected_before_authorization()
    {
        StubRoleProvisioner accessControl = new();
        StubAuthorizationService authorization = new(_ => AccessDecision.Allowed());
        WorkspaceAccessProfileAssignmentPolicy policy = new(accessControl, authorization);

        bool allowed = await policy.IsAllowedAsync(CreateContext(Manager), CancellationToken.None);

        Assert.False(allowed);
        Assert.Equal(3, accessControl.Checks.Count);
        Assert.Empty(authorization.Requirements);
    }

    [Fact]
    public async Task Another_workspace_or_unknown_nested_scope_is_rejected_without_lookup()
    {
        StubRoleProvisioner accessControl = new();
        WorkspaceAccessProfileAssignmentPolicy policy = CreatePolicy(accessControl);

        bool anotherWorkspace = await policy.IsAllowedAsync(
            CreateContext(
                AccessSubject.System(WorkspaceAccessActors.Provisioner),
                WorkspaceAccessScopes.Create("workspace-b")),
            CancellationToken.None);
        bool unknownNestedScope = await policy.IsAllowedAsync(
            CreateContext(
                AccessSubject.System(WorkspaceAccessActors.Provisioner),
                AccessScope.Parse("tenant:workspace-a/room:room-a")),
            CancellationToken.None);

        Assert.False(anotherWorkspace);
        Assert.False(unknownNestedScope);
        Assert.Empty(accessControl.Checks);
    }

    [Fact]
    public async Task Non_user_target_or_non_delegable_profile_is_rejected_without_lookup()
    {
        StubRoleProvisioner accessControl = new();
        WorkspaceAccessProfileAssignmentPolicy policy = CreatePolicy(accessControl);

        bool serviceAllowed = await policy.IsAllowedAsync(
            CreateContext(
                AccessSubject.System(WorkspaceAccessActors.Provisioner),
                subject: AccessSubject.Service("service-a")),
            CancellationToken.None);
        bool permissionAllowed = await policy.IsAllowedAsync(
            CreateContext(
                AccessSubject.System(WorkspaceAccessActors.Provisioner),
                permissions: ["system.root"]),
            CancellationToken.None);

        Assert.False(serviceAllowed);
        Assert.False(permissionAllowed);
        Assert.Empty(accessControl.Checks);
    }

    private static WorkspaceAccessProfileAssignmentPolicy CreatePolicy(
        StubRoleProvisioner accessControl) =>
        new(accessControl, new StubAuthorizationService(_ => AccessDecision.Allowed()));

    private static AccessProfileAssignmentPolicyContext CreateContext(
        AccessSubject actor,
        AccessScope? assignmentScope = null,
        IReadOnlyList<string>? permissions = null,
        AccessSubject? subject = null) =>
        new(
            Guid.NewGuid(),
            "front-desk",
            WorkspaceScope,
            actor,
            subject ?? Target,
            permissions ?? ["reservations.read"],
            assignmentScope);

    private static void AssertRequirement(
        AccessRequirement requirement,
        AccessSubject subject,
        string permission,
        AccessScope scope)
    {
        Assert.Equal(subject, requirement.Subject);
        Assert.Equal(permission, requirement.Permission.Value);
        Assert.Equal(scope, requirement.Scope);
    }

    private sealed class StubRoleProvisioner : IAccessControlRoleProvisioner
    {
        private readonly HashSet<(AccessSubjectKind Kind, string Subject, string Role, string Scope)> assigned = [];

        public List<(AccessSubject Subject, string Role, AccessScope Scope)> Checks { get; } = [];

        public void Assign(AccessSubject subject, string role, AccessScope scope) =>
            this.assigned.Add((subject.Kind, subject.Id, role, scope.Value));

        public Task<bool> HasAssignmentAsync(
            AccessSubject subject,
            string roleName,
            AccessScope scope,
            CancellationToken cancellationToken = default)
        {
            this.Checks.Add((subject, roleName, scope));
            return Task.FromResult(this.assigned.Contains((subject.Kind, subject.Id, roleName, scope.Value)));
        }

        public Task EnsureRoleAsync(
            AccessControlRoleDefinition role,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task EnsureAssignmentAsync(
            AccessSubject subject,
            string roleName,
            AccessScope scope,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<AccessControlAssignmentRemovalOutcome> RemoveAssignmentAsync(
            AccessSubject subject,
            string roleName,
            AccessScope scope,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AccessControlAssignmentRemovalOutcome.NotFound);

        public Task<AccessControlPage<AccessControlRoleAssignment>> ListAssignmentsAsync(
            string roleName,
            AccessScope scope,
            int page,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AccessControlPage<AccessControlRoleAssignment>([], page, pageSize, false));
    }

    private sealed class StubAuthorizationService(Func<AccessRequirement, AccessDecision> decide)
        : IAccessAuthorizationService
    {
        public List<AccessRequirement> Requirements { get; } = [];

        public Task<AccessDecision> AuthorizeAsync(
            AccessRequirement requirement,
            CancellationToken cancellationToken)
        {
            this.Requirements.Add(requirement);
            return Task.FromResult(decide(requirement));
        }

        public Task<IReadOnlyList<AccessDecision>> AuthorizeManyAsync(
            IReadOnlyList<AccessRequirement> requirements,
            CancellationToken cancellationToken)
        {
            this.Requirements.AddRange(requirements);
            return Task.FromResult<IReadOnlyList<AccessDecision>>(
                requirements.Select(decide).ToArray());
        }
    }
}
