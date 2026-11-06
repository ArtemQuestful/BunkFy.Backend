namespace BunkFy.Modules.Guests.Application.Commands;

using BunkFy.Modules.Guests.Domain.Retention;
using BunkFy.Modules.Retention.Contracts;
using Gma.Framework.Cqrs;

internal sealed record CompleteGuestRetentionExecutionCommand(
    Guid ExecutionId,
    int Attempt,
    GuestRetentionExecutionState State,
    int ScannedCount,
    int RemainingCount,
    string OutcomeCode,
    DateTimeOffset CompletedAtUtc,
    DateTimeOffset? HoldReviewDueAtUtc,
    long ExpectedAfterProjectionOrdinal,
    long NextAfterProjectionOrdinal)
    : ITransactionalCommand<RetentionContributionResult>;
