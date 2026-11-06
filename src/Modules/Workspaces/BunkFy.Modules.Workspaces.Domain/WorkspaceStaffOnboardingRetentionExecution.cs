namespace BunkFy.Modules.Workspaces.Domain;

using Gma.Framework.Domain.Models;
using Gma.Framework.Naming;
using Gma.Framework.Results;

public sealed class WorkspaceStaffOnboardingRetentionExecution
    : ScopedAggregateRoot<Guid>
{
    public const int DataClassKeyMaxLength = 64;
    public const int OutcomeCodeMaxLength = 100;

    private WorkspaceStaffOnboardingRetentionExecution() { }

    private WorkspaceStaffOnboardingRetentionExecution(Guid id, string scopeId)
        : base(id, scopeId) { }

    public string DataClassKey { get; private set; } = string.Empty;
    public int ExecutionPolicyVersion { get; private set; }
    public int Attempt { get; private set; }
    public WorkspaceStaffOnboardingRetentionExecutionState State { get; private set; }
    public DateTimeOffset StartedAtUtc { get; private set; }
    public DateTimeOffset DeadlineUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public int AffectedCount { get; private set; }
    public int ScannedCount { get; private set; }
    public int? RemainingCount { get; private set; }
    public string? OutcomeCode { get; private set; }
    public long Version { get; private set; } = 1;

    public static Result<WorkspaceStaffOnboardingRetentionExecution> Start(
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
            !IsKey(normalized, DataClassKeyMaxLength) ||
            executionPolicyVersion < 1 ||
            attempt < 1 ||
            startedAtUtc == default ||
            deadlineUtc <= startedAtUtc)
        {
            return Result.Failure<WorkspaceStaffOnboardingRetentionExecution>(
                WorkspaceStaffRetentionErrors.ExecutionCoordinateInvalid);
        }

        return Result.Success(
            new WorkspaceStaffOnboardingRetentionExecution(id, scopeId!)
            {
                DataClassKey = normalized,
                ExecutionPolicyVersion = executionPolicyVersion,
                Attempt = attempt,
                State = WorkspaceStaffOnboardingRetentionExecutionState.Running,
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
            this.State == WorkspaceStaffOnboardingRetentionExecutionState.Failed &&
            (!this.CompletedAtUtc.HasValue ||
             startedAtUtc < this.CompletedAtUtc.Value);
        if (this.State is not (
                WorkspaceStaffOnboardingRetentionExecutionState.Running or
                WorkspaceStaffOnboardingRetentionExecutionState.Failed) ||
            attempt <= this.Attempt ||
            startedAtUtc == default ||
            startedAtUtc < this.StartedAtUtc ||
            deadlineUtc <= startedAtUtc ||
            deadlineUtc <= this.DeadlineUtc ||
            failedWindowInvalid)
        {
            return Result.Failure(
                WorkspaceStaffRetentionErrors.ExecutionTransitionInvalid);
        }

        this.Attempt = attempt;
        this.State = WorkspaceStaffOnboardingRetentionExecutionState.Running;
        this.StartedAtUtc = startedAtUtc;
        this.DeadlineUtc = deadlineUtc;
        this.CompletedAtUtc = null;
        this.RemainingCount = null;
        this.OutcomeCode = null;
        this.Version++;
        return Result.Success();
    }

    public Result RecordCandidate(int attempt, bool affected)
    {
        if (this.State != WorkspaceStaffOnboardingRetentionExecutionState.Running ||
            attempt != this.Attempt ||
            this.ScannedCount == int.MaxValue ||
            (affected && this.AffectedCount == int.MaxValue))
        {
            return Result.Failure(
                WorkspaceStaffRetentionErrors.ExecutionTransitionInvalid);
        }

        this.ScannedCount++;
        if (affected)
        {
            this.AffectedCount++;
        }

        this.Version++;
        return Result.Success();
    }

    public Result Complete(
        WorkspaceStaffOnboardingRetentionExecutionState state,
        int attempt,
        int scannedCount,
        int remainingCount,
        string outcomeCode,
        DateTimeOffset completedAtUtc)
    {
        string normalized = outcomeCode?.Trim() ?? string.Empty;
        bool failed = state ==
            WorkspaceStaffOnboardingRetentionExecutionState.Failed;
        bool scannedCountInvalid = failed
            ? scannedCount != this.ScannedCount &&
              (this.ScannedCount == int.MaxValue ||
               scannedCount != this.ScannedCount + 1)
            : scannedCount != this.ScannedCount;
        if (state is not (
                WorkspaceStaffOnboardingRetentionExecutionState.Completed or
                WorkspaceStaffOnboardingRetentionExecutionState.Failed) ||
            attempt != this.Attempt ||
            scannedCount < 0 ||
            scannedCountInvalid ||
            this.AffectedCount > scannedCount ||
            remainingCount < 0 ||
            (failed && remainingCount == 0) ||
            !IsKey(normalized, OutcomeCodeMaxLength) ||
            completedAtUtc < this.StartedAtUtc ||
            completedAtUtc > this.DeadlineUtc)
        {
            return Result.Failure(
                WorkspaceStaffRetentionErrors.ExecutionResultInvalid);
        }

        if (this.State != WorkspaceStaffOnboardingRetentionExecutionState.Running)
        {
            return this.MatchesResult(
                    state,
                    attempt,
                    scannedCount,
                    remainingCount,
                    normalized,
                    completedAtUtc)
                ? Result.Success()
                : Result.Failure(
                    WorkspaceStaffRetentionErrors.ExecutionTransitionInvalid);
        }

        this.State = state;
        this.ScannedCount = scannedCount;
        this.RemainingCount = remainingCount;
        this.OutcomeCode = normalized;
        this.CompletedAtUtc = completedAtUtc;
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

    public bool MatchesAttemptWindow(
        int attempt,
        DateTimeOffset startedAtUtc,
        DateTimeOffset deadlineUtc) =>
        this.Attempt == attempt &&
        this.StartedAtUtc == startedAtUtc &&
        this.DeadlineUtc == deadlineUtc;

    private bool MatchesResult(
        WorkspaceStaffOnboardingRetentionExecutionState state,
        int attempt,
        int scannedCount,
        int remainingCount,
        string outcomeCode,
        DateTimeOffset completedAtUtc) =>
        this.State == state &&
        this.Attempt == attempt &&
        this.ScannedCount == scannedCount &&
        this.RemainingCount == remainingCount &&
        string.Equals(this.OutcomeCode, outcomeCode, StringComparison.Ordinal) &&
        this.CompletedAtUtc == completedAtUtc;

    private static bool IsKey(string value, int maximumLength) =>
        value.Length > 0 && value.Length <= maximumLength &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '-' or '.');
}
