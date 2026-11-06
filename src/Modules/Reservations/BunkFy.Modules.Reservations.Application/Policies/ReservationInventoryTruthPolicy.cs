namespace BunkFy.Modules.Reservations.Application.Policies;

using BunkFy.Modules.Reservations.Domain.Aggregates;

internal static class ReservationInventoryTruthPolicy
{
    public static bool HoldsInventory(
        ReservationState status,
        Guid? allocationId) =>
        allocationId.HasValue &&
        status is ReservationState.Confirmed or
            ReservationState.CancellationPending or
            ReservationState.CheckedIn or
            ReservationState.NoShowPending or
            ReservationState.CheckoutPending;

    public static bool MatchesRequestedUnits(
        Reservation reservation,
        IReadOnlyCollection<Guid> confirmedInventoryUnitIds) =>
        reservation.RequestedUnits.Count == confirmedInventoryUnitIds.Count &&
        reservation.RequestedUnits
            .Select(unit => unit.InventoryUnitId)
            .Order()
            .SequenceEqual(confirmedInventoryUnitIds.Order());
}
