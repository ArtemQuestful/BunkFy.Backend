namespace BunkFy.Modules.Reservations.Application.Queries;

using BunkFy.Modules.Reservations.Contracts;
using Gma.Framework.Cqrs;

public sealed record GetReservationOperationsSnapshotQuery(
    Guid PropertyId,
    DateOnly? LocalDate,
    int UpcomingLimit)
    : IQuery<ReservationOperationsSnapshotDto>;
