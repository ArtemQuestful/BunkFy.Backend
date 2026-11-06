namespace BunkFy.Modules.Reservations.Contracts;

public sealed record ReservationStayAmendmentRecoveryCursorDto(
    ReservationStayAmendmentOutcome Outcome,
    DateTimeOffset UpdatedAtUtc,
    Guid OperationId,
    Guid ReservationId);
