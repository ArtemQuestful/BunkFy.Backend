namespace BunkFy.Modules.Guests.Persistence.Repositories;

using System.Text.Json.Serialization;
using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.Models;
using BunkFy.Modules.Guests.Domain.Retention;
using BunkFy.Modules.Guests.Application.Ports;
using GuestContractStatus = BunkFy.Modules.Guests.Contracts.GuestStatus;

[AttributeUsage(AttributeTargets.Property)]
internal sealed class GuestsTenantExportFieldAttribute(string fieldId)
    : Attribute
{
    public string FieldId { get; } = fieldId;
}

internal sealed record GuestProfileTenantExport(
    [property: GuestsTenantExportField("guests.scope-id")]
    string ScopeId,
    [property: GuestsTenantExportField("guests.property-id")]
    Guid PropertyId,
    [property: GuestsTenantExportField("guests.guest-id")]
    Guid GuestId,
    [property: GuestsTenantExportField("guests.profile-state")]
    GuestProfileStateTenantExport ProfileState,
    [property: GuestsTenantExportField("guests.staff-attribution")]
    GuestProfileStaffTenantExport StaffAttribution);

internal sealed record GuestProfileStateTenantExport(
    string DisplayName,
    string? LegalName,
    string? Email,
    string? Phone,
    DateOnly? DateOfBirth,
    string? NationalityCountryCode,
    string? PreferredLanguageTag,
    string? Notes,
    Guid? CreationConfirmationId,
    GuestProfileState Status,
    long Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastChangedAtUtc,
    DateTimeOffset? ArchivedAtUtc,
    DateTimeOffset? AnonymisedAtUtc)
{
    public string[] LanguageTags { get; init; } = [];
}

internal sealed record GuestProfileStaffTenantExport(
    string CreatedBy,
    string LastChangedBy);

internal sealed record GuestManagementOperationTenantExport(
    [property: GuestsTenantExportField("guests.scope-id")]
    string ScopeId,
    [property: GuestsTenantExportField("guests.property-id")]
    Guid PropertyId,
    [property: GuestsTenantExportField("guests.guest-id")]
    Guid GuestId,
    [property: GuestsTenantExportField("guests.record-id")]
    Guid OperationId,
    [property: GuestsTenantExportField("guests.management-operation")]
    GuestManagementOperationStateTenantExport ManagementOperation);

internal sealed record GuestManagementOperationStateTenantExport(
    GuestManagementOperationKind Kind,
    long ExpectedVersion,
    string? RequestFingerprint,
    GuestContractStatus ResultStatus,
    long ResultVersion,
    DateTimeOffset CompletedAtUtc);

internal sealed record GuestDataRightsCorrectionReceiptTenantExport(
    [property: GuestsTenantExportField("guests.scope-id")]
    string ScopeId,
    [property: GuestsTenantExportField("guests.property-id")]
    Guid PropertyId,
    [property: GuestsTenantExportField("guests.guest-id")]
    Guid GuestId,
    [property: GuestsTenantExportField("guests.record-id")]
    Guid ReceiptId,
    [property: GuestsTenantExportField("guests.data-rights-proof")]
    GuestDataRightsCorrectionProofTenantExport DataRightsProof);

internal sealed record GuestDataRightsCorrectionProofTenantExport(
    int ContractVersion,
    Guid IdempotencyKey,
    Guid CaseId,
    long ApprovalRevision,
    long SelectedRecordVersion,
    long CurrentRecordVersion,
    int ChangedFieldsMask,
    Guid EventId,
    Guid CompletionEventId,
    DateTimeOffset CompletedAtUtc);

internal sealed record GuestProcessingRestrictionTenantExport(
    [property: GuestsTenantExportField("guests.scope-id")]
    string ScopeId,
    [property: GuestsTenantExportField("guests.property-id")]
    Guid PropertyId,
    [property: GuestsTenantExportField("guests.guest-id")]
    Guid GuestId,
    [property: GuestsTenantExportField("guests.record-id")]
    Guid RestrictionId,
    [property: GuestsTenantExportField(
        "guests.processing-restriction")]
    GuestProcessingRestrictionStateTenantExport ProcessingRestriction,
    [property: GuestsTenantExportField("guests.staff-attribution")]
    GuestRestrictionStaffTenantExport StaffAttribution);

internal sealed record GuestProcessingRestrictionStateTenantExport(
    Guid ApplyCaseId,
    long ApplyApprovalRevision,
    long ApplySelectedGuestVersion,
    GuestProcessingRestrictionState Status,
    long Version,
    DateTimeOffset AppliedAtUtc,
    Guid? ReleaseCaseId,
    long? ReleaseApprovalRevision,
    long? ReleaseSelectedGuestVersion,
    DateTimeOffset? ReleasedAtUtc);

internal sealed record GuestRestrictionStaffTenantExport(
    string AppliedBy,
    string? ReleasedBy);

internal sealed record GuestProcessingRestrictionReceiptTenantExport(
    [property: GuestsTenantExportField("guests.scope-id")]
    string ScopeId,
    [property: GuestsTenantExportField("guests.property-id")]
    Guid PropertyId,
    [property: GuestsTenantExportField("guests.guest-id")]
    Guid GuestId,
    [property: GuestsTenantExportField("guests.record-id")]
    Guid ReceiptId,
    [property: GuestsTenantExportField(
        "guests.processing-restriction")]
    GuestProcessingRestrictionReceiptStateTenantExport
        ProcessingRestriction,
    [property: GuestsTenantExportField("guests.staff-attribution")]
    GuestActorStaffTenantExport StaffAttribution);

internal sealed record GuestProcessingRestrictionReceiptStateTenantExport(
    Guid IdempotencyKey,
    Guid RestrictionId,
    GuestProcessingRestrictionAction Action,
    Guid CaseId,
    long ApprovalRevision,
    long SelectedGuestVersion,
    long ResultingRestrictionVersion,
    long ResultingProjectionRevision,
    bool EffectiveRestricted,
    Guid EventId,
    DateTimeOffset CompletedAtUtc);

internal sealed record GuestDataHoldTenantExport(
    [property: GuestsTenantExportField("guests.scope-id")]
    string ScopeId,
    [property: GuestsTenantExportField("guests.property-id")]
    Guid PropertyId,
    [property: GuestsTenantExportField("guests.guest-id")]
    Guid GuestId,
    [property: GuestsTenantExportField("guests.record-id")]
    Guid HoldId,
    [property: GuestsTenantExportField("guests.data-hold")]
    GuestDataHoldStateTenantExport DataHold,
    [property: GuestsTenantExportField("guests.staff-attribution")]
    GuestDataHoldStaffTenantExport StaffAttribution);

internal sealed record GuestDataHoldStateTenantExport(
    string ReasonCode,
    GuestDataHoldState State,
    DateTimeOffset PlacedAtUtc,
    DateTimeOffset? ReleasedAtUtc,
    long Version);

internal sealed record GuestDataHoldStaffTenantExport(
    string PlacedBy,
    string? ReleasedBy);

internal sealed record GuestDataHoldReceiptTenantExport(
    [property: GuestsTenantExportField("guests.scope-id")]
    string ScopeId,
    [property: GuestsTenantExportField("guests.property-id")]
    Guid PropertyId,
    [property: GuestsTenantExportField("guests.guest-id")]
    Guid GuestId,
    [property: GuestsTenantExportField("guests.record-id")]
    Guid ReceiptId,
    [property: GuestsTenantExportField("guests.data-hold")]
    GuestDataHoldReceiptStateTenantExport DataHold,
    [property: GuestsTenantExportField("guests.staff-attribution")]
    GuestActorStaffTenantExport StaffAttribution);

internal sealed record GuestDataHoldReceiptStateTenantExport(
    Guid IdempotencyKey,
    Guid HoldId,
    GuestDataHoldAction Action,
    string ReasonCode,
    long SelectedGuestVersion,
    long ResultingHoldVersion,
    DateTimeOffset CompletedAtUtc);

internal sealed record GuestAnonymisationReceiptTenantExport(
    [property: GuestsTenantExportField("guests.scope-id")]
    string ScopeId,
    [property: GuestsTenantExportField("guests.property-id")]
    Guid PropertyId,
    [property: GuestsTenantExportField("guests.guest-id")]
    Guid GuestId,
    [property: GuestsTenantExportField("guests.record-id")]
    Guid ReceiptId,
    [property: GuestsTenantExportField("guests.anonymisation-proof")]
    GuestAnonymisationReceiptProofTenantExport AnonymisationProof,
    [property: GuestsTenantExportField("guests.staff-attribution")]
    GuestActorStaffTenantExport StaffAttribution);

internal sealed record GuestAnonymisationReceiptProofTenantExport(
    int ContractVersion,
    Guid IdempotencyKey,
    Guid CaseId,
    long ApprovalRevision,
    long OperationRevision,
    long SelectedGuestVersion,
    long ResultingGuestVersion,
    GuestAnonymisationDisposition Disposition,
    GuestAnonymisationReason Reason,
    int AffectedPropertyCount,
    string ApprovalEvidenceSha256,
    string PolicySetSha256,
    Guid EventId,
    DateTimeOffset CompletedAtUtc,
    string CanonicalSha256);

internal sealed record GuestAnonymisationTombstoneTenantExport(
    [property: GuestsTenantExportField("guests.scope-id")]
    string ScopeId,
    [property: GuestsTenantExportField("guests.guest-id")]
    Guid GuestId,
    [property: GuestsTenantExportField("guests.anonymisation-proof")]
    GuestAnonymisationTombstoneProofTenantExport AnonymisationProof);

internal sealed record GuestAnonymisationTombstoneProofTenantExport(
    int ContractVersion,
    long Revision,
    GuestAnonymisationTombstoneState State,
    GuestAnonymisationAuthority Authority,
    DateTimeOffset CompletedAtUtc,
    Guid? LedgerEntryId,
    string OwnerReceiptSha256,
    DateTimeOffset? LastReplayedAtUtc);

internal sealed record GuestAnonymisationRestoreReceiptTenantExport(
    [property: GuestsTenantExportField("guests.scope-id")]
    string ScopeId,
    [property: GuestsTenantExportField("guests.guest-id")]
    Guid GuestId,
    [property: GuestsTenantExportField("guests.record-id")]
    Guid ReceiptId,
    [property: GuestsTenantExportField("guests.anonymisation-proof")]
    GuestAnonymisationRestoreProofTenantExport AnonymisationProof);

internal sealed record GuestAnonymisationRestoreProofTenantExport(
    int ContractVersion,
    Guid LedgerEntryId,
    int OwnerReceiptContractVersion,
    Guid OwnerReceiptId,
    string OwnerReceiptSha256,
    long ResultingGuestVersion,
    long TombstoneRevision,
    DateTimeOffset ReplayedAtUtc,
    string CanonicalSha256);

internal sealed record GuestRetentionExecutionTenantExport(
    [property: GuestsTenantExportField("guests.scope-id")]
    string ScopeId,
    [property: GuestsTenantExportField("guests.record-id")]
    Guid ExecutionId,
    [property: GuestsTenantExportField("guests.retention-execution")]
    GuestRetentionExecutionStateTenantExport RetentionExecution);

internal sealed record GuestRetentionExecutionStateTenantExport(
    string DataClassKey,
    int ExecutionPolicyVersion,
    int Attempt,
    long StartingProjectionOrdinal,
    GuestRetentionExecutionState State,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset DeadlineUtc,
    DateTimeOffset? CompletedAtUtc,
    int AffectedCount,
    int? ScannedCount,
    int? RemainingCount,
    string? OutcomeCode,
    DateTimeOffset? HoldReviewDueAtUtc,
    long Version);

internal sealed record GuestRetentionAnonymisationReceiptTenantExport(
    [property: GuestsTenantExportField("guests.scope-id")]
    string ScopeId,
    [property: GuestsTenantExportField("guests.guest-id")]
    Guid GuestId,
    [property: GuestsTenantExportField("guests.record-id")]
    Guid ReceiptId,
    [property: GuestsTenantExportField("guests.retention-proof")]
    GuestRetentionAnonymisationProofTenantExport RetentionProof,
    [property: GuestsTenantExportField("guests.staff-attribution")]
    GuestActorStaffTenantExport StaffAttribution);

internal sealed record GuestRetentionAnonymisationProofTenantExport(
    int ContractVersion,
    Guid ExecutionId,
    long SelectedGuestVersion,
    long ResultingGuestVersion,
    int AffectedPropertyCount,
    DateTimeOffset RetentionDeadlineUtc,
    string PolicySetSha256,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? TimeZoneCatalogVersion,
    Guid EventId,
    DateTimeOffset CompletedAtUtc,
    string CanonicalSha256);

internal sealed record GuestActorStaffTenantExport(string ActorId);
