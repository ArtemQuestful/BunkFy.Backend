namespace BunkFy.Modules.Reservations.Application.Handlers;

using Gma.Framework.Cqrs;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Application.Queries;
using BunkFy.Modules.Reservations.Contracts;

internal sealed class ListReservationsQueryHandler(IReservationRepository reservations)
    : IQueryHandler<ListReservationsQuery, ReservationListResponse>
{
    public async Task<Result<ReservationListResponse>> HandleAsync(
        ListReservationsQuery query,
        CancellationToken cancellationToken) =>
        Result.Success(await reservations.ListWindowAsync(
            query.PropertyId,
            query.Statuses,
            query.Search,
            query.Order,
            PageRequest.Normalize(query.Page, query.PageSize),
            query.OverlapsFrom,
            query.OverlapsTo,
            query.View,
            query.OperatingDate,
            cancellationToken).ConfigureAwait(false));
}
