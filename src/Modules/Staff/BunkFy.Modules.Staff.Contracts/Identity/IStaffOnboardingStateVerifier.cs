namespace BunkFy.Modules.Staff.Contracts;

public interface IStaffOnboardingStateVerifier
{
    Task<StaffOnboardingStateVerificationResult> VerifyAsync(
        StaffOnboardingStateVerificationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record StaffOnboardingStateVerificationRequest(
    string TenantId,
    Guid OperationId,
    Guid StaffMemberId,
    string AuthSubjectId,
    IReadOnlyCollection<Guid> PropertyIds);

public sealed record StaffOnboardingStateVerificationResult(
    StaffOnboardingStateVerificationStatus Status,
    StaffOnboardingStateVerificationReason Reason,
    int ProofContractVersion,
    long? OperationResultVersion,
    long? CurrentMemberVersion,
    long? TenantRevision,
    int PropertyCount,
    string? PropertySetSha256,
    string? StateProofSha256,
    DateTimeOffset ObservedAtUtc)
{
    public bool IsEquivalent =>
        this.Status == StaffOnboardingStateVerificationStatus.Equivalent;
}

public enum StaffOnboardingStateVerificationStatus
{
    Unknown = 0,
    Equivalent = 1,
    ReviewRequired = 2,
    RetryLater = 3
}

public enum StaffOnboardingStateVerificationReason
{
    Unknown = 0,
    Equivalent = 1,
    InvalidRequest = 2,
    OperationMissing = 3,
    OperationConflict = 4,
    MemberMissing = 5,
    MemberConflict = 6,
    PropertySetConflict = 7,
    TenantClosed = 8,
    TenantStateUnavailable = 9,
    RevisionChanged = 10,
    PersistenceUnavailable = 11,
    ProcessingRestricted = 12
}
