namespace BunkFy.Modules.Reservations.Application.Handlers;

using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Reservations.Application.Policies;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Domain.Aggregates;

internal static class ReservationMappings
{
    public static ReservationMutationReceiptDto ToMutationReceipt(
        this Reservation reservation) => new(
        reservation.Id,
        reservation.PropertyId,
        MapStatus(reservation.Status),
        reservation.DetailsRevision,
        reservation.Version);

    public static ReservationDto ToDto(this Reservation reservation) => new(
        reservation.Id,
        reservation.PropertyId,
        reservation.Arrival,
        reservation.Departure,
        reservation.ExpectedArrivalTime,
        reservation.ExpectedDepartureTime,
        reservation.RequestedUnits.Select(unit => unit.InventoryUnitId).ToArray(),
        reservation.PrimaryGuestName,
        reservation.Email,
        reservation.Phone,
        reservation.GuestCount,
        reservation.Source == ReservationSource.Direct ? ReservationSourceKind.Direct : ReservationSourceKind.External,
        reservation.SourceSystem,
        reservation.SourceReference,
        reservation.Notes,
        reservation.Status switch
        {
            ReservationState.PendingAllocation => ReservationStatus.PendingAllocation,
            ReservationState.Confirmed => ReservationStatus.Confirmed,
            ReservationState.AllocationRejected => ReservationStatus.AllocationRejected,
            ReservationState.CancellationPending => ReservationStatus.CancellationPending,
            ReservationState.Cancelled => ReservationStatus.Cancelled,
            ReservationState.CheckedIn => ReservationStatus.CheckedIn,
            ReservationState.NoShowPending => ReservationStatus.NoShowPending,
            ReservationState.NoShow => ReservationStatus.NoShow,
            ReservationState.CheckoutPending => ReservationStatus.CheckoutPending,
            ReservationState.CheckedOut => ReservationStatus.CheckedOut,
            _ => ReservationStatus.Unknown
        },
        ReservationInventoryTruthPolicy.HoldsInventory(
            reservation.Status,
            reservation.AllocationId),
        reservation.AllocationRequestId,
        reservation.AllocationId,
        reservation.AllocationVersion,
        reservation.AllocationRejection.HasValue
            ? (InventoryAllocationRejectionReason)(int)reservation.AllocationRejection.Value
            : null,
        reservation.PendingAllocationAmendmentId,
        reservation.LastAllocationAmendmentRejectionCode.HasValue
            ? (InventoryAllocationRejectionReason)reservation.LastAllocationAmendmentRejectionCode.Value
            : null,
        reservation.DetailsRevision,
        (ReservationDetailsChangeOriginKind)(int)reservation.LastDetailsChangeOrigin,
        reservation.Version,
        reservation.CreatedAtUtc,
        reservation.UpdatedAtUtc,
        reservation.PendingStayBusinessDate,
        reservation.PendingStayActorId,
        reservation.CheckedInBusinessDate,
        reservation.CheckedInAtUtc,
        reservation.CheckedInBy,
        reservation.NoShowBusinessDate,
        reservation.NoShowAtUtc,
        reservation.NoShowBy,
        reservation.CheckedOutBusinessDate,
        reservation.CheckedOutAtUtc,
        reservation.CheckedOutBy,
        reservation.Guests.Where(guest => guest.IsCurrent).Select(guest => new ReservationGuestDto(
            guest.GuestId,
            (ReservationGuestRoleKind)(int)guest.Role)).ToArray());

    private static ReservationStatus MapStatus(ReservationState status) => status switch
    {
        ReservationState.PendingAllocation => ReservationStatus.PendingAllocation,
        ReservationState.Confirmed => ReservationStatus.Confirmed,
        ReservationState.AllocationRejected => ReservationStatus.AllocationRejected,
        ReservationState.CancellationPending => ReservationStatus.CancellationPending,
        ReservationState.Cancelled => ReservationStatus.Cancelled,
        ReservationState.CheckedIn => ReservationStatus.CheckedIn,
        ReservationState.NoShowPending => ReservationStatus.NoShowPending,
        ReservationState.NoShow => ReservationStatus.NoShow,
        ReservationState.CheckoutPending => ReservationStatus.CheckoutPending,
        ReservationState.CheckedOut => ReservationStatus.CheckedOut,
        _ => ReservationStatus.Unknown
    };
}
