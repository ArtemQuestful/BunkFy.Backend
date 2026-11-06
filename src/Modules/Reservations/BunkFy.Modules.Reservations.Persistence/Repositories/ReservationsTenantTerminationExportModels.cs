namespace BunkFy.Modules.Reservations.Persistence.Repositories;

using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Domain.DataRights;
using BunkFy.Modules.Reservations.Domain.GuestRecords;
using BunkFy.Modules.Reservations.Domain.Models;
using BunkFy.Modules.Reservations.Domain.Retention;
using BunkFy.Modules.Reservations.Domain.StayAmendments;
using DomainAnonymisationDisposition =
    BunkFy.Modules.Reservations.Domain.DataRights
        .ReservationAnonymisationDisposition;
using DomainAnonymisationReason =
    BunkFy.Modules.Reservations.Domain.DataRights
        .ReservationAnonymisationReason;
using DomainDataHoldAction =
    BunkFy.Modules.Reservations.Domain.Models.ReservationDataHoldAction;
using DomainGuestRecordLinkReviewReason =
    BunkFy.Modules.Reservations.Domain.GuestRecords
        .ReservationGuestRecordLinkReviewReason;

[AttributeUsage(AttributeTargets.Property)]
internal sealed class ReservationsTenantExportFieldAttribute(string fieldId)
    : Attribute
{
    public string FieldId { get; } = fieldId;
}

internal sealed record ReservationTenantExport(
    [property: ReservationsTenantExportField("reservations.scope-id")]
    string ScopeId,
    [property: ReservationsTenantExportField("reservations.property-id")]
    Guid PropertyId,
    [property: ReservationsTenantExportField("reservations.reservation-id")]
    Guid ReservationId,
    [property: ReservationsTenantExportField("reservations.booking-state")]
    ReservationBookingStateTenantExport BookingState,
    [property: ReservationsTenantExportField("reservations.guest-details")]
    ReservationGuestDetailsTenantExport GuestDetails,
    [property: ReservationsTenantExportField(
        "reservations.provider-provenance")]
    ReservationProviderProvenanceTenantExport ProviderProvenance,
    [property: ReservationsTenantExportField(
        "reservations.staff-attribution")]
    ReservationStaffAttributionTenantExport StaffAttribution);

internal sealed record ReservationBookingStateTenantExport(
    Guid AllocationRequestId,
    Guid? AllocationId,
    long? AllocationVersion,
    ReservationAllocationRejection? AllocationRejection,
    Guid? ReleaseRequestId,
    int? LastReleaseRejectionCode,
    Guid? PendingAllocationAmendmentId,
    Guid? PendingInventoryAmendmentRequestId,
    int? LastAllocationAmendmentRejectionCode,
    DateOnly? PendingStayBusinessDate,
    DateOnly? CheckedInBusinessDate,
    DateTimeOffset? CheckedInAtUtc,
    DateOnly? NoShowBusinessDate,
    DateTimeOffset? NoShowAtUtc,
    DateOnly? CheckedOutBusinessDate,
    DateTimeOffset? CheckedOutAtUtc,
    DateOnly Arrival,
    DateOnly Departure,
    TimeOnly? ExpectedArrivalTime,
    TimeOnly? ExpectedDepartureTime,
    long DetailsRevision,
    ReservationState Status,
    long Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? UpdatedAtUtc,
    DateTimeOffset? TerminalAtUtc,
    bool IsAnonymised,
    DateTimeOffset? AnonymisedAtUtc);

internal sealed record ReservationGuestDetailsTenantExport(
    string PrimaryGuestName,
    string? Email,
    string? Phone,
    int GuestCount,
    string? Notes);

internal sealed record ReservationProviderProvenanceTenantExport(
    ReservationSource Source,
    string? SourceSystem,
    string? SourceReference,
    ReservationDetailsChangeOrigin LastDetailsChangeOrigin,
    Guid? LastDetailsAdapterConnectionId,
    Guid? LastDetailsExternalOperationId,
    DateTimeOffset LastDetailsChangedAtUtc);

internal sealed record ReservationStaffAttributionTenantExport(
    string? PendingCancellationActorId,
    string? PendingStayActorId,
    string? CheckedInBy,
    string? NoShowBy,
    string? CheckedOutBy,
    string? LastDetailsActorId);

internal sealed record ReservationRequestedInventoryUnitTenantExport(
    [property: ReservationsTenantExportField("reservations.scope-id")]
    string ScopeId,
    [property: ReservationsTenantExportField("reservations.property-id")]
    Guid PropertyId,
    [property: ReservationsTenantExportField("reservations.reservation-id")]
    Guid ReservationId,
    [property: ReservationsTenantExportField(
        "reservations.inventory-unit-id")]
    Guid InventoryUnitId);

internal sealed record ReservationPendingAmendmentTenantExport(
    [property: ReservationsTenantExportField("reservations.scope-id")]
    string ScopeId,
    [property: ReservationsTenantExportField("reservations.property-id")]
    Guid PropertyId,
    [property: ReservationsTenantExportField("reservations.reservation-id")]
    Guid ReservationId,
    [property: ReservationsTenantExportField("reservations.record-id")]
    Guid AmendmentRequestId,
    [property: ReservationsTenantExportField("reservations.booking-state")]
    ReservationPendingAmendmentStateTenantExport BookingState,
    [property: ReservationsTenantExportField("reservations.guest-details")]
    ReservationGuestDetailsTenantExport GuestDetails,
    [property: ReservationsTenantExportField(
        "reservations.provider-provenance")]
    ReservationPendingAmendmentProviderTenantExport ProviderProvenance,
    [property: ReservationsTenantExportField(
        "reservations.staff-attribution")]
    ReservationPendingAmendmentStaffTenantExport StaffAttribution);

internal sealed record ReservationPendingAmendmentStateTenantExport(
    DateOnly Arrival,
    DateOnly Departure,
    TimeOnly? ExpectedArrivalTime,
    TimeOnly? ExpectedDepartureTime,
    IReadOnlyCollection<Guid> InventoryUnitIds,
    long ReservationVersion);

internal sealed record ReservationPendingAmendmentProviderTenantExport(
    string RequestFingerprint,
    ReservationDetailsChangeOrigin ChangeOrigin,
    Guid? AdapterConnectionId,
    Guid? ExternalOperationId,
    Guid CorrelationId);

internal sealed record ReservationPendingAmendmentStaffTenantExport(
    string? ActorId);

internal sealed record ReservationGuestLinkTenantExport(
    [property: ReservationsTenantExportField("reservations.scope-id")]
    string ScopeId,
    [property: ReservationsTenantExportField("reservations.property-id")]
    Guid PropertyId,
    [property: ReservationsTenantExportField("reservations.reservation-id")]
    Guid ReservationId,
    [property: ReservationsTenantExportField("reservations.guest-id")]
    Guid GuestId,
    [property: ReservationsTenantExportField("reservations.guest-link")]
    ReservationGuestLinkStateTenantExport GuestLink,
    [property: ReservationsTenantExportField(
        "reservations.staff-attribution")]
    ReservationGuestLinkStaffTenantExport StaffAttribution);

internal sealed record ReservationGuestLinkStateTenantExport(
    ReservationGuestRole Role,
    DateTimeOffset LinkedAtUtc,
    long LinkVersion,
    bool IsCurrent,
    DateTimeOffset? UnlinkedAtUtc,
    DateOnly? UnlinkedArrival,
    DateOnly? UnlinkedDeparture,
    ReservationState? UnlinkedReservationStatus,
    DateOnly? UnlinkedCheckedInBusinessDate,
    DateOnly? UnlinkedNoShowBusinessDate,
    DateOnly? UnlinkedCheckedOutBusinessDate);

internal sealed record ReservationGuestLinkStaffTenantExport(
    string LinkedBy,
    string? UnlinkedBy);

internal sealed record ReservationGuestRecordLinkProcessTenantExport(
    [property: ReservationsTenantExportField("reservations.scope-id")]
    string ScopeId,
    [property: ReservationsTenantExportField("reservations.property-id")]
    Guid PropertyId,
    [property: ReservationsTenantExportField("reservations.reservation-id")]
    Guid ReservationId,
    [property: ReservationsTenantExportField("reservations.guest-id")]
    Guid GuestId,
    [property: ReservationsTenantExportField("reservations.record-id")]
    Guid OperationId,
    [property: ReservationsTenantExportField(
        "reservations.guest-record-link-process")]
    ReservationGuestRecordLinkProcessStateTenantExport Process,
    [property: ReservationsTenantExportField(
        "reservations.staff-attribution")]
    ReservationGuestRecordLinkProcessStaffTenantExport StaffAttribution);

internal sealed record ReservationGuestRecordLinkProcessStateTenantExport(
    Guid CreationConfirmationId,
    long ExpectedReservationVersion,
    ReservationGuestRecordLinkProcessState State,
    DomainGuestRecordLinkReviewReason ReviewReason,
    long Revision,
    int DispatchRevision,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

internal sealed record ReservationGuestRecordLinkProcessStaffTenantExport(
    string? RequestedBy);

internal sealed record ReservationDetailsHistoryTenantExport(
    [property: ReservationsTenantExportField("reservations.scope-id")]
    string ScopeId,
    [property: ReservationsTenantExportField("reservations.property-id")]
    Guid PropertyId,
    [property: ReservationsTenantExportField("reservations.reservation-id")]
    Guid ReservationId,
    [property: ReservationsTenantExportField("reservations.record-id")]
    Guid ChangeId,
    [property: ReservationsTenantExportField("reservations.details-history")]
    ReservationDetailsHistoryStateTenantExport DetailsHistory,
    [property: ReservationsTenantExportField(
        "reservations.provider-provenance")]
    ReservationDetailsHistoryProviderTenantExport ProviderProvenance,
    [property: ReservationsTenantExportField(
        "reservations.staff-attribution")]
    ReservationDetailsHistoryStaffTenantExport StaffAttribution);

internal sealed record ReservationDetailsHistoryStateTenantExport(
    long FromRevision,
    long ToRevision,
    ReservationDetailsChangeOrigin Origin,
    string OperationDeduplicationKey,
    Guid CorrelationId,
    string ChangedFieldsJson,
    string? BeforeSnapshotJson,
    string AfterSnapshotJson,
    string AfterSnapshotHash,
    DateTimeOffset OccurredAtUtc);

internal sealed record ReservationDetailsHistoryProviderTenantExport(
    Guid? AdapterConnectionId,
    Guid? ExternalOperationId);

internal sealed record ReservationDetailsHistoryStaffTenantExport(
    string? ActorId);

internal sealed record ReservationExternalOperationTenantExport(
    [property: ReservationsTenantExportField("reservations.scope-id")]
    string ScopeId,
    [property: ReservationsTenantExportField("reservations.property-id")]
    Guid PropertyId,
    [property: ReservationsTenantExportField("reservations.reservation-id")]
    Guid? ReservationId,
    [property: ReservationsTenantExportField("reservations.record-id")]
    Guid OperationId,
    [property: ReservationsTenantExportField(
        "reservations.provider-provenance")]
    ReservationExternalOperationStateTenantExport ProviderProvenance);

internal sealed record ReservationExternalOperationStateTenantExport(
    Guid ReceiptId,
    Guid ConnectionId,
    ExternalReservationOperationKind Kind,
    string RequestFingerprint,
    ExternalReservationOperationOutcome Outcome,
    long? DetailsRevision,
    long? ReservationVersion,
    string? ErrorCode,
    DateTimeOffset CompletedAtUtc);

internal sealed record ReservationManagementOperationTenantExport(
    [property: ReservationsTenantExportField("reservations.scope-id")]
    string ScopeId,
    [property: ReservationsTenantExportField("reservations.property-id")]
    Guid PropertyId,
    [property: ReservationsTenantExportField("reservations.reservation-id")]
    Guid ReservationId,
    [property: ReservationsTenantExportField("reservations.record-id")]
    Guid OperationId,
    [property: ReservationsTenantExportField(
        "reservations.management-operation")]
    ReservationManagementOperationStateTenantExport ManagementOperation,
    [property: ReservationsTenantExportField("reservations.staff-attribution")]
    ReservationManagementStaffTenantExport? StaffAttribution = null);

internal sealed record ReservationManagementStaffTenantExport(Guid StationId, Guid BrowserSessionId, Guid StaffMemberId,
    Guid ActorSessionId, long Generation,
    BunkFy.Modules.Reservations.Contracts.Stations.StationReservationAuthority Authority, long ResultingVersion, long? JobGrantRevision);

internal sealed record ReservationManagementOperationStateTenantExport(
    ReservationManagementOperationKind Kind,
    long? ExpectedVersion,
    long? ExpectedDetailsRevision,
    DateOnly? BusinessDate,
    string? RequestFingerprint,
    DateTimeOffset CreatedAtUtc);

internal sealed record ReservationStayAmendmentOperationTenantExport(
    [property: ReservationsTenantExportField("reservations.scope-id")]
    string ScopeId,
    [property: ReservationsTenantExportField("reservations.property-id")]
    Guid PropertyId,
    [property: ReservationsTenantExportField("reservations.reservation-id")]
    Guid ReservationId,
    [property: ReservationsTenantExportField("reservations.record-id")]
    Guid OperationId,
    [property: ReservationsTenantExportField(
        "reservations.stay-amendment-operation")]
    ReservationStayAmendmentOperationStateTenantExport StayAmendmentOperation,
    [property: ReservationsTenantExportField(
        "reservations.staff-attribution")]
    ReservationStayAmendmentOperationStaffTenantExport StaffAttribution);

internal sealed record ReservationStayAmendmentOperationStateTenantExport(
    Guid? InventoryRequestId,
    int RequestSchemaVersion,
    string RequestFingerprint,
    DateOnly? TargetArrival,
    DateOnly? TargetDeparture,
    TimeOnly? TargetExpectedArrivalTime,
    TimeOnly? TargetExpectedDepartureTime,
    IReadOnlyCollection<Guid>? TargetInventoryUnitIds,
    long ExpectedDetailsRevision,
    ReservationStayAmendmentOperationOutcome Outcome,
    long OperationVersion,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    long? ResultingDetailsRevision,
    long? ResultingReservationVersion,
    long? ResultingAllocationVersion,
    int? RejectionCode,
    int ReconciliationCount,
    DateTimeOffset? LastReconciledAtUtc);

internal sealed record ReservationStayAmendmentOperationStaffTenantExport(
    string? RequestedBy,
    string? LastReconciledBy);

internal sealed record ReservationArrivalReminderTenantExport(
    [property: ReservationsTenantExportField("reservations.scope-id")]
    string ScopeId,
    [property: ReservationsTenantExportField("reservations.property-id")]
    Guid PropertyId,
    [property: ReservationsTenantExportField("reservations.reservation-id")]
    Guid ReservationId,
    [property: ReservationsTenantExportField("reservations.record-id")]
    Guid ReminderId,
    [property: ReservationsTenantExportField("reservations.reminder-state")]
    ReservationArrivalReminderStateTenantExport ReminderState);

internal sealed record ReservationArrivalReminderStateTenantExport(
    long DetailsRevision,
    string TimeZoneId,
    DateOnly Arrival,
    TimeOnly ExpectedArrivalTime,
    DateTimeOffset ExpectedArrivalAtUtc,
    DateTimeOffset DueAtUtc,
    int LeadTimeMinutes,
    ReservationArrivalReminderState State,
    DateTimeOffset? DispatchedAtUtc,
    long Version);

internal sealed record ReservationDataRightsCorrectionReceiptTenantExport(
    [property: ReservationsTenantExportField("reservations.scope-id")]
    string ScopeId,
    [property: ReservationsTenantExportField("reservations.property-id")]
    Guid PropertyId,
    [property: ReservationsTenantExportField("reservations.reservation-id")]
    Guid ReservationId,
    [property: ReservationsTenantExportField("reservations.record-id")]
    Guid ReceiptId,
    [property: ReservationsTenantExportField("reservations.data-rights-proof")]
    ReservationDataRightsCorrectionProofTenantExport DataRightsProof);

internal sealed record ReservationDataRightsCorrectionProofTenantExport(
    int ContractVersion,
    Guid IdempotencyKey,
    Guid CaseId,
    long ApprovalRevision,
    long SelectedRecordVersion,
    long CurrentRecordVersion,
    long SelectedDetailsRevision,
    long CurrentDetailsRevision,
    int ChangedFieldsMask,
    Guid DetailsChangeEventId,
    Guid CorrelationId,
    Guid EventId,
    Guid CompletionEventId,
    DateTimeOffset CompletedAtUtc);

internal sealed record ReservationProcessingRestrictionTenantExport(
    [property: ReservationsTenantExportField("reservations.scope-id")]
    string ScopeId,
    [property: ReservationsTenantExportField("reservations.property-id")]
    Guid PropertyId,
    [property: ReservationsTenantExportField("reservations.reservation-id")]
    Guid ReservationId,
    [property: ReservationsTenantExportField("reservations.record-id")]
    Guid RestrictionId,
    [property: ReservationsTenantExportField(
        "reservations.processing-restriction")]
    ReservationProcessingRestrictionStateTenantExport ProcessingRestriction,
    [property: ReservationsTenantExportField(
        "reservations.staff-attribution")]
    ReservationProcessingRestrictionStaffTenantExport StaffAttribution);

internal sealed record ReservationProcessingRestrictionStateTenantExport(
    Guid ApplyCaseId,
    long ApplyApprovalRevision,
    long ApplySelectedReservationVersion,
    ReservationProcessingRestrictionStatus Status,
    long Version,
    DateTimeOffset AppliedAtUtc,
    Guid? ReleaseCaseId,
    long? ReleaseApprovalRevision,
    long? ReleaseSelectedReservationVersion,
    DateTimeOffset? ReleasedAtUtc);

internal sealed record ReservationProcessingRestrictionStaffTenantExport(
    string AppliedBy,
    string? ReleasedBy);

internal sealed record
    ReservationProcessingRestrictionReceiptTenantExport(
        [property: ReservationsTenantExportField("reservations.scope-id")]
        string ScopeId,
        [property: ReservationsTenantExportField("reservations.property-id")]
        Guid PropertyId,
        [property: ReservationsTenantExportField(
            "reservations.reservation-id")]
        Guid ReservationId,
        [property: ReservationsTenantExportField("reservations.record-id")]
        Guid ReceiptId,
        [property: ReservationsTenantExportField(
            "reservations.processing-restriction")]
        ReservationProcessingRestrictionReceiptStateTenantExport
            ProcessingRestriction);

internal sealed record
    ReservationProcessingRestrictionReceiptStateTenantExport(
        Guid IdempotencyKey,
        Guid RestrictionId,
        ReservationProcessingRestrictionAction Action,
        Guid CaseId,
        long ApprovalRevision,
        long SelectedReservationVersion,
        int ContractVersion,
        long ResultingRestrictionVersion,
        long ResultingProjectionRevision,
        bool EffectiveRestricted,
        Guid EventId,
        DateTimeOffset CompletedAtUtc);

internal sealed record ReservationDataHoldTenantExport(
    [property: ReservationsTenantExportField("reservations.scope-id")]
    string ScopeId,
    [property: ReservationsTenantExportField("reservations.property-id")]
    Guid PropertyId,
    [property: ReservationsTenantExportField("reservations.reservation-id")]
    Guid ReservationId,
    [property: ReservationsTenantExportField("reservations.record-id")]
    Guid HoldId,
    [property: ReservationsTenantExportField("reservations.data-hold")]
    ReservationDataHoldStateTenantExport DataHold,
    [property: ReservationsTenantExportField(
        "reservations.staff-attribution")]
    ReservationDataHoldStaffTenantExport StaffAttribution);

internal sealed record ReservationDataHoldStateTenantExport(
    string ReasonCode,
    ReservationDataHoldState State,
    DateTimeOffset PlacedAtUtc,
    DateTimeOffset? ReleasedAtUtc,
    long Version);

internal sealed record ReservationDataHoldStaffTenantExport(
    string PlacedBy,
    string? ReleasedBy);

internal sealed record ReservationDataHoldReceiptTenantExport(
    [property: ReservationsTenantExportField("reservations.scope-id")]
    string ScopeId,
    [property: ReservationsTenantExportField("reservations.property-id")]
    Guid PropertyId,
    [property: ReservationsTenantExportField("reservations.reservation-id")]
    Guid ReservationId,
    [property: ReservationsTenantExportField("reservations.record-id")]
    Guid ReceiptId,
    [property: ReservationsTenantExportField("reservations.data-hold")]
    ReservationDataHoldReceiptStateTenantExport DataHold);

internal sealed record ReservationDataHoldReceiptStateTenantExport(
    Guid IdempotencyKey,
    Guid HoldId,
    DomainDataHoldAction Action,
    string ReasonCode,
    long SelectedReservationVersion,
    long SelectedDetailsRevision,
    long ResultingHoldVersion,
    DateTimeOffset CompletedAtUtc);

internal sealed record ReservationAnonymisationReceiptTenantExport(
    [property: ReservationsTenantExportField("reservations.scope-id")]
    string ScopeId,
    [property: ReservationsTenantExportField("reservations.property-id")]
    Guid PropertyId,
    [property: ReservationsTenantExportField("reservations.reservation-id")]
    Guid ReservationId,
    [property: ReservationsTenantExportField("reservations.record-id")]
    Guid ReceiptId,
    [property: ReservationsTenantExportField(
        "reservations.anonymisation-proof")]
    ReservationAnonymisationReceiptProofTenantExport AnonymisationProof,
    [property: ReservationsTenantExportField(
        "reservations.staff-attribution")]
    ReservationAnonymisationStaffTenantExport StaffAttribution);

internal sealed record ReservationAnonymisationReceiptProofTenantExport(
    int ContractVersion,
    Guid IdempotencyKey,
    Guid CaseId,
    long ApprovalRevision,
    long OperationRevision,
    long SelectedReservationVersion,
    long ResultingReservationVersion,
    long SelectedDetailsRevision,
    long ResultingDetailsRevision,
    DomainAnonymisationDisposition Disposition,
    DomainAnonymisationReason Reason,
    int RedactedHistoryCount,
    int RemovedGuestLinkCount,
    int ReducedExternalOperationCount,
    int SuppressedReminderCount,
    string ApprovalEvidenceSha256,
    string PolicyEvidenceSha256,
    Guid EventId,
    DateTimeOffset CompletedAtUtc,
    string CanonicalSha256);

internal sealed record ReservationAnonymisationStaffTenantExport(
    string ActorId);

internal sealed record ReservationAnonymisationTombstoneTenantExport(
    [property: ReservationsTenantExportField("reservations.scope-id")]
    string ScopeId,
    [property: ReservationsTenantExportField("reservations.property-id")]
    Guid PropertyId,
    [property: ReservationsTenantExportField("reservations.reservation-id")]
    Guid ReservationId,
    [property: ReservationsTenantExportField(
        "reservations.anonymisation-proof")]
    ReservationAnonymisationTombstoneProofTenantExport AnonymisationProof);

internal sealed record ReservationAnonymisationTombstoneProofTenantExport(
    int ContractVersion,
    long Revision,
    ReservationAnonymisationAuthority Authority,
    int OwnerReceiptContractVersion,
    Guid OwnerReceiptId,
    string OwnerReceiptSha256,
    long ResultingReservationVersion,
    long? ResultingDetailsRevision,
    DateTimeOffset CompletedAtUtc,
    Guid? LedgerEntryId,
    DateTimeOffset? LastReplayedAtUtc);

internal sealed record ReservationAnonymisationRestoreReceiptTenantExport(
    [property: ReservationsTenantExportField("reservations.scope-id")]
    string ScopeId,
    [property: ReservationsTenantExportField("reservations.property-id")]
    Guid PropertyId,
    [property: ReservationsTenantExportField("reservations.reservation-id")]
    Guid ReservationId,
    [property: ReservationsTenantExportField("reservations.record-id")]
    Guid ReceiptId,
    [property: ReservationsTenantExportField(
        "reservations.anonymisation-proof")]
    ReservationAnonymisationRestoreProofTenantExport AnonymisationProof);

internal sealed record ReservationAnonymisationRestoreProofTenantExport(
    int ContractVersion,
    Guid LedgerEntryId,
    long TenantSequence,
    string LedgerEntrySha256,
    int OwnerReceiptContractVersion,
    Guid OwnerReceiptId,
    string OwnerReceiptSha256,
    long ResultingReservationVersion,
    long? ResultingDetailsRevision,
    DateTimeOffset OriginallyCompletedAtUtc,
    long TombstoneRevision,
    DateTimeOffset ReplayedAtUtc,
    string CanonicalSha256);

internal sealed record ReservationRetentionExecutionTenantExport(
    [property: ReservationsTenantExportField("reservations.scope-id")]
    string ScopeId,
    [property: ReservationsTenantExportField("reservations.record-id")]
    Guid ExecutionId,
    [property: ReservationsTenantExportField(
        "reservations.retention-execution")]
    ReservationRetentionExecutionStateTenantExport RetentionExecution);

internal sealed record ReservationRetentionExecutionStateTenantExport(
    string DataClassKey,
    int ExecutionPolicyVersion,
    int Attempt,
    long StartingProjectionOrdinal,
    ReservationRetentionExecutionState State,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset DeadlineUtc,
    DateTimeOffset? CompletedAtUtc,
    int AffectedCount,
    int? ScannedCount,
    int? RemainingCount,
    string? OutcomeCode,
    DateTimeOffset? HoldReviewDueAtUtc,
    long Version);

internal sealed record
    ReservationRetentionAnonymisationReceiptTenantExport(
        [property: ReservationsTenantExportField("reservations.scope-id")]
        string ScopeId,
        [property: ReservationsTenantExportField("reservations.property-id")]
        Guid PropertyId,
        [property: ReservationsTenantExportField(
            "reservations.reservation-id")]
        Guid ReservationId,
        [property: ReservationsTenantExportField("reservations.record-id")]
        Guid ReceiptId,
        [property: ReservationsTenantExportField(
            "reservations.retention-proof")]
        ReservationRetentionAnonymisationProofTenantExport RetentionProof,
        [property: ReservationsTenantExportField(
            "reservations.staff-attribution")]
        ReservationRetentionStaffTenantExport StaffAttribution);

internal sealed record ReservationRetentionAnonymisationProofTenantExport(
    int ContractVersion,
    Guid ExecutionId,
    long SelectedReservationVersion,
    long ResultingReservationVersion,
    long SelectedDetailsRevision,
    long ResultingDetailsRevision,
    DateTimeOffset TerminalAtUtc,
    DateTimeOffset RetentionDeadlineUtc,
    string PolicyEvidenceSha256,
    int RedactedHistoryCount,
    int RemovedGuestLinkCount,
    int ReducedExternalOperationCount,
    int SuppressedReminderCount,
    Guid EventId,
    DateTimeOffset CompletedAtUtc,
    string CanonicalSha256);

internal sealed record ReservationRetentionStaffTenantExport(
    string ActorId);
