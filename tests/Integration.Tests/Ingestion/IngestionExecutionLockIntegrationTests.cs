namespace Integration.Tests;

using BunkFy.Adapter.Abstractions;
using BunkFy.DataGovernance;
using BunkFy.Modules.Ingestion.Application;
using BunkFy.Modules.Ingestion.Application.Adapters;
using BunkFy.Modules.Ingestion.Application.Commands;
using BunkFy.Modules.Ingestion.Application.Handlers;
using BunkFy.Modules.Ingestion.Application.Ports;
using BunkFy.Modules.Ingestion.Contracts;
using BunkFy.Modules.Ingestion.Domain.Connections;
using BunkFy.Modules.Ingestion.Domain.Errors;
using BunkFy.Modules.Ingestion.Domain.Reservations;
using BunkFy.Modules.Ingestion.Persistence;
using BunkFy.Modules.Ingestion.Persistence.Repositories;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class IngestionExecutionLockIntegrationTests
{
    private const string TenantId = "tenant-a";
    private static readonly DateTimeOffset Now =
        new(2026, 8, 6, 11, 0, 0, TimeSpan.Zero);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Execution_locks_serialize_writers_without_serializing_fan_in_or_unrelated_adapters()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_ingestion_execution_lock_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);
        string connectionString = postgreSql.GetConnectionString();
        AdapterConnection connection = CreateConnection();

        await using (IngestionDbContext seed = CreateDbContext(connectionString))
        {
            await seed.Database.MigrateAsync().ConfigureAwait(false);
            seed.AdapterConnections.Add(connection);
            await seed.SaveChangesAsync().ConfigureAwait(false);
        }

        await ProveWaitingWriterReloadsAuthoritativeStateAsync(
            connectionString,
            connection).ConfigureAwait(false);
        await ProveSharedReadsAndCoordinateIsolationAsync(
            connectionString,
            connection.Id).ConfigureAwait(false);
        await ProveRetentionExecutionSerializationAsync(connectionString)
            .ConfigureAwait(false);
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Source_graph_locks_serialize_same_source_and_reload_the_committed_winner()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_ingestion_source_lock_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);
        string connectionString = postgreSql.GetConnectionString();
        AdapterConnection connection = CreateConnection();
        ReservationSourceLink source = CreateSourceLink(
            connection,
            "booking-123");
        ReservationSourceLink unrelatedSource = CreateSourceLink(
            connection,
            "booking-456");

        await using (IngestionDbContext seed = CreateDbContext(connectionString))
        {
            await seed.Database.MigrateAsync().ConfigureAwait(false);
            seed.AdapterConnections.Add(connection);
            seed.ReservationSourceLinks.AddRange(source, unrelatedSource);
            await seed.SaveChangesAsync().ConfigureAwait(false);
        }

        await using IngestionDbContext firstDb = CreateDbContext(connectionString);
        await using IngestionDbContext waitingDb = CreateDbContext(connectionString);
        await using IDbContextTransaction firstTransaction =
            await firstDb.Database.BeginTransactionAsync().ConfigureAwait(false);
        await using IDbContextTransaction waitingTransaction =
            await waitingDb.Database.BeginTransactionAsync().ConfigureAwait(false);
        IngestionSourceMutationCoordinator first =
            CreateSourceMutationCoordinator(firstDb);
        IngestionSourceMutationCoordinator waiting =
            CreateSourceMutationCoordinator(waitingDb);

        IngestionSourceMutationLease? firstLease = await first
            .AcquireSourceLinkAsync(source.Id, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.NotNull(firstLease);
        ReservationSourceLink firstSource = await firstDb.ReservationSourceLinks
            .SingleAsync(item => item.Id == source.Id)
            .ConfigureAwait(false);
        var observed = firstSource.Observe(
            Guid.NewGuid(),
            "2",
            2,
            Now,
            new string('a', ReservationSourceLink.ContentHashLength),
            Now.AddMinutes(1));
        Assert.True(observed.IsSuccess, observed.Error.Code);
        await firstDb.SaveChangesAsync().ConfigureAwait(false);

        Task<IngestionSourceMutationLease?> waitingAcquisition = waiting
            .AcquireSourceLinkAsync(source.Id, CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
        Assert.False(waitingAcquisition.IsCompleted);

        await using (IngestionDbContext unrelatedDb =
            CreateDbContext(connectionString))
        await using (IDbContextTransaction unrelatedTransaction =
            await unrelatedDb.Database.BeginTransactionAsync()
                .ConfigureAwait(false))
        {
            IngestionSourceMutationLease? unrelatedLease =
                await CreateSourceMutationCoordinator(unrelatedDb)
                    .AcquireSourceLinkAsync(
                        unrelatedSource.Id,
                        CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(2))
                    .ConfigureAwait(false);
            Assert.NotNull(unrelatedLease);
            await unrelatedTransaction.CommitAsync().ConfigureAwait(false);
        }

        await firstTransaction.CommitAsync().ConfigureAwait(false);
        IngestionSourceMutationLease? waitingLease = await waitingAcquisition
            .WaitAsync(TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);
        Assert.NotNull(waitingLease);
        ReservationSourceLink reloaded = await waitingDb.ReservationSourceLinks
            .SingleAsync(item => item.Id == source.Id)
            .ConfigureAwait(false);
        Assert.Equal(2, reloaded.Version);
        Assert.Equal("2", reloaded.LastObservedSourceRevision);
        Assert.Equal(
            new string('a', ReservationSourceLink.ContentHashLength),
            reloaded.LastObservedContentHash);
        await waitingTransaction.CommitAsync().ConfigureAwait(false);

        await using IngestionDbContext verification =
            CreateDbContext(connectionString);
        int operationLockTableCount = await verification.Database
            .SqlQueryRaw<int>(
                """
                SELECT COUNT(*)::int AS "Value"
                FROM information_schema.tables
                WHERE table_schema = 'ingestion'
                  AND table_name = 'source_operation_locks'
                """)
            .SingleAsync()
            .ConfigureAwait(false);
        Assert.Equal(0, operationLockTableCount);
    }

    private static async Task ProveWaitingWriterReloadsAuthoritativeStateAsync(
        string connectionString,
        AdapterConnection seeded)
    {
        await using IngestionDbContext firstDb = CreateDbContext(connectionString);
        await using IngestionDbContext secondDb = CreateDbContext(connectionString);
        await using IDbContextTransaction firstTransaction =
            await firstDb.Database.BeginTransactionAsync().ConfigureAwait(false);
        await using IDbContextTransaction secondTransaction =
            await secondDb.Database.BeginTransactionAsync().ConfigureAwait(false);
        UpdateAdapterConnectionCommandHandler first = CreateUpdateHandler(firstDb);
        UpdateAdapterConnectionCommandHandler second = CreateUpdateHandler(secondDb);

        var firstResult = await first.HandleAsync(
            CreateUpdate(
                seeded,
                "configuration://winner",
                seeded.Version),
            CancellationToken.None).ConfigureAwait(false);
        Assert.True(firstResult.IsSuccess, firstResult.Error.Code);
        await firstDb.SaveChangesAsync().ConfigureAwait(false);

        Task<Gma.Framework.Results.Result<AdapterConnectionMutationReceiptDto>>
            waitingUpdate = second.HandleAsync(
                CreateUpdate(
                    seeded,
                    "configuration://waiting-operator",
                    seeded.Version),
                CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
        Assert.False(waitingUpdate.IsCompleted);

        await using (IngestionDbContext unrelatedDb = CreateDbContext(connectionString))
        await using (IDbContextTransaction unrelatedTransaction =
            await unrelatedDb.Database.BeginTransactionAsync().ConfigureAwait(false))
        {
            IngestionExecutionLock unrelatedLock = new(unrelatedDb);
            await unrelatedLock.AcquireConnectionWriteAsync(
                TenantId,
                Guid.NewGuid(),
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            await unrelatedTransaction.CommitAsync().ConfigureAwait(false);
        }

        await firstTransaction.CommitAsync().ConfigureAwait(false);
        var secondResult = await waitingUpdate
            .WaitAsync(TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);
        Assert.Equal(IngestionDomainErrors.VersionConflict, secondResult.Error);
        await secondTransaction.CommitAsync().ConfigureAwait(false);

        await using IngestionDbContext verification =
            CreateDbContext(connectionString);
        AdapterConnection persisted = await verification.AdapterConnections
            .SingleAsync(item => item.Id == seeded.Id)
            .ConfigureAwait(false);
        Assert.Equal("configuration://winner", persisted.ConfigurationReference);
        Assert.Equal(seeded.Version + 1, persisted.Version);
    }

    private static async Task ProveSharedReadsAndCoordinateIsolationAsync(
        string connectionString,
        Guid connectionId)
    {
        await using IngestionDbContext firstReaderDb =
            CreateDbContext(connectionString);
        await using IngestionDbContext secondReaderDb =
            CreateDbContext(connectionString);
        await using IngestionDbContext writerDb =
            CreateDbContext(connectionString);
        await using IDbContextTransaction firstReaderTransaction =
            await firstReaderDb.Database.BeginTransactionAsync().ConfigureAwait(false);
        await using IDbContextTransaction secondReaderTransaction =
            await secondReaderDb.Database.BeginTransactionAsync().ConfigureAwait(false);
        await using IDbContextTransaction writerTransaction =
            await writerDb.Database.BeginTransactionAsync().ConfigureAwait(false);
        IngestionExecutionLock firstReader = new(firstReaderDb);
        IngestionExecutionLock secondReader = new(secondReaderDb);
        IngestionExecutionLock writer = new(writerDb);

        await firstReader.AcquireRunReadAsync(
            TenantId,
            connectionId,
            CancellationToken.None).ConfigureAwait(false);
        await secondReader.AcquireRunReadAsync(
            TenantId,
            connectionId,
            CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        Task waitingWriter = writer.AcquireRunWriteAsync(
            TenantId,
            connectionId,
            CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
        Assert.False(waitingWriter.IsCompleted);

        await firstReaderTransaction.CommitAsync().ConfigureAwait(false);
        await secondReaderTransaction.CommitAsync().ConfigureAwait(false);
        await waitingWriter.WaitAsync(TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);
        await writerTransaction.CommitAsync().ConfigureAwait(false);
    }

    private static async Task ProveRetentionExecutionSerializationAsync(
        string connectionString)
    {
        Guid executionId = Guid.NewGuid();
        await using IngestionDbContext firstDb = CreateDbContext(connectionString);
        await using IngestionDbContext waitingDb = CreateDbContext(connectionString);
        await using IDbContextTransaction firstTransaction =
            await firstDb.Database.BeginTransactionAsync().ConfigureAwait(false);
        await using IDbContextTransaction waitingTransaction =
            await waitingDb.Database.BeginTransactionAsync().ConfigureAwait(false);
        IngestionExecutionLock first = new(firstDb);
        IngestionExecutionLock waiting = new(waitingDb);

        await first.AcquireRetentionExecutionAsync(
            TenantId,
            executionId,
            CancellationToken.None).ConfigureAwait(false);
        Task waitingAcquisition = waiting.AcquireRetentionExecutionAsync(
            TenantId,
            executionId,
            CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
        Assert.False(waitingAcquisition.IsCompleted);

        await using (IngestionDbContext unrelatedDb =
            CreateDbContext(connectionString))
        await using (IDbContextTransaction unrelatedTransaction =
            await unrelatedDb.Database.BeginTransactionAsync()
                .ConfigureAwait(false))
        {
            await new IngestionExecutionLock(unrelatedDb)
                .AcquireRetentionExecutionAsync(
                    TenantId,
                    Guid.NewGuid(),
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
            await unrelatedTransaction.CommitAsync().ConfigureAwait(false);
        }

        await firstTransaction.CommitAsync().ConfigureAwait(false);
        await waitingAcquisition.WaitAsync(TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);
        await waitingTransaction.CommitAsync().ConfigureAwait(false);
    }

    private static UpdateAdapterConnectionCommandHandler CreateUpdateHandler(
        IngestionDbContext dbContext)
    {
        AdapterConnectionRepository connections = new(dbContext);
        IngestionRunRepository runs = new(dbContext);
        IngestionExecutionMutationCoordinator coordinator = new(
            new IngestionExecutionLock(dbContext),
            connections,
            runs,
            new TestScope());
        return new UpdateAdapterConnectionCommandHandler(
            coordinator,
            new IngestionConnectionManagementOperationRepository(dbContext),
            new AllowCountryPolicyAdmission(),
            new TestDescriptors(),
            new TestScope(),
            new TestClock());
    }

    private static IngestionSourceMutationCoordinator
        CreateSourceMutationCoordinator(IngestionDbContext dbContext)
    {
        AdapterConnectionRepository connections = new(dbContext);
        IngestionRunRepository runs = new(dbContext);
        IngestionExecutionMutationCoordinator execution = new(
            new IngestionExecutionLock(dbContext),
            connections,
            runs,
            new TestScope());
        return new(
            new IngestionSourceGraphLocator(dbContext),
            new IngestionSourceGraphLock(dbContext),
            execution,
            new TestScope());
    }

    private static UpdateAdapterConnectionCommand CreateUpdate(
        AdapterConnection connection,
        string configurationReference,
        long expectedVersion) => new(
            Guid.NewGuid(),
            connection.PropertyId,
            connection.Id,
            AdapterExecutionMode.Continuous,
            AdapterConflictPolicy.SuggestionsOnly,
            configurationReference,
            SecretReferenceUpdateMode.Keep,
            null,
            expectedVersion);

    private static AdapterConnection CreateConnection() => AdapterConnection.Create(
        Guid.NewGuid(),
        TenantId,
        Guid.NewGuid(),
        "fake.http",
        AdapterExecutionMode.Polling,
        IngestionConflictPolicy.SuggestionsOnly,
        "configuration://initial",
        null,
        Now).Value;

    private static ReservationSourceLink CreateSourceLink(
        AdapterConnection connection,
        string sourceReference) => ReservationSourceLink.Create(
        ReservationOperationIdentity.CreateSourceLinkId(
            TenantId,
            connection.Id,
            sourceReference),
        TenantId,
        connection.PropertyId,
        connection.Id,
        connection.AdapterType,
        sourceReference,
        Now).Value;

    private static IngestionDbContext CreateDbContext(
        string connectionString) => new(
            new DbContextOptionsBuilder<IngestionDbContext>()
                .UseNpgsql(
                    connectionString,
                    options => options.MigrationsAssembly(
                        IngestionMigrations.PostgreSqlAssembly))
                .Options,
            new TestScope(),
            new OpenTerminationFence());

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

    private sealed class TestScope : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => TenantId;
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now.AddMinutes(1);
    }

    private sealed class AllowCountryPolicyAdmission
        : IIngestionCountryPolicyAdmission
    {
        public Task<CountryPolicyDecision> EvaluateAsync(
            Guid propertyId,
            string purposeCode,
            CountryPolicySurface surface,
            string sourceProvenance,
            CancellationToken cancellationToken) => Task.FromResult(
            CountryPolicyDecision.Allow(new CountryPolicyEvidence(
                "GB",
                "gb-hostel",
                1,
                "eu-west",
                "none",
                "guest-standard",
                1,
                new string('a', 64),
                purposeCode,
                surface,
                sourceProvenance,
                CountryPolicyApprovalState.Approved,
                Now.AddDays(-1),
                Now.AddDays(30),
                Now,
                [])));
    }

    private sealed class OpenTerminationFence : IWorkspaceTerminationFenceReader
    {
        public Task<WorkspaceTerminationFenceSnapshot?> GetCurrentAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<WorkspaceTerminationFenceSnapshot?>(null);
    }
}
