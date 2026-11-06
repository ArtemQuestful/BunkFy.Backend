namespace BunkFy.Modules.Reservations.Contracts;

public sealed record ReservationStayAmendmentRecoveryPageDto(
    IReadOnlyCollection<ReservationStayAmendmentRecoveryItemDto> Operations,
    ReservationStayAmendmentRecoveryCursorDto? NextCursor);
