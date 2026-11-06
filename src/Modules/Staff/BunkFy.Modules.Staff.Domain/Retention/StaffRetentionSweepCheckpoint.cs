namespace BunkFy.Modules.Staff.Domain.Retention;

using BunkFy.Modules.Staff.Domain.Errors;
using Gma.Framework.Domain.Models;
using Gma.Framework.Naming;
using Gma.Framework.Results;

public sealed class StaffRetentionSweepCheckpoint
    : ScopedAggregateRoot<Guid>
{
    private StaffRetentionSweepCheckpoint() { }

    private StaffRetentionSweepCheckpoint(Guid id, string scopeId)
        : base(id, scopeId)
    {
    }

    public string DataClassKey { get; private set; } = string.Empty;
    public int ExecutionPolicyVersion { get; private set; }
    public long AfterProjectionOrdinal { get; private set; }
    public Guid? LastExecutionId { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public long Version { get; private set; } = 1;

    public static Result<StaffRetentionSweepCheckpoint> Create(
        Guid id,
        string tenantId,
        string dataClassKey,
        int executionPolicyVersion,
        DateTimeOffset nowUtc)
    {
        string normalized =
            dataClassKey?.Trim().ToLowerInvariant() ?? string.Empty;
        if (id == Guid.Empty ||
            !TenantIds.TryNormalize(tenantId, out string? scopeId) ||
            normalized.Length is 0 or >
                StaffRetentionExecution.DataClassKeyMaxLength ||
            normalized.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) ||
                  character is '-' or '.')) ||
            executionPolicyVersion < 1 ||
            nowUtc == default)
        {
            return Result.Failure<StaffRetentionSweepCheckpoint>(
                StaffDomainErrors.RetentionCheckpointInvalid);
        }

        return Result.Success(
            new StaffRetentionSweepCheckpoint(id, scopeId)
            {
                DataClassKey = normalized,
                ExecutionPolicyVersion = executionPolicyVersion,
                UpdatedAtUtc = nowUtc
            });
    }

    public Result Advance(
        long expectedAfterProjectionOrdinal,
        long nextAfterProjectionOrdinal,
        Guid executionId,
        DateTimeOffset nowUtc)
    {
        if (this.LastExecutionId == executionId)
        {
            return this.AfterProjectionOrdinal ==
                    nextAfterProjectionOrdinal
                ? Result.Success()
                : Result.Failure(
                    StaffDomainErrors.RetentionCheckpointConflict);
        }

        if (expectedAfterProjectionOrdinal !=
                this.AfterProjectionOrdinal ||
            nextAfterProjectionOrdinal < 0 ||
            executionId == Guid.Empty ||
            nowUtc < this.UpdatedAtUtc)
        {
            return Result.Failure(
                StaffDomainErrors.RetentionCheckpointConflict);
        }

        this.AfterProjectionOrdinal = nextAfterProjectionOrdinal;
        this.LastExecutionId = executionId;
        this.UpdatedAtUtc = nowUtc;
        this.Version++;
        return Result.Success();
    }

    public Result PrepareRetry(
        StaffRetentionExecution failedExecution,
        DateTimeOffset retryStartedAtUtc)
    {
        if (failedExecution.State !=
                StaffRetentionExecutionState.Failed ||
            !string.Equals(
                this.ScopeId,
                failedExecution.ScopeId,
                StringComparison.Ordinal) ||
            !string.Equals(
                this.DataClassKey,
                failedExecution.DataClassKey,
                StringComparison.Ordinal) ||
            this.ExecutionPolicyVersion !=
                failedExecution.ExecutionPolicyVersion ||
            !failedExecution.CompletedAtUtc.HasValue ||
            retryStartedAtUtc == default ||
            retryStartedAtUtc < failedExecution.CompletedAtUtc.Value)
        {
            return Result.Failure(
                StaffDomainErrors.RetentionCheckpointConflict);
        }

        if (this.LastExecutionId == failedExecution.Id)
        {
            if (this.UpdatedAtUtc !=
                failedExecution.CompletedAtUtc.Value)
            {
                return Result.Failure(
                    StaffDomainErrors.RetentionCheckpointConflict);
            }

            this.AfterProjectionOrdinal =
                failedExecution.StartingProjectionOrdinal;
            this.LastExecutionId = null;
            this.UpdatedAtUtc = retryStartedAtUtc;
            this.Version++;
            return Result.Success();
        }

        return this.AfterProjectionOrdinal ==
                   failedExecution.StartingProjectionOrdinal &&
               this.UpdatedAtUtc <= failedExecution.StartedAtUtc
            ? Result.Success()
            : Result.Failure(
                StaffDomainErrors.RetentionCheckpointConflict);
    }
}
