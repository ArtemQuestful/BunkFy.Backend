namespace BunkFy.Modules.Workspaces.Tests;

using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.AccessControl;
using Gma.Framework.Results;
using Gma.Modules.AccessControl.Contracts;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspaceAccessProvisionerTests
{
    private const string WorkspaceId = "workspace-a";
    private static readonly AccessScope WorkspaceScope = WorkspaceAccessScopes.Create(WorkspaceId);
    private static readonly AccessScope PropertyScope = AccessScope.Create(
        AccessScopeSegment.Create(WorkspaceAccessScopes.SegmentName, WorkspaceId),
        AccessScopeSegment.Create("property", "property-a"));

    [Fact]
    public async Task Default_member_provisioning_preserves_custom_profiles_and_retires_legacy_access_last()
    {
        List<string> operations = [];
        FakeRoles roles = new(operations);
        FakeProfiles profiles = new(operations);
        AccessSubject member = AccessSubject.User("member-a");
        roles.Add(member, WorkspaceAccessRoles.LegacyMember, WorkspaceScope);
        AccessProfileDto custom = profiles.AddProfile(WorkspaceScope, "custom", ["reservations.read"]);
        profiles.Assign(member, WorkspaceScope, custom.Id);
        profiles.Assign(member, PropertyScope, custom.Id);
        WorkspaceAccessProvisioner provisioner = new(roles, profiles);

        await provisioner.ProvisionDefaultMemberAsync(WorkspaceId, member.Id, CancellationToken.None);
        await provisioner.ProvisionDefaultMemberAsync(WorkspaceId, member.Id, CancellationToken.None);

        Assert.True(roles.Has(member, WorkspaceAccessRoles.MembershipMarker, WorkspaceScope));
        Assert.False(roles.Has(member, WorkspaceAccessRoles.LegacyMember, WorkspaceScope));
        Assert.Empty(roles.Definitions[WorkspaceAccessRoles.MembershipMarker]);
        Assert.Equal(
            WorkspaceAccessRoles.ProvisionerPermissions,
            roles.Definitions[WorkspaceAccessRoles.Provisioner]);
        Assert.True(roles.Has(
            AccessSubject.System(WorkspaceAccessActors.Provisioner),
            WorkspaceAccessRoles.Provisioner,
            WorkspaceScope));
        AccessProfileDto frontDesk = Assert.Single(
            profiles.Profiles,
            profile => profile.Key == WorkspaceAccessProfileSeeds.FrontDeskKey);
        Assert.Equal(
            new[] { custom.Id, frontDesk.Id }.Order().ToArray(),
            profiles.AssignedProfileIds(member, WorkspaceScope).Order().ToArray());
        Assert.Equal([custom.Id], profiles.AssignedProfileIds(member, PropertyScope));

        int markerAssigned = operations.IndexOf("role:assign:bunkfy-workspace-member-v2:member-a");
        int provisionerAssigned = operations.IndexOf(
            "role:assign:bunkfy-workspace-access-provisioner:bunkfy-workspace-access-provisioner");
        int profileEnsured = operations.IndexOf("profile:ensure:front-desk");
        int reconciled = operations.IndexOf("profile:reconcile:member-a");
        int legacyRemoved = operations.IndexOf("role:remove:bunkfy-workspace-member:member-a");
        Assert.True(provisionerAssigned >= 0 && provisionerAssigned < profileEnsured);
        Assert.True(markerAssigned >= 0 && markerAssigned < reconciled);
        Assert.True(reconciled < legacyRemoved);
    }

    [Fact]
    public async Task Backfill_drains_every_legacy_page_once_and_is_replay_safe()
    {
        List<string> operations = [];
        FakeRoles roles = new(operations);
        FakeProfiles profiles = new(operations);
        WorkspaceAccessProvisioner provisioner = new(roles, profiles);
        AccessSubject[] members = Enumerable.Range(1, 205)
            .Select(index => AccessSubject.User($"member-{index:D3}"))
            .ToArray();
        foreach (AccessSubject member in members)
        {
            roles.Add(member, WorkspaceAccessRoles.LegacyMember, WorkspaceScope);
        }

        AccessProfileDto custom = profiles.AddProfile(WorkspaceScope, "custom", ["inventory.read"]);
        profiles.Assign(members[0], WorkspaceScope, custom.Id);

        WorkspaceAccessBootstrapResult first = await provisioner.BackfillLegacyMembersAsync(
            WorkspaceId,
            CancellationToken.None);
        WorkspaceAccessBootstrapResult replay = await provisioner.BackfillLegacyMembersAsync(
            WorkspaceId,
            CancellationToken.None);

        Assert.Equal(WorkspaceAccessProfileSeeds.Version, first.SeedVersion);
        Assert.Equal(WorkspaceAccessProfileSeeds.All.Count, first.SeedProfileCount);
        Assert.Equal(205, first.MigratedMemberCount);
        Assert.Equal(0, replay.MigratedMemberCount);
        Assert.Equal(WorkspaceAccessProfileSeeds.All.Count, profiles.Profiles.Length - 1);
        Assert.All(roles.ListRequests, request => Assert.Equal(1, request.Page));
        Assert.Equal(5, roles.ListRequests.Count);

        AccessProfileDto frontDesk = Assert.Single(
            profiles.Profiles,
            profile => profile.Key == WorkspaceAccessProfileSeeds.FrontDeskKey);
        foreach (AccessSubject member in members)
        {
            Assert.True(roles.Has(member, WorkspaceAccessRoles.MembershipMarker, WorkspaceScope));
            Assert.False(roles.Has(member, WorkspaceAccessRoles.LegacyMember, WorkspaceScope));
            Assert.Contains(frontDesk.Id, profiles.AssignedProfileIds(member, WorkspaceScope));
        }

        Assert.Contains(custom.Id, profiles.AssignedProfileIds(members[0], WorkspaceScope));
    }

    [Fact]
    public async Task Failed_profile_reconciliation_keeps_the_legacy_grant_for_a_safe_retry()
    {
        List<string> operations = [];
        FakeRoles roles = new(operations);
        FakeProfiles profiles = new(operations) { FailReconciliation = true };
        AccessSubject member = AccessSubject.User("member-a");
        roles.Add(member, WorkspaceAccessRoles.LegacyMember, WorkspaceScope);
        WorkspaceAccessProvisioner provisioner = new(roles, profiles);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provisioner.ProvisionDefaultMemberAsync(WorkspaceId, member.Id, CancellationToken.None));

        Assert.True(roles.Has(member, WorkspaceAccessRoles.MembershipMarker, WorkspaceScope));
        Assert.True(roles.Has(member, WorkspaceAccessRoles.LegacyMember, WorkspaceScope));
        Assert.DoesNotContain("role:remove:bunkfy-workspace-member:member-a", operations);

        profiles.FailReconciliation = false;
        await provisioner.ProvisionDefaultMemberAsync(WorkspaceId, member.Id, CancellationToken.None);

        Assert.False(roles.Has(member, WorkspaceAccessRoles.LegacyMember, WorkspaceScope));
    }

    [Fact]
    public async Task Backfill_rejects_an_unexpected_non_user_legacy_assignment()
    {
        FakeRoles roles = new([]);
        roles.Add(AccessSubject.Service("legacy-service"), WorkspaceAccessRoles.LegacyMember, WorkspaceScope);
        WorkspaceAccessProvisioner provisioner = new(roles, new FakeProfiles([]));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provisioner.BackfillLegacyMembersAsync(WorkspaceId, CancellationToken.None));

        Assert.Contains("non-user", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inspection_counts_seed_states_and_every_assignment_page()
    {
        FakeRoles roles = new([]);
        FakeProfiles profiles = new([]);
        profiles.AddProfile(WorkspaceScope, WorkspaceAccessProfileSeeds.Manager);
        profiles.AddProfile(
            WorkspaceScope,
            WorkspaceAccessProfileSeeds.FrontDesk,
            AccessProfileStatus.Archived);
        profiles.AddProfile(WorkspaceScope, WorkspaceAccessProfileSeeds.Housekeeping);
        profiles.AddProfile(
            WorkspaceScope,
            WorkspaceAccessProfileSeeds.ViewerKey,
            ["properties.read"]);

        foreach (int index in Enumerable.Range(1, 205))
        {
            roles.Add(
                AccessSubject.User($"legacy-{index:D3}"),
                WorkspaceAccessRoles.LegacyMember,
                WorkspaceScope);
        }

        foreach (int index in Enumerable.Range(1, 101))
        {
            roles.Add(
                AccessSubject.User($"marker-{index:D3}"),
                WorkspaceAccessRoles.MembershipMarker,
                WorkspaceScope);
        }

        WorkspaceAccessBootstrapStatus status = await new WorkspaceAccessProvisioner(roles, profiles)
            .InspectAsync(WorkspaceId, CancellationToken.None);

        Assert.Equal(WorkspaceAccessProfileSeeds.Version, status.SeedVersion);
        Assert.Equal(WorkspaceAccessProfileSeeds.All.Count, status.ExpectedSeedProfileCount);
        Assert.Equal(3, status.ActiveSeedProfileCount);
        Assert.Equal(1, status.DriftedSeedProfileCount);
        Assert.Equal(1, status.ArchivedSeedProfileCount);
        Assert.Equal(205, status.LegacyMemberCount);
        Assert.Equal(101, status.MarkerMemberCount);
        Assert.True(status.RequiresBackfill);
        Assert.Equal(
            [
                (WorkspaceAccessRoles.LegacyMember, 1, 100),
                (WorkspaceAccessRoles.LegacyMember, 2, 100),
                (WorkspaceAccessRoles.LegacyMember, 3, 100),
                (WorkspaceAccessRoles.MembershipMarker, 1, 100),
                (WorkspaceAccessRoles.MembershipMarker, 2, 100)
            ],
            roles.ListRequests);
    }

    [Fact]
    public async Task Active_workspace_initialization_reconciles_an_outdated_seed_once()
    {
        FakeProfiles profiles = new([]);
        AccessProfileDto existing = profiles.AddProfile(
            WorkspaceScope,
            WorkspaceAccessProfileSeeds.ManagerKey,
            ["properties.read"]);
        FakeProfileManager profileManager = new(profiles);
        WorkspaceAccessProvisioner provisioner = new(
            new FakeRoles([]),
            profiles,
            profiles,
            profileManager);

        await provisioner.EnsureSeedProfilesAsync(WorkspaceId, CancellationToken.None);
        await provisioner.EnsureSeedProfilesAsync(WorkspaceId, CancellationToken.None);

        Assert.Equal(WorkspaceAccessProfileSeeds.All.Count, profiles.Profiles.Length);
        AccessProfileDto managerProfile = Assert.Single(
            profiles.Profiles,
            profile => profile.Key == WorkspaceAccessProfileSeeds.ManagerKey);
        Assert.Equal(existing.Id, managerProfile.Id);
        Assert.Equal(2, managerProfile.Version);
        Assert.Equal(
            WorkspaceAccessProfileSeeds.Manager.Permissions.Order(StringComparer.Ordinal),
            managerProfile.Permissions.Order(StringComparer.Ordinal));
        Assert.Equal(1, profileManager.UpdateCount);
    }

    [Fact]
    public async Task Active_workspace_initialization_fails_when_an_outdated_seed_cannot_be_reconciled()
    {
        FakeProfiles profiles = new([]);
        AccessProfileDto existing = profiles.AddProfile(
            WorkspaceScope,
            WorkspaceAccessProfileSeeds.ManagerKey,
            ["properties.read"]);
        FakeProfileManager profileManager = new(profiles) { FailUpdate = true };
        WorkspaceAccessProvisioner provisioner = new(
            new FakeRoles([]),
            profiles,
            profiles,
            profileManager);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provisioner.EnsureSeedProfilesAsync(WorkspaceId, CancellationToken.None));

        Assert.Contains("could not be reconciled", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(existing, Assert.Single(profiles.Profiles));
        Assert.Equal(1, profileManager.UpdateCount);
    }

    [Fact]
    public async Task Backfill_stops_before_member_migration_when_a_seed_is_archived()
    {
        FakeRoles roles = new([]);
        FakeProfiles profiles = new([]);
        profiles.AddProfile(
            WorkspaceScope,
            WorkspaceAccessProfileSeeds.ManagerKey,
            WorkspaceAccessProfileSeeds.Manager.Permissions,
            AccessProfileStatus.Archived);
        AccessSubject member = AccessSubject.User("member-a");
        roles.Add(member, WorkspaceAccessRoles.LegacyMember, WorkspaceScope);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new WorkspaceAccessProvisioner(roles, profiles)
                .BackfillLegacyMembersAsync(WorkspaceId, CancellationToken.None));

        Assert.True(roles.Has(member, WorkspaceAccessRoles.LegacyMember, WorkspaceScope));
        Assert.False(roles.Has(member, WorkspaceAccessRoles.MembershipMarker, WorkspaceScope));
        Assert.Empty(profiles.AssignedProfileIds(member, WorkspaceScope));
    }

    [Fact]
    public async Task Lifecycle_snapshot_maps_legacy_access_without_mutating_assignments()
    {
        FakeRoles roles = new([]);
        FakeProfiles profiles = new([]);
        AccessSubject member = AccessSubject.User("member-a");
        roles.Add(member, WorkspaceAccessRoles.LegacyMember, WorkspaceScope);
        AccessProfileDto frontDesk = profiles.AddProfile(
            WorkspaceScope,
            WorkspaceAccessProfileSeeds.FrontDeskKey,
            WorkspaceAccessProfileSeeds.FrontDesk.Permissions);
        AccessProfileDto custom = profiles.AddProfile(
            WorkspaceScope,
            "night-auditor",
            ["reservations.read"]);
        profiles.Assign(member, WorkspaceScope, custom.Id);

        IReadOnlyCollection<WorkspaceStaffAccessProfileTarget> snapshot =
            await new WorkspaceAccessProvisioner(roles, profiles)
                .CaptureRestorableProfilesAsync(WorkspaceId, member.Id, CancellationToken.None);

        Assert.Equal(
            new[] { custom.Id, frontDesk.Id }.Order(),
            snapshot.Select(target => target.ProfileId).Order());
        Assert.All(snapshot, target => Assert.Equal(WorkspaceScope.Value, target.AssignmentScope));
        Assert.False(roles.Has(member, WorkspaceAccessRoles.MembershipMarker, WorkspaceScope));
        Assert.True(roles.Has(member, WorkspaceAccessRoles.LegacyMember, WorkspaceScope));
        Assert.Equal([custom.Id], profiles.AssignedProfileIds(member, WorkspaceScope));
    }

    [Fact]
    public async Task Lifecycle_snapshot_does_not_block_denial_when_legacy_seed_is_missing()
    {
        FakeRoles roles = new([]);
        FakeProfiles profiles = new([]);
        AccessSubject member = AccessSubject.User("member-a");
        roles.Add(member, WorkspaceAccessRoles.LegacyMember, WorkspaceScope);

        IReadOnlyCollection<WorkspaceStaffAccessProfileTarget> snapshot =
            await new WorkspaceAccessProvisioner(roles, profiles)
                .CaptureRestorableProfilesAsync(
                    WorkspaceId,
                    member.Id,
                    CancellationToken.None);

        Assert.Empty(snapshot);
        Assert.Empty(profiles.Profiles);
        Assert.True(roles.Has(member, WorkspaceAccessRoles.LegacyMember, WorkspaceScope));
    }

    [Fact]
    public async Task Lifecycle_denial_removes_profiles_marker_and_legacy_assignment_idempotently()
    {
        FakeRoles roles = new([]);
        FakeProfiles profiles = new([]);
        AccessSubject member = AccessSubject.User("member-a");
        roles.Add(member, WorkspaceAccessRoles.MembershipMarker, WorkspaceScope);
        roles.Add(member, WorkspaceAccessRoles.LegacyMember, WorkspaceScope);
        AccessProfileDto profile = profiles.AddProfile(
            WorkspaceScope,
            "custom",
            ["inventory.read"]);
        profiles.Assign(member, WorkspaceScope, profile.Id);
        profiles.Assign(member, PropertyScope, profile.Id);
        WorkspaceAccessProvisioner provisioner = new(roles, profiles);

        await provisioner.DenyMemberAsync(WorkspaceId, member.Id, CancellationToken.None);
        await provisioner.DenyMemberAsync(WorkspaceId, member.Id, CancellationToken.None);

        Assert.Empty(profiles.AssignedProfileIds(member, WorkspaceScope));
        Assert.Empty(profiles.AssignedProfileIds(member, PropertyScope));
        Assert.False(roles.Has(member, WorkspaceAccessRoles.MembershipMarker, WorkspaceScope));
        Assert.False(roles.Has(member, WorkspaceAccessRoles.LegacyMember, WorkspaceScope));
    }

    [Fact]
    public async Task Lifecycle_restoration_installs_only_the_exact_snapshot_and_marker()
    {
        FakeRoles roles = new([]);
        FakeProfiles profiles = new([]);
        AccessSubject member = AccessSubject.User("member-a");
        AccessProfileDto first = profiles.AddProfile(
            WorkspaceScope,
            "first",
            ["properties.read"]);
        AccessProfileDto second = profiles.AddProfile(
            WorkspaceScope,
            "second",
            ["reservations.read"]);
        profiles.Assign(member, WorkspaceScope, second.Id);

        await new WorkspaceAccessProvisioner(roles, profiles).RestoreMemberAsync(
            WorkspaceId,
            member.Id,
            [new WorkspaceStaffAccessProfileTarget(first.Id, WorkspaceScope.Value)],
            CancellationToken.None);

        Assert.Equal([first.Id], profiles.AssignedProfileIds(member, WorkspaceScope));
        Assert.True(roles.Has(member, WorkspaceAccessRoles.MembershipMarker, WorkspaceScope));
        Assert.False(roles.Has(member, WorkspaceAccessRoles.LegacyMember, WorkspaceScope));
    }

    [Fact]
    public async Task Lifecycle_snapshot_and_restore_preserve_property_assignment_scopes()
    {
        FakeRoles roles = new([]);
        FakeProfiles profiles = new([]);
        AccessSubject member = AccessSubject.User("member-a");
        AccessProfileDto profile = profiles.AddProfile(
            WorkspaceScope,
            "property-manager",
            ["properties.read"]);
        profiles.Assign(member, PropertyScope, profile.Id);
        WorkspaceAccessProvisioner provisioner = new(roles, profiles);

        IReadOnlyCollection<WorkspaceStaffAccessProfileTarget> snapshot =
            await provisioner.CaptureRestorableProfilesAsync(
                WorkspaceId,
                member.Id,
                CancellationToken.None);
        await provisioner.DenyMemberAsync(WorkspaceId, member.Id, CancellationToken.None);
        await provisioner.RestoreMemberAsync(
            WorkspaceId,
            member.Id,
            snapshot,
            CancellationToken.None);

        WorkspaceStaffAccessProfileTarget target = Assert.Single(snapshot);
        Assert.Equal(profile.Id, target.ProfileId);
        Assert.Equal(PropertyScope.Value, target.AssignmentScope);
        Assert.Empty(profiles.AssignedProfileIds(member, WorkspaceScope));
        Assert.Equal([profile.Id], profiles.AssignedProfileIds(member, PropertyScope));
    }

    [Fact]
    public async Task Recovery_inspection_accepts_only_exact_current_two_property_access()
    {
        Guid firstPropertyId = Guid.NewGuid();
        Guid secondPropertyId = Guid.NewGuid();
        AccessScope firstProperty = WorkspaceAccessScopes.CreateProperty(
            WorkspaceId,
            firstPropertyId);
        AccessScope secondProperty = WorkspaceAccessScopes.CreateProperty(
            WorkspaceId,
            secondPropertyId);
        FakeRoles roles = new([]);
        FakeProfiles profiles = new([]);
        AccessSubject member = AccessSubject.User("member-a");
        AccessProfileDto manager = profiles.AddProfile(
            WorkspaceScope,
            WorkspaceAccessProfileSeeds.Manager);
        roles.Add(
            member,
            WorkspaceAccessRoles.MembershipMarker,
            WorkspaceScope);
        profiles.Assign(member, firstProperty, manager.Id);
        profiles.Assign(member, secondProperty, manager.Id);

        WorkspaceMemberAccessEquivalence result = await new
            WorkspaceAccessProvisioner(roles, profiles)
            .InspectMemberEquivalenceAsync(
                WorkspaceId,
                member.Id,
                manager,
                [secondPropertyId, firstPropertyId],
                CancellationToken.None);

        Assert.Equal(WorkspaceMemberAccessEquivalence.Equivalent, result);
    }

    [Theory]
    [InlineData("missing-marker")]
    [InlineData("legacy")]
    [InlineData("owner")]
    [InlineData("extra-profile")]
    [InlineData("extra-scope")]
    [InlineData("version-drift")]
    public async Task Recovery_inspection_rejects_non_exact_or_broadened_access(
        string hazard)
    {
        Guid firstPropertyId = Guid.NewGuid();
        Guid secondPropertyId = Guid.NewGuid();
        AccessScope firstProperty = WorkspaceAccessScopes.CreateProperty(
            WorkspaceId,
            firstPropertyId);
        AccessScope secondProperty = WorkspaceAccessScopes.CreateProperty(
            WorkspaceId,
            secondPropertyId);
        FakeRoles roles = new([]);
        FakeProfiles profiles = new([]);
        AccessSubject member = AccessSubject.User("member-a");
        AccessProfileDto manager = profiles.AddProfile(
            WorkspaceScope,
            WorkspaceAccessProfileSeeds.Manager);
        if (!string.Equals(hazard, "missing-marker", StringComparison.Ordinal))
        {
            roles.Add(
                member,
                WorkspaceAccessRoles.MembershipMarker,
                WorkspaceScope);
        }

        profiles.Assign(member, firstProperty, manager.Id);
        profiles.Assign(member, secondProperty, manager.Id);
        switch (hazard)
        {
            case "missing-marker":
                break;
            case "legacy":
                roles.Add(member, WorkspaceAccessRoles.LegacyMember, WorkspaceScope);
                break;
            case "owner":
                roles.Add(member, WorkspaceAccessRoles.Owner, WorkspaceScope);
                break;
            case "extra-profile":
                AccessProfileDto extra = profiles.AddProfile(
                    WorkspaceScope,
                    "extra",
                    ["properties.read"]);
                profiles.Assign(member, firstProperty, extra.Id);
                break;
            case "extra-scope":
                profiles.Assign(member, WorkspaceScope, manager.Id);
                break;
            case "version-drift":
                manager = manager with { Version = manager.Version + 1 };
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown recovery hazard '{hazard}'.");
        }

        WorkspaceMemberAccessEquivalence result = await new
            WorkspaceAccessProvisioner(roles, profiles)
            .InspectMemberEquivalenceAsync(
                WorkspaceId,
                member.Id,
                manager,
                [firstPropertyId, secondPropertyId],
                CancellationToken.None);

        Assert.Equal(WorkspaceMemberAccessEquivalence.Conflict, result);
    }

    [Fact]
    public async Task Member_provisioning_preserves_stable_manager_failure_and_legacy_access()
    {
        FakeRoles roles = new([]);
        FakeProfiles profiles = new([]);
        AccessSubject member = AccessSubject.User("member-a");
        AccessProfileDto manager = profiles.AddProfile(
            WorkspaceScope,
            WorkspaceAccessProfileSeeds.Manager);
        roles.Add(member, WorkspaceAccessRoles.LegacyMember, WorkspaceScope);
        Error failure = ScopedAccessProfileManagementErrors
            .PermissionEscalation;
        WorkspaceAccessProvisioner provisioner = new(
            roles,
            profiles,
            profiles,
            [],
            [new FailingScopedProfileManager(failure)]);

        Result result = await provisioner.ProvisionMemberAsync(
            WorkspaceId,
            member.Id,
            manager.Id,
            [Guid.NewGuid()],
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(failure, result.Error);
        Assert.True(roles.Has(
            member,
            WorkspaceAccessRoles.MembershipMarker,
            WorkspaceScope));
        Assert.True(roles.Has(
            member,
            WorkspaceAccessRoles.LegacyMember,
            WorkspaceScope));
    }

    private sealed class FakeRoles(List<string> operations) : IAccessControlRoleProvisioner
    {
        private readonly HashSet<RoleAssignment> assignments = [];

        public Dictionary<string, IReadOnlyList<string>> Definitions { get; } = new(StringComparer.Ordinal);
        public List<(string Role, int Page, int PageSize)> ListRequests { get; } = [];

        public void Add(AccessSubject subject, string role, AccessScope scope) =>
            this.assignments.Add(new RoleAssignment(subject.Kind, subject.Id, role, scope.Value));

        public bool Has(AccessSubject subject, string role, AccessScope scope) =>
            this.assignments.Contains(new RoleAssignment(subject.Kind, subject.Id, role, scope.Value));

        public Task EnsureRoleAsync(
            AccessControlRoleDefinition role,
            CancellationToken cancellationToken = default)
        {
            this.Definitions[role.Name] = role.Permissions.ToArray();
            operations.Add($"role:ensure:{role.Name}");
            return Task.CompletedTask;
        }

        public Task EnsureAssignmentAsync(
            AccessSubject subject,
            string roleName,
            AccessScope scope,
            CancellationToken cancellationToken = default)
        {
            this.Add(subject, roleName, scope);
            operations.Add($"role:assign:{roleName}:{subject.Id}");
            return Task.CompletedTask;
        }

        public Task<AccessControlAssignmentRemovalOutcome> RemoveAssignmentAsync(
            AccessSubject subject,
            string roleName,
            AccessScope scope,
            CancellationToken cancellationToken = default)
        {
            bool removed = this.assignments.Remove(new RoleAssignment(
                subject.Kind,
                subject.Id,
                roleName,
                scope.Value));
            operations.Add($"role:remove:{roleName}:{subject.Id}");
            return Task.FromResult(removed
                ? AccessControlAssignmentRemovalOutcome.Removed
                : AccessControlAssignmentRemovalOutcome.NotFound);
        }

        public Task<bool> HasAssignmentAsync(
            AccessSubject subject,
            string roleName,
            AccessScope scope,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(this.Has(subject, roleName, scope));

        public Task<AccessControlPage<AccessControlRoleAssignment>> ListAssignmentsAsync(
            string roleName,
            AccessScope scope,
            int page,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            this.ListRequests.Add((roleName, page, pageSize));
            RoleAssignment[] matching = this.assignments
                .Where(assignment =>
                    assignment.Role == roleName &&
                    assignment.Scope == scope.Value)
                .OrderBy(assignment => assignment.SubjectId, StringComparer.Ordinal)
                .ToArray();
            AccessControlRoleAssignment[] items = matching
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(assignment => new AccessControlRoleAssignment(
                    Guid.NewGuid(),
                    assignment.SubjectKind,
                    assignment.SubjectId,
                    assignment.Role,
                    scope,
                    DateTimeOffset.UnixEpoch))
                .ToArray();
            return Task.FromResult(new AccessControlPage<AccessControlRoleAssignment>(
                items,
                page,
                pageSize,
                matching.Length > page * pageSize));
        }

        private sealed record RoleAssignment(
            AccessSubjectKind SubjectKind,
            string SubjectId,
            string Role,
            string Scope);
    }

    private sealed class FakeProfiles(List<string> operations)
        : IAccessProfileProvisioner, IScopedAccessProfileProvisioner
    {
        private readonly Dictionary<(AccessSubjectKind Kind, string SubjectId, string Scope), HashSet<Guid>> assignments = [];
        private readonly Dictionary<(string Scope, string Key), AccessProfileDto> profiles = [];

        public bool FailReconciliation { get; set; }
        public AccessProfileDto[] Profiles => this.profiles.Values.ToArray();

        public AccessProfileDto AddProfile(
            AccessScope scope,
            string key,
            IReadOnlyCollection<string> permissions,
            AccessProfileStatus status = AccessProfileStatus.Active)
        {
            AccessProfileDto profile = CreateProfile(scope, key, key, permissions, status);
            this.profiles[(scope.Value, key)] = profile;
            return profile;
        }

        public AccessProfileDto AddProfile(
            AccessScope scope,
            AccessProfileDefinition definition,
            AccessProfileStatus status = AccessProfileStatus.Active)
        {
            AccessProfileDto profile = CreateProfile(
                scope,
                definition.Key,
                definition.DisplayName,
                definition.Permissions,
                status) with
            {
                Description = definition.Description?.Trim() ?? string.Empty
            };
            this.profiles[(scope.Value, definition.Key)] = profile;
            return profile;
        }

        public void Assign(AccessSubject subject, AccessScope scope, Guid profileId) =>
            this.GetAssignmentSet(subject, scope).Add(profileId);

        public Guid[] AssignedProfileIds(AccessSubject subject, AccessScope scope) =>
            this.GetAssignmentSet(subject, scope).ToArray();

        public Task<AccessProfileDto> EnsureProfileAsync(
            AccessScope ownerScope,
            AccessProfileDefinition definition,
            AccessSubject actor,
            CancellationToken cancellationToken = default)
        {
            if (!this.profiles.TryGetValue((ownerScope.Value, definition.Key), out AccessProfileDto? profile))
            {
                profile = CreateProfile(
                    ownerScope,
                    definition.Key,
                    definition.DisplayName,
                    definition.Permissions) with
                {
                    Description = definition.Description?.Trim() ?? string.Empty
                };
                this.profiles[(ownerScope.Value, definition.Key)] = profile;
            }

            operations.Add($"profile:ensure:{definition.Key}");
            return Task.FromResult(profile);
        }

        public AccessProfileDto ReconcileProfile(
            Guid profileId,
            AccessProfileUpdate update)
        {
            KeyValuePair<(string Scope, string Key), AccessProfileDto> entry =
                this.profiles.Single(pair => pair.Value.Id == profileId);
            if (entry.Value.Version != update.ExpectedVersion)
            {
                throw new InvalidOperationException("Profile version conflict.");
            }

            AccessProfileDto reconciled = entry.Value with
            {
                DisplayName = update.DisplayName.Trim(),
                Description = update.Description?.Trim() ?? string.Empty,
                Permissions = update.Permissions.Order(StringComparer.Ordinal).ToArray(),
                Version = entry.Value.Version + 1,
                LastChangedAtUtc = entry.Value.LastChangedAtUtc.AddMinutes(1)
            };
            this.profiles[entry.Key] = reconciled;
            operations.Add($"profile:update:{entry.Key.Key}");
            return reconciled;
        }

        public Task<AccessProfileDto?> FindProfileByKeyAsync(
            AccessScope ownerScope,
            string key,
            CancellationToken cancellationToken = default)
        {
            this.profiles.TryGetValue((ownerScope.Value, key), out AccessProfileDto? profile);
            return Task.FromResult(profile);
        }

        public Task<AccessProfileAssignmentSet> GetSubjectAssignmentsAsync(
            AccessSubject subject,
            AccessScope ownerScope,
            CancellationToken cancellationToken = default)
        {
            HashSet<Guid> assigned = this.GetAssignmentSet(subject, ownerScope);
            AccessProfileDto[] current = this.profiles.Values
                .Where(profile => assigned.Contains(profile.Id))
                .ToArray();
            return Task.FromResult(new AccessProfileAssignmentSet(subject, ownerScope, current));
        }

        public Task<AccessProfileAssignmentReconciliation> ReconcileSubjectAssignmentsAsync(
            AccessSubject subject,
            AccessScope ownerScope,
            IReadOnlyCollection<Guid> profileIds,
            AccessSubject actor,
            CancellationToken cancellationToken = default)
        {
            operations.Add($"profile:reconcile:{subject.Id}");
            if (this.FailReconciliation)
            {
                throw new InvalidOperationException("Profile reconciliation failed.");
            }

            HashSet<Guid> assigned = this.GetAssignmentSet(subject, ownerScope);
            int assignedCount = profileIds.Count(profileId => !assigned.Contains(profileId));
            int unassignedCount = assigned.Count(profileId => !profileIds.Contains(profileId));
            assigned.Clear();
            assigned.UnionWith(profileIds);
            return Task.FromResult(new AccessProfileAssignmentReconciliation(
                subject,
                ownerScope,
                profileIds.ToArray(),
                assignedCount,
                unassignedCount));
        }

        public Task<ScopedAccessProfileAssignmentSet> GetSubjectScopedAssignmentsAsync(
            AccessSubject subject,
            AccessScope ownerScope,
            CancellationToken cancellationToken = default)
        {
            ScopedAccessProfileAssignment[] current = this.assignments
                .Where(entry =>
                    entry.Key.Kind == subject.Kind &&
                    entry.Key.SubjectId == subject.Id &&
                    IsWithin(ownerScope.Value, entry.Key.Scope))
                .SelectMany(entry => entry.Value.Select(profileId =>
                    (ProfileId: profileId, AssignmentScope: AccessScope.Parse(entry.Key.Scope))))
                .Where(entry =>
                    this.profiles.Values.Any(profile =>
                        profile.Id == entry.ProfileId &&
                        profile.OwnerScope == ownerScope.Value))
                .Select(entry => new ScopedAccessProfileAssignment(
                    this.profiles.Values.Single(profile => profile.Id == entry.ProfileId),
                    entry.AssignmentScope))
                .ToArray();
            return Task.FromResult(new ScopedAccessProfileAssignmentSet(subject, ownerScope, current));
        }

        public async Task<ScopedAccessProfileAssignmentReconciliation>
            ReconcileSubjectScopedAssignmentsAsync(
                AccessSubject subject,
                AccessScope ownerScope,
                IReadOnlyCollection<AccessProfileAssignmentTarget> targets,
                AccessSubject actor,
                CancellationToken cancellationToken = default)
        {
            operations.Add($"profile:reconcile:{subject.Id}");
            if (this.FailReconciliation)
            {
                throw new InvalidOperationException("Profile reconciliation failed.");
            }

            ScopedAccessProfileAssignmentSet existing = await this.GetSubjectScopedAssignmentsAsync(
                subject,
                ownerScope,
                cancellationToken);
            AccessProfileAssignmentTarget[] current = existing.Assignments
                .Select(assignment => new AccessProfileAssignmentTarget(
                    assignment.Profile.Id,
                    assignment.AssignmentScope))
                .ToArray();
            AccessProfileAssignmentTarget[] desired = targets.Distinct().ToArray();

            foreach (AccessProfileAssignmentTarget removed in current.Except(desired))
            {
                this.GetAssignmentSet(subject, removed.AssignmentScope).Remove(removed.ProfileId);
            }

            foreach (AccessProfileAssignmentTarget added in desired.Except(current))
            {
                this.GetAssignmentSet(subject, added.AssignmentScope).Add(added.ProfileId);
            }

            return new ScopedAccessProfileAssignmentReconciliation(
                subject,
                ownerScope,
                desired,
                desired.Except(current).Count(),
                current.Except(desired).Count());
        }

        private HashSet<Guid> GetAssignmentSet(AccessSubject subject, AccessScope scope)
        {
            (AccessSubjectKind Kind, string Id, string Scope) key = (subject.Kind, subject.Id, scope.Value);
            if (!this.assignments.TryGetValue(key, out HashSet<Guid>? assigned))
            {
                assigned = [];
                this.assignments[key] = assigned;
            }

            return assigned;
        }

        private static bool IsWithin(string ownerScope, string assignmentScope) =>
            string.Equals(ownerScope, assignmentScope, StringComparison.Ordinal) ||
            assignmentScope.StartsWith(ownerScope + "/", StringComparison.Ordinal);

        private static AccessProfileDto CreateProfile(
            AccessScope scope,
            string key,
            string displayName,
            IReadOnlyCollection<string> permissions,
            AccessProfileStatus status = AccessProfileStatus.Active) => new(
            Guid.NewGuid(),
            scope.Value,
            key,
            displayName,
            string.Empty,
            status,
            1,
            permissions.ToArray(),
            0,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
    }

    private sealed class FakeProfileManager(FakeProfiles profiles)
        : IAccessProfileManager
    {
        public int UpdateCount { get; private set; }
        public bool FailUpdate { get; init; }

        public Task<Result<AccessProfileDto>> UpdateProfileAsync(
            Guid profileId,
            AccessScope ownerScope,
            AccessProfileUpdate update,
            AccessSubject actor,
            CancellationToken cancellationToken = default)
        {
            this.UpdateCount++;
            if (this.FailUpdate)
            {
                return Task.FromResult(Result.Failure<AccessProfileDto>(
                    new Error("Workspaces.AccessProfileSeedReconciliationFailed", "Failed.")));
            }

            return Task.FromResult(Result.Success(
                profiles.ReconcileProfile(profileId, update)));
        }

        public Task<Result<AccessControlPage<AccessProfileDto>>> ListProfilesAsync(
            AccessScope ownerScope,
            bool includeArchived,
            int page,
            int pageSize,
            AccessSubject actor,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<IReadOnlyList<string>>> ListAllowedPermissionsAsync(
            AccessScope ownerScope,
            AccessSubject actor,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<AccessProfileDto>> GetProfileAsync(
            Guid profileId,
            AccessScope ownerScope,
            AccessSubject actor,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<AccessProfileDto>> CreateProfileAsync(
            AccessScope ownerScope,
            AccessProfileDefinition definition,
            AccessSubject actor,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result> ArchiveProfileAsync(
            Guid profileId,
            AccessScope ownerScope,
            long expectedVersion,
            AccessSubject actor,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FailingScopedProfileManager(Error error)
        : IScopedAccessProfileManager
    {
        public Task<Result<ScopedAccessProfileAssignmentSet>>
            GetSubjectAssignmentsAsync(
                AccessSubject subject,
                AccessScope ownerScope,
                AccessSubject actor,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<ScopedAccessProfileAssignmentReconciliation>>
            ReconcileSubjectAssignmentsAsync(
                AccessSubject subject,
                AccessScope ownerScope,
                IReadOnlyCollection<AccessProfileAssignmentTarget> targets,
                AccessSubject actor,
                CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Failure<
                ScopedAccessProfileAssignmentReconciliation>(error));
    }
}
