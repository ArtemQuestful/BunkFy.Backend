namespace BunkFy.Modules.Staff.Contracts;

using System.Text.Json.Serialization;

public interface IStaffIdentityProvisioningAnchorCutover
{
    Task<StaffIdentityProvisioningAnchorInspection> InspectAsync(
        IReadOnlyList<StaffIdentityProvisioningAnchorCandidate> candidates,
        CancellationToken cancellationToken = default);

    Task<StaffIdentityProvisioningAnchorApplyResult> ApplyAsync(
        IReadOnlyList<StaffIdentityProvisioningAnchorCandidate> candidates,
        CancellationToken cancellationToken = default);
}

public static class StaffWorkspaceOnboardingAnchorCutoverLimits
{
    public const int MaximumBatchSize = 500;
}

public sealed record StaffIdentityProvisioningAnchorCandidate(
    StaffIdentityProvisioningAnchorSourceKind SourceKind,
    Guid SourceId,
    Guid? StaffMemberId,
    [property: JsonIgnore] string? ExpectedAuthSubjectId = null,
    bool ReviewedErasedTarget = false);

public sealed record StaffIdentityProvisioningAnchorInspection(
    bool IsSuccess,
    IReadOnlyList<StaffIdentityProvisioningAnchorCandidateInspection> Items,
    string? ErrorCode);

public sealed record StaffIdentityProvisioningAnchorCandidateInspection(
    StaffIdentityProvisioningAnchorSourceKind SourceKind,
    Guid SourceId,
    StaffIdentityProvisioningAnchorCutoverDisposition Disposition);

public sealed record StaffIdentityProvisioningAnchorApplyResult(
    bool IsSuccess,
    int AppliedCount,
    int AlreadyAnchoredCount,
    string? ErrorCode);

public enum StaffIdentityProvisioningAnchorSourceKind
{
    Unknown = 0,
    WorkspaceOnboarding = 1,
    OrganizationMembership = 2
}

public enum StaffIdentityProvisioningAnchorCutoverDisposition
{
    Unknown = 0,
    AlreadyAnchored = 1,
    SeedableFromWorkspace = 2,
    SeedableFromReviewedOwnerMap = 3,
    Ambiguous = 4,
    Conflict = 5
}
