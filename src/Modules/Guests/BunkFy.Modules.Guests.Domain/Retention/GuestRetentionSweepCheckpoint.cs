namespace BunkFy.Modules.Guests.Domain.Retention;

using BunkFy.Modules.Guests.Domain.Errors;
using Gma.Framework.Domain.Models;
using Gma.Framework.Naming;
using Gma.Framework.Results;

public sealed class GuestRetentionSweepCheckpoint
    : ScopedAggregateRoot<Guid>
{
    private GuestRetentionSweepCheckpoint() { }

    private GuestRetentionSweepCheckpoint(Guid id, string scopeId)
        : base(id, scopeId)
    {
    }

    public string DataClassKey { get; private set; } = string.Empty;
    public long AfterProjectionOrdinal { get; private set; }
    public Guid? LastExecutionId { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public long Version { get; private set; } = 1;

    public static Result<GuestRetentionSweepCheckpoint> Create(
        Guid id,
        string tenantId,
        string dataClassKey,
        DateTimeOffset nowUtc)
    {
        string normalized =
            dataClassKey?.Trim().ToLowerInvariant() ?? string.Empty;
        if (id == Guid.Empty ||
            !TenantIds.TryNormalize(tenantId, out string? scopeId) ||
            normalized.Length is 0 or >
                GuestRetentionExecution.DataClassKeyMaxLength ||
            normalized.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) ||
                  character is '-' or '.')) ||
            nowUtc == default)
        {
            return Result.Failure<GuestRetentionSweepCheckpoint>(
                GuestsDomainErrors.RetentionCheckpointInvalid);
        }

        return Result.Success(
            new GuestRetentionSweepCheckpoint(id, scopeId)
            {
                DataClassKey = normalized,
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
            return this.AfterProjectionOrdinal == nextAfterProjectionOrdinal
                ? Result.Success()
                : Result.Failure(
                    GuestsDomainErrors.RetentionCheckpointConflict);
        }

        if (expectedAfterProjectionOrdinal !=
                this.AfterProjectionOrdinal ||
            nextAfterProjectionOrdinal < 0 ||
            executionId == Guid.Empty ||
            nowUtc < this.UpdatedAtUtc)
        {
            return Result.Failure(
                GuestsDomainErrors.RetentionCheckpointConflict);
        }

        this.AfterProjectionOrdinal = nextAfterProjectionOrdinal;
        this.LastExecutionId = executionId;
        this.UpdatedAtUtc = nowUtc;
        this.Version++;
        return Result.Success();
    }

    public Result PrepareRetry(
        GuestRetentionExecution failedExecution,
        DateTimeOffset retryStartedAtUtc)
    {
        if (failedExecution.State !=
                GuestRetentionExecutionState.Failed ||
            !string.Equals(
                this.ScopeId,
                failedExecution.ScopeId,
                StringComparison.Ordinal) ||
            !string.Equals(
                this.DataClassKey,
                failedExecution.DataClassKey,
                StringComparison.Ordinal) ||
            !failedExecution.CompletedAtUtc.HasValue ||
            retryStartedAtUtc == default ||
            retryStartedAtUtc < failedExecution.CompletedAtUtc.Value)
        {
            return Result.Failure(
                GuestsDomainErrors.RetentionCheckpointConflict);
        }

        if (this.LastExecutionId == failedExecution.Id)
        {
            if (this.UpdatedAtUtc !=
                failedExecution.CompletedAtUtc.Value)
            {
                return Result.Failure(
                    GuestsDomainErrors.RetentionCheckpointConflict);
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
                GuestsDomainErrors.RetentionCheckpointConflict);
    }
}
