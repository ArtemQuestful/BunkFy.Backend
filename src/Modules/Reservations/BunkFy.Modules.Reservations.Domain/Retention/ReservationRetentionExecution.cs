namespace BunkFy.Modules.Reservations.Domain.Retention;

using BunkFy.Modules.Reservations.Domain.Errors;
using Gma.Framework.Domain.Models;
using Gma.Framework.Naming;
using Gma.Framework.Results;

public sealed class ReservationRetentionExecution
    : ScopedAggregateRoot<Guid>
{
    public const int DataClassKeyMaxLength = 64;
    public const int OutcomeCodeMaxLength = 100;

    private ReservationRetentionExecution() { }

    private ReservationRetentionExecution(Guid id, string scopeId)
        : base(id, scopeId)
    {
    }

    public string DataClassKey { get; private set; } = string.Empty;
    public int ExecutionPolicyVersion { get; private set; }
    public int Attempt { get; private set; }
    public long StartingProjectionOrdinal { get; private set; }
    public ReservationRetentionExecutionState State { get; private set; }
    public DateTimeOffset StartedAtUtc { get; private set; }
    public DateTimeOffset DeadlineUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public int AffectedCount { get; private set; }
    public int? ScannedCount { get; private set; }
    public int? RemainingCount { get; private set; }
    public string? OutcomeCode { get; private set; }
    public DateTimeOffset? HoldReviewDueAtUtc { get; private set; }
    public long Version { get; private set; } = 1;

    public static Result<ReservationRetentionExecution> Start(
        Guid id,
        string tenantId,
        string dataClassKey,
        int executionPolicyVersion,
        int attempt,
        long startingProjectionOrdinal,
        DateTimeOffset startedAtUtc,
        DateTimeOffset deadlineUtc)
    {
        string normalized =
            dataClassKey?.Trim().ToLowerInvariant() ?? string.Empty;
        if (id == Guid.Empty ||
            !TenantIds.TryNormalize(tenantId, out string? scopeId) ||
            !IsKey(normalized) ||
            executionPolicyVersion <= 0 ||
            attempt <= 0 ||
            startingProjectionOrdinal < 0 ||
            startedAtUtc == default ||
            deadlineUtc <= startedAtUtc)
        {
            return Result.Failure<ReservationRetentionExecution>(
                ReservationsDomainErrors
                    .RetentionExecutionCoordinateInvalid);
        }

        return Result.Success(
            new ReservationRetentionExecution(id, scopeId)
            {
                DataClassKey = normalized,
                ExecutionPolicyVersion = executionPolicyVersion,
                Attempt = attempt,
                StartingProjectionOrdinal = startingProjectionOrdinal,
                State = ReservationRetentionExecutionState.Running,
                StartedAtUtc = startedAtUtc,
                DeadlineUtc = deadlineUtc
            });
    }

    public Result BeginRetry(
        int attempt,
        DateTimeOffset startedAtUtc,
        DateTimeOffset deadlineUtc)
    {
        Result valid = this.ValidateRetry(
            attempt,
            startedAtUtc,
            deadlineUtc);
        if (valid.IsFailure)
        {
            return valid;
        }

        this.Attempt = attempt;
        this.State = ReservationRetentionExecutionState.Running;
        this.StartedAtUtc = startedAtUtc;
        this.DeadlineUtc = deadlineUtc;
        this.CompletedAtUtc = null;
        this.ScannedCount = null;
        this.RemainingCount = null;
        this.OutcomeCode = null;
        this.HoldReviewDueAtUtc = null;
        this.Version++;
        return Result.Success();
    }

    public Result ValidateRetry(
        int attempt,
        DateTimeOffset startedAtUtc,
        DateTimeOffset deadlineUtc)
    {
        bool failedWindowInvalid =
            this.State == ReservationRetentionExecutionState.Failed &&
            (!this.CompletedAtUtc.HasValue ||
             startedAtUtc < this.CompletedAtUtc.Value);
        return this.State is not (
                   ReservationRetentionExecutionState.Running or
                   ReservationRetentionExecutionState.Failed) ||
               attempt <= this.Attempt ||
               startedAtUtc == default ||
               startedAtUtc < this.StartedAtUtc ||
               deadlineUtc <= startedAtUtc ||
               failedWindowInvalid
            ? Result.Failure(
                ReservationsDomainErrors
                    .RetentionExecutionTransitionInvalid)
            : Result.Success();
    }

    public Result RecordAffected()
    {
        if (this.State != ReservationRetentionExecutionState.Running)
        {
            return Result.Failure(
                ReservationsDomainErrors
                    .RetentionExecutionTransitionInvalid);
        }

        this.AffectedCount = checked(this.AffectedCount + 1);
        this.Version++;
        return Result.Success();
    }

    public Result Complete(
        ReservationRetentionExecutionState state,
        int attempt,
        int scannedCount,
        int remainingCount,
        string outcomeCode,
        DateTimeOffset completedAtUtc,
        DateTimeOffset? holdReviewDueAtUtc)
    {
        string normalized = outcomeCode?.Trim() ?? string.Empty;
        bool blocked =
            state == ReservationRetentionExecutionState.Blocked;
        if (state is not (
                ReservationRetentionExecutionState.Completed or
                ReservationRetentionExecutionState.Blocked or
                ReservationRetentionExecutionState.Failed) ||
            attempt != this.Attempt ||
            scannedCount < 0 ||
            this.AffectedCount > scannedCount ||
            remainingCount < 0 ||
            !IsOutcomeCode(normalized) ||
            completedAtUtc < this.StartedAtUtc ||
            completedAtUtc > this.DeadlineUtc ||
            blocked != holdReviewDueAtUtc.HasValue)
        {
            return Result.Failure(
                ReservationsDomainErrors.RetentionExecutionResultInvalid);
        }

        if (this.State != ReservationRetentionExecutionState.Running)
        {
            return this.MatchesResult(
                    state,
                    attempt,
                    scannedCount,
                    remainingCount,
                    normalized,
                    completedAtUtc,
                    holdReviewDueAtUtc)
                ? Result.Success()
                : Result.Failure(
                    ReservationsDomainErrors
                        .RetentionExecutionTransitionInvalid);
        }

        this.State = state;
        this.ScannedCount = scannedCount;
        this.RemainingCount = remainingCount;
        this.OutcomeCode = normalized;
        this.CompletedAtUtc = completedAtUtc;
        this.HoldReviewDueAtUtc = holdReviewDueAtUtc;
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

    private bool MatchesResult(
        ReservationRetentionExecutionState state,
        int attempt,
        int scannedCount,
        int remainingCount,
        string outcomeCode,
        DateTimeOffset completedAtUtc,
        DateTimeOffset? holdReviewDueAtUtc) =>
        this.State == state &&
        this.Attempt == attempt &&
        this.ScannedCount == scannedCount &&
        this.RemainingCount == remainingCount &&
        string.Equals(
            this.OutcomeCode,
            outcomeCode,
            StringComparison.Ordinal) &&
        this.CompletedAtUtc == completedAtUtc &&
        this.HoldReviewDueAtUtc == holdReviewDueAtUtc;

    private static bool IsKey(string value) =>
        value.Length is > 0 and <= DataClassKeyMaxLength &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '-' or '.');

    private static bool IsOutcomeCode(string value) =>
        value.Length is > 0 and <= OutcomeCodeMaxLength &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '-' or '.');
}
