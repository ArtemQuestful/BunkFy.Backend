namespace BunkFy.Modules.Reservations.Application.Commands;

using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Contracts.Stations;
using Gma.Framework.Cqrs;

public sealed record StationCheckOutReservationCommand(Guid OperationId, Guid PropertyId, Guid ReservationId,
    DateOnly BusinessDate, long ExpectedVersion, Guid? AllocationId, long? AllocationVersion,
    StationCheckInProvenance Provenance) : ITransactionalCommand<ReservationMutationReceiptDto>;
