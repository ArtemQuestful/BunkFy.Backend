namespace BunkFy.Modules.Reservations.Application.StayAmendments;

using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Domain.StayAmendments;

internal static class ReservationStayAmendmentMappings
{
    public static ReservationStayAmendmentReceiptDto ToReceipt(
        this ReservationStayAmendmentOperation operation,
        DateTimeOffset nowUtc) => new(
            operation.Id,
            operation.PropertyId,
            operation.ReservationId,
            ToTarget(operation),
            ToContractOutcome(operation.Outcome),
            operation.ExpectedDetailsRevision,
            operation.OperationVersion,
            operation.RequestedAtUtc,
            operation.UpdatedAtUtc,
            operation.CompletedAtUtc,
            operation.ResultingDetailsRevision,
            operation.ResultingReservationVersion,
            ToRejectionReason(operation.RejectionCode),
            operation.ReconciliationCount,
            operation.LastReconciledAtUtc,
            operation.IsReconciliationEligible(nowUtc),
            operation.NextReconciliationEligibleAtUtc);

    public static ReservationStayAmendmentRecoveryItemDto ToRecoveryItem(
        this ReservationStayAmendmentOperation operation,
        DateTimeOffset nowUtc) => new(
            operation.Id,
            operation.PropertyId,
            operation.ReservationId,
            ToContractOutcome(operation.Outcome),
            operation.OperationVersion,
            operation.RequestedAtUtc,
            operation.UpdatedAtUtc,
            operation.IsReconciliationEligible(nowUtc),
            operation.NextReconciliationEligibleAtUtc);

    public static ReservationStayAmendmentRecoveryCursorDto ToDto(
        this ReservationStayAmendmentRecoveryCursorRecord cursor) => new(
            ToContractOutcome(cursor.Outcome),
            cursor.UpdatedAtUtc,
            cursor.OperationId,
            cursor.ReservationId);

    public static ReservationStayAmendmentRecoveryCursorRecord ToRecord(
        this ReservationStayAmendmentRecoveryCursorDto cursor) => new(
            ToDomainOutcome(cursor.Outcome),
            cursor.UpdatedAtUtc,
            cursor.OperationId,
            cursor.ReservationId);

    private static ReservationStayAmendmentTargetDto? ToTarget(
        ReservationStayAmendmentOperation operation) =>
        operation.TargetArrival.HasValue && operation.TargetDeparture.HasValue &&
        operation.TargetInventoryUnitIds is not null
            ? new ReservationStayAmendmentTargetDto(
                operation.TargetArrival.Value,
                operation.TargetDeparture.Value,
                operation.TargetExpectedArrivalTime,
                operation.TargetExpectedDepartureTime,
                operation.GetTargetInventoryUnitIds())
            : null;

    private static InventoryAllocationRejectionReason? ToRejectionReason(int? code) =>
        code.HasValue && Enum.IsDefined((InventoryAllocationRejectionReason)code.Value) &&
        (InventoryAllocationRejectionReason)code.Value != InventoryAllocationRejectionReason.Unknown
            ? (InventoryAllocationRejectionReason)code.Value
            : null;

    private static ReservationStayAmendmentOutcome ToContractOutcome(
        ReservationStayAmendmentOperationOutcome outcome) => outcome switch
        {
            ReservationStayAmendmentOperationOutcome.Pending => ReservationStayAmendmentOutcome.Pending,
            ReservationStayAmendmentOperationOutcome.Applied => ReservationStayAmendmentOutcome.Applied,
            ReservationStayAmendmentOperationOutcome.Rejected => ReservationStayAmendmentOutcome.Rejected,
            ReservationStayAmendmentOperationOutcome.OutcomeUnknown => ReservationStayAmendmentOutcome.OutcomeUnknown,
            _ => ReservationStayAmendmentOutcome.Unknown
        };

    private static ReservationStayAmendmentOperationOutcome ToDomainOutcome(
        ReservationStayAmendmentOutcome outcome) => outcome switch
        {
            ReservationStayAmendmentOutcome.Pending => ReservationStayAmendmentOperationOutcome.Pending,
            ReservationStayAmendmentOutcome.OutcomeUnknown => ReservationStayAmendmentOperationOutcome.OutcomeUnknown,
            ReservationStayAmendmentOutcome.Applied => ReservationStayAmendmentOperationOutcome.Applied,
            ReservationStayAmendmentOutcome.Rejected => ReservationStayAmendmentOperationOutcome.Rejected,
            _ => ReservationStayAmendmentOperationOutcome.Unknown
        };
}
