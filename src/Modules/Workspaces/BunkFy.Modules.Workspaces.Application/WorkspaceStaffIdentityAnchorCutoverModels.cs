namespace BunkFy.Modules.Workspaces.Application;

using BunkFy.Modules.Staff.Contracts;

public sealed record WorkspaceStaffIdentityAnchorOwnerManifest(
    int ContractVersion,
    string TenantId,
    DateTimeOffset ReviewedAtUtc,
    WorkspaceStaffIdentityAnchorHistoricalEvidence HistoricalEvidence,
    IReadOnlyList<WorkspaceStaffIdentityAnchorOwnerBinding> Bindings);

public sealed record WorkspaceStaffIdentityAnchorHistoricalEvidence(
    WorkspaceStaffIdentityAnchorHistoricalEvidenceKind Kind,
    string EvidenceSha256);

public sealed record WorkspaceStaffIdentityAnchorOwnerBinding(
    Guid OrganizationId,
    string ScopeId,
    Guid MembershipId,
    Guid StaffMemberId,
    long ObservedMembershipVersion,
    WorkspaceStaffIdentityAnchorOwnerBindingEvidenceKind EvidenceKind,
    bool ReviewedErasedTarget = false);

public enum WorkspaceStaffIdentityAnchorHistoricalEvidenceKind
{
    Unknown = 0,
    ReviewedHistoricalOwnerUniverse = 1,
    ReviewedNoHistoricalOwnerSources = 2
}

public enum WorkspaceStaffIdentityAnchorOwnerBindingEvidenceKind
{
    Unknown = 0,
    CurrentActiveOwnerReview = 1,
    HistoricalOwnerExternalReview = 2
}

public static class WorkspaceStaffIdentityAnchorCutoverStatusLimits
{
    public const int MaximumIssues = 100;
}

public sealed record WorkspaceStaffIdentityAnchorCutoverIssue(
    StaffIdentityProvisioningAnchorSourceKind SourceKind,
    Guid SourceId,
    StaffIdentityProvisioningAnchorCutoverDisposition Disposition,
    long? ObservedMembershipVersion,
    Guid? CandidateStaffMemberId);

public sealed record WorkspaceStaffIdentityAnchorCutoverStatus(
    long WorkspaceSourceCount,
    long OwnerBindingCount,
    long AlreadyAnchoredCount,
    long SeedableWorkspaceCount,
    long SeedableOwnerCount,
    long AmbiguousCount,
    long ConflictCount,
    string SourceEvidenceSha256,
    string AnchorStateSha256,
    string? OwnerManifestSha256,
    bool OwnerManifestProvided,
    bool CanReconcile,
    bool IsReady,
    long AuthoritativeOwnerCount,
    long OrganizationsScopeRevision,
    long HistoricalBindingCount,
    WorkspaceStaffIdentityAnchorHistoricalEvidenceKind
        HistoricalEvidenceKind,
    string? HistoricalEvidenceSha256,
    long TotalIssueCount,
    bool HasMoreIssues,
    IReadOnlyList<WorkspaceStaffIdentityAnchorCutoverIssue> Issues);

public sealed record WorkspaceStaffIdentityAnchorReconcileResult(
    int? AppliedCount,
    WorkspaceStaffIdentityAnchorCutoverStatus? Status,
    WorkspaceStaffIdentityAnchorReconcileOutcome Outcome,
    bool MustRerunStatus,
    string AcceptedSourceEvidenceSha256,
    string AcceptedAnchorStateSha256,
    string AcceptedOwnerManifestSha256);

public enum WorkspaceStaffIdentityAnchorReconcileOutcome
{
    Unknown = 0,
    AppliedAndVerified = 1,
    ApplyOutcomeUnknown = 2
}
