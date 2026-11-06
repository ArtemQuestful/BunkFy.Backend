namespace BunkFy.Modules.Workspaces.Application;

using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.AccessControl;
using Gma.Modules.AccessControl.Contracts;

internal static class WorkspaceStaffOnboardingRecoveryAccessEvaluator
{
    public static WorkspaceRecoveryVerification Evaluate(
        AccessControlSubjectAuthoritySnapshot snapshot,
        AccessProfileDto expectedProfile,
        string expectedSubjectId,
        string workspaceId,
        IReadOnlyCollection<Guid> propertyIds)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(expectedProfile);
        if (snapshot.Status is AccessControlAuthoritySnapshotStatus.Stale or
            AccessControlAuthoritySnapshotStatus.Unavailable)
        {
            return WorkspaceRecoveryVerification.RetryLater;
        }

        if (snapshot.Status != AccessControlAuthoritySnapshotStatus.Stable ||
            snapshot.ContractVersion != 2 ||
            !snapshot.ManagementRevision.HasValue ||
            string.IsNullOrWhiteSpace(snapshot.FactsSha256))
        {
            return WorkspaceRecoveryVerification.ReviewRequired;
        }

        AccessScope workspaceScope = WorkspaceAccessScopes.Create(workspaceId);
        if (snapshot.SubjectKind != AccessSubjectKind.User ||
            !string.Equals(
                snapshot.SubjectId,
                expectedSubjectId,
                StringComparison.Ordinal) ||
            snapshot.RootScope is null ||
            !snapshot.RootScope.Equals(workspaceScope) ||
            snapshot.RoleGrants.Count != 1)
        {
            return WorkspaceRecoveryVerification.ReviewRequired;
        }

        AccessControlAuthorityRoleGrant marker = snapshot.RoleGrants[0];
        if (!string.Equals(
                marker.RoleName,
                WorkspaceAccessRoles.MembershipMarker,
                StringComparison.Ordinal) ||
            !marker.Scope.Equals(workspaceScope) ||
            marker.ExpiresAtUtc is not null ||
            marker.Permissions.Count != 0)
        {
            return WorkspaceRecoveryVerification.ReviewRequired;
        }

        AccessScope[] expectedScopes = propertyIds.Count == 0
            ? [workspaceScope]
            : propertyIds
                .Distinct()
                .Order()
                .Select(propertyId =>
                    WorkspaceAccessScopes.CreateProperty(
                        workspaceId,
                        propertyId))
                .OrderBy(scope => scope.Value, StringComparer.Ordinal)
                .ToArray();
        if (!snapshot.ExpectedTargetScopes.SequenceEqual(expectedScopes) ||
            snapshot.ProfileGrants.Count != expectedScopes.Length ||
            !AccessScope.TryParse(
                expectedProfile.OwnerScope,
                out AccessScope? expectedOwnerScope) ||
            !expectedOwnerScope.Equals(workspaceScope))
        {
            return WorkspaceRecoveryVerification.ReviewRequired;
        }

        string[] expectedPermissions = expectedProfile.Permissions
            .Order(StringComparer.Ordinal)
            .ToArray();
        bool equivalent = snapshot.ProfileGrants.All(grant =>
            grant.ProfileId == expectedProfile.Id &&
            grant.OwnerScope.Equals(workspaceScope) &&
            string.Equals(
                grant.ProfileKey,
                expectedProfile.Key,
                StringComparison.Ordinal) &&
            grant.ProfileStatus == AccessProfileStatus.Active &&
            grant.ProfileVersion == expectedProfile.Version &&
            grant.Permissions.SequenceEqual(
                expectedPermissions,
                StringComparer.Ordinal) &&
            expectedScopes.Any(scope => scope.Equals(grant.AssignmentScope)) &&
            snapshot.ProfileGrants.Count(candidate =>
                candidate.AssignmentScope.Equals(grant.AssignmentScope)) == 1);
        return equivalent
            ? WorkspaceRecoveryVerification.Equivalent
            : WorkspaceRecoveryVerification.ReviewRequired;
    }
}
