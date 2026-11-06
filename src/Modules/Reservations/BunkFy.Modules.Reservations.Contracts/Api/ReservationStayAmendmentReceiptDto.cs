namespace BunkFy.Modules.Reservations.Contracts;

using BunkFy.Modules.Inventory.Contracts;

public sealed record ReservationStayAmendmentReceiptDto(
    Guid OperationId,
    Guid PropertyId,
    Guid ReservationId,
    ReservationStayAmendmentTargetDto? Target,
    ReservationStayAmendmentOutcome Outcome,
    long ExpectedDetailsRevision,
    long OperationVersion,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    long? ResultingDetailsRevision,
    long? ResultingReservationVersion,
    InventoryAllocationRejectionReason? RejectionReason,
    int ReconciliationCount,
    DateTimeOffset? LastReconciledAtUtc,
    bool RecoveryEligible,
    DateTimeOffset? NextRecoveryEligibleAtUtc);
