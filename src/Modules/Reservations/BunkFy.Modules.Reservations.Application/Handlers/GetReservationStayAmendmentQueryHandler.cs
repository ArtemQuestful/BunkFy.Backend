namespace BunkFy.Modules.Reservations.Application.Handlers;

using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Application.Queries;
using BunkFy.Modules.Reservations.Application.StayAmendments;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Domain.StayAmendments;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;

internal sealed class GetReservationStayAmendmentQueryHandler(
    IReservationStayAmendmentOperationRepository operations,
    ISystemClock clock)
    : IQueryHandler<GetReservationStayAmendmentQuery, ReservationStayAmendmentReceiptDto>
{
    public async Task<Result<ReservationStayAmendmentReceiptDto>> HandleAsync(
        GetReservationStayAmendmentQuery query,
        CancellationToken cancellationToken)
    {
        ReservationStayAmendmentOperation? operation = await operations.GetVisibleAsync(
            query.PropertyId,
            query.ReservationId,
            query.OperationId,
            cancellationToken).ConfigureAwait(false);
        return operation is null
            ? Result.Failure<ReservationStayAmendmentReceiptDto>(
                ReservationsApplicationErrors.StayAmendmentOperationNotFound)
            : Result.Success(operation.ToReceipt(clock.UtcNow));
    }
}
