namespace BunkFy.Modules.Ingestion.Application.Handlers;

using BunkFy.Modules.Ingestion.Application.Contributors;
using BunkFy.Modules.Ingestion.Application.Ports;
using BunkFy.Modules.Ingestion.Domain.Retention;
using Gma.Framework.Results;
using Gma.Framework.Scoping;

internal sealed record IngestionRetentionExecutionLease(
    IngestionRetentionExecution? Execution);

internal sealed class IngestionRetentionMutationCoordinator(
    IIngestionExecutionLock executionLock,
    IIngestionRetentionExecutionRepository executions,
    IScopeContext scopeContext)
{
    public async Task<Result<IngestionRetentionExecutionLease>> AcquireAsync(
        Guid executionId,
        CancellationToken cancellationToken)
    {
        string tenantId = this.GetTenantId();
        if (tenantId.Length == 0 || executionId == Guid.Empty)
        {
            return Result.Failure<IngestionRetentionExecutionLease>(
                IngestionRetentionExecutionErrors.CoordinateInvalid);
        }

        await executionLock.AcquireRetentionExecutionAsync(
            tenantId,
            executionId,
            cancellationToken).ConfigureAwait(false);
        IngestionRetentionExecution? execution = await executions.GetAsync(
            executionId,
            cancellationToken).ConfigureAwait(false);
        if (execution is not null &&
            !string.Equals(execution.ScopeId, tenantId, StringComparison.Ordinal))
        {
            return Result.Failure<IngestionRetentionExecutionLease>(
                IngestionRetentionExecutionErrors.CoordinateInvalid);
        }

        return Result.Success(new IngestionRetentionExecutionLease(execution));
    }

    public async Task<Result<IngestionRetentionExecutionLease>> AcquireRunningAsync(
        Guid? executionId,
        int? attempt,
        string dataClassKey,
        CancellationToken cancellationToken)
    {
        if (executionId is null && attempt is null)
        {
            return Result.Success(new IngestionRetentionExecutionLease(null));
        }

        if (executionId is not { } id ||
            id == Guid.Empty ||
            attempt is not > 0)
        {
            return Result.Failure<IngestionRetentionExecutionLease>(
                IngestionRetentionExecutionErrors.CoordinateInvalid);
        }

        Result<IngestionRetentionExecutionLease> acquired = await this.AcquireAsync(
            id,
            cancellationToken).ConfigureAwait(false);
        if (acquired.IsFailure)
        {
            return acquired;
        }

        IngestionRetentionExecution? execution = acquired.Value.Execution;
        if (execution is null)
        {
            return Result.Failure<IngestionRetentionExecutionLease>(
                IngestionApplicationErrors.RetentionExecutionNotFound);
        }

        return execution.State == IngestionRetentionExecutionState.Running &&
            execution.Attempt == attempt.Value &&
            execution.MatchesCoordinate(
                dataClassKey,
                IngestionRetentionCoordinates.ExecutionPolicyVersion)
                ? acquired
                : Result.Failure<IngestionRetentionExecutionLease>(
                    IngestionRetentionExecutionErrors.CoordinateInvalid);
    }

    public async Task<Result<IngestionRetentionExecution>> AcquireAttemptAsync(
        Guid executionId,
        int attempt,
        CancellationToken cancellationToken)
    {
        if (attempt <= 0)
        {
            return Result.Failure<IngestionRetentionExecution>(
                IngestionRetentionExecutionErrors.CoordinateInvalid);
        }

        Result<IngestionRetentionExecutionLease> acquired = await this.AcquireAsync(
            executionId,
            cancellationToken).ConfigureAwait(false);
        if (acquired.IsFailure)
        {
            return Result.Failure<IngestionRetentionExecution>(acquired.Error);
        }

        IngestionRetentionExecution? execution = acquired.Value.Execution;
        if (execution is null)
        {
            return Result.Failure<IngestionRetentionExecution>(
                IngestionApplicationErrors.RetentionExecutionNotFound);
        }

        return execution.Attempt == attempt
            ? Result.Success(execution)
            : Result.Failure<IngestionRetentionExecution>(
                IngestionRetentionExecutionErrors.CoordinateInvalid);
    }

    private string GetTenantId() => scopeContext.IsEnabled
        ? scopeContext.ScopeId?.Trim() ?? string.Empty
        : string.Empty;
}
