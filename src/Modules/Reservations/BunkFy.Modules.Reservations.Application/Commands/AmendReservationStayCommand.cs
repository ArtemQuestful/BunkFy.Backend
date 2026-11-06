namespace BunkFy.Modules.Reservations.Application.Commands;

using BunkFy.Modules.Reservations.Contracts;
using Gma.Framework.Cqrs;

public sealed record AmendReservationStayCommand(
    Guid OperationId,
    Guid PropertyId,
    Guid ReservationId,
    DateOnly Arrival,
    DateOnly Departure,
    TimeOnly? ExpectedArrivalTime,
    TimeOnly? ExpectedDepartureTime,
    IReadOnlyCollection<Guid> InventoryUnitIds,
    long ExpectedDetailsRevision,
    string ActorId)
    : ITransactionalCommand<ReservationStayAmendmentReceiptDto>;
