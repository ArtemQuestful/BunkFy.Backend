namespace BunkFy.Modules.Reservations.Application.Handlers;

using BunkFy.Modules.Reservations.Application.Commands;
using BunkFy.Modules.Reservations.Application.StayAmendments;
using BunkFy.Modules.Reservations.Contracts;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;

internal sealed class AmendReservationStayCommandHandler(
    ReservationStayAmendmentCoordinator coordinator)
    : ICommandHandler<AmendReservationStayCommand, ReservationStayAmendmentReceiptDto>
{
    public Task<Result<ReservationStayAmendmentReceiptDto>> HandleAsync(
        AmendReservationStayCommand command,
        CancellationToken cancellationToken) =>
        coordinator.AmendAsync(command, cancellationToken);
}
