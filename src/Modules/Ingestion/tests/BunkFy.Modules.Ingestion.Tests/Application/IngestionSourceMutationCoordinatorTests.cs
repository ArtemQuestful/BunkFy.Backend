namespace BunkFy.Modules.Ingestion.Tests.Application;

using System.Reflection;
using BunkFy.Adapter.Abstractions;
using BunkFy.Modules.Ingestion.Application;
using BunkFy.Modules.Ingestion.Application.Handlers;
using BunkFy.Modules.Ingestion.Application.Ports;
using BunkFy.Modules.Ingestion.Domain.Connections;
using Gma.Framework.Scoping;
using Xunit;

[Trait("Category", "Unit")]
public sealed class IngestionSourceMutationCoordinatorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Receipt_identity_is_discovered_before_connection_and_source_locking()
    {
        Guid connectionId = Guid.Parse(
            "00000000-0000-0000-0000-000000000001");
        Guid receiptId = Guid.Parse(
            "10000000-0000-0000-0000-000000000001");
        Guid sourceLinkId = Guid.Parse(
            "20000000-0000-0000-0000-000000000001");
        List<string> calls = [];
        IngestionSourceMutationCoordinator coordinator = CreateCoordinator(
            [CreateConnection(connectionId)],
            new(receiptId, connectionId, sourceLinkId),
            calls);

        IngestionSourceMutationLease? lease =
            await coordinator.AcquireReceiptAsync(
                receiptId,
                CancellationToken.None);

        Assert.NotNull(lease);
        Assert.Equal(sourceLinkId, lease.Coordinate.SourceLinkId);
        Assert.Equal(
            [
                $"locate:{receiptId:N}",
                $"connection-lock:{connectionId:N}",
                $"connection-reload:{connectionId:N}",
                $"source-lock:{sourceLinkId:N}"
            ],
            calls);
    }

    [Fact]
    public async Task Batch_locks_distinct_connections_then_sources_in_stable_order()
    {
        Guid connectionA = Guid.Parse(
            "00000000-0000-0000-0000-000000000001");
        Guid connectionB = Guid.Parse(
            "00000000-0000-0000-0000-000000000002");
        Guid sourceA = Guid.Parse(
            "10000000-0000-0000-0000-000000000001");
        Guid sourceB = Guid.Parse(
            "10000000-0000-0000-0000-000000000002");
        List<string> calls = [];
        IngestionSourceMutationCoordinator coordinator = CreateCoordinator(
            [CreateConnection(connectionB), CreateConnection(connectionA)],
            coordinate: null,
            calls);

        await coordinator.AcquireAllAsync(
            [
                new(Guid.NewGuid(), connectionB, sourceA),
                new(Guid.NewGuid(), connectionA, sourceB),
                new(Guid.NewGuid(), connectionA, sourceA)
            ],
            CancellationToken.None);

        Assert.Equal(
            [
                $"connection-lock:{connectionA:N}",
                $"connection-reload:{connectionA:N}",
                $"connection-lock:{connectionB:N}",
                $"connection-reload:{connectionB:N}",
                $"source-lock:{sourceA:N}",
                $"source-lock:{sourceB:N}"
            ],
            calls);
    }

    [Fact]
    public async Task Known_authoritative_connection_only_acquires_the_source_coordinate()
    {
        AdapterConnection connection = CreateConnection(Guid.Parse(
            "00000000-0000-0000-0000-000000000003"));
        List<string> calls = [];
        IngestionSourceMutationCoordinator coordinator = CreateCoordinator(
            [connection],
            coordinate: null,
            calls);

        IngestionSourceMutationLease lease =
            await coordinator.AcquireIdentityAsync(
                connection,
                "booking-42",
                CancellationToken.None);

        Guid expected = ReservationOperationIdentity.CreateSourceLinkId(
            "tenant-a",
            connection.Id,
            "booking-42");
        Assert.Equal(expected, lease.Coordinate.SourceLinkId);
        Assert.Equal([$"source-lock:{expected:N}"], calls);
    }

    [Fact]
    public async Task Missing_connection_fails_closed_before_the_source_lock()
    {
        Guid connectionId = Guid.Parse(
            "00000000-0000-0000-0000-000000000004");
        Guid sourceLinkId = Guid.Parse(
            "10000000-0000-0000-0000-000000000004");
        List<string> calls = [];
        IngestionSourceMutationCoordinator coordinator = CreateCoordinator(
            [],
            new(Guid.NewGuid(), connectionId, sourceLinkId),
            calls);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.AcquireReceiptAsync(
                Guid.NewGuid(),
                CancellationToken.None));

        Assert.DoesNotContain(
            calls,
            call => call.StartsWith("source-lock:", StringComparison.Ordinal));
    }

    [Fact]
    public void Source_graph_writers_require_the_shared_coordinator()
    {
        foreach (Type writerType in SourceWriterTypes)
        {
            bool hasCoordinator = writerType
                .GetConstructors(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic)
                .SelectMany(constructor => constructor.GetParameters())
                .Any(parameter => parameter.ParameterType ==
                    typeof(IngestionSourceMutationCoordinator));

            Assert.True(
                hasCoordinator,
                $"{writerType.Name} must serialize through " +
                $"{nameof(IngestionSourceMutationCoordinator)}.");
        }
    }

    private static readonly Type[] SourceWriterTypes =
    [
        typeof(ReceiveObservationCommandHandler),
        typeof(ObservationReceiptAcceptedHandler),
        typeof(DispatchNormalizedReservationObservationCommandHandler),
        typeof(AcceptChangeProposalCommandHandler),
        typeof(RejectChangeProposalCommandHandler),
        typeof(ReservationOperationOutcomeHandler),
        typeof(ReservationCancelledForIngestionHandler),
        typeof(PrepareObservationReprocessingCommandHandler),
        typeof(FailPreparedObservationReprocessingCommandHandler),
        typeof(StartObservationReprocessingCommandHandler),
        typeof(ScheduleObservationReprocessingRetryCommandHandler),
        typeof(CompleteObservationReprocessingCommandHandler),
        typeof(CancelObservationReprocessingCommandHandler),
        typeof(RecordObservationReprocessingOutputCommandHandler),
        typeof(ClaimExpiredRawPayloadsCommandHandler),
        typeof(CompleteRawPayloadPurgeCommandHandler),
        typeof(RedactExpiredSensitiveHistoryCommandHandler),
        typeof(BeginIngestionAnonymisationCommandHandler),
        typeof(CompleteIngestionAnonymisationCommandHandler),
        typeof(BeginIngestionAnonymisationRestoreCommandHandler),
        typeof(CompleteIngestionAnonymisationRestoreCommandHandler)
    ];

    private static IngestionSourceMutationCoordinator CreateCoordinator(
        IReadOnlyCollection<AdapterConnection> connections,
        IngestionSourceGraphCoordinate? coordinate,
        List<string> calls)
    {
        TestScope scope = new();
        RecordingConnectionRepository repository = new(connections, calls);
        return new(
            new RecordingLocator(coordinate, calls),
            new RecordingSourceLock(calls),
            TestIngestionExecution.Create(
                repository,
                executionLock: new RecordingExecutionLock(calls),
                scopeContext: scope),
            scope);
    }

    private static AdapterConnection CreateConnection(Guid id) =>
        AdapterConnection.Create(
            id,
            "tenant-a",
            Guid.NewGuid(),
            "fake.http",
            AdapterExecutionMode.Polling,
            IngestionConflictPolicy.SuggestionsOnly,
            "configuration://main",
            null,
            Now).Value;

    private sealed class RecordingLocator(
        IngestionSourceGraphCoordinate? coordinate,
        List<string> calls)
        : IIngestionSourceGraphLocator
    {
        public Task<IngestionSourceGraphCoordinate?> FindReceiptAsync(
            Guid receiptId,
            CancellationToken cancellationToken)
        {
            calls.Add($"locate:{receiptId:N}");
            return Task.FromResult(coordinate);
        }

        public Task<IngestionSourceGraphCoordinate?> FindProposalAsync(
            Guid proposalId,
            CancellationToken cancellationToken) =>
            Task.FromResult(coordinate);

        public Task<IngestionSourceGraphCoordinate?> FindDispatchAsync(
            Guid dispatchId,
            CancellationToken cancellationToken) =>
            Task.FromResult(coordinate);

        public Task<IngestionSourceGraphCoordinate?>
            FindReprocessingAttemptAsync(
            Guid attemptId,
            CancellationToken cancellationToken) =>
            Task.FromResult(coordinate);

        public Task<IngestionSourceGraphCoordinate?> FindSourceLinkAsync(
            Guid sourceLinkId,
            CancellationToken cancellationToken) =>
            Task.FromResult(coordinate);

        public Task<IngestionSourceGraphCoordinate?>
            FindAcceptedCancellationAsync(
            Guid reservationId,
            CancellationToken cancellationToken) =>
            Task.FromResult(coordinate);
    }

    private sealed class RecordingSourceLock(List<string> calls)
        : IIngestionSourceOperationLock
    {
        public Task AcquireAsync(
            string tenantId,
            Guid sourceLinkId,
            CancellationToken cancellationToken)
        {
            Assert.Equal("tenant-a", tenantId);
            calls.Add($"source-lock:{sourceLinkId:N}");
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingExecutionLock(List<string> calls)
        : IIngestionExecutionLock
    {
        public Task AcquireConnectionReadAsync(
            string tenantId,
            Guid connectionId,
            CancellationToken cancellationToken)
        {
            Assert.Equal("tenant-a", tenantId);
            calls.Add($"connection-lock:{connectionId:N}");
            return Task.CompletedTask;
        }

        public Task AcquireTaskExecutionAsync(
            string tenantId,
            Guid taskRunId,
            int taskAttempt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AcquireRetentionExecutionAsync(
            string tenantId,
            Guid executionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AcquireConnectionWriteAsync(
            string tenantId,
            Guid connectionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AcquireRunReadAsync(
            string tenantId,
            Guid runId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AcquireRunWriteAsync(
            string tenantId,
            Guid runId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingConnectionRepository(
        IReadOnlyCollection<AdapterConnection> connections,
        List<string> calls)
        : IAdapterConnectionRepository
    {
        private readonly IReadOnlyDictionary<Guid, AdapterConnection> byId =
            connections.ToDictionary(connection => connection.Id);

        public Task<AdapterConnection?> GetAsync(
            Guid connectionId,
            CancellationToken cancellationToken)
        {
            calls.Add($"connection-reload:{connectionId:N}");
            return Task.FromResult(
                this.byId.GetValueOrDefault(connectionId));
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

    private sealed class TestScope : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }
}
