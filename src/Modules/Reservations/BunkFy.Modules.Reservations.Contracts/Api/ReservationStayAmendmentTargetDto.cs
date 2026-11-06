namespace BunkFy.Modules.Reservations.Contracts;

public sealed record ReservationStayAmendmentTargetDto(
    DateOnly Arrival,
    DateOnly Departure,
    TimeOnly? ExpectedArrivalTime,
    TimeOnly? ExpectedDepartureTime,
    IReadOnlyCollection<Guid> InventoryUnitIds);
