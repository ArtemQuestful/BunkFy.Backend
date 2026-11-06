namespace BunkFy.Modules.Reservations.Application.Handlers;

using BunkFy.Modules.Reservations.Application.Commands;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Contracts;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;

internal sealed class StationCheckInReservationCommandHandler(ReservationManagementLifecycleCoordinator lifecycle, IIdGenerator ids)
    : ICommandHandler<StationCheckInReservationCommand, ReservationMutationReceiptDto>
{
    public Task<Result<ReservationMutationReceiptDto>> HandleAsync(StationCheckInReservationCommand command, CancellationToken cancellationToken) =>
        lifecycle.ExecuteAsync(command.OperationId, command.PropertyId, command.ReservationId,
            ReservationManagementOperationKind.CheckIn, command.ExpectedVersion, command.BusinessDate,
            command.Provenance.StaffMemberId.ToString("D"),
            (reservation, now) => command.AllocationId is not null && command.AllocationVersion is > 0 &&
                reservation.AllocationId == command.AllocationId && reservation.AllocationVersion == command.AllocationVersion
                ? reservation.CheckIn(command.ExpectedVersion, command.BusinessDate, command.Provenance.StaffMemberId.ToString("D"), ids.NewId(), now)
                : Result.Failure(ReservationsApplicationErrors.ManagementOperationConflict),
            cancellationToken, command.Provenance);
}
