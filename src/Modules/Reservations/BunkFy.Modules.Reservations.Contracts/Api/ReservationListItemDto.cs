namespace BunkFy.Modules.Reservations.Contracts;

using System.Text.Json.Serialization;

public sealed record ReservationListItemDto(
    [property: JsonRequired] Guid ReservationId,
    [property: JsonRequired] Guid PropertyId,
    [property: JsonRequired] DateOnly Arrival,
    [property: JsonRequired] DateOnly Departure,
    [property: JsonRequired] TimeOnly? ExpectedArrivalTime,
    [property: JsonRequired] TimeOnly? ExpectedDepartureTime,
    [property: JsonRequired] string PrimaryGuestName,
    [property: JsonRequired] int GuestCount,
    [property: JsonRequired] int InventoryUnitCount,
    [property: JsonRequired] IReadOnlyCollection<Guid> InventoryUnitIds,
    [property: JsonRequired] bool HoldsInventory,
    [property: JsonRequired] ReservationSourceKind SourceKind,
    [property: JsonRequired] ReservationStatus Status);
