namespace BunkFy.Modules.Reservations.Persistence.Repositories;

using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Domain.Models;
using BunkFy.Modules.Reservations.Domain.StayAmendments;
using DomainDataHoldAction =
    BunkFy.Modules.Reservations.Domain.Models.ReservationDataHoldAction;
using DomainAnonymisationDisposition =
    BunkFy.Modules.Reservations.Domain.DataRights.ReservationAnonymisationDisposition;
using DomainAnonymisationReason =
    BunkFy.Modules.Reservations.Domain.DataRights.ReservationAnonymisationReason;

internal sealed record ReservationDataRightsExport(
    Guid ReservationId,
    Guid PropertyId,
    Guid AllocationRequestId,
    Guid? AllocationId,
    long? AllocationVersion,
    ReservationAllocationRejection? AllocationRejection,
    Guid? ReleaseRequestId,
    int? LastReleaseRejectionCode,
    int? LastAllocationAmendmentRejectionCode,
    Guid? PendingInventoryAmendmentRequestId,
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
    IReadOnlyCollection<Guid> RequestedInventoryUnitIds,
    string PrimaryGuestName,
    string? Email,
    string? Phone,
    int GuestCount,
    ReservationSource Source,
    string? SourceSystem,
    string? SourceReference,
    string? Notes,
    long DetailsRevision,
    ReservationDetailsChangeOrigin LastDetailsChangeOrigin,
    Guid? LastDetailsAdapterConnectionId,
    Guid? LastDetailsExternalOperationId,
    DateTimeOffset LastDetailsChangedAtUtc,
    ReservationState Status,
    long Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? UpdatedAtUtc,
    bool IsAnonymised,
    DateTimeOffset? AnonymisedAtUtc);

internal sealed record ReservationPendingAmendmentDataRightsExport(
    Guid AmendmentRequestId,
    Guid ReservationId,
    string RequestFingerprint,
    DateOnly Arrival,
    DateOnly Departure,
    TimeOnly? ExpectedArrivalTime,
    TimeOnly? ExpectedDepartureTime,
    IReadOnlyCollection<Guid> RequestedInventoryUnitIds,
    string PrimaryGuestName,
    string? Email,
    string? Phone,
    int GuestCount,
    string? Notes,
    ReservationDetailsChangeOrigin ChangeOrigin,
    Guid? AdapterConnectionId,
    Guid? ExternalOperationId,
    Guid CorrelationId,
    long ReservationVersion);

internal sealed record ReservationGuestLinkDataRightsExport(
    Guid GuestId,
    Guid ReservationId,
    Guid PropertyId,
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

internal sealed record ReservationDetailsHistoryDataRightsExport(
    Guid ChangeId,
    Guid ReservationId,
    Guid PropertyId,
    long FromRevision,
    long ToRevision,
    ReservationDetailsChangeOrigin Origin,
    Guid? AdapterConnectionId,
    Guid? ExternalOperationId,
    string OperationDeduplicationKey,
    Guid CorrelationId,
    string ChangedFieldsJson,
    string? BeforeSnapshotJson,
    string AfterSnapshotJson,
    string AfterSnapshotHash,
    DateTimeOffset OccurredAtUtc);

internal sealed record ReservationExternalOperationDataRightsExport(
    Guid OperationId,
    Guid ReceiptId,
    Guid ConnectionId,
    Guid PropertyId,
    ExternalReservationOperationKind Kind,
    string RequestFingerprint,
    ExternalReservationOperationOutcome Outcome,
    Guid ReservationId,
    long? DetailsRevision,
    long? ReservationVersion,
    string? ErrorCode,
    DateTimeOffset CompletedAtUtc);

internal sealed record ReservationManagementOperationDataRightsExport(
    Guid OperationId,
    Guid PropertyId,
    Guid ReservationId,
    ReservationManagementOperationKind Kind,
    long? ExpectedVersion,
    long? ExpectedDetailsRevision,
    DateOnly? BusinessDate,
    string? RequestFingerprint,
    DateTimeOffset CreatedAtUtc);

internal sealed record ReservationStayAmendmentOperationDataRightsExport(
    Guid OperationId,
    Guid PropertyId,
    Guid ReservationId,
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

internal sealed record ReservationArrivalReminderDataRightsExport(
    Guid ReminderId,
    Guid ReservationId,
    Guid PropertyId,
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

internal sealed record ReservationDataRightsCorrectionReceiptDataRightsExport(
    int ContractVersion,
    Guid ReceiptId,
    Guid PropertyId,
    Guid CaseId,
    long ApprovalRevision,
    Guid ReservationId,
    long PreviousVersion,
    long CurrentVersion,
    long PreviousDetailsRevision,
    long CurrentDetailsRevision,
    IReadOnlyCollection<ReservationDetailsField> ChangedFields,
    Guid DetailsChangeEventId,
    Guid EventId,
    DateTimeOffset CompletedAtUtc);

internal sealed record ReservationProcessingRestrictionDataRightsExport(
    Guid RestrictionId,
    Guid PropertyId,
    Guid ReservationId,
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

internal sealed record ReservationProcessingRestrictionStateDataRightsExport(
    Guid PropertyId,
    Guid ReservationId,
    int ContractVersion,
    long Revision,
    int ActiveRestrictionCount,
    bool IsRestricted,
    DateTimeOffset LastTransitionAtUtc);

internal sealed record ReservationProcessingRestrictionReceiptDataRightsExport(
    Guid ReceiptId,
    Guid RestrictionId,
    ReservationProcessingRestrictionAction Action,
    Guid PropertyId,
    Guid ReservationId,
    Guid CaseId,
    long ApprovalRevision,
    long SelectedReservationVersion,
    int ContractVersion,
    long ResultingRestrictionVersion,
    long ResultingProjectionRevision,
    bool EffectiveRestricted,
    Guid EventId,
    DateTimeOffset CompletedAtUtc);

internal sealed record ReservationDataHoldDataRightsExport(
    Guid HoldId,
    Guid PropertyId,
    Guid ReservationId,
    string ReasonCode,
    ReservationDataHoldState State,
    DateTimeOffset PlacedAtUtc,
    DateTimeOffset? ReleasedAtUtc,
    long Version);

internal sealed record ReservationDataHoldReceiptDataRightsExport(
    Guid ReceiptId,
    Guid HoldId,
    DomainDataHoldAction Action,
    Guid PropertyId,
    Guid ReservationId,
    string ReasonCode,
    long SelectedReservationVersion,
    long SelectedDetailsRevision,
    long ResultingHoldVersion,
    DateTimeOffset CompletedAtUtc);

internal sealed record ReservationAnonymisationReceiptDataRightsExport(
    int ContractVersion,
    Guid ReceiptId,
    Guid PropertyId,
    Guid CaseId,
    long ApprovalRevision,
    long OperationRevision,
    Guid ReservationId,
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
