namespace BunkFy.Modules.Ingestion.Application.Commands;

using BunkFy.Modules.Retention.Contracts;
using Gma.Framework.Cqrs;

public sealed record CompleteIngestionRetentionExecutionCommand(
    Guid ExecutionId,
    int Attempt,
    int RemainingCount,
    string OutcomeCode,
    DateTimeOffset CompletedAtUtc,
    DateTimeOffset? HoldReviewDueAtUtc)
    : ITransactionalCommand<RetentionContributionResult>;
