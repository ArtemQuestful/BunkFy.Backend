namespace BunkFy.Modules.Reservations.Persistence.Repositories;

using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Domain.Aggregates;

internal static class ReservationVisibilityQueries
{
    internal static IQueryable<Reservation> Ordinary(
        ReservationsDbContext dbContext) =>
        dbContext.Reservations.Where(reservation =>
            !reservation.IsAnonymised &&
            dbContext.ProcessingRestrictionProjections.Any(projection =>
                projection.PropertyId == reservation.PropertyId &&
                projection.ReservationId == reservation.Id &&
                projection.ContractVersion ==
                    ReservationProcessingRestrictionContract.CurrentVersion &&
                !projection.IsRestricted));
}
