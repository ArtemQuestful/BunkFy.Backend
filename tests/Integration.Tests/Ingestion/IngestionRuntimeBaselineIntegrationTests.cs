namespace Integration.Tests;

using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using BunkFy.Adapter.Abstractions;
using BunkFy.Adapters.FakeHttp;
using BunkFy.Modules.Ingestion.Application.Commands;
using BunkFy.Modules.Ingestion.Contracts;
using BunkFy.Modules.Ingestion.Domain.Connections;
using BunkFy.Modules.Ingestion.Persistence;
using BunkFy.Modules.Properties.Contracts;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Gma.Framework.Cqrs;
using Gma.Framework.Messaging;
using Gma.Framework.Messaging.Nats;
using Gma.Framework.Results;
using Gma.Framework.Tenancy;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Minio;
using Minio.DataModel.Args;
using NATS.Client.Core;
using NATS.Client.JetStream;
using Testcontainers.PostgreSql;
using Xunit;
using Xunit.Abstractions;

public sealed class IngestionRuntimeBaselineIntegrationTests(ITestOutputHelper output)
{
    private const string AccessKey = "minioadmin";
    private const string SecretKey = "minioadmin";
    private const int BurstSize = 24;

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    [Trait("Category", "IngestionRuntime")]
    public async Task Multi_tenant_burst_recovers_from_storage_and_broker_outages_with_bounded_growth()
    {
        await using IContainer nats = AuthTestContainers.CreateNatsContainer();
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("bunkfy_ingestion_runtime_baseline")
            .Build();
        await using IContainer minio = ProviderTestContainers.Minio()
            .WithEnvironment("MINIO_ROOT_USER", AccessKey)
            .WithEnvironment("MINIO_ROOT_PASSWORD", SecretKey)
            .WithPortBinding(9000, assignRandomHostPort: true)
            .WithCommand("server", "/data", "--console-address", ":9001")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(9000))
            .Build();

        await Task.WhenAll(nats.StartAsync(), postgreSql.StartAsync(), minio.StartAsync()).ConfigureAwait(false);
        string natsConnectionString = AuthTestContainers.GetNatsConnectionString(nats);
        string minioEndpoint = $"localhost:{minio.GetMappedPublicPort(9000)}";
        string bucketName = $"bunkfy-runtime-{Guid.NewGuid():N}";
        ScopeSeed firstScope = ScopeSeed.Create("runtime-a");
        ScopeSeed secondScope = ScopeSeed.Create("runtime-b");

        await using AuthTestApplication api = new(
            "PostgreSql",
            postgreSql.GetConnectionString(),
            natsConnectionString,
            disableOutboxPublisher: false,
            minioEndpoint: minioEndpoint,
            minioAccessKey: AccessKey,
            minioSecretKey: SecretKey,
            minioBucketName: bucketName,
            minioCreateBucketIfMissing: true);
        await api.MigrateGuestRecordsAuthorizationDatabaseAsync().ConfigureAwait(false);
        await api.MigrateStaffAuthorizationDatabaseAsync().ConfigureAwait(false);
        await api.MigrateIngestionDatabaseAsync().ConfigureAwait(false);
        await SeedScopeAsync(api, firstScope).ConfigureAwait(false);
        await SeedScopeAsync(api, secondScope).ConfigureAwait(false);

        Guid storageRecoveryOperationId = Guid.NewGuid();
        byte[] storageRecoveryPayload = Payload("storage-recovery", 0);
        await minio.PauseAsync().ConfigureAwait(false);
        using (CancellationTokenSource storageTimeout = new(TimeSpan.FromSeconds(5)))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => SubmitAsync(
                api,
                firstScope,
                storageRecoveryOperationId,
                "storage-recovery",
                storageRecoveryPayload,
                storageTimeout.Token)).ConfigureAwait(false);
        }

        Assert.Equal(0, await CountReceiptsAsync(api, firstScope.TenantId).ConfigureAwait(false));
        await minio.UnpauseAsync().ConfigureAwait(false);
        TimedObservation storageRecovery = await SubmitTimedAsync(
            api,
            firstScope,
            storageRecoveryOperationId,
            "storage-recovery",
            storageRecoveryPayload).ConfigureAwait(false);
        Assert.Equal(AdapterObservationDisposition.Accepted, storageRecovery.Result.Disposition);
        await WaitForOutboxDrainAsync(api, TimeSpan.FromSeconds(30)).ConfigureAwait(false);

        await nats.PauseAsync().ConfigureAwait(false);
        DateTimeOffset burstStartedAtUtc = DateTimeOffset.UtcNow;
        TimedObservation[] accepted = await Task.WhenAll(Enumerable.Range(0, BurstSize).Select(index =>
        {
            ScopeSeed scope = index % 2 == 0 ? firstScope : secondScope;
            return SubmitTimedAsync(api, scope, Guid.NewGuid(), index.ToString(CultureInfo.InvariantCulture), Payload("burst", index));
        })).ConfigureAwait(false);

        Assert.All(accepted, observation =>
            Assert.Equal(AdapterObservationDisposition.Accepted, observation.Result.Disposition));
        await WaitForOutboxBacklogAsync(api, BurstSize, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        OutboxBacklog backlog = await ReadOutboxBacklogAsync(api).ConfigureAwait(false);
        Assert.True(backlog.Count >= BurstSize);

        TimedObservation[] duplicates = await Task.WhenAll(accepted.Take(8).Select(observation =>
            SubmitTimedAsync(
                api,
                observation.Scope,
                observation.OperationId,
                observation.SourceRevision,
                observation.Payload))).ConfigureAwait(false);
        Assert.All(duplicates, observation =>
            Assert.Equal(AdapterObservationDisposition.Duplicate, observation.Result.Disposition));

        TimedObservation original = accepted[0];
        Result<AdapterObservationResult> conflictingOperation = await DispatchAsync(
            api,
            original.Scope,
            original.OperationId,
            original.SourceRevision,
            Payload("conflict", 999),
            CancellationToken.None).ConfigureAwait(false);
        Assert.True(conflictingOperation.IsFailure);
        Assert.Equal("Ingestion.OperationIdentityConflict", conflictingOperation.Error.Code);

        Assert.Equal((BurstSize / 2) + 1, await CountReceiptsAsync(api, firstScope.TenantId).ConfigureAwait(false));
        Assert.Equal(BurstSize / 2, await CountReceiptsAsync(api, secondScope.TenantId).ConfigureAwait(false));

        await nats.UnpauseAsync().ConfigureAwait(false);
        TimeSpan drainDuration = await WaitForOutboxDrainAsync(api, TimeSpan.FromSeconds(45)).ConfigureAwait(false);
        DatabaseGrowth database = await ReadDatabaseGrowthAsync(api).ConfigureAwait(false);
        ObjectStorageGrowth objectStorage = await ReadObjectStorageGrowthAsync(api, bucketName).ConfigureAwait(false);
        BrokerGrowth broker = await ReadBrokerGrowthAsync(api).ConfigureAwait(false);

        int acceptedObjectCount = BurstSize + 1;
        Assert.Equal(acceptedObjectCount, objectStorage.ObjectCount);
        Assert.True(objectStorage.RetainedBytes >= acceptedObjectCount);
        Assert.True(broker.Messages >= acceptedObjectCount);
        Assert.True(database.TableBytes > 0);
        Assert.True(database.IndexBytes > 0);

        double[] acceptanceMilliseconds = accepted
            .Select(item => item.Elapsed.TotalMilliseconds)
            .OrderBy(value => value)
            .ToArray();
        double[] duplicateMilliseconds = duplicates
            .Select(item => item.Elapsed.TotalMilliseconds)
            .OrderBy(value => value)
            .ToArray();
        RuntimeBaseline baseline = new(
            BurstSize,
            TenantCount: 2,
            AcceptanceP50Milliseconds: Percentile(acceptanceMilliseconds, 0.50),
            AcceptanceP95Milliseconds: Percentile(acceptanceMilliseconds, 0.95),
            AcceptanceMaxMilliseconds: acceptanceMilliseconds[^1],
            DuplicateP95Milliseconds: Percentile(duplicateMilliseconds, 0.95),
            StorageRecoveryMilliseconds: storageRecovery.Elapsed.TotalMilliseconds,
            BrokerBacklogCount: backlog.Count,
            BrokerBacklogOldestAgeMilliseconds: backlog.OldestCreatedAtUtc is null
                ? 0
                : (burstStartedAtUtc - backlog.OldestCreatedAtUtc.Value).Duration().TotalMilliseconds,
            BrokerDrainMilliseconds: drainDuration.TotalMilliseconds,
            PostgreSqlReceiptTableBytes: database.TableBytes,
            PostgreSqlReceiptIndexBytes: database.IndexBytes,
            MinioObjectCount: objectStorage.ObjectCount,
            MinioRetainedBytes: objectStorage.RetainedBytes,
            JetStreamMessages: broker.Messages,
            JetStreamBytes: broker.Bytes);
        output.WriteLine($"runtime-baseline={JsonSerializer.Serialize(baseline)}");
    }

    private static async Task SeedScopeAsync(AuthTestApplication api, ScopeSeed seed)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(seed.TenantId);
        IngestionDbContext dbContext = scope.ServiceProvider.GetRequiredService<IngestionDbContext>();
        IIntegrationEventHandler<PropertyCreatedIntegrationEvent> handler =
            ResolveHandler<PropertyCreatedIntegrationEvent>(scope.ServiceProvider, IngestionModuleMetadata.Name);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await ModuleTransactionIntegrationTestData.ExecuteAsync(
            dbContext,
            token => handler.HandleAsync(
                new PropertyCreatedIntegrationEvent(
                    Guid.NewGuid(),
                    seed.TenantId,
                    now,
                    seed.PropertyId,
                    $"Runtime {seed.TenantId}",
                    seed.TenantId,
                    "UTC",
                    PropertyStatus.Active,
                    1),
                token)).ConfigureAwait(false);
        await CountryPolicyIntegrationTestData.ApplyActivationAsync(
            scope.ServiceProvider,
            IngestionModuleMetadata.Name,
            seed.TenantId,
            seed.PropertyId,
            2).ConfigureAwait(false);
        dbContext.AdapterConnections.Add(AdapterConnection.Create(
            seed.ConnectionId,
            seed.TenantId,
            seed.PropertyId,
            FakeHttpAdapterDescriptor.Value.AdapterType,
            AdapterExecutionMode.Push,
            IngestionConflictPolicy.SuggestionsOnly,
            "configuration://runtime",
            secretReference: null,
            now).Value);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    private static IIntegrationEventHandler<TEvent> ResolveHandler<TEvent>(IServiceProvider services, string consumerModule)
        where TEvent : IIntegrationEvent
    {
        IntegrationEventSubscription subscription = services
            .GetRequiredService<IIntegrationEventSubscriptionRegistry>()
            .Subscriptions
            .Single(item => item.ConsumerModule == consumerModule && item.EventType == typeof(TEvent));
        return (IIntegrationEventHandler<TEvent>)services.GetRequiredService(subscription.HandlerType);
    }

    private static async Task<TimedObservation> SubmitTimedAsync(
        AuthTestApplication api,
        ScopeSeed scope,
        Guid operationId,
        string sourceRevision,
        byte[] payload)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        Result<AdapterObservationResult> result = await DispatchAsync(
            api,
            scope,
            operationId,
            sourceRevision,
            payload,
            CancellationToken.None).ConfigureAwait(false);
        stopwatch.Stop();
        Assert.True(result.IsSuccess, result.Error.Code);
        return new TimedObservation(scope, operationId, sourceRevision, payload, result.Value, stopwatch.Elapsed);
    }

    private static async Task<AdapterObservationResult> SubmitAsync(
        AuthTestApplication api,
        ScopeSeed scope,
        Guid operationId,
        string sourceRevision,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        Result<AdapterObservationResult> result = await DispatchAsync(
            api,
            scope,
            operationId,
            sourceRevision,
            payload,
            cancellationToken).ConfigureAwait(false);
        Assert.True(result.IsSuccess, result.Error.Code);
        return result.Value;
    }

    private static async Task<Result<AdapterObservationResult>> DispatchAsync(
        AuthTestApplication api,
        ScopeSeed scopeSeed,
        Guid operationId,
        string sourceRevision,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(scopeSeed.TenantId);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return await scope.ServiceProvider.GetRequiredService<IRequestDispatcher>().SendAsync(
            new ReceiveObservationCommand(
                scopeSeed.ConnectionId,
                RunId: null,
                operationId,
                "runtime.v1",
                $"external-{sourceRevision}",
                sourceRevision,
                now,
                now,
                "application/json",
                payload,
                AdapterPayloadHash.ComputeSha256(payload)),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> CountReceiptsAsync(AuthTestApplication api, string tenantId)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(tenantId);
        return await scope.ServiceProvider.GetRequiredService<IngestionDbContext>()
            .ObservationReceipts
            .CountAsync()
            .ConfigureAwait(false);
    }

    private static async Task<OutboxBacklog> ReadOutboxBacklogAsync(AuthTestApplication api)
    {
        using IServiceScope scope = api.Services.CreateScope();
        IngestionDbContext dbContext = scope.ServiceProvider.GetRequiredService<IngestionDbContext>();
        IQueryable<Gma.Framework.Messaging.Infrastructure.OutboxMessage> pending = dbContext.OutboxMessages
            .IgnoreQueryFilters()
            .Where(message => message.ProcessedAtUtc == null);
        return new(
            await pending.CountAsync().ConfigureAwait(false),
            await pending.MinAsync(message => (DateTimeOffset?)message.CreatedAtUtc).ConfigureAwait(false));
    }

    private static async Task WaitForOutboxBacklogAsync(
        AuthTestApplication api,
        int minimumCount,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if ((await ReadOutboxBacklogAsync(api).ConfigureAwait(false)).Count >= minimumCount)
            {
                return;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new TimeoutException($"Ingestion outbox did not reach {minimumCount} pending messages.");
    }

    private static async Task<TimeSpan> WaitForOutboxDrainAsync(AuthTestApplication api, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if ((await ReadOutboxBacklogAsync(api).ConfigureAwait(false)).Count == 0)
            {
                return stopwatch.Elapsed;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new TimeoutException("Ingestion outbox did not drain after broker recovery.");
    }

    private static async Task<DatabaseGrowth> ReadDatabaseGrowthAsync(AuthTestApplication api)
    {
        using IServiceScope scope = api.Services.CreateScope();
        DbConnection connection = scope.ServiceProvider.GetRequiredService<IngestionDbContext>().Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync().ConfigureAwait(false);
        }

        await using DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT pg_total_relation_size('ingestion.observation_receipts'), " +
                              "pg_indexes_size('ingestion.observation_receipts')";
        await using DbDataReader reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.True(await reader.ReadAsync().ConfigureAwait(false));
        return new(reader.GetInt64(0), reader.GetInt64(1));
    }

    private static async Task<ObjectStorageGrowth> ReadObjectStorageGrowthAsync(
        AuthTestApplication api,
        string bucketName)
    {
        IMinioClient client = api.Services.GetRequiredService<IMinioClient>();
        int count = 0;
        long bytes = 0;
        await foreach (var item in client.ListObjectsEnumAsync(
            new ListObjectsArgs().WithBucket(bucketName).WithRecursive(true),
            CancellationToken.None))
        {
            count++;
            bytes += (long)item.Size;
        }

        return new(count, bytes);
    }

    private static async Task<BrokerGrowth> ReadBrokerGrowthAsync(AuthTestApplication api)
    {
        INatsConnection connection = api.Services.GetRequiredService<INatsConnection>();
        string streamName = api.Services.GetRequiredService<NatsJetStreamStreamManager>().StreamName;
        INatsJSStream stream = await new NatsJSContext(connection)
            .GetStreamAsync(streamName, cancellationToken: CancellationToken.None)
            .ConfigureAwait(false);
        return new(stream.Info.State.Messages, stream.Info.State.Bytes);
    }

    private static byte[] Payload(string kind, int sequence) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        kind,
        sequence,
        padding = new string('x', 256),
    }));

    private static double Percentile(double[] sortedValues, double percentile)
    {
        int index = (int)Math.Ceiling(sortedValues.Length * percentile) - 1;
        return sortedValues[Math.Clamp(index, 0, sortedValues.Length - 1)];
    }

    private sealed record ScopeSeed(string TenantId, Guid PropertyId, Guid ConnectionId)
    {
        public static ScopeSeed Create(string tenantId) => new(tenantId, Guid.NewGuid(), Guid.NewGuid());
    }

    private sealed record TimedObservation(
        ScopeSeed Scope,
        Guid OperationId,
        string SourceRevision,
        byte[] Payload,
        AdapterObservationResult Result,
        TimeSpan Elapsed);

    private sealed record OutboxBacklog(int Count, DateTimeOffset? OldestCreatedAtUtc);
    private sealed record DatabaseGrowth(long TableBytes, long IndexBytes);
    private sealed record ObjectStorageGrowth(int ObjectCount, long RetainedBytes);
    private sealed record BrokerGrowth(long Messages, long Bytes);
    private sealed record RuntimeBaseline(
        int BurstSize,
        int TenantCount,
        double AcceptanceP50Milliseconds,
        double AcceptanceP95Milliseconds,
        double AcceptanceMaxMilliseconds,
        double DuplicateP95Milliseconds,
        double StorageRecoveryMilliseconds,
        int BrokerBacklogCount,
        double BrokerBacklogOldestAgeMilliseconds,
        double BrokerDrainMilliseconds,
        long PostgreSqlReceiptTableBytes,
        long PostgreSqlReceiptIndexBytes,
        int MinioObjectCount,
        long MinioRetainedBytes,
        long JetStreamMessages,
        long JetStreamBytes);
}
