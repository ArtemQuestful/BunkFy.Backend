namespace BunkFy.Modules.Workspaces.Persistence.Repositories;

using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Domain.DataRights;

internal sealed record WorkspaceStaffOnboardingDataRightsExport(
    Guid Id,
    string ScopeId,
    WorkspaceStaffOnboardingSource SourceKind,
    Guid SourceId,
    Guid? ClaimId,
    long? ClaimVersion,
    string SubjectId,
    string? VerifiedAccountEmail,
    string? DisplayName,
    string? LegalName,
    string? WorkEmail,
    string? WorkPhone,
    string? EmployeeNumber,
    string? JobTitle,
    string? Department,
    WorkspaceStaffOnboardingState Status,
    Guid? StaffMemberId,
    Guid? IdentityAnchorExpectedResolutionEventId,
    Guid? IdentityAnchorContinuationEventId,
    Guid? IdentityAnchorResolutionEventId,
    Guid? IdentityAnchorResolutionStaffMemberId,
    long? IdentityAnchorResolutionApplicationVersion,
    WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition?
        IdentityAnchorResolutionDisposition,
    DateTimeOffset? IdentityAnchorResolutionIntentAtUtc,
    DateTimeOffset? IdentityAnchorResolutionObservedAtUtc,
    long IdentityAnchorSweepOrdinal,
    string? FailureCode,
    long Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastChangedAtUtc);

internal sealed record WorkspaceStaffDeferredClaimWithdrawalDataRightsExport(
    Guid ClaimId,
    string ScopeId,
    Guid EnrollmentLinkId,
    long ClaimVersion,
    Guid EventId,
    DateTimeOffset OccurredAtUtc);

internal sealed record
    WorkspaceStaffOnboardingCorrectionReceiptDataRightsExport(
        int ContractVersion,
        Guid ReceiptId,
        Guid ExecutionId,
        Guid CaseId,
        long ApprovalRevision,
        Guid ApplicationId,
        long SelectedRecordVersion,
        long CurrentRecordVersion,
        IReadOnlyCollection<WorkspaceStaffOnboardingApplicantField>
            ChangedFields,
        Guid ApplicantEventId,
        Guid CompletionEventId,
        DateTimeOffset CompletedAtUtc);

internal sealed record
    WorkspaceStaffOnboardingRecoveryReceiptDataRightsExport(
        Guid ReceiptId,
        string ScopeId,
        int ContractVersion,
        Guid OperationId,
        Guid ApplicationId,
        Guid SourceId,
        Guid StaffMemberId,
        WorkspaceStaffOnboardingRecoveryTriggerKind TriggerKind,
        Guid TriggerId,
        string? ApplicantSubjectId,
        string? PlanCreatorActorId,
        string? RecoveryActorId,
        string? EffectiveSystemActorId,
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

internal sealed record
    WorkspaceStaffOnboardingProcessingRestrictionDataRightsExport(
        Guid RestrictionId,
        Guid ApplicationId,
        Guid ApplyCaseId,
        long ApplyApprovalRevision,
        long ApplySelectedOnboardingVersion,
        WorkspaceStaffOnboardingProcessingRestrictionState Status,
        long Version,
        DateTimeOffset AppliedAtUtc,
        Guid? ReleaseCaseId,
        long? ReleaseApprovalRevision,
        long? ReleaseSelectedOnboardingVersion,
        DateTimeOffset? ReleasedAtUtc);

internal sealed record
    WorkspaceStaffOnboardingProcessingRestrictionReceiptDataRightsExport(
        Guid ReceiptId,
        Guid RestrictionId,
        WorkspaceStaffOnboardingProcessingRestrictionAction Action,
        Guid ApplicationId,
        Guid CaseId,
        long ApprovalRevision,
        long SelectedOnboardingVersion,
        long ResultingRestrictionVersion,
        long ResultingProjectionRevision,
        bool EffectiveRestricted,
        Guid EventId,
        DateTimeOffset CompletedAtUtc);

internal sealed record WorkspaceStaffAccessProcessDataRightsExport(
    Guid Id,
    string ScopeId,
    Guid StaffMemberId,
    string SubjectId,
    WorkspaceStaffAccessTargetState TargetState,
    long TargetStaffVersion,
    DateOnly EffectiveOn,
    string RequestedBy,
    WorkspaceStaffAccessProcessState State,
    WorkspaceStaffAccessRestorationDisposition RestorationDisposition,
    string? FailureCode,
    long Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastChangedAtUtc,
    DateTimeOffset? CompletedAtUtc);

internal sealed record WorkspaceStaffAccessProfileDataRightsExport(
    Guid ProcessId,
    Guid ProfileId,
    string AssignmentScope);

internal sealed record WorkspaceStaffAccessPlanDataRightsExport(
    Guid Id,
    string ScopeId,
    WorkspaceStaffOnboardingSource SourceKind,
    Guid ProfileId,
    string ProfileKey,
    string CreatedBySubjectId,
    WorkspaceStaffAccessPlanState Status,
    DateTimeOffset? SourceExpiredAtUtc,
    long Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastChangedAtUtc);

internal sealed record WorkspaceStaffAccessPlanPropertyDataRightsExport(
    string ScopeId,
    Guid PlanId,
    Guid PropertyId);

internal sealed record WorkspaceStaffRetentionCorrelationDataRightsExport(
    string ScopeId,
    Guid StaffMemberId,
    long SelectedStaffVersion,
    WorkspaceStaffRetentionCorrelationProofDataRightsExport Proof);

internal sealed record WorkspaceStaffRetentionCorrelationProofDataRightsExport(
    Guid Id,
    int ContractVersion,
    Guid ExecutionId,
    int OnboardingRecordsScrubbed,
    int AccessProcessRecordsScrubbed,
    int AccessPlanRecordsScrubbed,
    DateTimeOffset CompletedAtUtc,
    string CanonicalSha256);

internal sealed record
    WorkspaceStaffOnboardingRetentionExecutionDataRightsExport(
        string ScopeId,
        Guid ExecutionId,
        WorkspaceStaffOnboardingRetentionExecutionStateDataRightsExport
            RetentionExecution);

internal sealed record
    WorkspaceStaffOnboardingRetentionExecutionStateDataRightsExport(
        string DataClassKey,
        int ExecutionPolicyVersion,
        int Attempt,
        WorkspaceStaffOnboardingRetentionExecutionState State,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset DeadlineUtc,
        DateTimeOffset? CompletedAtUtc,
        int AffectedCount,
        int ScannedCount,
        int? RemainingCount,
        string? OutcomeCode,
        long Version);

internal sealed record
    WorkspaceStaffIdentityAnchorSweepCheckpointDataRightsExport(
        WorkspaceStaffIdentityAnchorSweepCheckpointExportState Checkpoint);

internal sealed record
    WorkspaceStaffHistoricalNoProvisionReceiptDataRightsExport(
        int ContractVersion,
        Guid ReceiptId,
        string ScopeId,
        Guid OperationId,
        Guid ApplicationId,
        WorkspaceStaffOnboardingSource SourceKind,
        Guid SourceId,
        long ExpectedApplicationVersion,
        WorkspaceStaffOnboardingState ExpectedApplicationStatus,
        long ResultApplicationVersion,
        WorkspaceStaffOnboardingState ResultApplicationStatus,
        long OrganizationsScopeRevision,
        long OrganizationsSourceVersion,
        WorkspaceStaffHistoricalNoProvisionAuthorityStatus
            OrganizationsSourceStatus,
        string StaffEvidenceSha256,
        Guid ExternalEvidenceManifestId,
        string ExternalEvidenceSha256,
        string ReviewerId,
        DateTimeOffset ReviewedAtUtc,
        string CanonicalSha256);

internal sealed record WorkspaceStaffIdentityAnchorSweepCheckpointExportState(
    int ProtocolVersion,
    bool HasActiveCycle,
    long? CycleUpperOrdinal,
    long? AfterOrdinal,
    DateTimeOffset? CycleStartedAtUtc,
    long CycleScannedCount,
    long CycleBacklogCount,
    long? LastCompletedUpperOrdinal,
    DateTimeOffset? LastCompletedAtUtc,
    long LastCompletedScannedCount,
    long LastCompletedBacklogCount,
    DateTimeOffset UpdatedAtUtc);
