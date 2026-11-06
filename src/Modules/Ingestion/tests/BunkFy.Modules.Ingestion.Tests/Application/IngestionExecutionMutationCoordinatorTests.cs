namespace BunkFy.Modules.Ingestion.Tests.Application;

using System.Reflection;
using BunkFy.Adapter.Abstractions;
using BunkFy.Modules.Ingestion.Application.Adapters;
using BunkFy.Modules.Ingestion.Application.Commands;
using BunkFy.Modules.Ingestion.Application.Handlers;
using BunkFy.Modules.Ingestion.Application.Ports;
using BunkFy.Modules.Ingestion.Contracts;
using BunkFy.Modules.Ingestion.Domain.Connections;
using BunkFy.Modules.Ingestion.Domain.Errors;
using BunkFy.Modules.Ingestion.Domain.Runs;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Xunit;

[Trait("Category", "Unit")]
public sealed class IngestionExecutionMutationCoordinatorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 6, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Connection_write_lock_precedes_the_authoritative_reload()
    {
        AdapterConnection connection = CreateConnection();
        List<string> calls = [];
        RecordingConnectionRepository connections = new(connection, calls);
        RecordingExecutionLock executionLock = new(
            calls,
            connectionWriteAcquired: () => connections.IsVisible = false);
        IngestionExecutionMutationCoordinator coordinator = new(
            executionLock,
            connections,
            new RecordingRunRepository(null, calls),
            new TestScope());

        AdapterConnection? result = await coordinator.AcquireConnectionWriteAsync(
            connection.Id,
            CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(["connection-write-lock", "connection-reload"], calls);
    }

    [Fact]
    public async Task Read_fences_are_shared_and_reload_connection_and_run_after_locking()
    {
        AdapterConnection connection = CreateConnection();
        IngestionRun run = CreateRun(connection);
        List<string> calls = [];
        IngestionExecutionMutationCoordinator coordinator = new(
            new RecordingExecutionLock(calls),
            new RecordingConnectionRepository(connection, calls),
            new RecordingRunRepository(run, calls),
            new TestScope());

        AdapterConnection? loadedConnection =
            await coordinator.AcquireConnectionReadAsync(
                connection.Id,
                CancellationToken.None);
        IngestionRun? loadedRun = await coordinator.AcquireRunReadAsync(
            run.Id,
            CancellationToken.None);

        Assert.Same(connection, loadedConnection);
        Assert.Same(run, loadedRun);
        Assert.Equal(
            [
                "connection-read-lock",
                "connection-reload",
                "run-read-lock",
                "run-reload"
            ],
            calls);
    }

    [Fact]
    public async Task Task_run_start_uses_task_then_connection_coordinates()
    {
        AdapterConnection connection = CreateConnection();
        List<string> calls = [];
        RecordingRunRepository runs = new(null, calls);
        IngestionExecutionMutationCoordinator coordinator = new(
            new RecordingExecutionLock(calls),
            new RecordingConnectionRepository(connection, calls),
            runs,
            new TestScope());
        StartAdapterRunCommandHandler handler = new(
            coordinator,
            new TestCountryPolicyAdmission(),
            runs,
            new TestDescriptors(),
            new TestScope(),
            new TestIds(),
            new TestClock());

        var result = await handler.HandleAsync(
            new StartAdapterRunCommand(connection.Id, Guid.NewGuid(), 1),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.Equal(
            [
                "task-lock",
                "connection-write-lock",
                "connection-reload",
                "task-run-id-query",
                "active-run-id-query",
                "run-add"
            ],
            calls);
    }

    [Fact]
    public async Task Contended_connection_update_returns_domain_version_conflict()
    {
        AdapterConnection connection = CreateConnection();
        long submittedVersion = connection.Version;
        List<string> calls = [];
        RecordingConnectionRepository connections = new(connection, calls);
        RecordingExecutionLock executionLock = new(
            calls,
            connectionWriteAcquired: () =>
            {
                var concurrent = connection.Configure(
                    AdapterExecutionMode.Continuous,
                    IngestionConflictPolicy.SuggestionsOnly,
                    "configuration://winner",
                    null,
                    submittedVersion,
                    Now.AddMinutes(1));
                Assert.True(concurrent.IsSuccess);
            });
        UpdateAdapterConnectionCommandHandler handler = new(
            new IngestionExecutionMutationCoordinator(
                executionLock,
                connections,
                new RecordingRunRepository(null, calls),
                new TestScope()),
            new EmptyConnectionManagementOperationRepository(),
            new TestCountryPolicyAdmission(),
            new TestDescriptors(),
            new TestScope(),
            new TestClock());

        var result = await handler.HandleAsync(
            new UpdateAdapterConnectionCommand(
                Guid.NewGuid(),
                connection.PropertyId,
                connection.Id,
                AdapterExecutionMode.Polling,
                AdapterConflictPolicy.SuggestionsOnly,
                "configuration://operator",
                SecretReferenceUpdateMode.Keep,
                null,
                submittedVersion),
            CancellationToken.None);

        Assert.Equal(IngestionDomainErrors.VersionConflict, result.Error);
        Assert.Equal("configuration://winner", connection.ConfigurationReference);
        Assert.Equal(["connection-write-lock", "connection-reload"], calls);
    }

    [Theory]
    [InlineData(typeof(UpdateAdapterConnectionCommandHandler))]
    [InlineData(typeof(SetAdapterConnectionEnabledCommandHandler))]
    [InlineData(typeof(ConfigureAdapterConnectionPollingScheduleCommandHandler))]
    [InlineData(typeof(ClearAdapterConnectionPollingScheduleCommandHandler))]
    [InlineData(typeof(ResetAdapterConnectionCheckpointCommandHandler))]
    [InlineData(typeof(CreateAdapterIngressCredentialCommandHandler))]
    [InlineData(typeof(RevokeAdapterIngressCredentialCommandHandler))]
    [InlineData(typeof(StartAdapterRunCommandHandler))]
    [InlineData(typeof(CompleteAdapterRunCommandHandler))]
    [InlineData(typeof(AdvanceConnectionCheckpointCommandHandler))]
    [InlineData(typeof(ClaimRemoteAdapterLeaseCommandHandler))]
    [InlineData(typeof(RenewRemoteAdapterLeaseCommandHandler))]
    [InlineData(typeof(CompleteRemoteAdapterRunCommandHandler))]
    [InlineData(typeof(ReceiveObservationCommandHandler))]
    public void Execution_sensitive_paths_require_the_coordinator(Type handlerType)
    {
        bool hasCoordinator = handlerType
            .GetConstructors(
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic)
            .SelectMany(constructor => constructor.GetParameters())
            .Any(parameter => parameter.ParameterType ==
                typeof(IngestionExecutionMutationCoordinator));

        Assert.True(
            hasCoordinator,
            $"{handlerType.Name} must serialize through " +
            $"{nameof(IngestionExecutionMutationCoordinator)}.");
    }

    private static AdapterConnection CreateConnection() => AdapterConnection.Create(
        Guid.NewGuid(),
        "tenant-a",
        Guid.NewGuid(),
        "fake.http",
        AdapterExecutionMode.Polling,
        IngestionConflictPolicy.SuggestionsOnly,
        "configuration://main",
        null,
        Now).Value;

    private static IngestionRun CreateRun(AdapterConnection connection) =>
        IngestionRun.Start(
            Guid.NewGuid(),
            "tenant-a",
            connection.Id,
            connection.PropertyId,
            Guid.NewGuid(),
            1,
            null,
            Now).Value;

    private sealed class RecordingExecutionLock(
        List<string> calls,
        Action? connectionWriteAcquired = null)
        : IIngestionExecutionLock
    {
        public Task AcquireTaskExecutionAsync(
            string tenantId,
            Guid taskRunId,
            int taskAttempt,
            CancellationToken cancellationToken)
        {
            Assert.Equal("tenant-a", tenantId);
            calls.Add("task-lock");
            return Task.CompletedTask;
        }

        public Task AcquireRetentionExecutionAsync(
            string tenantId,
            Guid executionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AcquireConnectionReadAsync(
            string tenantId,
            Guid connectionId,
            CancellationToken cancellationToken)
        {
            Assert.Equal("tenant-a", tenantId);
            calls.Add("connection-read-lock");
            return Task.CompletedTask;
        }

        public Task AcquireConnectionWriteAsync(
            string tenantId,
            Guid connectionId,
            CancellationToken cancellationToken)
        {
            Assert.Equal("tenant-a", tenantId);
            calls.Add("connection-write-lock");
            connectionWriteAcquired?.Invoke();
            return Task.CompletedTask;
        }

        public Task AcquireRunReadAsync(
            string tenantId,
            Guid runId,
            CancellationToken cancellationToken)
        {
            Assert.Equal("tenant-a", tenantId);
            calls.Add("run-read-lock");
            return Task.CompletedTask;
        }

        public Task AcquireRunWriteAsync(
            string tenantId,
            Guid runId,
            CancellationToken cancellationToken)
        {
            Assert.Equal("tenant-a", tenantId);
            calls.Add("run-write-lock");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingConnectionRepository(
        AdapterConnection connection,
        List<string> calls)
        : IAdapterConnectionRepository
    {
        public bool IsVisible { get; set; } = true;

        public Task<AdapterConnection?> GetAsync(
            Guid connectionId,
            CancellationToken cancellationToken)
        {
            calls.Add("connection-reload");
            return Task.FromResult<AdapterConnection?>(
                this.IsVisible && connection.Id == connectionId
                    ? connection
                    : null);
        }

        public Task<AdapterConnection?> GetAsync(
            Guid propertyId,
            Guid connectionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddAsync(
            AdapterConnection value,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingRunRepository(
        IngestionRun? run,
        List<string> calls)
        : IIngestionRunRepository
    {
        public Task<IngestionRun?> GetAsync(
            Guid runId,
            CancellationToken cancellationToken)
        {
            calls.Add("run-reload");
            return Task.FromResult(run?.Id == runId ? run : null);
        }

        public Task<IngestionRun?> FindByTaskExecutionAsync(
            Guid taskRunId,
            int taskAttempt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Guid?> FindByTaskExecutionIdAsync(
            Guid taskRunId,
            int taskAttempt,
            CancellationToken cancellationToken)
        {
            calls.Add("task-run-id-query");
            return Task.FromResult<Guid?>(null);
        }

        public Task<IngestionRun?> FindActiveByConnectionAsync(
            Guid connectionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Guid?> FindActiveIdByConnectionAsync(
            Guid connectionId,
            CancellationToken cancellationToken)
        {
            calls.Add("active-run-id-query");
            return Task.FromResult<Guid?>(null);
        }

        public Task AddAsync(
            IngestionRun value,
            CancellationToken cancellationToken)
        {
            calls.Add("run-add");
            return Task.CompletedTask;
        }
    }

    private sealed class TestDescriptors : IAdapterDescriptorRegistry
    {
        private static readonly AdapterDescriptor Descriptor = new(
            "fake.http",
            1,
            1,
            [AdapterExecutionMode.Polling, AdapterExecutionMode.Continuous]);

        public IReadOnlyCollection<AdapterDescriptor> GetAll() => [Descriptor];

        public bool TryGet(
            string adapterType,
            out AdapterDescriptor? descriptor)
        {
            descriptor = string.Equals(
                adapterType,
                Descriptor.AdapterType,
                StringComparison.Ordinal)
                    ? Descriptor
                    : null;
            return descriptor is not null;
        }
    }

    private sealed class EmptyConnectionManagementOperationRepository
        : IIngestionConnectionManagementOperationRepository
    {
        public Task<IngestionConnectionManagementOperationRecord?> GetAsync(
            Guid connectionId,
            Guid operationId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IngestionConnectionManagementOperationRecord?>(null);

        public Task AddAsync(
            IngestionConnectionManagementOperationRecord operation,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "A version-conflicted update must not bind its operation id.");
    }

    private sealed class TestScope : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class TestIds : IIdGenerator
    {
        public Guid NewId() => Guid.NewGuid();
    }
}
