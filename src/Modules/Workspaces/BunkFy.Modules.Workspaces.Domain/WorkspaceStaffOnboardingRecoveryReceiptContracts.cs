namespace BunkFy.Modules.Workspaces.Domain;

public sealed record WorkspaceStaffOnboardingRecoveryReceiptValues(
    Guid ReceiptId,
    string TenantId,
    Guid OperationId,
    Guid ApplicationId,
    Guid SourceId,
    Guid StaffMemberId,
    WorkspaceStaffOnboardingRecoveryTriggerKind TriggerKind,
    Guid TriggerId,
    string ApplicantSubjectId,
    string PlanCreatorActorId,
    string RecoveryActorId,
    string EffectiveSystemActorId,
    string RequestSha256,
    long ExpectedApplicationVersion,
    long SelectedApplicationVersion,
    WorkspaceStaffOnboardingState SelectedApplicationStatus,
    long ResultingApplicationVersion,
    WorkspaceStaffOnboardingState ResultingApplicationStatus,
    Guid CurrentProfileId,
    long CurrentProfileVersion,
    int PropertyCount,
    string TargetSetSha256,
    long? OrganizationRevision,
    Guid? MembershipId,
    long? MembershipVersion,
    int? MembershipStatus,
    long? StaffTenantRevision,
    long? StaffMemberVersion,
    string? StaffStateProofSha256,
    string? StaffPropertySetSha256,
    long? AccessManagementRevision,
    string? AccessFactsSha256,
    WorkspaceRecoveryVerification OrganizationVerification,
    WorkspaceRecoveryVerification StaffVerification,
    WorkspaceRecoveryVerification AccessVerification,
    WorkspaceStaffOnboardingRecoveryOutcome Outcome,
    WorkspaceStaffOnboardingRecoveryReason Reason,
    bool AccessMutationPerformed,
    DateTimeOffset? OrganizationObservedAtUtc,
    DateTimeOffset? StaffObservedAtUtc,
    DateTimeOffset? AccessObservedAtUtc,
    DateTimeOffset RecordedAtUtc);

public enum WorkspaceStaffOnboardingRecoveryTriggerKind
{
    Unknown = 0,
    ManualOperator = 1
}

public enum WorkspaceRecoveryVerification
{
    Unknown = 0,
    Equivalent = 1,
    ReviewRequired = 2,
    RetryLater = 3
}

public enum WorkspaceStaffOnboardingRecoveryOutcome
{
    Unknown = 0,
    Recovered = 1,
    ReviewRequired = 2,
    RetryLater = 3
}

public enum WorkspaceStaffOnboardingRecoveryReason
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
