namespace BunkFy.Modules.Ingestion.Application.Ports;

internal interface IIngestionExecutionLock
{
    Task AcquireTaskExecutionAsync(
        string tenantId,
        Guid taskRunId,
        int taskAttempt,
        CancellationToken cancellationToken);

    Task AcquireRetentionExecutionAsync(
        string tenantId,
        Guid executionId,
        CancellationToken cancellationToken);

    Task AcquireConnectionReadAsync(
        string tenantId,
        Guid connectionId,
        CancellationToken cancellationToken);

    Task AcquireConnectionWriteAsync(
        string tenantId,
        Guid connectionId,
        CancellationToken cancellationToken);

    Task AcquireRunReadAsync(
        string tenantId,
        Guid runId,
        CancellationToken cancellationToken);

    Task AcquireRunWriteAsync(
        string tenantId,
        Guid runId,
        CancellationToken cancellationToken);
}
