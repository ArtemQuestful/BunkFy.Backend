namespace BunkFy.Modules.Reservations.Application.Commands;

using Gma.Framework.Cqrs;

internal sealed record ApplyReservationRetentionCommand(
    Guid ExecutionId,
    int Attempt,
    Guid PropertyId,
    Guid ReservationId,
    long ExpectedReservationVersion,
    long ExpectedDetailsRevision)
    : ITransactionalCommand<ReservationRetentionMutationResult>;
