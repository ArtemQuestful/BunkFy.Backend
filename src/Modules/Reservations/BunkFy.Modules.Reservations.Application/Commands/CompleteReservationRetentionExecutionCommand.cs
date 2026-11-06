namespace BunkFy.Modules.Reservations.Application.Commands;

using BunkFy.Modules.Reservations.Domain.Retention;
using BunkFy.Modules.Retention.Contracts;
using Gma.Framework.Cqrs;

internal sealed record CompleteReservationRetentionExecutionCommand(
    Guid ExecutionId,
    int Attempt,
    ReservationRetentionExecutionState State,
    int ScannedCount,
    int RemainingCount,
    string OutcomeCode,
    DateTimeOffset CompletedAtUtc,
    DateTimeOffset? HoldReviewDueAtUtc,
    long ExpectedAfterProjectionOrdinal,
    long NextAfterProjectionOrdinal)
    : ITransactionalCommand<RetentionContributionResult>;
