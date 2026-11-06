namespace BunkFy.Modules.Reservations.Application.Ports;

using BunkFy.Modules.Reservations.Domain.StayAmendments;

public sealed record ReservationStayAmendmentRecoveryPageRecord(
    IReadOnlyCollection<ReservationStayAmendmentOperation> Operations,
    ReservationStayAmendmentRecoveryCursorRecord? NextCursor);
