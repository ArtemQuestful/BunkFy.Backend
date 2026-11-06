namespace BunkFy.Modules.Workspaces.Application;

using BunkFy.Modules.Staff.Contracts;
using Gma.Modules.AccessControl.Contracts;

internal static class WorkspaceStaffOnboardingRecoveryProofComparer
{
    public static bool Matches(
        WorkspaceOrganizationMembershipObservation first,
        WorkspaceOrganizationMembershipObservation second) =>
        first.Status == WorkspaceRecoveryVerificationStatus.Equivalent &&
        second.Status == WorkspaceRecoveryVerificationStatus.Equivalent &&
        first.Reason == second.Reason &&
        first.OrganizationRevision == second.OrganizationRevision &&
        first.MembershipId == second.MembershipId &&
        first.MembershipVersion == second.MembershipVersion &&
        first.MembershipStatus == second.MembershipStatus;

    public static bool Matches(
        StaffOnboardingStateVerificationResult first,
        StaffOnboardingStateVerificationResult second) =>
        first.Status == StaffOnboardingStateVerificationStatus.Equivalent &&
        second.Status == StaffOnboardingStateVerificationStatus.Equivalent &&
        first.Reason == second.Reason &&
        first.ProofContractVersion == second.ProofContractVersion &&
        first.OperationResultVersion == second.OperationResultVersion &&
        first.CurrentMemberVersion == second.CurrentMemberVersion &&
        first.TenantRevision == second.TenantRevision &&
        first.PropertyCount == second.PropertyCount &&
        string.Equals(
            first.PropertySetSha256,
            second.PropertySetSha256,
            StringComparison.Ordinal) &&
        string.Equals(
            first.StateProofSha256,
            second.StateProofSha256,
            StringComparison.Ordinal);

    public static bool Matches(
        AccessControlSubjectAuthoritySnapshot first,
        AccessControlSubjectAuthoritySnapshot second) =>
        first.Status == AccessControlAuthoritySnapshotStatus.Stable &&
        second.Status == AccessControlAuthoritySnapshotStatus.Stable &&
        first.ContractVersion == second.ContractVersion &&
        first.SubjectKind == second.SubjectKind &&
        string.Equals(first.SubjectId, second.SubjectId, StringComparison.Ordinal) &&
        Equals(first.RootScope, second.RootScope) &&
        first.ExpectedTargetScopes.SequenceEqual(second.ExpectedTargetScopes) &&
        first.ManagementRevision == second.ManagementRevision &&
        first.EarliestExpiryAtUtc == second.EarliestExpiryAtUtc &&
        string.Equals(
            first.FactsSha256,
            second.FactsSha256,
            StringComparison.Ordinal) &&
        RoleGrantsMatch(first.RoleGrants, second.RoleGrants) &&
        ProfileGrantsMatch(first.ProfileGrants, second.ProfileGrants);

    private static bool RoleGrantsMatch(
        IReadOnlyList<AccessControlAuthorityRoleGrant> first,
        IReadOnlyList<AccessControlAuthorityRoleGrant> second) =>
        first.Count == second.Count && first
            .Zip(second)
            .All(pair =>
                pair.First.AssignmentId == pair.Second.AssignmentId &&
                pair.First.RoleId == pair.Second.RoleId &&
                string.Equals(
                    pair.First.RoleName,
                    pair.Second.RoleName,
                    StringComparison.Ordinal) &&
                pair.First.Permissions.SequenceEqual(
                    pair.Second.Permissions,
                    StringComparer.Ordinal) &&
                pair.First.Scope.Equals(pair.Second.Scope) &&
                pair.First.ExpiresAtUtc == pair.Second.ExpiresAtUtc);

    private static bool ProfileGrantsMatch(
        IReadOnlyList<AccessControlAuthorityProfileGrant> first,
        IReadOnlyList<AccessControlAuthorityProfileGrant> second) =>
        first.Count == second.Count && first
            .Zip(second)
            .All(pair =>
                pair.First.AssignmentId == pair.Second.AssignmentId &&
                pair.First.AssignmentScope.Equals(pair.Second.AssignmentScope) &&
                pair.First.ProfileId == pair.Second.ProfileId &&
                pair.First.OwnerScope.Equals(pair.Second.OwnerScope) &&
                string.Equals(
                    pair.First.ProfileKey,
                    pair.Second.ProfileKey,
                    StringComparison.Ordinal) &&
                pair.First.ProfileStatus == pair.Second.ProfileStatus &&
                pair.First.ProfileVersion == pair.Second.ProfileVersion &&
                pair.First.Permissions.SequenceEqual(
                    pair.Second.Permissions,
                    StringComparer.Ordinal));
}
