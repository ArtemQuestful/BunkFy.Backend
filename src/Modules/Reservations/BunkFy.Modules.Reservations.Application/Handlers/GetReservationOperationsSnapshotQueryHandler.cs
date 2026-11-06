namespace BunkFy.Modules.Reservations.Application.Handlers;

using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Application.Queries;
using BunkFy.Modules.Reservations.Contracts;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;

internal sealed class GetReservationOperationsSnapshotQueryHandler(
    IReservationOperationsSnapshotReader reader,
    ISystemClock clock)
    : IQueryHandler<
        GetReservationOperationsSnapshotQuery,
        ReservationOperationsSnapshotDto>
{
    public async Task<Result<ReservationOperationsSnapshotDto>> HandleAsync(
        GetReservationOperationsSnapshotQuery query,
        CancellationToken cancellationToken)
    {
        if (query.UpcomingLimit is < 0 or > ReservationsContractLimits.MaximumOperationsSnapshotUpcomingLimit)
        {
            return Result.Failure<ReservationOperationsSnapshotDto>(
                ReservationsApplicationErrors.OperationsSnapshotLimitInvalid);
        }

        ReservationOperationsSnapshotReadResult read = await reader.ReadAsync(
            query.PropertyId,
            query.LocalDate,
            clock.UtcNow,
            query.UpcomingLimit,
            cancellationToken).ConfigureAwait(false);

        return read.Status switch
        {
            ReservationOperationsSnapshotReadStatus.Found when read.Snapshot is not null =>
                Result.Success(read.Snapshot),
            ReservationOperationsSnapshotReadStatus.PropertyNotFound =>
                Result.Failure<ReservationOperationsSnapshotDto>(
                    ReservationsApplicationErrors.PropertyNotFound),
            ReservationOperationsSnapshotReadStatus.PropertyInactive =>
                Result.Failure<ReservationOperationsSnapshotDto>(
                    ReservationsApplicationErrors.PropertyInactive),
            ReservationOperationsSnapshotReadStatus.PropertyTimeZoneUnavailable =>
                Result.Failure<ReservationOperationsSnapshotDto>(
                    ReservationsApplicationErrors.PropertyTimeZoneUnavailable),
            _ => throw new InvalidOperationException(
                "The Reservations operations snapshot reader returned an invalid result.")
        };
    }
}
