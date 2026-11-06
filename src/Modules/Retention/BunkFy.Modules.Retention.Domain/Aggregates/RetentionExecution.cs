namespace BunkFy.Modules.Retention.Domain.Aggregates;

using BunkFy.Modules.Retention.Domain.Errors;
using BunkFy.Modules.Retention.Domain.Models;
using Gma.Framework.Domain.Models;
using Gma.Framework.Naming;
using Gma.Framework.Results;

public sealed class RetentionExecution : ScopedAggregateRoot<Guid>
{
    public const int KeyMaxLength = 64;
    public const int OutcomeCodeMaxLength = 100;

    private RetentionExecution() { }

    private RetentionExecution(Guid id, string scopeId) : base(id, scopeId) { }

    public string OwnerKey { get; private set; } = string.Empty;
    public string DataClassKey { get; private set; } = string.Empty;
    public RetentionExecutionTargetKind TargetKind { get; private set; }
    public Guid? PropertyId { get; private set; }
    public int ExecutionPolicyVersion { get; private set; }
    public int Attempt { get; private set; }
    public RetentionExecutionState State { get; private set; }
    public DateTimeOffset StartedAtUtc { get; private set; }
    public DateTimeOffset DeadlineUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public int? ScannedCount { get; private set; }
    public int? AffectedCount { get; private set; }
    public int? RemainingCount { get; private set; }
    public string? OutcomeCode { get; private set; }
    public DateTimeOffset? HoldReviewDueAtUtc { get; private set; }
    public long Version { get; private set; } = 1;

    public static Result<RetentionExecution> Start(
        Guid id,
        string tenantId,
        string ownerKey,
        string dataClassKey,
        RetentionExecutionTargetKind targetKind,
        Guid? propertyId,
        int executionPolicyVersion,
        int attempt,
        DateTimeOffset startedAtUtc,
        DateTimeOffset deadlineUtc)
    {
        if (id == Guid.Empty ||
            !TenantIds.TryNormalize(tenantId, out string? scopeId) ||
            !TryNormalizeKey(ownerKey, out string? normalizedOwner) ||
            !TryNormalizeKey(dataClassKey, out string? normalizedDataClass) ||
            executionPolicyVersion <= 0 ||
            attempt <= 0 ||
            startedAtUtc == default ||
            deadlineUtc <= startedAtUtc ||
            !IsValidTarget(targetKind, propertyId))
        {
            return Result.Failure<RetentionExecution>(
                RetentionDomainErrors.CoordinateInvalid);
        }

        return Result.Success(new RetentionExecution(id, scopeId)
        {
            OwnerKey = normalizedOwner!,
            DataClassKey = normalizedDataClass!,
            TargetKind = targetKind,
            PropertyId = propertyId,
            ExecutionPolicyVersion = executionPolicyVersion,
            Attempt = attempt,
            State = RetentionExecutionState.Running,
            StartedAtUtc = startedAtUtc,
            DeadlineUtc = deadlineUtc
        });
    }

    public Result BeginRetry(
        int attempt,
        DateTimeOffset startedAtUtc,
        DateTimeOffset deadlineUtc)
    {
        bool failedWindowInvalid =
            this.State == RetentionExecutionState.Failed &&
            (!this.CompletedAtUtc.HasValue ||
             startedAtUtc < this.CompletedAtUtc.Value);
        if (attempt <= this.Attempt ||
            startedAtUtc == default ||
            startedAtUtc < this.StartedAtUtc ||
            failedWindowInvalid ||
            deadlineUtc <= startedAtUtc ||
            this.Version == long.MaxValue)
        {
            return Result.Failure(RetentionDomainErrors.AttemptInvalid);
        }

        if (this.State is RetentionExecutionState.Completed or RetentionExecutionState.Blocked)
        {
            return Result.Failure(RetentionDomainErrors.TransitionInvalid);
        }

        this.Attempt = attempt;
        this.State = RetentionExecutionState.Running;
        this.StartedAtUtc = startedAtUtc;
        this.DeadlineUtc = deadlineUtc;
        this.CompletedAtUtc = null;
        this.ScannedCount = null;
        this.AffectedCount = null;
        this.RemainingCount = null;
        this.OutcomeCode = null;
        this.HoldReviewDueAtUtc = null;
        this.Version++;
        return Result.Success();
    }

    public Result Complete(
        RetentionExecutionState state,
        int attempt,
        int scannedCount,
        int affectedCount,
        int remainingCount,
        string outcomeCode,
        DateTimeOffset completedAtUtc,
        DateTimeOffset? holdReviewDueAtUtc)
    {
        string normalizedCode = outcomeCode?.Trim() ?? string.Empty;
        bool isBlocked = state == RetentionExecutionState.Blocked;
        bool hasHoldReview = holdReviewDueAtUtc is not null;
        bool lateNonFailure =
            state != RetentionExecutionState.Failed &&
            completedAtUtc > this.DeadlineUtc;

        if (state is not (RetentionExecutionState.Completed or
                RetentionExecutionState.Blocked or RetentionExecutionState.Failed) ||
            attempt != this.Attempt ||
            scannedCount < 0 ||
            affectedCount < 0 ||
            remainingCount < 0 ||
            affectedCount > scannedCount ||
            !IsOutcomeCode(normalizedCode) ||
            completedAtUtc < this.StartedAtUtc ||
            lateNonFailure ||
            isBlocked != hasHoldReview)
        {
            return Result.Failure(RetentionDomainErrors.CompletionInvalid);
        }

        if (this.State != RetentionExecutionState.Running)
        {
            return this.MatchesResult(
                    state,
                    attempt,
                    scannedCount,
                    affectedCount,
                    remainingCount,
                    normalizedCode,
                    completedAtUtc,
                    holdReviewDueAtUtc)
                ? Result.Success()
                : Result.Failure(RetentionDomainErrors.TransitionInvalid);
        }

        if (this.Version == long.MaxValue)
        {
            return Result.Failure(RetentionDomainErrors.TransitionInvalid);
        }

        this.State = state;
        this.CompletedAtUtc = completedAtUtc;
        this.ScannedCount = scannedCount;
        this.AffectedCount = affectedCount;
        this.RemainingCount = remainingCount;
        this.OutcomeCode = normalizedCode;
        this.HoldReviewDueAtUtc = holdReviewDueAtUtc;
        this.Version++;
        return Result.Success();
    }

    public bool MatchesCoordinate(
        string ownerKey,
        string dataClassKey,
        RetentionExecutionTargetKind targetKind,
        Guid? propertyId,
        int executionPolicyVersion) =>
        TryNormalizeKey(ownerKey, out string? normalizedOwner) &&
        TryNormalizeKey(dataClassKey, out string? normalizedDataClass) &&
        string.Equals(this.OwnerKey, normalizedOwner, StringComparison.Ordinal) &&
        string.Equals(this.DataClassKey, normalizedDataClass, StringComparison.Ordinal) &&
        this.TargetKind == targetKind &&
        this.PropertyId == propertyId &&
        this.ExecutionPolicyVersion == executionPolicyVersion;

    private bool MatchesResult(
        RetentionExecutionState state,
        int attempt,
        int scannedCount,
        int affectedCount,
        int remainingCount,
        string outcomeCode,
        DateTimeOffset completedAtUtc,
        DateTimeOffset? holdReviewDueAtUtc) =>
        this.State == state &&
        this.Attempt == attempt &&
        this.ScannedCount == scannedCount &&
        this.AffectedCount == affectedCount &&
        this.RemainingCount == remainingCount &&
        string.Equals(this.OutcomeCode, outcomeCode, StringComparison.Ordinal) &&
        this.CompletedAtUtc == completedAtUtc &&
        this.HoldReviewDueAtUtc == holdReviewDueAtUtc;

    private static bool IsValidTarget(
        RetentionExecutionTargetKind targetKind,
        Guid? propertyId) =>
        targetKind switch
        {
            RetentionExecutionTargetKind.Tenant => propertyId is null,
            RetentionExecutionTargetKind.Property => propertyId is not null &&
                propertyId != Guid.Empty,
            _ => false
        };

    private static bool TryNormalizeKey(string value, out string? normalized)
    {
        normalized = value?.Trim().ToLowerInvariant();
        return normalized is { Length: > 0 and <= KeyMaxLength } &&
            normalized.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '.');
    }

    private static bool IsOutcomeCode(string value) =>
        value.Length is > 0 and <= OutcomeCodeMaxLength &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '.');
}
