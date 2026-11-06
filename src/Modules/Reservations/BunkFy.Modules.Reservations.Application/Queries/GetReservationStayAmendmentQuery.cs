namespace BunkFy.Modules.Reservations.Application.Queries;

using BunkFy.Modules.Reservations.Contracts;
using Gma.Framework.Cqrs;

public sealed record GetReservationStayAmendmentQuery(
    Guid PropertyId,
    Guid ReservationId,
    Guid OperationId)
    : IQuery<ReservationStayAmendmentReceiptDto>;
