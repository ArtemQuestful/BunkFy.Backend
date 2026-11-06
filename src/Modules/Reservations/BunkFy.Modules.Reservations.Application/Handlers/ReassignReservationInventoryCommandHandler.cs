namespace BunkFy.Modules.Reservations.Application.Handlers;

using BunkFy.Modules.Reservations.Application.Commands;
using BunkFy.Modules.Reservations.Application.StayAmendments;
using BunkFy.Modules.Reservations.Contracts;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;

internal sealed class ReassignReservationInventoryCommandHandler(
    ReservationStayAmendmentCoordinator coordinator)
    : ICommandHandler<ReassignReservationInventoryCommand, ReservationMutationReceiptDto>
{
    public Task<Result<ReservationMutationReceiptDto>> HandleAsync(
        ReassignReservationInventoryCommand command,
        CancellationToken cancellationToken) =>
        coordinator.ReassignInventoryAsync(command, cancellationToken);
}
