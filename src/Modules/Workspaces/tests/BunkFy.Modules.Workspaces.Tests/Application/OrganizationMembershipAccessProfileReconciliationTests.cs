namespace BunkFy.Modules.Workspaces.Tests;

using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Application.Handlers;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.AccessControl;
using Gma.Framework.Pagination;
using Gma.Modules.AccessControl.Contracts;
using Gma.Modules.Organizations.Contracts;
using Xunit;

[Trait("Category", "Unit")]
public sealed class OrganizationMembershipAccessProfileReconciliationTests
{
    private static readonly Guid OrganizationId = Guid.NewGuid();
    private static readonly Guid MembershipId = Guid.NewGuid();
    private static readonly Guid StaffMemberId = Guid.NewGuid();
    private static readonly string WorkspaceId = OrganizationId.ToString("D");
    private static readonly string SubjectId = Guid.NewGuid().ToString("D");

    [Fact]
    public async Task Stale_demotion_grants_current_owner_before_clearing_member_access()
    {
        TestContext context = CreateContext(
            CreateMembership(OrganizationMembershipRole.Owner, version: 4),
            [CreateStaff(StaffStatus.Active)]);
        context.Roles.Add(WorkspaceAccessRoles.MembershipMarker);
        context.Roles.Add(WorkspaceAccessRoles.LegacyMember);
        context.Profiles.AssignCustom("night-auditor");

        await context.Handler.HandleAsync(
            CreateEvent(
                OrganizationMembershipRole.Member,
                OrganizationMembershipStatus.Active,
                OrganizationMembershipChange.DemotedToMember,
                version: 3),
            CancellationToken.None);

        Assert.True(context.Roles.Has(WorkspaceAccessRoles.Owner));
        Assert.False(context.Roles.Has(WorkspaceAccessRoles.MembershipMarker));
        Assert.False(context.Roles.Has(WorkspaceAccessRoles.LegacyMember));
        Assert.Empty(context.Profiles.AssignedProfileKeys());
        AssertOrdered(
            context.Operations,
            "subject:lock",
            "staff:lock",
            $"role:assign:{WorkspaceAccessRoles.Owner}",
            "profiles:reconcile:0");
    }

    [Fact]
    public async Task Stale_promotion_removes_owner_before_restoring_active_staff_access()
    {
        TestContext context = CreateContext(
            CreateMembership(OrganizationMembershipRole.Member, version: 4),
            [CreateStaff(StaffStatus.Active)]);
        context.Roles.Add(WorkspaceAccessRoles.Owner);

        await context.Handler.HandleAsync(
            CreateEvent(
                OrganizationMembershipRole.Owner,
                OrganizationMembershipStatus.Active,
                OrganizationMembershipChange.PromotedToOwner,
                version: 3),
            CancellationToken.None);

        Assert.False(context.Roles.Has(WorkspaceAccessRoles.Owner));
        Assert.True(context.Roles.Has(WorkspaceAccessRoles.MembershipMarker));
        Assert.Contains(
            WorkspaceAccessProfileSeeds.FrontDeskKey,
            context.Profiles.AssignedProfileKeys());
        AssertOrdered(
            context.Operations,
            "subject:lock",
            "staff:lock",
            $"role:remove:{WorkspaceAccessRoles.Owner}",
            $"role:assign:{WorkspaceAccessRoles.MembershipMarker}",
            "profiles:reconcile:1");
    }

    [Theory]
    [InlineData(StaffStatus.Unknown)]
    [InlineData(StaffStatus.Suspended)]
    [InlineData(StaffStatus.Departed)]
    public async Task Inactive_staff_member_loses_owner_and_ordinary_access(StaffStatus status)
    {
        TestContext context = CreateContext(
            CreateMembership(OrganizationMembershipRole.Member, version: 4),
            [CreateStaff(status)]);
        SeedAllAccess(context);

        await context.Handler.HandleAsync(
            CreateEvent(
                OrganizationMembershipRole.Member,
                OrganizationMembershipStatus.Active,
                OrganizationMembershipChange.Joined,
                version: 4),
            CancellationToken.None);

        AssertNoOperationalAccess(context);
        Assert.Contains("subject:lock", context.Operations);
        Assert.Contains("staff:lock", context.Operations);
    }

    [Fact]
    public async Task Missing_staff_member_loses_owner_and_ordinary_access()
    {
        TestContext context = CreateContext(
            CreateMembership(OrganizationMembershipRole.Member, version: 4),
            []);
        SeedAllAccess(context);

        await context.Handler.HandleAsync(
            CreateEvent(
                OrganizationMembershipRole.Member,
                OrganizationMembershipStatus.Active,
                OrganizationMembershipChange.Joined,
                version: 4),
            CancellationToken.None);

        AssertNoOperationalAccess(context);
        Assert.Contains("subject:lock", context.Operations);
        Assert.DoesNotContain("staff:lock", context.Operations);
    }

    [Fact]
    public async Task Suspended_organization_owner_loses_all_workspace_access()
    {
        TestContext context = CreateContext(
            CreateMembership(
                OrganizationMembershipRole.Owner,
                OrganizationMembershipStatus.Suspended,
                version: 5),
            [CreateStaff(StaffStatus.Active)]);
        SeedAllAccess(context);

        await context.Handler.HandleAsync(
            CreateEvent(
                OrganizationMembershipRole.Owner,
                OrganizationMembershipStatus.Active,
                OrganizationMembershipChange.PromotedToOwner,
                version: 4),
            CancellationToken.None);

        AssertNoOperationalAccess(context);
    }

    [Fact]
    public async Task Owner_of_suspended_organization_loses_all_workspace_access()
    {
        TestContext context = CreateContext(
            CreateMembership(OrganizationMembershipRole.Owner, version: 5),
            [CreateStaff(StaffStatus.Active)],
            organizationStatus: OrganizationStatus.Suspended);
        SeedAllAccess(context);

        await context.Handler.HandleAsync(
            CreateEvent(
                OrganizationMembershipRole.Owner,
                OrganizationMembershipStatus.Active,
                OrganizationMembershipChange.PromotedToOwner,
                version: 4),
            CancellationToken.None);

        AssertNoOperationalAccess(context);
    }

    [Fact]
    public async Task Membership_snapshot_behind_same_event_retries_without_access_mutation()
    {
        TestContext context = CreateContext(
            CreateMembership(OrganizationMembershipRole.Member, version: 2),
            [CreateStaff(StaffStatus.Active)]);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Handler.HandleAsync(
                CreateEvent(
                    OrganizationMembershipRole.Member,
                    OrganizationMembershipStatus.Active,
                    OrganizationMembershipChange.Joined,
                    version: 3),
                CancellationToken.None));

        Assert.Contains("behind", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(context.Operations, IsAccessMutation);
    }

    [Fact]
    public async Task Changed_staff_identity_retries_before_reading_membership_or_mutating_access()
    {
        TestContext context = CreateContext(
            CreateMembership(OrganizationMembershipRole.Member, version: 4),
            [
                CreateStaff(StaffStatus.Active),
                CreateStaff(StaffStatus.Active) with { StaffMemberId = Guid.NewGuid() }
            ]);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Handler.HandleAsync(
                CreateEvent(
                    OrganizationMembershipRole.Member,
                    OrganizationMembershipStatus.Active,
                    OrganizationMembershipChange.Joined,
                    version: 4),
                CancellationToken.None));

        Assert.Contains("identity changed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("membership:read", context.Operations);
        Assert.DoesNotContain(context.Operations, IsAccessMutation);
    }

    [Fact]
    public async Task Final_owner_protection_stops_member_restoration()
    {
        TestContext context = CreateContext(
            CreateMembership(OrganizationMembershipRole.Member, version: 4),
            [CreateStaff(StaffStatus.Active)]);
        context.Roles.Add(WorkspaceAccessRoles.Owner);
        context.Roles.OwnerRemovalOutcome =
            AccessControlAssignmentRemovalOutcome.LastOwnerProtected;

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Handler.HandleAsync(
                CreateEvent(
                    OrganizationMembershipRole.Member,
                    OrganizationMembershipStatus.Active,
                    OrganizationMembershipChange.DemotedToMember,
                    version: 4),
                CancellationToken.None));

        Assert.Contains("obsolete", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(context.Roles.Has(WorkspaceAccessRoles.Owner));
        Assert.False(context.Roles.Has(WorkspaceAccessRoles.MembershipMarker));
        Assert.Empty(context.Profiles.AssignedProfileKeys());
    }

    [Fact]
    public async Task Restricted_workspace_retries_current_owner_before_access_mutation()
    {
        TestContext context = CreateContext(
            CreateMembership(OrganizationMembershipRole.Owner, version: 4),
            [],
            restricted: true);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Handler.HandleAsync(
                CreateEvent(
                    OrganizationMembershipRole.Owner,
                    OrganizationMembershipStatus.Active,
                    OrganizationMembershipChange.Joined,
                    version: 4),
                CancellationToken.None));

        Assert.Contains("reconciliation", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(context.Operations, IsAccessMutation);
    }

    [Fact]
    public async Task Mismatched_event_scope_is_rejected_before_authoritative_reads()
    {
        TestContext context = CreateContext(
            CreateMembership(OrganizationMembershipRole.Owner, version: 4),
            [CreateStaff(StaffStatus.Active)]);
        OrganizationMembershipChangedIntegrationEvent integrationEvent = CreateEvent(
            OrganizationMembershipRole.Owner,
            OrganizationMembershipStatus.Active,
            OrganizationMembershipChange.Joined,
            version: 4,
            scopeId: Guid.NewGuid().ToString("D"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Handler.HandleAsync(integrationEvent, CancellationToken.None));

        Assert.Empty(context.Operations);
    }

    private static TestContext CreateContext(
        OrganizationMembershipDto? membership,
        IReadOnlyList<StaffOperationalIdentitySnapshot?> staffSnapshots,
        bool restricted = false,
        OrganizationStatus organizationStatus = OrganizationStatus.Active)
    {
        List<string> operations = [];
        RecordingRoles roles = new(operations);
        RecordingProfiles profiles = new(operations);
        WorkspaceAccessProvisioner provisioner = new(roles, profiles);
        WorkspaceStaffAccessMutationCoordinator mutations = new(
            new RecordingOperationLock(operations),
            new UnusedProcessRepository());
        return new TestContext(
            new OrganizationMembershipAccessProfileSeedHandler(
                provisioner,
                mutations,
                roles,
                new StubMembershipReader(
                    new OrganizationMembershipSnapshotDto(
                        OrganizationId,
                        organizationStatus,
                        membership),
                    operations),
                new StubStaffReader(staffSnapshots, operations),
                restricted
                    ? WorkspaceOperationalAdmissionTestSupport.Restricted(WorkspaceId)
                    : WorkspaceOperationalAdmissionTestSupport.Allowed(WorkspaceId)),
            roles,
            profiles,
            operations);
    }

    private static OrganizationMembershipDto CreateMembership(
        OrganizationMembershipRole role,
        OrganizationMembershipStatus status = OrganizationMembershipStatus.Active,
        long version = 1) =>
        new(
            MembershipId,
            OrganizationId,
            SubjectId,
            role,
            status,
            version,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddMinutes(version));

    private static StaffOperationalIdentitySnapshot CreateStaff(StaffStatus status) =>
        new(StaffMemberId, SubjectId, status, 7);

    private static OrganizationMembershipChangedIntegrationEvent CreateEvent(
        OrganizationMembershipRole role,
        OrganizationMembershipStatus status,
        OrganizationMembershipChange change,
        long version,
        string? scopeId = null) =>
        new(
            Guid.NewGuid(),
            DateTimeOffset.UnixEpoch.AddMinutes(version),
            scopeId ?? WorkspaceId,
            OrganizationId,
            MembershipId,
            SubjectId,
            change,
            role,
            status,
            version);

    private static void SeedAllAccess(TestContext context)
    {
        context.Roles.Add(WorkspaceAccessRoles.Owner);
        context.Roles.Add(WorkspaceAccessRoles.MembershipMarker);
        context.Roles.Add(WorkspaceAccessRoles.LegacyMember);
        context.Profiles.AssignCustom("night-auditor");
    }

    private static void AssertNoOperationalAccess(TestContext context)
    {
        Assert.False(context.Roles.Has(WorkspaceAccessRoles.Owner));
        Assert.False(context.Roles.Has(WorkspaceAccessRoles.MembershipMarker));
        Assert.False(context.Roles.Has(WorkspaceAccessRoles.LegacyMember));
        Assert.Empty(context.Profiles.AssignedProfileKeys());
    }

    private static void AssertOrdered(List<string> operations, params string[] expected)
    {
        int previous = -1;
        foreach (string operation in expected)
        {
            int index = operations.FindIndex(previous + 1, candidate => candidate == operation);
            Assert.True(index > previous, $"Operation '{operation}' was not ordered after index {previous}.");
            previous = index;
        }
    }

    private static bool IsAccessMutation(string operation) =>
        operation.StartsWith("role:", StringComparison.Ordinal) ||
        operation.StartsWith("profiles:", StringComparison.Ordinal);

    private sealed record TestContext(
        OrganizationMembershipAccessProfileSeedHandler Handler,
        RecordingRoles Roles,
        RecordingProfiles Profiles,
        List<string> Operations);

    private sealed class StubMembershipReader(
        OrganizationMembershipSnapshotDto? snapshot,
        List<string> operations) : IOrganizationMembershipReader
    {
        public Task<OrganizationMembershipSnapshotDto?> FindAsync(
            Guid organizationId,
            string subjectId,
            CancellationToken cancellationToken = default)
        {
            operations.Add("membership:read");
            Assert.Equal(OrganizationId, organizationId);
            Assert.Equal(SubjectId, subjectId);
            return Task.FromResult(snapshot);
        }
    }

    private sealed class StubStaffReader(
        IReadOnlyList<StaffOperationalIdentitySnapshot?> snapshots,
        List<string> operations) : IStaffOperationalIdentityReader
    {
        private int readCount;

        public Task<StaffOperationalIdentitySnapshot?> FindAsync(
            string tenantId,
            string authSubjectId,
            CancellationToken cancellationToken = default)
        {
            operations.Add("staff:read");
            Assert.Equal(WorkspaceId, tenantId);
            Assert.Equal(SubjectId, authSubjectId);
            StaffOperationalIdentitySnapshot? result = snapshots.Count == 0
                ? null
                : snapshots[Math.Min(this.readCount, snapshots.Count - 1)];
            this.readCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingOperationLock(List<string> operations)
        : IWorkspaceStaffAccessOperationLock
    {
        public Task AcquireSubjectAsync(
            string subjectId,
            CancellationToken cancellationToken)
        {
            Assert.Equal(SubjectId, subjectId);
            operations.Add("subject:lock");
            return Task.CompletedTask;
        }

        public Task AcquireStaffAsync(
            Guid staffMemberId,
            CancellationToken cancellationToken)
        {
            operations.Add("staff:lock");
            return Task.CompletedTask;
        }

        public Task AcquireCoordinatesAsync(
            Guid staffMemberId,
            string subjectId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> TryAcquireProcessAsync(
            Guid processId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> TryAcquireStaffVersionAsync(
            Guid staffMemberId,
            long targetStaffVersion,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedProcessRepository : IWorkspaceStaffAccessProcessRepository
    {
        public Task<WorkspaceStaffAccessProcess?> GetAsync(
            Guid processId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WorkspaceStaffAccessProcess?> GetByStaffVersionAsync(
            Guid staffMemberId,
            long targetStaffVersion,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WorkspaceStaffAccessProcess?> GetOpenByStaffAsync(
            Guid staffMemberId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WorkspaceStaffAccessProcess?> GetLatestCompletedSuspensionAsync(
            Guid staffMemberId,
            string subjectId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WorkspaceStaffAccessProcess?> GetCompletedDepartureAsync(
            Guid staffMemberId,
            long targetStaffVersion,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WorkspaceStaffAccessProcessListResponse> ListOpenAsync(
            PageRequest page,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddAsync(
            WorkspaceStaffAccessProcess value,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingRoles(List<string> operations)
        : IAccessControlRoleProvisioner
    {
        private readonly HashSet<string> assignments = [];

        public AccessControlAssignmentRemovalOutcome? OwnerRemovalOutcome { get; set; }

        public void Add(string roleName) => this.assignments.Add(roleName);

        public bool Has(string roleName) => this.assignments.Contains(roleName);

        public Task EnsureRoleAsync(
            AccessControlRoleDefinition role,
            CancellationToken cancellationToken = default)
        {
            operations.Add($"role:ensure:{role.Name}");
            return Task.CompletedTask;
        }

        public Task EnsureAssignmentAsync(
            AccessSubject subject,
            string roleName,
            AccessScope scope,
            CancellationToken cancellationToken = default)
        {
            if (string.Equals(roleName, WorkspaceAccessRoles.Provisioner, StringComparison.Ordinal))
            {
                Assert.Equal(AccessSubject.System(WorkspaceAccessActors.Provisioner), subject);
                Assert.Equal(WorkspaceAccessScopes.Create(WorkspaceId), scope);
            }
            else
            {
                AssertTarget(subject, scope);
            }

            this.assignments.Add(roleName);
            operations.Add($"role:assign:{roleName}");
            return Task.CompletedTask;
        }

        public Task<AccessControlAssignmentRemovalOutcome> RemoveAssignmentAsync(
            AccessSubject subject,
            string roleName,
            AccessScope scope,
            CancellationToken cancellationToken = default)
        {
            AssertTarget(subject, scope);
            operations.Add($"role:remove:{roleName}");
            if (string.Equals(roleName, WorkspaceAccessRoles.Owner, StringComparison.Ordinal) &&
                this.OwnerRemovalOutcome is { } ownerOutcome)
            {
                return Task.FromResult(ownerOutcome);
            }

            return Task.FromResult(this.assignments.Remove(roleName)
                ? AccessControlAssignmentRemovalOutcome.Removed
                : AccessControlAssignmentRemovalOutcome.NotFound);
        }

        public Task<bool> HasAssignmentAsync(
            AccessSubject subject,
            string roleName,
            AccessScope scope,
            CancellationToken cancellationToken = default)
        {
            AssertTarget(subject, scope);
            return Task.FromResult(this.assignments.Contains(roleName));
        }

        public Task<AccessControlPage<AccessControlRoleAssignment>> ListAssignmentsAsync(
            string roleName,
            AccessScope scope,
            int page,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AccessControlPage<AccessControlRoleAssignment>([], page, pageSize, false));

        private static void AssertTarget(AccessSubject subject, AccessScope scope)
        {
            Assert.Equal(AccessSubject.User(SubjectId), subject);
            Assert.Equal(WorkspaceAccessScopes.Create(WorkspaceId), scope);
        }
    }

    private sealed class RecordingProfiles(List<string> operations)
        : IAccessProfileProvisioner, IScopedAccessProfileProvisioner
    {
        private readonly Dictionary<string, AccessProfileDto> profiles = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<Guid>> assignments = new(StringComparer.Ordinal);

        public void AssignCustom(string key)
        {
            AccessProfileDto profile = this.CreateProfile(
                key,
                ["reservations.read"]);
            this.GetAssignments(WorkspaceAccessScopes.Create(WorkspaceId).Value).Add(profile.Id);
        }

        public string[] AssignedProfileKeys() => this.assignments.Values
            .SelectMany(ids => ids)
            .Distinct()
            .Select(id => this.profiles.Values.Single(profile => profile.Id == id).Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        public Task<AccessProfileDto> EnsureProfileAsync(
            AccessScope ownerScope,
            AccessProfileDefinition definition,
            AccessSubject actor,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(WorkspaceAccessScopes.Create(WorkspaceId), ownerScope);
            Assert.Equal(AccessSubject.System(WorkspaceAccessActors.Provisioner), actor);
            if (!this.profiles.TryGetValue(definition.Key, out AccessProfileDto? profile))
            {
                profile = this.CreateProfile(definition.Key, definition.Permissions);
            }

            operations.Add($"profiles:ensure:{definition.Key}");
            return Task.FromResult(profile);
        }

        public Task<AccessProfileDto?> FindProfileByKeyAsync(
            AccessScope ownerScope,
            string key,
            CancellationToken cancellationToken = default)
        {
            this.profiles.TryGetValue(key, out AccessProfileDto? profile);
            return Task.FromResult(profile);
        }

        public Task<AccessProfileAssignmentSet> GetSubjectAssignmentsAsync(
            AccessSubject subject,
            AccessScope ownerScope,
            CancellationToken cancellationToken = default)
        {
            AccessProfileDto[] current = this.GetAssignments(ownerScope.Value)
                .Select(id => this.profiles.Values.Single(profile => profile.Id == id))
                .ToArray();
            return Task.FromResult(new AccessProfileAssignmentSet(subject, ownerScope, current));
        }

        public Task<AccessProfileAssignmentReconciliation> ReconcileSubjectAssignmentsAsync(
            AccessSubject subject,
            AccessScope ownerScope,
            IReadOnlyCollection<Guid> profileIds,
            AccessSubject actor,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ScopedAccessProfileAssignmentSet> GetSubjectScopedAssignmentsAsync(
            AccessSubject subject,
            AccessScope ownerScope,
            CancellationToken cancellationToken = default)
        {
            ScopedAccessProfileAssignment[] current = this.assignments
                .Where(entry => IsWithin(ownerScope.Value, entry.Key))
                .SelectMany(entry => entry.Value.Select(id => new ScopedAccessProfileAssignment(
                    this.profiles.Values.Single(profile => profile.Id == id),
                    AccessScope.Parse(entry.Key))))
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
            ScopedAccessProfileAssignmentSet current = await this.GetSubjectScopedAssignmentsAsync(
                subject,
                ownerScope,
                cancellationToken);
            AccessProfileAssignmentTarget[] previous = current.Assignments
                .Select(assignment => new AccessProfileAssignmentTarget(
                    assignment.Profile.Id,
                    assignment.AssignmentScope))
                .ToArray();
            foreach (string scope in this.assignments.Keys
                         .Where(scope => IsWithin(ownerScope.Value, scope))
                         .ToArray())
            {
                this.assignments.Remove(scope);
            }

            foreach (AccessProfileAssignmentTarget target in targets.Distinct())
            {
                this.GetAssignments(target.AssignmentScope.Value).Add(target.ProfileId);
            }

            AccessProfileAssignmentTarget[] desired = targets.Distinct().ToArray();
            operations.Add($"profiles:reconcile:{desired.Length}");
            return new ScopedAccessProfileAssignmentReconciliation(
                subject,
                ownerScope,
                desired,
                desired.Except(previous).Count(),
                previous.Except(desired).Count());
        }

        private AccessProfileDto CreateProfile(
            string key,
            IReadOnlyCollection<string> permissions)
        {
            AccessProfileDto profile = new(
                Guid.NewGuid(),
                WorkspaceAccessScopes.Create(WorkspaceId).Value,
                key,
                key,
                string.Empty,
                AccessProfileStatus.Active,
                1,
                permissions.ToArray(),
                0,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch);
            this.profiles[key] = profile;
            return profile;
        }

        private HashSet<Guid> GetAssignments(string scope)
        {
            if (!this.assignments.TryGetValue(scope, out HashSet<Guid>? values))
            {
                values = [];
                this.assignments[scope] = values;
            }

            return values;
        }

        private static bool IsWithin(string ownerScope, string assignmentScope) =>
            string.Equals(ownerScope, assignmentScope, StringComparison.Ordinal) ||
            assignmentScope.StartsWith(ownerScope + "/", StringComparison.Ordinal);
    }
}
