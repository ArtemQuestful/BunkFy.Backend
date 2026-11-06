namespace BunkFy.Modules.Reservations.Application.Ports;

using BunkFy.Modules.Reservations.Domain.StayAmendments;

public sealed record ReservationStayAmendmentRecoveryCursorRecord(
    ReservationStayAmendmentOperationOutcome Outcome,
    DateTimeOffset UpdatedAtUtc,
    Guid OperationId,
    Guid ReservationId);
