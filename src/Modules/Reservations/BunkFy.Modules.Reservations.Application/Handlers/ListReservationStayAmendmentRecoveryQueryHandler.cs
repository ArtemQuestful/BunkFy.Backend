namespace BunkFy.Modules.Reservations.Application.Handlers;

using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Application.Queries;
using BunkFy.Modules.Reservations.Application.StayAmendments;
using BunkFy.Modules.Reservations.Contracts;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;

internal sealed class ListReservationStayAmendmentRecoveryQueryHandler(
    IReservationStayAmendmentOperationRepository operations,
    ISystemClock clock)
    : IQueryHandler<
        ListReservationStayAmendmentRecoveryQuery,
        ReservationStayAmendmentRecoveryPageDto>
{
    public async Task<Result<ReservationStayAmendmentRecoveryPageDto>> HandleAsync(
        ListReservationStayAmendmentRecoveryQuery query,
        CancellationToken cancellationToken)
    {
        ReservationStayAmendmentRecoveryPageRecord page = await operations.ListRecoveryAsync(
            query.PropertyId,
            query.Cursor?.ToRecord(),
            query.PageSize,
            cancellationToken).ConfigureAwait(false);
        DateTimeOffset nowUtc = clock.UtcNow;
        return Result.Success(new ReservationStayAmendmentRecoveryPageDto(
            page.Operations.Select(operation => operation.ToRecoveryItem(nowUtc)).ToArray(),
            page.NextCursor?.ToDto()));
    }
}
