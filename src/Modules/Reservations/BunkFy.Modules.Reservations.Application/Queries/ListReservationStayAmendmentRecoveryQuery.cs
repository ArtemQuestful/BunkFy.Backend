namespace BunkFy.Modules.Reservations.Application.Queries;

using BunkFy.Modules.Reservations.Contracts;
using Gma.Framework.Cqrs;

public sealed record ListReservationStayAmendmentRecoveryQuery(
    Guid PropertyId,
    ReservationStayAmendmentRecoveryCursorDto? Cursor,
    int PageSize)
    : IQuery<ReservationStayAmendmentRecoveryPageDto>;
