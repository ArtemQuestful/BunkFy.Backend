namespace BunkFy.Modules.Ingestion.Tests.Application;

using BunkFy.Modules.Ingestion.Application.Handlers;
using BunkFy.Modules.Ingestion.Application.Ports;
using BunkFy.Modules.Ingestion.Domain.Runs;
using Gma.Framework.Scoping;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

internal static class TestIngestionExecution
{
    public static IngestionExecutionMutationCoordinator Create(
        IAdapterConnectionRepository connections,
        IIngestionRunRepository? runs = null,
        IIngestionExecutionLock? executionLock = null,
        IScopeContext? scopeContext = null) => new(
            executionLock ?? new NoOpExecutionLock(),
            connections,
            runs ?? new EmptyRunRepository(),
            scopeContext ?? new TestScopeContext());

    public static IServiceCollection AddNoOpExecutionLock(
        this IServiceCollection services)
    {
        services.TryAddSingleton<IIngestionExecutionLock>(
            new NoOpExecutionLock());
        services.TryAddSingleton<IIngestionRunRepository>(
            new EmptyRunRepository());
        services.TryAddSingleton<IScopeContext>(new TestScopeContext());
        return services;
    }

    private sealed class NoOpExecutionLock : IIngestionExecutionLock
    {
        public Task AcquireTaskExecutionAsync(
            string tenantId,
            Guid taskRunId,
            int taskAttempt,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AcquireRetentionExecutionAsync(
            string tenantId,
            Guid executionId,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AcquireConnectionReadAsync(
            string tenantId,
            Guid connectionId,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AcquireConnectionWriteAsync(
            string tenantId,
            Guid connectionId,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AcquireRunReadAsync(
            string tenantId,
            Guid runId,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AcquireRunWriteAsync(
            string tenantId,
            Guid runId,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class EmptyRunRepository : IIngestionRunRepository
    {
        public Task<IngestionRun?> GetAsync(
            Guid runId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IngestionRun?>(null);

        public Task<IngestionRun?> FindByTaskExecutionAsync(
            Guid taskRunId,
            int taskAttempt,
            CancellationToken cancellationToken) =>
            Task.FromResult<IngestionRun?>(null);

        public Task<Guid?> FindByTaskExecutionIdAsync(
            Guid taskRunId,
            int taskAttempt,
            CancellationToken cancellationToken) =>
            Task.FromResult<Guid?>(null);

        public Task<IngestionRun?> FindActiveByConnectionAsync(
            Guid connectionId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IngestionRun?>(null);

        public Task<Guid?> FindActiveIdByConnectionAsync(
            Guid connectionId,
            CancellationToken cancellationToken) =>
            Task.FromResult<Guid?>(null);

        public Task AddAsync(
            IngestionRun run,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }
}
