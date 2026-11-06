namespace BunkFy.Modules.Ingestion.Persistence;

using BunkFy.Modules.Ingestion.Application.Ports;
using Gma.Framework.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

internal sealed class IngestionExecutionLock(IngestionDbContext dbContext)
    : IIngestionExecutionLock
{
    private const string TaskExecutionPrefix =
        "bunkfy:ingestion:execution:task:";
    private const string RetentionExecutionPrefix =
        "bunkfy:ingestion:execution:retention:";
    private const string ConnectionPrefix =
        "bunkfy:ingestion:execution:connection:";
    private const string RunPrefix =
        "bunkfy:ingestion:execution:run:";

    public Task AcquireTaskExecutionAsync(
        string tenantId,
        Guid taskRunId,
        int taskAttempt,
        CancellationToken cancellationToken)
    {
        string scopeId = this.ValidateCoordinates(tenantId, taskRunId);
        if (taskAttempt <= 0)
        {
            throw new InvalidOperationException(
                "An Ingestion task-execution lock requires a valid attempt coordinate.");
        }

        return this.AcquireAsync(
            TaskExecutionPrefix + scopeId + ':' + taskRunId.ToString("N") + ':' + taskAttempt,
            EfTransactionKeyLockMode.Exclusive,
            cancellationToken);
    }

    public Task AcquireConnectionReadAsync(
        string tenantId,
        Guid connectionId,
        CancellationToken cancellationToken) => this.AcquireResourceAsync(
        ConnectionPrefix,
        tenantId,
        connectionId,
        EfTransactionKeyLockMode.Shared,
        cancellationToken);

    public Task AcquireRetentionExecutionAsync(
        string tenantId,
        Guid executionId,
        CancellationToken cancellationToken) => this.AcquireResourceAsync(
        RetentionExecutionPrefix,
        tenantId,
        executionId,
        EfTransactionKeyLockMode.Exclusive,
        cancellationToken);

    public Task AcquireConnectionWriteAsync(
        string tenantId,
        Guid connectionId,
        CancellationToken cancellationToken) => this.AcquireResourceAsync(
        ConnectionPrefix,
        tenantId,
        connectionId,
        EfTransactionKeyLockMode.Exclusive,
        cancellationToken);

    public Task AcquireRunReadAsync(
        string tenantId,
        Guid runId,
        CancellationToken cancellationToken) => this.AcquireResourceAsync(
        RunPrefix,
        tenantId,
        runId,
        EfTransactionKeyLockMode.Shared,
        cancellationToken);

    public Task AcquireRunWriteAsync(
        string tenantId,
        Guid runId,
        CancellationToken cancellationToken) => this.AcquireResourceAsync(
        RunPrefix,
        tenantId,
        runId,
        EfTransactionKeyLockMode.Exclusive,
        cancellationToken);

    private Task AcquireResourceAsync(
        string prefix,
        string tenantId,
        Guid resourceId,
        EfTransactionKeyLockMode mode,
        CancellationToken cancellationToken)
    {
        string scopeId = this.ValidateCoordinates(tenantId, resourceId);
        return this.AcquireAsync(
            prefix + scopeId + ':' + resourceId.ToString("N"),
            mode,
            cancellationToken);
    }

    private Task AcquireAsync(
        string resource,
        EfTransactionKeyLockMode mode,
        CancellationToken cancellationToken)
    {
        if (!dbContext.Database.IsRelational())
        {
            return Task.CompletedTask;
        }

        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "An Ingestion execution lock requires an active database transaction.");
        }

        return EfTransactionKeyLock.AcquireAsync(
            dbContext,
            resource,
            mode,
            cancellationToken);
    }

    private string ValidateCoordinates(string tenantId, Guid resourceId)
    {
        string scopeId = tenantId?.Trim() ?? string.Empty;
        if (scopeId.Length == 0 ||
            resourceId == Guid.Empty ||
            !string.Equals(
                scopeId,
                dbContext.CurrentScopeId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A scoped Ingestion execution lock requires valid coordinates.");
        }

        return scopeId;
    }
}
