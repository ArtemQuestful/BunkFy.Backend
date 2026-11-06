namespace BunkFy.Modules.Staff.Persistence.Repositories;

using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Domain.Aggregates;
using BunkFy.Modules.Staff.Domain.Models;
using BunkFy.Modules.Staff.Domain.Retention;
using StaffContractStatus = BunkFy.Modules.Staff.Contracts.StaffStatus;
using StaffAnchorResolutionDisposition = BunkFy.Modules.Staff.Contracts.StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition;

[AttributeUsage(AttributeTargets.Property)]
internal sealed class StaffTenantExportFieldAttribute(string fieldId)
    : Attribute
{
    public string FieldId { get; } = fieldId;
}

internal sealed record StaffMemberTenantExport(
    [property: StaffTenantExportField("staff.scope-id")]
    string ScopeId,
    [property: StaffTenantExportField("staff.staff-member-id")]
    Guid StaffMemberId,
    [property: StaffTenantExportField("staff.profile-state")]
    StaffProfileStateTenantExport ProfileState,
    [property: StaffTenantExportField("staff.staff-attribution")]
    StaffChangeAttributionTenantExport StaffAttribution);

internal sealed record StaffProfileStateTenantExport(
    string DisplayName,
    string? LegalName,
    string? WorkEmail,
    string? WorkPhone,
    string? EmployeeNumber,
    string? JobTitle,
    string? Department,
    string? AuthSubjectId,
    StaffMemberState Status,
    long Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastChangedAtUtc,
    DateTimeOffset? SuspendedAtUtc,
    DateTimeOffset? DepartedAtUtc,
    DateOnly? DepartureEffectiveOn,
    DateTimeOffset? AnonymisedAtUtc);

internal sealed record StaffChangeAttributionTenantExport(
    string CreatedBy,
    string LastChangedBy);

internal sealed record StaffMemberMutationOperationTenantExport(
    [property: StaffTenantExportField("staff.scope-id")]
    string ScopeId,
    [property: StaffTenantExportField("staff.staff-member-id")]
    Guid StaffMemberId,
    [property: StaffTenantExportField("staff.record-id")]
    Guid OperationId,
    [property: StaffTenantExportField("staff.member-mutation-operation")]
    StaffMemberMutationOperationStateTenantExport MemberMutationOperation);

internal sealed record StaffMemberMutationOperationStateTenantExport(
    StaffMemberMutationKind Kind,
    long ExpectedVersion,
    string RequestFingerprint,
    StaffContractStatus ResultStatus,
    long ResultVersion,
    DateTimeOffset CompletedAtUtc);

internal sealed record StaffIdentityProvisioningAnchorTenantExport(
    [property: StaffTenantExportField("staff.scope-id")]
    string ScopeId,
    [property: StaffTenantExportField("staff.staff-member-id")]
    Guid StaffMemberId,
    [property: StaffTenantExportField("staff.identity-provisioning-anchor")]
    StaffIdentityProvisioningAnchorStateTenantExport
        IdentityProvisioningAnchor);

internal sealed record StaffIdentityProvisioningAnchorStateTenantExport(
    StaffIdentityProvisioningSourceKind SourceKind,
    Guid SourceId,
    Guid? ResolutionEventId,
    DateTimeOffset AnchoredAtUtc);

internal sealed record StaffIdentityProvisioningAnchorResolutionTenantExport(
    [property: StaffTenantExportField("staff.scope-id")]
    string ScopeId,
    [property: StaffTenantExportField("staff.staff-member-id")]
    Guid StaffMemberId,
    [property: StaffTenantExportField(
        "staff.identity-provisioning-anchor-resolution")]
    StaffIdentityProvisioningAnchorResolutionStateTenantExport
        IdentityProvisioningAnchorResolution);

internal sealed record StaffIdentityProvisioningAnchorResolutionStateTenantExport(
    StaffIdentityProvisioningSourceKind SourceKind,
    Guid SourceId,
    long WorkspaceApplicationVersion,
    StaffAnchorResolutionDisposition Disposition,
    Guid ResolutionEventId,
    DateTimeOffset ResolvedAtUtc);

internal sealed record StaffPropertyAssignmentTenantExport(
    [property: StaffTenantExportField("staff.scope-id")]
    string ScopeId,
    [property: StaffTenantExportField("staff.property-id")]
    Guid PropertyId,
    [property: StaffTenantExportField("staff.staff-member-id")]
    Guid StaffMemberId,
    [property: StaffTenantExportField("staff.record-id")]
    Guid AssignmentId,
    [property: StaffTenantExportField("staff.assignment-record")]
    StaffPropertyAssignmentStateTenantExport AssignmentState,
    [property: StaffTenantExportField("staff.staff-attribution")]
    StaffAssignmentAttributionTenantExport StaffAttribution);

internal sealed record StaffPropertyAssignmentStateTenantExport(
    string? PropertyJobTitle,
    bool IsPrimary,
    bool IsCurrent,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    long AssignedAtVersion,
    string? UnassignmentReason,
    long? UnassignedAtVersion);

internal sealed record StaffAssignmentAttributionTenantExport(
    string AssignedBy,
    DateTimeOffset AssignedAtUtc,
    string? UnassignedBy,
    DateTimeOffset? UnassignedAtUtc);

internal sealed record StaffDataRightsCorrectionReceiptTenantExport(
    [property: StaffTenantExportField("staff.scope-id")]
    string ScopeId,
    [property: StaffTenantExportField("staff.staff-member-id")]
    Guid StaffMemberId,
    [property: StaffTenantExportField("staff.record-id")]
    Guid ReceiptId,
    [property: StaffTenantExportField("staff.data-rights-proof")]
    StaffDataRightsCorrectionProofTenantExport DataRightsProof);

internal sealed record StaffDataRightsCorrectionProofTenantExport(
    int ContractVersion,
    Guid ExecutionId,
    Guid CaseId,
    long ApprovalRevision,
    long SelectedRecordVersion,
    long CurrentRecordVersion,
    int ChangedFieldsMask,
    string RequestSha256,
    Guid ProfileEventId,
    Guid CompletionEventId,
    DateTimeOffset CompletedAtUtc);

internal sealed record StaffProcessingRestrictionTenantExport(
    [property: StaffTenantExportField("staff.scope-id")]
    string ScopeId,
    [property: StaffTenantExportField("staff.staff-member-id")]
    Guid StaffMemberId,
    [property: StaffTenantExportField("staff.record-id")]
    Guid RestrictionId,
    [property: StaffTenantExportField("staff.processing-restriction")]
    StaffProcessingRestrictionStateTenantExport ProcessingRestriction,
    [property: StaffTenantExportField("staff.staff-attribution")]
    StaffLifecycleAttributionTenantExport StaffAttribution);

internal sealed record StaffProcessingRestrictionStateTenantExport(
    Guid ApplyCaseId,
    long ApplyApprovalRevision,
    long ApplySelectedStaffVersion,
    StaffProcessingRestrictionState Status,
    long Version,
    DateTimeOffset AppliedAtUtc,
    Guid? ReleaseCaseId,
    long? ReleaseApprovalRevision,
    long? ReleaseSelectedStaffVersion,
    DateTimeOffset? ReleasedAtUtc);

internal sealed record StaffLifecycleAttributionTenantExport(
    string AppliedBy,
    string? ReleasedBy);

internal sealed record StaffProcessingRestrictionReceiptTenantExport(
    [property: StaffTenantExportField("staff.scope-id")]
    string ScopeId,
    [property: StaffTenantExportField("staff.staff-member-id")]
    Guid StaffMemberId,
    [property: StaffTenantExportField("staff.record-id")]
    Guid ReceiptId,
    [property: StaffTenantExportField("staff.processing-restriction")]
    StaffProcessingRestrictionReceiptStateTenantExport
        ProcessingRestriction,
    [property: StaffTenantExportField("staff.staff-attribution")]
    StaffActorAttributionTenantExport StaffAttribution);

internal sealed record StaffProcessingRestrictionReceiptStateTenantExport(
    Guid IdempotencyKey,
    Guid RestrictionId,
    StaffProcessingRestrictionAction Action,
    Guid CaseId,
    long ApprovalRevision,
    long SelectedStaffVersion,
    long ResultingRestrictionVersion,
    long ResultingProjectionRevision,
    bool EffectiveRestricted,
    Guid EventId,
    DateTimeOffset CompletedAtUtc);

internal sealed record StaffEmploymentGovernanceTenantExport(
    [property: StaffTenantExportField("staff.scope-id")]
    string ScopeId,
    [property: StaffTenantExportField("staff.staff-member-id")]
    Guid StaffMemberId,
    [property: StaffTenantExportField("staff.employment-governance")]
    StaffEmploymentGovernanceStateTenantExport EmploymentGovernance,
    [property: StaffTenantExportField("staff.staff-attribution")]
    StaffActorAttributionTenantExport StaffAttribution);

internal sealed record StaffEmploymentGovernanceStateTenantExport(
    int GovernanceContractVersion,
    long SelectedStaffVersion,
    string OperatingCountryCode,
    string PolicyId,
    int PolicyVersion,
    string DataRegionId,
    string TransferProfileId,
    string RetentionPolicyId,
    int RetentionPolicyVersion,
    string PolicyContentSha256,
    DateTimeOffset PolicyEffectiveAtUtc,
    DateTimeOffset PolicyExpiresAtUtc,
    DateTimeOffset EvaluatedAtUtc,
    IReadOnlyList<StaffGovernanceAcknowledgementTenantExport>
        AcceptedAcknowledgements,
    DateTimeOffset ConfiguredAtUtc,
    long Version);

internal sealed record StaffGovernanceAcknowledgementTenantExport(
    string AcknowledgementId,
    int AcknowledgementVersion);

internal sealed record StaffEmploymentGovernanceChangeReceiptTenantExport(
    [property: StaffTenantExportField("staff.scope-id")]
    string ScopeId,
    [property: StaffTenantExportField("staff.staff-member-id")]
    Guid StaffMemberId,
    [property: StaffTenantExportField("staff.record-id")]
    Guid ReceiptId,
    [property: StaffTenantExportField("staff.employment-governance-proof")]
    StaffEmploymentGovernanceChangeProofTenantExport
        EmploymentGovernanceProof,
    [property: StaffTenantExportField("staff.staff-attribution")]
    StaffActorAttributionTenantExport StaffAttribution);

internal sealed record StaffEmploymentGovernanceChangeProofTenantExport(
    Guid IdempotencyKey,
    int GovernanceContractVersion,
    long SelectedStaffVersion,
    long PreviousGovernanceVersion,
    long ResultingGovernanceVersion,
    string PolicyContentSha256,
    string AcknowledgementsSha256,
    string RequestSha256,
    string ReceiptSha256,
    DateTimeOffset CompletedAtUtc);

internal sealed record StaffDataHoldTenantExport(
    [property: StaffTenantExportField("staff.scope-id")]
    string ScopeId,
    [property: StaffTenantExportField("staff.staff-member-id")]
    Guid StaffMemberId,
    [property: StaffTenantExportField("staff.record-id")]
    Guid HoldId,
    [property: StaffTenantExportField("staff.data-hold")]
    StaffDataHoldStateTenantExport DataHold,
    [property: StaffTenantExportField("staff.staff-attribution")]
    StaffLifecycleAttributionTenantExport StaffAttribution);

internal sealed record StaffDataHoldStateTenantExport(
    string ReasonCode,
    StaffDataHoldState State,
    DateTimeOffset PlacedAtUtc,
    DateTimeOffset? ReleasedAtUtc,
    long Version);

internal sealed record StaffDataHoldReceiptTenantExport(
    [property: StaffTenantExportField("staff.scope-id")]
    string ScopeId,
    [property: StaffTenantExportField("staff.staff-member-id")]
    Guid StaffMemberId,
    [property: StaffTenantExportField("staff.record-id")]
    Guid ReceiptId,
    [property: StaffTenantExportField("staff.data-hold")]
    StaffDataHoldReceiptStateTenantExport DataHold,
    [property: StaffTenantExportField("staff.staff-attribution")]
    StaffActorAttributionTenantExport StaffAttribution);

internal sealed record StaffDataHoldReceiptStateTenantExport(
    Guid IdempotencyKey,
    Guid HoldId,
    StaffDataHoldAction Action,
    string ReasonCode,
    long SelectedStaffVersion,
    long ResultingHoldVersion,
    DateTimeOffset CompletedAtUtc);

internal sealed record StaffAnonymisationReceiptTenantExport(
    [property: StaffTenantExportField("staff.scope-id")]
    string ScopeId,
    [property: StaffTenantExportField("staff.staff-member-id")]
    Guid StaffMemberId,
    [property: StaffTenantExportField("staff.record-id")]
    Guid ReceiptId,
    [property: StaffTenantExportField("staff.anonymisation-proof")]
    StaffAnonymisationReceiptProofTenantExport AnonymisationProof,
    [property: StaffTenantExportField("staff.staff-attribution")]
    StaffActorAttributionTenantExport StaffAttribution);

internal sealed record StaffAnonymisationReceiptProofTenantExport(
    int ContractVersion,
    Guid IdempotencyKey,
    Guid CaseId,
    long ApprovalRevision,
    long OperationRevision,
    long SelectedStaffVersion,
    long ResultingStaffVersion,
    long SelectedOperationLockRevision,
    long ResultingOperationLockRevision,
    StaffAnonymisationDisposition Disposition,
    StaffAnonymisationReason Reason,
    string ApprovalEvidenceSha256,
    string StateBindingsSha256,
    Guid EventId,
    DateTimeOffset CompletedAtUtc,
    string CanonicalSha256);

internal sealed record StaffAnonymisationTombstoneTenantExport(
    [property: StaffTenantExportField("staff.scope-id")]
    string ScopeId,
    [property: StaffTenantExportField("staff.staff-member-id")]
    Guid StaffMemberId,
    [property: StaffTenantExportField("staff.anonymisation-proof")]
    StaffAnonymisationTombstoneProofTenantExport AnonymisationProof);

internal sealed record StaffAnonymisationTombstoneProofTenantExport(
    int ContractVersion,
    long Revision,
    StaffAnonymisationTombstoneState State,
    StaffAnonymisationAuthority Authority,
    DateTimeOffset CompletedAtUtc,
    Guid? LedgerEntryId,
    string OwnerReceiptSha256,
    DateTimeOffset? LastReplayedAtUtc);

internal sealed record StaffAnonymisationRestoreReceiptTenantExport(
    [property: StaffTenantExportField("staff.scope-id")]
    string ScopeId,
    [property: StaffTenantExportField("staff.staff-member-id")]
    Guid StaffMemberId,
    [property: StaffTenantExportField("staff.record-id")]
    Guid ReceiptId,
    [property: StaffTenantExportField("staff.anonymisation-proof")]
    StaffAnonymisationRestoreProofTenantExport AnonymisationProof);

internal sealed record StaffAnonymisationRestoreProofTenantExport(
    int ContractVersion,
    Guid LedgerEntryId,
    int OwnerReceiptContractVersion,
    Guid OwnerReceiptId,
    string OwnerReceiptSha256,
    long ResultingStaffVersion,
    long TombstoneRevision,
    DateTimeOffset ReplayedAtUtc,
    string CanonicalSha256);

internal sealed record StaffRetentionExecutionTenantExport(
    [property: StaffTenantExportField("staff.scope-id")]
    string ScopeId,
    [property: StaffTenantExportField("staff.record-id")]
    Guid ExecutionId,
    [property: StaffTenantExportField("staff.retention-execution")]
    StaffRetentionExecutionStateTenantExport RetentionExecution);

internal sealed record StaffRetentionExecutionStateTenantExport(
    string DataClassKey,
    int ExecutionPolicyVersion,
    int Attempt,
    long StartingProjectionOrdinal,
    StaffRetentionExecutionState State,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset DeadlineUtc,
    DateTimeOffset? CompletedAtUtc,
    int AffectedCount,
    int? ScannedCount,
    int? RemainingCount,
    string? OutcomeCode,
    DateTimeOffset? HoldReviewDueAtUtc,
    long Version);

internal sealed record StaffRetentionAnonymisationReceiptTenantExport(
    [property: StaffTenantExportField("staff.scope-id")]
    string ScopeId,
    [property: StaffTenantExportField("staff.staff-member-id")]
    Guid StaffMemberId,
    [property: StaffTenantExportField("staff.record-id")]
    Guid ReceiptId,
    [property: StaffTenantExportField("staff.retention-proof")]
    StaffRetentionAnonymisationProofTenantExport RetentionProof,
    [property: StaffTenantExportField("staff.staff-attribution")]
    StaffActorAttributionTenantExport StaffAttribution);

internal sealed record StaffRetentionAnonymisationProofTenantExport(
    int ContractVersion,
    Guid ExecutionId,
    long SelectedStaffVersion,
    long ResultingStaffVersion,
    long SelectedOperationLockRevision,
    long ResultingOperationLockRevision,
    DateTimeOffset DepartedAtUtc,
    DateTimeOffset RetentionDeadlineUtc,
    string PolicyEvidenceSha256,
    Guid EventId,
    DateTimeOffset CompletedAtUtc,
    string CanonicalSha256);

internal sealed record StaffActorAttributionTenantExport(string ActorId);
