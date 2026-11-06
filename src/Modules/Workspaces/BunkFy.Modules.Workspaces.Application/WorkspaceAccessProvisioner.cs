namespace BunkFy.Modules.Workspaces.Application;

using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.AccessControl;
using Gma.Framework.Results;
using Gma.Modules.AccessControl.Contracts;

internal sealed class WorkspaceAccessProvisioner(
    IAccessControlRoleProvisioner roles,
    IAccessProfileProvisioner profiles,
    IScopedAccessProfileProvisioner scopedProfiles,
    IEnumerable<IAccessProfileManager> profileManagers,
    IEnumerable<IScopedAccessProfileManager> scopedProfileManagers)
{
    internal const string ProvisioningActorId = WorkspaceAccessActors.Provisioner;
    private const int AssignmentPageSize = 100;
    private readonly IAccessControlRoleProvisioner roles = roles;
    private readonly IAccessProfileProvisioner profiles = profiles;
    private readonly IScopedAccessProfileProvisioner scopedProfiles = scopedProfiles;
    private readonly IAccessProfileManager? profileManager = profileManagers.SingleOrDefault();
    private readonly IScopedAccessProfileManager scopedProfileManager =
        scopedProfileManagers.SingleOrDefault() ??
        throw new InvalidOperationException(
            "Workspace access provisioning requires the supported scoped profile manager composition.");

    internal WorkspaceAccessProvisioner(
        IAccessControlRoleProvisioner roles,
        IAccessProfileProvisioner profiles,
        IScopedAccessProfileProvisioner scopedProfiles,
        IAccessProfileManager? profileManager)
        : this(
            roles,
            profiles,
            scopedProfiles,
            profileManager is null ? [] : [profileManager],
            [new LegacyScopedProfileManagerAdapter(scopedProfiles)])
    {
    }

    internal WorkspaceAccessProvisioner(
        IAccessControlRoleProvisioner roles,
        IAccessProfileProvisioner profiles)
        : this(
            roles,
            profiles,
            profiles as IScopedAccessProfileProvisioner ??
                throw new ArgumentException(
                    "The profile provisioner must support scoped assignments.",
                    nameof(profiles)),
            profiles as IAccessProfileManager)
    {
    }

    internal WorkspaceAccessProvisioner(
        IAccessControlRoleProvisioner roles,
        IAccessProfileProvisioner profiles,
        IScopedAccessProfileProvisioner scopedProfiles)
        : this(roles, profiles, scopedProfiles, profiles as IAccessProfileManager)
    {
    }

    public async Task ProvisionDefaultMemberAsync(
        string workspaceId,
        string subjectId,
        CancellationToken cancellationToken)
    {
        AccessScope scope = WorkspaceAccessScopes.Create(workspaceId);
        AccessSubject subject = AccessSubject.User(subjectId);
        AccessSubject actor = AccessSubject.System(ProvisioningActorId);

        await this.EnsureProvisionerAsync(scope, cancellationToken).ConfigureAwait(false);
        await this.EnsureMembershipMarkerAsync(cancellationToken).ConfigureAwait(false);
        AccessProfileDto frontDesk = await this.EnsureSeedProfileAsync(
                scope,
                WorkspaceAccessProfileSeeds.FrontDesk,
                actor,
                cancellationToken)
            .ConfigureAwait(false);
        if (frontDesk.Status != AccessProfileStatus.Active)
        {
            throw new InvalidOperationException("The Front desk access profile is not active.");
        }

        await this.ProvisionDefaultMemberAsync(
                subject,
                scope,
                frontDesk.Id,
                actor,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<Result> ProvisionMemberAsync(
        string workspaceId,
        string subjectId,
        Guid profileId,
        IReadOnlyCollection<Guid> propertyIds,
        CancellationToken cancellationToken)
    {
        if (profileId == Guid.Empty)
        {
            throw new ArgumentException("An access-profile id is required.", nameof(profileId));
        }

        AccessScope workspaceScope = WorkspaceAccessScopes.Create(workspaceId);
        AccessSubject subject = AccessSubject.User(subjectId);
        AccessSubject actor = AccessSubject.System(ProvisioningActorId);
        await this.EnsureProvisionerAsync(workspaceScope, cancellationToken).ConfigureAwait(false);
        await this.EnsureMembershipMarkerAsync(cancellationToken).ConfigureAwait(false);
        await this.roles.EnsureAssignmentAsync(
            subject,
            WorkspaceAccessRoles.MembershipMarker,
            workspaceScope,
            cancellationToken).ConfigureAwait(false);

        Guid[] distinctPropertyIds = propertyIds.Distinct().Order().ToArray();
        AccessProfileAssignmentTarget[] targets = distinctPropertyIds.Length == 0
            ? [new AccessProfileAssignmentTarget(profileId, workspaceScope)]
            : distinctPropertyIds.Select(propertyId => new AccessProfileAssignmentTarget(
                profileId,
                WorkspaceAccessScopes.CreateProperty(workspaceId, propertyId))).ToArray();
        Result<ScopedAccessProfileAssignmentReconciliation> reconciled =
            await this.scopedProfileManager.ReconcileSubjectAssignmentsAsync(
                subject,
                workspaceScope,
                targets,
                actor,
                cancellationToken).ConfigureAwait(false);
        if (reconciled.IsFailure)
        {
            return Result.Failure(reconciled.Error);
        }

        await this.RemoveLegacyMemberAssignmentAsync(subject, workspaceScope, cancellationToken)
            .ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<WorkspaceMemberAccessEquivalence> InspectMemberEquivalenceAsync(
        string workspaceId,
        string subjectId,
        AccessProfileDto expectedProfile,
        IReadOnlyCollection<Guid> propertyIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expectedProfile);
        AccessScope workspaceScope = WorkspaceAccessScopes.Create(workspaceId);
        AccessSubject subject = AccessSubject.User(subjectId);
        bool hasMarker = await this.roles.HasAssignmentAsync(
                subject,
                WorkspaceAccessRoles.MembershipMarker,
                workspaceScope,
                cancellationToken).ConfigureAwait(false);
        bool hasLegacy = await this.roles.HasAssignmentAsync(
                subject,
                WorkspaceAccessRoles.LegacyMember,
                workspaceScope,
                cancellationToken).ConfigureAwait(false);
        bool hasOwner = await this.roles.HasAssignmentAsync(
                subject,
                WorkspaceAccessRoles.Owner,
                workspaceScope,
                cancellationToken).ConfigureAwait(false);
        ScopedAccessProfileAssignmentSet current = await this.scopedProfiles
            .GetSubjectScopedAssignmentsAsync(
                subject,
                workspaceScope,
                cancellationToken)
            .ConfigureAwait(false);

        AccessScope[] expectedScopes = propertyIds.Distinct().Order().Any()
            ? propertyIds.Distinct().Order()
                .Select(propertyId =>
                    WorkspaceAccessScopes.CreateProperty(
                        workspaceId,
                        propertyId))
                .ToArray()
            : [workspaceScope];
        bool exactAssignments = current.Assignments.Count == expectedScopes.Length &&
            current.Assignments.All(assignment =>
                MatchesProfile(assignment.Profile, expectedProfile) &&
                expectedScopes.Contains(assignment.AssignmentScope));
        return hasMarker && !hasLegacy && !hasOwner && exactAssignments
            ? WorkspaceMemberAccessEquivalence.Equivalent
            : WorkspaceMemberAccessEquivalence.Conflict;
    }

    private static bool MatchesProfile(
        AccessProfileDto actual,
        AccessProfileDto expected) =>
        actual.Id == expected.Id &&
        string.Equals(
            actual.OwnerScope,
            expected.OwnerScope,
            StringComparison.Ordinal) &&
        string.Equals(actual.Key, expected.Key, StringComparison.Ordinal) &&
        actual.Status == AccessProfileStatus.Active &&
        expected.Status == AccessProfileStatus.Active &&
        actual.Version == expected.Version &&
        actual.Permissions.Order(StringComparer.Ordinal).SequenceEqual(
            expected.Permissions.Order(StringComparer.Ordinal),
            StringComparer.Ordinal);

    private async Task ProvisionDefaultMemberAsync(
        AccessSubject subject,
        AccessScope scope,
        Guid frontDeskProfileId,
        AccessSubject actor,
        CancellationToken cancellationToken)
    {
        await this.roles.EnsureAssignmentAsync(
                subject,
                WorkspaceAccessRoles.MembershipMarker,
                scope,
                cancellationToken)
            .ConfigureAwait(false);

        ScopedAccessProfileAssignmentSet current = await this.scopedProfiles.GetSubjectScopedAssignmentsAsync(
                subject,
                scope,
                cancellationToken)
            .ConfigureAwait(false);
        AccessProfileAssignmentTarget[] desiredTargets = current.Assignments
            .Select(assignment => new AccessProfileAssignmentTarget(
                assignment.Profile.Id,
                assignment.AssignmentScope))
            .Append(new AccessProfileAssignmentTarget(frontDeskProfileId, scope))
            .Distinct()
            .ToArray();

        await this.scopedProfiles.ReconcileSubjectScopedAssignmentsAsync(
                subject,
                scope,
                desiredTargets,
                actor,
                cancellationToken)
            .ConfigureAwait(false);

        await this.RemoveLegacyMemberAssignmentAsync(subject, scope, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkspaceAccessBootstrapResult> BackfillLegacyMembersAsync(
        string workspaceId,
        CancellationToken cancellationToken)
    {
        AccessScope scope = WorkspaceAccessScopes.Create(workspaceId);
        AccessSubject actor = AccessSubject.System(ProvisioningActorId);

        await this.EnsureProvisionerAsync(scope, cancellationToken).ConfigureAwait(false);
        await this.EnsureMembershipMarkerAsync(cancellationToken).ConfigureAwait(false);
        AccessProfileDto? frontDesk = null;
        foreach (AccessProfileDefinition seed in WorkspaceAccessProfileSeeds.All)
        {
            AccessProfileDto ensured = await this.EnsureSeedProfileAsync(
                    scope,
                    seed,
                    actor,
                    cancellationToken)
                .ConfigureAwait(false);
            if (string.Equals(seed.Key, WorkspaceAccessProfileSeeds.FrontDeskKey, StringComparison.Ordinal))
            {
                frontDesk = ensured;
            }

            if (ensured.Status != AccessProfileStatus.Active)
            {
                throw new InvalidOperationException(
                    $"The workspace access profile seed '{seed.Key}' is not active.");
            }
        }

        if (frontDesk is null)
        {
            throw new InvalidOperationException("The Front desk access profile seed is missing.");
        }

        int migratedMemberCount = 0;
        while (true)
        {
            AccessControlPage<AccessControlRoleAssignment> page = await this.roles.ListAssignmentsAsync(
                    WorkspaceAccessRoles.LegacyMember,
                    scope,
                    page: 1,
                    pageSize: AssignmentPageSize,
                    cancellationToken)
                .ConfigureAwait(false);
            if (page.Items.Count == 0)
            {
                break;
            }

            foreach (AccessControlRoleAssignment assignment in page.Items)
            {
                if (assignment.SubjectKind != AccessSubjectKind.User)
                {
                    throw new InvalidOperationException(
                        "The legacy workspace member role contains a non-user assignment.");
                }

                await this.ProvisionDefaultMemberAsync(
                        AccessSubject.User(assignment.SubjectId),
                        scope,
                        frontDesk.Id,
                        actor,
                        cancellationToken)
                    .ConfigureAwait(false);
                migratedMemberCount++;
            }
        }

        return new WorkspaceAccessBootstrapResult(
            WorkspaceAccessProfileSeeds.Version,
            WorkspaceAccessProfileSeeds.All.Count,
            migratedMemberCount);
    }

    public async Task EnsureSeedProfilesAsync(
        string workspaceId,
        CancellationToken cancellationToken)
    {
        AccessScope scope = WorkspaceAccessScopes.Create(workspaceId);
        AccessSubject actor = AccessSubject.System(ProvisioningActorId);
        await this.EnsureProvisionerAsync(scope, cancellationToken).ConfigureAwait(false);
        foreach (AccessProfileDefinition seed in WorkspaceAccessProfileSeeds.All)
        {
            await this.EnsureSeedProfileAsync(scope, seed, actor, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<AccessProfileDto> EnsureSeedProfileAsync(
        AccessScope scope,
        AccessProfileDefinition definition,
        AccessSubject actor,
        CancellationToken cancellationToken)
    {
        AccessProfileDto profile = await this.profiles.EnsureProfileAsync(
                scope,
                definition,
                actor,
                cancellationToken)
            .ConfigureAwait(false);
        if (profile.Status != AccessProfileStatus.Active)
        {
            throw new InvalidOperationException(
                $"The workspace access profile seed '{definition.Key}' is not active.");
        }

        if (MatchesSeed(profile, definition) || this.profileManager is null)
        {
            return profile;
        }

        Result<AccessProfileDto> reconciled =
            await this.profileManager.UpdateProfileAsync(
                profile.Id,
                scope,
                new AccessProfileUpdate(
                    definition.DisplayName,
                    definition.Description,
                    definition.Permissions,
                    profile.Version),
                actor,
                cancellationToken).ConfigureAwait(false);
        if (reconciled.IsFailure)
        {
            throw new InvalidOperationException(
                $"The workspace access profile seed '{definition.Key}' could not be reconciled ({reconciled.Error.Code}).");
        }

        return reconciled.Value;
    }

    private static bool MatchesSeed(
        AccessProfileDto profile,
        AccessProfileDefinition definition) =>
        string.Equals(
            profile.DisplayName,
            definition.DisplayName.Trim(),
            StringComparison.Ordinal) &&
        string.Equals(
            profile.Description,
            definition.Description?.Trim() ?? string.Empty,
            StringComparison.Ordinal) &&
        profile.Permissions.Order(StringComparer.Ordinal).SequenceEqual(
            definition.Permissions.Order(StringComparer.Ordinal),
            StringComparer.Ordinal);

    public async Task<IReadOnlyCollection<WorkspaceStaffAccessProfileTarget>> CaptureRestorableProfilesAsync(
        string workspaceId,
        string subjectId,
        CancellationToken cancellationToken)
    {
        AccessScope scope = WorkspaceAccessScopes.Create(workspaceId);
        AccessSubject subject = AccessSubject.User(subjectId);
        bool hasLegacyAccess = await this.roles.HasAssignmentAsync(
                subject,
                WorkspaceAccessRoles.LegacyMember,
                scope,
                cancellationToken).ConfigureAwait(false);

        ScopedAccessProfileAssignmentSet assignments = await this.scopedProfiles.GetSubjectScopedAssignmentsAsync(
            subject,
            scope,
            cancellationToken).ConfigureAwait(false);
        List<WorkspaceStaffAccessProfileTarget> targets = assignments.Assignments
            .Select(assignment => new WorkspaceStaffAccessProfileTarget(
                assignment.Profile.Id,
                assignment.AssignmentScope.Value))
            .ToList();
        if (hasLegacyAccess)
        {
            AccessProfileDto? frontDesk = await this.profiles.FindProfileByKeyAsync(
                    scope,
                    WorkspaceAccessProfileSeeds.FrontDeskKey,
                    cancellationToken)
                .ConfigureAwait(false);
            if (frontDesk?.Status == AccessProfileStatus.Active)
            {
                targets.Add(new WorkspaceStaffAccessProfileTarget(
                    frontDesk.Id,
                    scope.Value));
            }
        }

        return targets.Distinct().ToArray();
    }

    public async Task DenyMemberAsync(
        string workspaceId,
        string subjectId,
        CancellationToken cancellationToken)
    {
        AccessScope scope = WorkspaceAccessScopes.Create(workspaceId);
        AccessSubject subject = AccessSubject.User(subjectId);
        AccessSubject actor = AccessSubject.System(ProvisioningActorId);
        await this.scopedProfiles.ReconcileSubjectScopedAssignmentsAsync(
            subject,
            scope,
            [],
            actor,
            cancellationToken).ConfigureAwait(false);
        await this.RemoveAssignmentAsync(
            subject, WorkspaceAccessRoles.MembershipMarker, scope, cancellationToken).ConfigureAwait(false);
        await this.RemoveAssignmentAsync(
            subject, WorkspaceAccessRoles.LegacyMember, scope, cancellationToken).ConfigureAwait(false);
    }

    public async Task RestoreMemberAsync(
        string workspaceId,
        string subjectId,
        IReadOnlyCollection<WorkspaceStaffAccessProfileTarget> profileTargets,
        CancellationToken cancellationToken)
    {
        AccessScope scope = WorkspaceAccessScopes.Create(workspaceId);
        AccessSubject subject = AccessSubject.User(subjectId);
        AccessSubject actor = AccessSubject.System(ProvisioningActorId);
        await this.EnsureProvisionerAsync(scope, cancellationToken).ConfigureAwait(false);
        await this.EnsureMembershipMarkerAsync(cancellationToken).ConfigureAwait(false);
        await this.roles.EnsureAssignmentAsync(
            subject,
            WorkspaceAccessRoles.MembershipMarker,
            scope,
            cancellationToken).ConfigureAwait(false);
        AccessProfileAssignmentTarget[] targets = profileTargets
            .Select(target => new AccessProfileAssignmentTarget(
                target.ProfileId,
                AccessScope.Parse(target.AssignmentScope)))
            .ToArray();
        await this.scopedProfiles.ReconcileSubjectScopedAssignmentsAsync(
            subject,
            scope,
            targets,
            actor,
            cancellationToken).ConfigureAwait(false);
        await this.RemoveAssignmentAsync(
            subject, WorkspaceAccessRoles.LegacyMember, scope, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkspaceAccessBootstrapStatus> InspectAsync(
        string workspaceId,
        CancellationToken cancellationToken)
    {
        AccessScope scope = WorkspaceAccessScopes.Create(workspaceId);
        int activeSeedProfileCount = 0;
        int driftedSeedProfileCount = 0;
        int archivedSeedProfileCount = 0;
        foreach (AccessProfileDefinition seed in WorkspaceAccessProfileSeeds.All)
        {
            AccessProfileDto? profile = await this.profiles.FindProfileByKeyAsync(
                    scope,
                    seed.Key,
                    cancellationToken)
                .ConfigureAwait(false);
            if (profile?.Status == AccessProfileStatus.Active)
            {
                activeSeedProfileCount++;
                if (!MatchesSeed(profile, seed))
                {
                    driftedSeedProfileCount++;
                }
            }
            else if (profile?.Status == AccessProfileStatus.Archived)
            {
                archivedSeedProfileCount++;
            }
        }

        int legacyMemberCount = await this.CountAssignmentsAsync(
                WorkspaceAccessRoles.LegacyMember,
                scope,
                cancellationToken)
            .ConfigureAwait(false);
        int markerMemberCount = await this.CountAssignmentsAsync(
                WorkspaceAccessRoles.MembershipMarker,
                scope,
                cancellationToken)
            .ConfigureAwait(false);

        return new WorkspaceAccessBootstrapStatus(
            WorkspaceAccessProfileSeeds.Version,
            WorkspaceAccessProfileSeeds.All.Count,
            activeSeedProfileCount,
            driftedSeedProfileCount,
            archivedSeedProfileCount,
            legacyMemberCount,
            markerMemberCount);
    }

    private Task EnsureMembershipMarkerAsync(CancellationToken cancellationToken) =>
        this.roles.EnsureRoleAsync(
            new AccessControlRoleDefinition(
                WorkspaceAccessRoles.MembershipMarker,
                WorkspaceAccessRoles.MembershipMarkerPermissions),
            cancellationToken);

    private async Task EnsureProvisionerAsync(
        AccessScope scope,
        CancellationToken cancellationToken)
    {
        await this.roles.EnsureRoleAsync(
                new AccessControlRoleDefinition(
                    WorkspaceAccessRoles.Provisioner,
                    WorkspaceAccessRoles.ProvisionerPermissions),
                cancellationToken)
            .ConfigureAwait(false);
        await this.roles.EnsureAssignmentAsync(
                AccessSubject.System(ProvisioningActorId),
                WorkspaceAccessRoles.Provisioner,
                scope,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RemoveLegacyMemberAssignmentAsync(
        AccessSubject subject,
        AccessScope scope,
        CancellationToken cancellationToken)
    {
        await this.RemoveAssignmentAsync(
            subject,
            WorkspaceAccessRoles.LegacyMember,
            scope,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task RemoveAssignmentAsync(
        AccessSubject subject,
        string roleName,
        AccessScope scope,
        CancellationToken cancellationToken)
    {
        AccessControlAssignmentRemovalOutcome outcome = await this.roles.RemoveAssignmentAsync(
            subject,
            roleName,
            scope,
            cancellationToken).ConfigureAwait(false);
        if (outcome is AccessControlAssignmentRemovalOutcome.Unknown or
            AccessControlAssignmentRemovalOutcome.LastOwnerProtected)
        {
            throw new InvalidOperationException("The workspace access assignment could not be removed.");
        }
    }

    private async Task<int> CountAssignmentsAsync(
        string roleName,
        AccessScope scope,
        CancellationToken cancellationToken)
    {
        int count = 0;
        int pageNumber = 1;
        while (true)
        {
            AccessControlPage<AccessControlRoleAssignment> page = await this.roles.ListAssignmentsAsync(
                    roleName,
                    scope,
                    pageNumber,
                    AssignmentPageSize,
                    cancellationToken)
                .ConfigureAwait(false);
            count += page.Items.Count;
            if (!page.HasMore)
            {
                return count;
            }

            pageNumber++;
        }
    }

    // Internal constructors support existing unit-level collaborators. Runtime
    // DI enters through the primary constructor and requires the Result-based
    // IScopedAccessProfileManager implementation.
    private sealed class LegacyScopedProfileManagerAdapter(
        IScopedAccessProfileProvisioner provisioner)
        : IScopedAccessProfileManager
    {
        public async Task<Result<ScopedAccessProfileAssignmentSet>>
            GetSubjectAssignmentsAsync(
                AccessSubject subject,
                AccessScope ownerScope,
                AccessSubject actor,
                CancellationToken cancellationToken = default) =>
            Result.Success(await provisioner.GetSubjectScopedAssignmentsAsync(
                    subject,
                    ownerScope,
                    cancellationToken)
                .ConfigureAwait(false));

        public async Task<Result<ScopedAccessProfileAssignmentReconciliation>>
            ReconcileSubjectAssignmentsAsync(
                AccessSubject subject,
                AccessScope ownerScope,
                IReadOnlyCollection<AccessProfileAssignmentTarget> targets,
                AccessSubject actor,
                CancellationToken cancellationToken = default) =>
            Result.Success(await provisioner.ReconcileSubjectScopedAssignmentsAsync(
                    subject,
                    ownerScope,
                    targets,
                    actor,
                    cancellationToken)
                .ConfigureAwait(false));
    }
}

internal enum WorkspaceMemberAccessEquivalence
{
    Conflict = 0,
    Equivalent = 1
}
