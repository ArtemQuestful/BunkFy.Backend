namespace BunkFy.Modules.Reservations.Application.Commands;

using BunkFy.Modules.Reservations.Contracts;
using Gma.Framework.Cqrs;

public sealed record ReconcileReservationStayAmendmentCommand(
    Guid PropertyId,
    Guid ReservationId,
    Guid OperationId,
    long ExpectedOperationVersion,
    string ActorId)
    : ITransactionalCommand<ReservationStayAmendmentReceiptDto>;
