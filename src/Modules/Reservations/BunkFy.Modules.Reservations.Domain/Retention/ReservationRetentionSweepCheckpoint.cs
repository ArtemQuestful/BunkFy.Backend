namespace BunkFy.Modules.Reservations.Domain.Retention;

using BunkFy.Modules.Reservations.Domain.Errors;
using Gma.Framework.Domain.Models;
using Gma.Framework.Naming;
using Gma.Framework.Results;

public sealed class ReservationRetentionSweepCheckpoint
    : ScopedAggregateRoot<Guid>
{
    private ReservationRetentionSweepCheckpoint() { }

    private ReservationRetentionSweepCheckpoint(
        Guid id,
        string scopeId)
        : base(id, scopeId)
    {
    }

    public string DataClassKey { get; private set; } = string.Empty;
    public int ExecutionPolicyVersion { get; private set; }
    public long AfterProjectionOrdinal { get; private set; }
    public Guid? LastExecutionId { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public long Version { get; private set; } = 1;

    public static Result<ReservationRetentionSweepCheckpoint> Create(
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
                ReservationRetentionExecution.DataClassKeyMaxLength ||
            normalized.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) ||
                  character is '-' or '.')) ||
            executionPolicyVersion < 1 ||
            nowUtc == default)
        {
            return Result.Failure<
                ReservationRetentionSweepCheckpoint>(
                ReservationsDomainErrors.RetentionCheckpointInvalid);
        }

        return Result.Success(
            new ReservationRetentionSweepCheckpoint(id, scopeId)
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
                    ReservationsDomainErrors
                        .RetentionCheckpointConflict);
        }

        if (expectedAfterProjectionOrdinal !=
                this.AfterProjectionOrdinal ||
            nextAfterProjectionOrdinal < 0 ||
            executionId == Guid.Empty ||
            nowUtc < this.UpdatedAtUtc)
        {
            return Result.Failure(
                ReservationsDomainErrors.RetentionCheckpointConflict);
        }

        this.AfterProjectionOrdinal = nextAfterProjectionOrdinal;
        this.LastExecutionId = executionId;
        this.UpdatedAtUtc = nowUtc;
        this.Version++;
        return Result.Success();
    }

    public Result PrepareRetry(
        ReservationRetentionExecution failedExecution,
        DateTimeOffset retryStartedAtUtc)
    {
        if (failedExecution.State !=
                ReservationRetentionExecutionState.Failed ||
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
                ReservationsDomainErrors.RetentionCheckpointConflict);
        }

        if (this.LastExecutionId == failedExecution.Id)
        {
            if (this.UpdatedAtUtc !=
                failedExecution.CompletedAtUtc.Value)
            {
                return Result.Failure(
                    ReservationsDomainErrors
                        .RetentionCheckpointConflict);
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
                ReservationsDomainErrors.RetentionCheckpointConflict);
    }
}
