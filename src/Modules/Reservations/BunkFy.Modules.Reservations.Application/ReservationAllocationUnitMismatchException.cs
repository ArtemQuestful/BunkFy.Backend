namespace BunkFy.Modules.Reservations.Application;

internal sealed class ReservationAllocationUnitMismatchException()
    : InvalidOperationException(ReservationsApplicationErrors.AllocationUnitMismatch.Code);
