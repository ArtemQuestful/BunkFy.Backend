namespace BunkFy.Modules.Reservations.Application.Handlers;

using BunkFy.Modules.Reservations.Application.Commands;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Contracts;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;

internal sealed class StationCheckOutReservationCommandHandler(ReservationManagementLifecycleCoordinator lifecycle, IIdGenerator ids)
    : ICommandHandler<StationCheckOutReservationCommand, ReservationMutationReceiptDto>
{
    public Task<Result<ReservationMutationReceiptDto>> HandleAsync(StationCheckOutReservationCommand command, CancellationToken cancellationToken) =>
        lifecycle.ExecuteAsync(command.OperationId, command.PropertyId, command.ReservationId,
            ReservationManagementOperationKind.CheckOut, command.ExpectedVersion, command.BusinessDate,
            command.Provenance.StaffMemberId.ToString("D"),
            (reservation, now) => command.AllocationId is not null && command.AllocationVersion is > 0 &&
                reservation.AllocationId == command.AllocationId && reservation.AllocationVersion == command.AllocationVersion &&
                reservation.Departure <= command.BusinessDate
                ? reservation.RequestCheckout(command.ExpectedVersion, command.BusinessDate, command.Provenance.StaffMemberId.ToString("D"), ids.NewId(), ids.NewId(), now)
                : Result.Failure(ReservationsApplicationErrors.ManagementOperationConflict),
            cancellationToken, command.Provenance);
}
