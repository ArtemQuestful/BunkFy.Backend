namespace BunkFy.Modules.Workspaces.Contracts;

public sealed record WorkspaceStaffOnboardingRecoveryAttemptDto(
    Guid OperationId,
    Guid ApplicationId,
    WorkspaceStaffOnboardingRecoveryAttemptOutcome Outcome,
    WorkspaceStaffOnboardingRecoveryAttemptReason Reason,
    WorkspaceStaffOnboardingStatus ApplicationStatus,
    long ApplicationVersion,
    WorkspaceStaffOnboardingRecoveryRetryDirective RetryDirective,
    int? RetryAfterSeconds,
    DateTimeOffset RecordedAtUtc)
{
    public const int RetryLaterDelaySeconds = 5;
}

public enum WorkspaceStaffOnboardingRecoveryRetryDirective
{
    None = 0,
    NewOperationAfterDelay = 1
}

public enum WorkspaceStaffOnboardingRecoveryAttemptOutcome
{
    Unknown = 0,
    Recovered = 1,
    ReviewRequired = 2,
    RetryLater = 3
}

public enum WorkspaceStaffOnboardingRecoveryAttemptReason
{
    Unknown = 0,
    ExactStateRecovered = 1,
    LegacyMembershipBindingMissing = 2,
    MembershipConflict = 3,
    MembershipUnavailable = 4,
    StaffConflict = 5,
    StaffUnavailable = 6,
    AccessConflict = 7,
    AccessUnavailable = 8,
    ProcessingRestricted = 9
}
