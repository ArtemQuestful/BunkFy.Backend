namespace BunkFy.Modules.Retention.Persistence;

using BunkFy.Modules.Retention.Domain.Aggregates;
using BunkFy.Modules.Retention.Domain.Models;
using Gma.Framework.Domain;

public sealed class RetentionScheduleState : IScopedEntity
{
    public const int TargetKeyMaxLength = 32;

    private RetentionScheduleState() { }

    public RetentionScheduleState(
        RetentionExecution execution,
        DateTimeOffset nextDueAtUtc)
    {
        this.ScopeId = execution.ScopeId;
        this.OwnerKey = execution.OwnerKey;
        this.DataClassKey = execution.DataClassKey;
        this.TargetKey = CreateTargetKey(execution.PropertyId);
        this.PropertyId = execution.PropertyId;
        this.ExecutionPolicyVersion = execution.ExecutionPolicyVersion;
        this.RecordStarted(execution, nextDueAtUtc);
    }

    public string ScopeId { get; private set; } = string.Empty;
    public string OwnerKey { get; private set; } = string.Empty;
    public string DataClassKey { get; private set; } = string.Empty;
    public string TargetKey { get; private set; } = string.Empty;
    public Guid? PropertyId { get; private set; }
    public int ExecutionPolicyVersion { get; private set; }
    public Guid LastExecutionId { get; private set; }
    public RetentionExecutionState State { get; private set; }
    public DateTimeOffset LastStartedAtUtc { get; private set; }
    public DateTimeOffset? LastCompletedAtUtc { get; private set; }
    public DateTimeOffset NextDueAtUtc { get; private set; }
    public int ConsecutiveFailures { get; private set; }
    public int? LastScannedCount { get; private set; }
    public int? LastAffectedCount { get; private set; }
    public int? LastRemainingCount { get; private set; }
    public string? OutcomeCode { get; private set; }
    public DateTimeOffset? HoldReviewDueAtUtc { get; private set; }
    public long Version { get; private set; } = 1;

    public void RecordStarted(
        RetentionExecution execution,
        DateTimeOffset nextDueAtUtc)
    {
        this.RequireCoordinate(execution);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            nextDueAtUtc,
            execution.StartedAtUtc);

        bool isInitialState = this.LastExecutionId == Guid.Empty;
        if (this.LastExecutionId == execution.Id &&
            this.LastStartedAtUtc == execution.StartedAtUtc &&
            this.State == RetentionExecutionState.Running)
        {
            return;
        }

        if (execution.StartedAtUtc < this.LastStartedAtUtc)
        {
            return;
        }

        if (!isInitialState && this.Version == long.MaxValue)
        {
            throw new InvalidOperationException(
                "Retention.ScheduleVersionExhausted");
        }

        this.LastExecutionId = execution.Id;
        this.State = RetentionExecutionState.Running;
        this.LastStartedAtUtc = execution.StartedAtUtc;
        this.LastCompletedAtUtc = null;
        this.NextDueAtUtc = nextDueAtUtc;
        this.LastScannedCount = null;
        this.LastAffectedCount = null;
        this.LastRemainingCount = null;
        this.OutcomeCode = null;
        this.HoldReviewDueAtUtc = null;
        if (!isInitialState)
        {
            this.Version++;
        }
    }

    public void RecordCompleted(RetentionExecution execution)
    {
        this.RequireCoordinate(execution);
        if (execution.State is not (
                RetentionExecutionState.Completed or
                RetentionExecutionState.Blocked or
                RetentionExecutionState.Failed) ||
            execution.CompletedAtUtc is null ||
            execution.Id != this.LastExecutionId ||
            execution.StartedAtUtc != this.LastStartedAtUtc)
        {
            throw new InvalidOperationException(
                "Retention.ScheduleCompletionConflict");
        }

        if (this.State == execution.State &&
            this.LastCompletedAtUtc == execution.CompletedAtUtc &&
            this.LastScannedCount == execution.ScannedCount &&
            this.LastAffectedCount == execution.AffectedCount &&
            this.LastRemainingCount == execution.RemainingCount &&
            string.Equals(
                this.OutcomeCode,
                execution.OutcomeCode,
                StringComparison.Ordinal) &&
            this.HoldReviewDueAtUtc == execution.HoldReviewDueAtUtc)
        {
            return;
        }

        if (this.Version == long.MaxValue)
        {
            throw new InvalidOperationException(
                "Retention.ScheduleVersionExhausted");
        }

        this.State = execution.State;
        this.LastCompletedAtUtc = execution.CompletedAtUtc;
        this.ConsecutiveFailures =
            execution.State == RetentionExecutionState.Failed
                ? checked(this.ConsecutiveFailures + 1)
                : 0;
        this.LastScannedCount = execution.ScannedCount;
        this.LastAffectedCount = execution.AffectedCount;
        this.LastRemainingCount = execution.RemainingCount;
        this.OutcomeCode = execution.OutcomeCode;
        this.HoldReviewDueAtUtc = execution.HoldReviewDueAtUtc;
        this.Version++;
    }

    public static string CreateTargetKey(Guid? propertyId) =>
        propertyId?.ToString("N") ?? "tenant";

    private void RequireCoordinate(RetentionExecution execution)
    {
        if (!string.Equals(this.ScopeId, execution.ScopeId, StringComparison.Ordinal) ||
            !string.Equals(this.OwnerKey, execution.OwnerKey, StringComparison.Ordinal) ||
            !string.Equals(
                this.DataClassKey,
                execution.DataClassKey,
                StringComparison.Ordinal) ||
            this.PropertyId != execution.PropertyId ||
            this.ExecutionPolicyVersion != execution.ExecutionPolicyVersion)
        {
            throw new InvalidOperationException(
                "Retention.ScheduleCoordinateConflict");
        }
    }
}
