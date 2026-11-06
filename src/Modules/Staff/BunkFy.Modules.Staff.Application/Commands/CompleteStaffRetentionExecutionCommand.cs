namespace BunkFy.Modules.Staff.Application.Commands;

using BunkFy.Modules.Retention.Contracts;
using BunkFy.Modules.Staff.Domain.Retention;
using Gma.Framework.Cqrs;

internal sealed record CompleteStaffRetentionExecutionCommand(
    Guid ExecutionId,
    int Attempt,
    StaffRetentionExecutionState State,
    int ScannedCount,
    int RemainingCount,
    string OutcomeCode,
    DateTimeOffset CompletedAtUtc,
    DateTimeOffset? HoldReviewDueAtUtc,
    long ExpectedAfterProjectionOrdinal,
    long NextAfterProjectionOrdinal)
    : ITransactionalCommand<RetentionContributionResult>;
