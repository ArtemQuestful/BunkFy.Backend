namespace BunkFy.Modules.Reservations.Contracts;

public sealed record ReservationStayAmendmentRecoveryItemDto(
    Guid OperationId,
    Guid PropertyId,
    Guid ReservationId,
    ReservationStayAmendmentOutcome Outcome,
    long OperationVersion,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    bool RecoveryEligible,
    DateTimeOffset? NextRecoveryEligibleAtUtc);
