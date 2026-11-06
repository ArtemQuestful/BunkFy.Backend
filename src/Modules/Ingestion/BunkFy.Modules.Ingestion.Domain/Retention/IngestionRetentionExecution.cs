namespace BunkFy.Modules.Ingestion.Domain.Retention;

using Gma.Framework.Domain.Models;
using Gma.Framework.Naming;
using Gma.Framework.Results;

public sealed class IngestionRetentionExecution : ScopedAggregateRoot<Guid>
{
    public const int DataClassKeyMaxLength = 64;
    public const int OutcomeCodeMaxLength = 100;

    private IngestionRetentionExecution() { }

    private IngestionRetentionExecution(Guid id, string scopeId)
        : base(id, scopeId) { }

    public string DataClassKey { get; private set; } = string.Empty;
    public int ExecutionPolicyVersion { get; private set; }
    public int Attempt { get; private set; }
    public IngestionRetentionExecutionState State { get; private set; }
    public DateTimeOffset StartedAtUtc { get; private set; }
    public DateTimeOffset DeadlineUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public int AffectedCount { get; private set; }
    public int? RemainingCount { get; private set; }
    public string? OutcomeCode { get; private set; }
    public DateTimeOffset? HoldReviewDueAtUtc { get; private set; }
    public long Version { get; private set; } = 1;

    public static Result<IngestionRetentionExecution> Start(
        Guid id,
        string tenantId,
        string dataClassKey,
        int executionPolicyVersion,
        int attempt,
        DateTimeOffset startedAtUtc,
        DateTimeOffset deadlineUtc)
    {
        string normalized = dataClassKey?.Trim().ToLowerInvariant() ?? string.Empty;
        if (id == Guid.Empty ||
            !TenantIds.TryNormalize(tenantId, out string? scopeId) ||
            normalized.Length is 0 or > DataClassKeyMaxLength ||
            normalized.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) ||
                  character is '-' or '.')) ||
            executionPolicyVersion <= 0 ||
            attempt <= 0 ||
            startedAtUtc == default ||
            deadlineUtc <= startedAtUtc)
        {
            return Result.Failure<IngestionRetentionExecution>(
                IngestionRetentionExecutionErrors.CoordinateInvalid);
        }

        return Result.Success(new IngestionRetentionExecution(id, scopeId!)
        {
            DataClassKey = normalized,
            ExecutionPolicyVersion = executionPolicyVersion,
            Attempt = attempt,
            State = IngestionRetentionExecutionState.Running,
            StartedAtUtc = startedAtUtc,
            DeadlineUtc = deadlineUtc
        });
    }

    public Result BeginRetry(
        int attempt,
        DateTimeOffset startedAtUtc,
        DateTimeOffset deadlineUtc)
    {
        if (this.State != IngestionRetentionExecutionState.Running ||
            attempt <= this.Attempt ||
            startedAtUtc == default ||
            startedAtUtc < this.StartedAtUtc ||
            deadlineUtc <= startedAtUtc)
        {
            return Result.Failure(
                IngestionRetentionExecutionErrors.TransitionInvalid);
        }

        this.Attempt = attempt;
        this.StartedAtUtc = startedAtUtc;
        this.DeadlineUtc = deadlineUtc;
        this.Version++;
        return Result.Success();
    }

    public Result RecordAffected(int attempt, int count)
    {
        if (this.State != IngestionRetentionExecutionState.Running ||
            attempt != this.Attempt ||
            count <= 0)
        {
            return Result.Failure(
                IngestionRetentionExecutionErrors.TransitionInvalid);
        }

        this.AffectedCount = checked(this.AffectedCount + count);
        this.Version++;
        return Result.Success();
    }

    public bool MatchesCoordinate(
        string dataClassKey,
        int executionPolicyVersion) =>
        string.Equals(
            this.DataClassKey,
            dataClassKey?.Trim().ToLowerInvariant(),
            StringComparison.Ordinal) &&
        this.ExecutionPolicyVersion == executionPolicyVersion;

    public Result Complete(
        IngestionRetentionExecutionState state,
        int attempt,
        int remainingCount,
        string outcomeCode,
        DateTimeOffset completedAtUtc,
        DateTimeOffset? holdReviewDueAtUtc)
    {
        if (attempt != this.Attempt)
        {
            return Result.Failure(
                IngestionRetentionExecutionErrors.TransitionInvalid);
        }

        string normalized = outcomeCode?.Trim() ?? string.Empty;
        bool blocked = state == IngestionRetentionExecutionState.Blocked;
        if (state is not (
                IngestionRetentionExecutionState.Completed or
                IngestionRetentionExecutionState.Blocked) ||
            remainingCount < 0 ||
            normalized.Length is 0 or > OutcomeCodeMaxLength ||
            completedAtUtc < this.StartedAtUtc ||
            completedAtUtc > this.DeadlineUtc ||
            blocked != (holdReviewDueAtUtc is not null))
        {
            return Result.Failure(
                IngestionRetentionExecutionErrors.ResultInvalid);
        }

        if (this.State != IngestionRetentionExecutionState.Running)
        {
            return this.State == state &&
                this.RemainingCount == remainingCount &&
                string.Equals(this.OutcomeCode, normalized, StringComparison.Ordinal) &&
                this.CompletedAtUtc == completedAtUtc &&
                this.HoldReviewDueAtUtc == holdReviewDueAtUtc
                ? Result.Success()
                : Result.Failure(
                    IngestionRetentionExecutionErrors.TransitionInvalid);
        }

        this.State = state;
        this.RemainingCount = remainingCount;
        this.OutcomeCode = normalized;
        this.CompletedAtUtc = completedAtUtc;
        this.HoldReviewDueAtUtc = holdReviewDueAtUtc;
        this.Version++;
        return Result.Success();
    }
}
