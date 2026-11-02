namespace Integration.Tests;

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using BunkFy.Adapter.Abstractions;
using BunkFy.Host.Worker;
using BunkFy.Modules.Ingestion.Application.Commands;
using BunkFy.Modules.Ingestion.Contracts;
using BunkFy.Modules.Ingestion.Domain.Connections;
using BunkFy.Modules.Ingestion.Domain.Proposals;
using BunkFy.Modules.Ingestion.Domain.Receipts;
using BunkFy.Modules.Ingestion.Domain.Reservations;
using BunkFy.Modules.Ingestion.Persistence;
using BunkFy.Modules.Inventory.Application.Commands;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using BunkFy.Modules.Inventory.Persistence;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Reservations.Application.Commands;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Application.Queries;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Persistence;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Gma.Framework.Cqrs;
using Gma.Framework.Messaging;
using Gma.Framework.ModuleComposition;
using Gma.Framework.Results;
using Gma.Framework.Tenancy;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Xunit;
using Xunit.Abstractions;

public sealed class IngestionReservationFlowIntegrationTests(ITestOutputHelper output)
{
    private const string TenantId = "tenant-ingestion-flow";
    private const string AccessKey = "minioadmin";
    private const string SecretKey = "minioadmin";
    private static readonly Guid PropertyId = Guid.Parse("81000000-0000-0000-0000-000000000001");
    private static readonly Guid RoomId = Guid.Parse("82000000-0000-0000-0000-000000000001");
    private static readonly Guid ConnectionId = Guid.Parse("83000000-0000-0000-0000-000000000001");

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    [Trait("Category", "IngestionRuntime")]
    public async Task Durable_observations_recover_after_worker_downtime_and_preserve_staff_changes()
    {
        await using IContainer nats = AuthTestContainers.CreateNatsContainer();
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("bunkfy_ingestion_flow_tests")
            .Build();
        await using IContainer minio = ProviderTestContainers.Minio()
            .WithEnvironment("MINIO_ROOT_USER", AccessKey)
            .WithEnvironment("MINIO_ROOT_PASSWORD", SecretKey)
            .WithPortBinding(9000, assignRandomHostPort: true)
            .WithCommand("server", "/data", "--console-address", ":9001")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(9000))
            .Build();

        await Task.WhenAll(nats.StartAsync(), postgreSql.StartAsync(), minio.StartAsync()).ConfigureAwait(false);
        string connectionString = postgreSql.GetConnectionString();
        string natsConnectionString = AuthTestContainers.GetNatsConnectionString(nats);
        string minioEndpoint = $"localhost:{minio.GetMappedPublicPort(9000)}";
        string bucketName = $"bunkfy-ingestion-{Guid.NewGuid():N}";
        await using AuthTestApplication api = new(
            "PostgreSql",
            connectionString,
            natsConnectionString,
            disableOutboxPublisher: false,
            minioEndpoint: minioEndpoint,
            minioAccessKey: AccessKey,
            minioSecretKey: SecretKey,
            minioBucketName: bucketName,
            minioCreateBucketIfMissing: true);
        await api.MigrateReservationsAuthorizationDatabaseAsync().ConfigureAwait(false);
        await api.MigrateIngestionDatabaseAsync().ConfigureAwait(false);
        await SeedAsync(api).ConfigureAwait(false);

        Stopwatch createDuration = Stopwatch.StartNew();
        Guid firstOperationId = Guid.NewGuid();
        byte[] firstPayload = Payload(1, "Adapter Guest");
        AdapterObservationResult first = await SubmitAsync(
            api,
            firstOperationId,
            "1",
            firstPayload).ConfigureAwait(false);
        AdapterObservationResult duplicate = await SubmitAsync(
            api,
            firstOperationId,
            "1",
            firstPayload).ConfigureAwait(false);
        Assert.Equal(AdapterObservationDisposition.Accepted, first.Disposition);
        Assert.Equal(AdapterObservationDisposition.Duplicate, duplicate.Disposition);
        Assert.Equal(first.ReceiptId, duplicate.ReceiptId);
        await WaitForProcessedIngestionOutboxAsync(api, minimumCount: 1, TimeSpan.FromSeconds(20)).ConfigureAwait(false);

        using (IHost worker = CreateWorker(
                         connectionString,
                         natsConnectionString,
                         minioEndpoint,
                         bucketName))
        {
            await worker.StartAsync().ConfigureAwait(false);
            await WaitForAppliedReceiptAsync(
                api,
                first.ReceiptId!.Value,
                ReservationDispatchKind.Create,
                expectedDetailsRevision: 1,
                new DateOnly(2026, 11, 3),
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            createDuration.Stop();
            await worker.StopAsync().ConfigureAwait(false);
        }

        Stopwatch amendmentDuration = Stopwatch.StartNew();
        long processedBeforeAmendment = await CountProcessedIngestionOutboxAsync(api).ConfigureAwait(false);
        Guid amendmentOperationId = Guid.NewGuid();
        byte[] amendmentPayload = Payload(2, "Adapter Amended", new DateOnly(2026, 11, 4));
        AdapterObservationResult amendment = await SubmitAsync(
            api,
            amendmentOperationId,
            "2",
            amendmentPayload).ConfigureAwait(false);
        AdapterObservationResult amendmentDuplicate = await SubmitAsync(
            api,
            amendmentOperationId,
            "2",
            amendmentPayload).ConfigureAwait(false);
        Assert.Equal(AdapterObservationDisposition.Accepted, amendment.Disposition);
        Assert.Equal(AdapterObservationDisposition.Duplicate, amendmentDuplicate.Disposition);
        Assert.Equal(amendment.ReceiptId, amendmentDuplicate.ReceiptId);
        await WaitForProcessedIngestionOutboxAsync(
            api,
            processedBeforeAmendment + 1,
            TimeSpan.FromSeconds(20)).ConfigureAwait(false);

        using (IHost amendmentWorker = CreateWorker(
                         connectionString,
                         natsConnectionString,
                         minioEndpoint,
                         bucketName))
        {
            await amendmentWorker.StartAsync().ConfigureAwait(false);
            await WaitForAppliedReceiptAsync(
                api,
                amendment.ReceiptId!.Value,
                ReservationDispatchKind.Amend,
                expectedDetailsRevision: 2,
                new DateOnly(2026, 11, 4),
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            amendmentDuration.Stop();
            await amendmentWorker.StopAsync().ConfigureAwait(false);
        }

        await SeedConflictingAllocationAsync(api).ConfigureAwait(false);
        long processedBeforeRejectedAmendment = await CountProcessedIngestionOutboxAsync(api).ConfigureAwait(false);
        Guid rejectedAmendmentOperationId = Guid.NewGuid();
        byte[] rejectedAmendmentPayload = Payload(3, "Adapter Conflicting", new DateOnly(2026, 11, 5));
        AdapterObservationResult rejectedAmendment = await SubmitAsync(
            api,
            rejectedAmendmentOperationId,
            "3",
            rejectedAmendmentPayload).ConfigureAwait(false);
        AdapterObservationResult rejectedAmendmentDuplicate = await SubmitAsync(
            api,
            rejectedAmendmentOperationId,
            "3",
            rejectedAmendmentPayload).ConfigureAwait(false);
        Assert.Equal(AdapterObservationDisposition.Accepted, rejectedAmendment.Disposition);
        Assert.Equal(AdapterObservationDisposition.Duplicate, rejectedAmendmentDuplicate.Disposition);
        Assert.Equal(rejectedAmendment.ReceiptId, rejectedAmendmentDuplicate.ReceiptId);
        await WaitForProcessedIngestionOutboxAsync(
            api,
            processedBeforeRejectedAmendment + 1,
            TimeSpan.FromSeconds(20)).ConfigureAwait(false);

        using (IHost rejectedAmendmentWorker = CreateWorker(
                         connectionString,
                         natsConnectionString,
                         minioEndpoint,
                         bucketName))
        {
            await rejectedAmendmentWorker.StartAsync().ConfigureAwait(false);
            await WaitForRejectedAmendmentAsync(
                api,
                rejectedAmendment.ReceiptId!.Value,
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            await rejectedAmendmentWorker.StopAsync().ConfigureAwait(false);
        }

        ReservationDto staffEdited = await ApplyStaffEditAsync(api).ConfigureAwait(false);
        Assert.Equal(3, staffEdited.DetailsRevision);
        Assert.Equal("Staff Corrected", staffEdited.PrimaryGuestName);

        Stopwatch proposalDuration = Stopwatch.StartNew();
        long processedBeforeConflict = await CountProcessedIngestionOutboxAsync(api).ConfigureAwait(false);
        Guid conflictOperationId = Guid.NewGuid();
        byte[] conflictPayload = Payload(4, "Adapter Newer", new DateOnly(2026, 11, 4));
        AdapterObservationResult conflict = await SubmitAsync(
            api,
            conflictOperationId,
            "4",
            conflictPayload).ConfigureAwait(false);
        AdapterObservationResult conflictDuplicate = await SubmitAsync(
            api,
            conflictOperationId,
            "4",
            conflictPayload).ConfigureAwait(false);
        Assert.Equal(AdapterObservationDisposition.Accepted, conflict.Disposition);
        Assert.Equal(AdapterObservationDisposition.Duplicate, conflictDuplicate.Disposition);
        Assert.Equal(conflict.ReceiptId, conflictDuplicate.ReceiptId);
        await WaitForProcessedIngestionOutboxAsync(
            api,
            processedBeforeConflict + 1,
            TimeSpan.FromSeconds(20)).ConfigureAwait(false);

        using (IHost restartedWorker = CreateWorker(
                         connectionString,
                         natsConnectionString,
                         minioEndpoint,
                         bucketName))
        {
            await restartedWorker.StartAsync().ConfigureAwait(false);
            await WaitForPendingProposalAsync(api, conflict.ReceiptId!.Value, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            proposalDuration.Stop();
            await restartedWorker.StopAsync().ConfigureAwait(false);
        }

        await AssertStaffAuthorityPreservedAsync(api, conflict.ReceiptId!.Value).ConfigureAwait(false);
        output.WriteLine($"flow-baseline={JsonSerializer.Serialize(new
        {
            AcceptedToCreatedReservationMilliseconds = createDuration.Elapsed.TotalMilliseconds,
            AcceptedToAppliedAmendmentMilliseconds = amendmentDuration.Elapsed.TotalMilliseconds,
            AcceptedToStaffConflictProposalMilliseconds = proposalDuration.Elapsed.TotalMilliseconds,
        })}");
    }

    private static IHost CreateWorker(
        string connectionString,
        string natsConnectionString,
        string minioEndpoint,
        string bucketName)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder([]);
        builder.Environment.EnvironmentName = "Integration";
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ApplicationIdentity:DisplayName"] = "BunkFy Ingestion Flow Worker",
            ["ApplicationIdentity:Namespace"] = "bunkfy",
            ["Persistence:Provider"] = "PostgreSql",
            ["ConnectionStrings:PostgreSql"] = connectionString,
            ["ConnectionStrings:nats"] = natsConnectionString,
            ["Tenancy:Enabled"] = "true",
            ["Caching:Enabled"] = "false",
            ["NatsJetStream:Enabled"] = "true",
            ["NatsConsumers:Enabled"] = "true",
            ["NatsConsumers:FetchBatchSize"] = "10",
            ["NatsConsumers:PollInterval"] = "00:00:00.100",
            ["NatsConsumers:AckWait"] = "00:00:05",
            ["NatsConsumers:AckProgressInterval"] = "00:00:01",
            ["NatsConsumers:HandlerTimeout"] = "00:00:10",
            ["NatsConsumers:NakDelay"] = "00:00:00.100",
            ["Outbox:PollIntervalMilliseconds"] = "100",
            ["Outbox:LockDurationMilliseconds"] = "5000",
            ["Worker:Modules:Properties"] = "true",
            ["Worker:Modules:Inventory"] = "true",
            ["Worker:Modules:Reservations"] = "true",
            ["Worker:Modules:Guests"] = "true",
            ["Worker:Modules:Ingestion"] = "true",
            ["Tasks:Worker:Enabled"] = "false",
            ["FileManagement:Enabled"] = "true",
            ["FileManagement:Provider"] = "Minio",
            ["FileManagement:MaximumObjectBytes"] = "10485760",
            ["FileManagement:AllowedContentTypes:0"] = "application/json",
            ["FileManagement:Minio:Endpoint"] = minioEndpoint,
            ["FileManagement:Minio:AccessKey"] = AccessKey,
            ["FileManagement:Minio:SecretKey"] = SecretKey,
            ["FileManagement:Minio:BucketName"] = bucketName,
            ["FileManagement:Minio:UseSsl"] = "false",
            ["FileManagement:Minio:CreateBucketIfMissing"] = "true"
        });
        builder.AddWorkerHost();
        CountryPolicyIntegrationTestData.InstallRegistry(builder.Services);
        builder.ValidateModuleComposition();
        return builder.Build();
    }

    private static async Task SeedAsync(AuthTestApplication api)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantId);
        IngestionDbContext ingestion = scope.ServiceProvider.GetRequiredService<IngestionDbContext>();
        InventoryDbContext inventory = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        ReservationsDbContext reservations = scope.ServiceProvider.GetRequiredService<ReservationsDbContext>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IIntegrationEventHandler<PropertyCreatedIntegrationEvent> propertyHandler =
            ResolveHandler<PropertyCreatedIntegrationEvent>(scope.ServiceProvider, InventoryModuleMetadata.Name);
        IIntegrationEventHandler<PropertyCreatedIntegrationEvent> ingestionPropertyHandler =
            ResolveHandler<PropertyCreatedIntegrationEvent>(scope.ServiceProvider, IngestionModuleMetadata.Name);
        IIntegrationEventHandler<PropertyCreatedIntegrationEvent> reservationPropertyHandler =
            ResolveHandler<PropertyCreatedIntegrationEvent>(scope.ServiceProvider, ReservationsModuleMetadata.Name);
        IIntegrationEventHandler<RoomCreatedIntegrationEvent> roomHandler =
            ResolveHandler<RoomCreatedIntegrationEvent>(scope.ServiceProvider, InventoryModuleMetadata.Name);
        PropertyCreatedIntegrationEvent propertyCreated = new(
            Guid.NewGuid(), TenantId, now, PropertyId, "Ingestion House", "ingestion", "UTC", PropertyStatus.Active, 1);
        await ModuleTransactionIntegrationTestData.ExecuteAsync(
            inventory,
            async token =>
            {
                await propertyHandler.HandleAsync(propertyCreated, token)
                    .ConfigureAwait(false);
                await roomHandler.HandleAsync(
                    new(Guid.NewGuid(), TenantId, now, PropertyId, RoomId, "101", null, null, RoomStatus.Active, 1),
                    token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        await ModuleTransactionIntegrationTestData.ExecuteAsync(
            ingestion,
            token => ingestionPropertyHandler.HandleAsync(propertyCreated, token))
            .ConfigureAwait(false);
        await ModuleTransactionIntegrationTestData.ExecuteAsync(
            reservations,
            token => reservationPropertyHandler.HandleAsync(propertyCreated, token))
            .ConfigureAwait(false);
        await CountryPolicyIntegrationTestData.ApplyActivationAsync(
            scope.ServiceProvider,
            IngestionModuleMetadata.Name,
            TenantId,
            PropertyId,
            2).ConfigureAwait(false);
        await CountryPolicyIntegrationTestData.ApplyActivationAsync(
            scope.ServiceProvider,
            ReservationsModuleMetadata.Name,
            TenantId,
            PropertyId,
            2).ConfigureAwait(false);
        Result<RoomInventoryMutationReceiptDto> configured = await scope.ServiceProvider.GetRequiredService<IRequestDispatcher>()
            .SendAsync(
                new ConfigureRoomSalesModeCommand(
                    Guid.NewGuid(),
                    PropertyId,
                    RoomId,
                    InventorySalesMode.RoomLevel,
                    1),
                CancellationToken.None)
            .ConfigureAwait(false);
        Assert.True(configured.IsSuccess, configured.Error.Code);
        AdapterConnection connection = AdapterConnection.Create(
            ConnectionId,
            TenantId,
            PropertyId,
            "fake.http",
            AdapterExecutionMode.Push,
            IngestionConflictPolicy.AutoApplyWhenAdapterBaselineUnchanged,
            "configuration://integration",
            secretReference: null,
            DateTimeOffset.UtcNow).Value;
        ingestion.AdapterConnections.Add(connection);
        reservations.InventoryUnitProjections.Add(ReservationInventoryUnitProjection.Create(
            new ReservationInventoryUnitWriteModel(
                TenantId,
                RoomId,
                PropertyId,
                RoomId,
                BedId: null,
                InventoryUnitKind.Room,
                "Room 101",
                IsTopologyActive: true,
                IsSellable: true,
                ConfigurationVersion: 1,
                UnitVersion: 1)));
        await ingestion.SaveChangesAsync().ConfigureAwait(false);
        await reservations.SaveChangesAsync().ConfigureAwait(false);
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

    private static async Task<AdapterObservationResult> SubmitAsync(
        AuthTestApplication api,
        Guid operationId,
        string sourceRevision,
        byte[] payload)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantId);
        DateTimeOffset observedAtUtc = DateTimeOffset.UtcNow;
        Result<AdapterObservationResult> result = await scope.ServiceProvider
            .GetRequiredService<IRequestDispatcher>()
            .SendAsync(
                new ReceiveObservationCommand(
                    ConnectionId,
                    RunId: null,
                    operationId,
                    "reservation.v1",
                    "booking-42",
                    sourceRevision,
                    observedAtUtc,
                    observedAtUtc,
                    "application/json",
                    payload,
                    AdapterPayloadHash.ComputeSha256(payload)),
                CancellationToken.None)
            .ConfigureAwait(false);
        Assert.True(result.IsSuccess, result.Error.Code);
        return result.Value;
    }

    private static async Task<ReservationDto> ApplyStaffEditAsync(AuthTestApplication api)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantId);
        Reservation reservation = await scope.ServiceProvider.GetRequiredService<ReservationsDbContext>()
            .Reservations
            .AsNoTracking()
            .SingleAsync(item => item.SourceReference == "booking-42")
            .ConfigureAwait(false);
        IRequestDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IRequestDispatcher>();
        Result<ReservationMutationReceiptDto> result = await dispatcher
            .SendAsync(
                new UpdateReservationGuestDetailsCommand(
                    Guid.NewGuid(),
                    PropertyId,
                    reservation.Id,
                    "Staff Corrected",
                    "staff@example.test",
                    Phone: null,
                    GuestCount: 1,
                    Notes: "Verified by front desk",
                    ExpectedDetailsRevision: reservation.DetailsRevision,
                    ReservationDetailsChangeOriginKind.Staff,
                    "staff:integration"),
                CancellationToken.None)
            .ConfigureAwait(false);
        Assert.True(result.IsSuccess, result.Error.Code);
        Result<ReservationDto> refreshed = await dispatcher.QueryAsync(
            new GetReservationQuery(PropertyId, reservation.Id),
            CancellationToken.None).ConfigureAwait(false);
        Assert.True(refreshed.IsSuccess, refreshed.Error.Code);
        return refreshed.Value;
    }

    private static async Task SeedConflictingAllocationAsync(AuthTestApplication api)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantId);
        InventoryDbContext inventory = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        Result<InventoryAllocation> created = InventoryAllocation.CreateAccepted(
            Guid.NewGuid(),
            TenantId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            PropertyId,
            new DateOnly(2026, 11, 4),
            new DateOnly(2026, 11, 6),
            [RoomId],
            DateTimeOffset.UtcNow);
        Assert.True(created.IsSuccess, created.Error.Code);
        inventory.Allocations.Add(created.Value);
        await inventory.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task WaitForRejectedAmendmentAsync(
        AuthTestApplication api,
        Guid receiptId,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        string lastObservedState = "No state observed.";
        while (DateTimeOffset.UtcNow < deadline)
        {
            using IServiceScope scope = api.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantId);
            IngestionDbContext ingestion = scope.ServiceProvider.GetRequiredService<IngestionDbContext>();
            ReservationsDbContext reservations = scope.ServiceProvider.GetRequiredService<ReservationsDbContext>();
            InventoryDbContext inventory = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            ObservationReceipt? receipt = await ingestion.ObservationReceipts.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == receiptId)
                .ConfigureAwait(false);
            ReservationDispatch? dispatch = await ingestion.ReservationDispatches.AsNoTracking()
                .SingleOrDefaultAsync(item => item.ReceiptId == receiptId)
                .ConfigureAwait(false);
            ReservationSourceLink? link = await ingestion.ReservationSourceLinks.AsNoTracking()
                .SingleOrDefaultAsync(item => item.SourceReference == "booking-42")
                .ConfigureAwait(false);
            Reservation? reservation = await reservations.Reservations.AsNoTracking()
                .SingleOrDefaultAsync(item => item.SourceReference == "booking-42")
                .ConfigureAwait(false);
            InventoryAllocation? allocation = reservation?.AllocationId is { } allocationId
                ? await inventory.Allocations.AsNoTracking()
                    .Include(item => item.Units)
                    .SingleOrDefaultAsync(item => item.Id == allocationId)
                    .ConfigureAwait(false)
                : null;
            InventoryAllocationAmendmentDecision? decision = reservation is null
                ? null
                : await inventory.AllocationAmendmentDecisions.AsNoTracking()
                    .Where(item =>
                        item.ReservationId == reservation.Id &&
                        !item.Confirmed &&
                        item.RejectionReason == InventoryAllocationRejectionReason.AllocationConflict)
                    .OrderByDescending(item => item.DecidedAtUtc)
                    .FirstOrDefaultAsync()
                    .ConfigureAwait(false);
            var inventoryInbox = await inventory.InboxMessages.AsNoTracking()
                .Where(item => item.ProcessedAtUtc == null || item.LastError != null)
                .OrderBy(item => item.CreatedAtUtc)
                .Select(item => new { item.EventType, item.Handler, item.Status, item.LastError })
                .ToListAsync()
                .ConfigureAwait(false);

            lastObservedState = JsonSerializer.Serialize(new
            {
                Receipt = receipt is null ? null : new { receipt.State, receipt.RejectionReason },
                Dispatch = dispatch is null ? null : new { dispatch.State, dispatch.Kind, dispatch.ErrorCode },
                Link = link is null ? null : new
                {
                    link.LastAppliedReservationDetailsRevision,
                    link.LastAppliedReceiptId,
                },
                Reservation = reservation is null ? null : new
                {
                    reservation.Status,
                    reservation.Departure,
                    reservation.DetailsRevision,
                    reservation.PendingAllocationAmendmentId,
                    reservation.LastAllocationAmendmentRejectionCode,
                },
                Allocation = allocation is null ? null : new
                {
                    allocation.Status,
                    allocation.Departure,
                    allocation.Version,
                },
                Decision = decision is null ? null : new
                {
                    decision.Confirmed,
                    decision.RejectionReason,
                },
                InventoryInbox = inventoryInbox,
            });

            if (receipt?.State == ObservationReceiptState.Processed &&
                dispatch?.State == ReservationDispatchState.Rejected &&
                dispatch.Kind == ReservationDispatchKind.Amend &&
                link?.LastAppliedReservationDetailsRevision == 2 &&
                link.LastAppliedReceiptId != receiptId &&
                reservation?.Status == ReservationState.Confirmed &&
                reservation.Departure == new DateOnly(2026, 11, 4) &&
                reservation.DetailsRevision == 2 &&
                !reservation.PendingAllocationAmendmentId.HasValue &&
                reservation.LastAllocationAmendmentRejectionCode ==
                    (int)InventoryAllocationRejectionReason.AllocationConflict &&
                allocation?.Status == InventoryAllocationState.Active &&
                allocation.Departure == new DateOnly(2026, 11, 4) &&
                allocation.Version == 2 &&
                decision is { Confirmed: false, RejectionReason: InventoryAllocationRejectionReason.AllocationConflict })
            {
                return;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"The conflicting allocation amendment did not reject atomically. Last state: {lastObservedState}");
    }

    private static async Task WaitForAppliedReceiptAsync(
        AuthTestApplication api,
        Guid receiptId,
        ReservationDispatchKind expectedKind,
        long expectedDetailsRevision,
        DateOnly expectedDeparture,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        string lastObservedState = "No state observed.";
        while (DateTimeOffset.UtcNow < deadline)
        {
            using IServiceScope scope = api.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantId);
            IngestionDbContext ingestion = scope.ServiceProvider.GetRequiredService<IngestionDbContext>();
            ObservationReceipt? receipt = await ingestion.ObservationReceipts.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == receiptId)
                .ConfigureAwait(false);
            ReservationDispatch? dispatch = await ingestion.ReservationDispatches.AsNoTracking()
                .SingleOrDefaultAsync(item => item.ReceiptId == receiptId)
                .ConfigureAwait(false);
            ReservationSourceLink? link = await ingestion.ReservationSourceLinks.AsNoTracking()
                .SingleOrDefaultAsync(item => item.LastAppliedReceiptId == receiptId)
                .ConfigureAwait(false);
            ReservationsDbContext reservations = scope.ServiceProvider.GetRequiredService<ReservationsDbContext>();
            Reservation? reservation = await reservations
                .Reservations.AsNoTracking()
                .SingleOrDefaultAsync(item => item.SourceReference == "booking-42")
                .ConfigureAwait(false);
            InventoryDbContext inventory = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
            InventoryAllocation? allocation = reservation is null
                ? null
                : await inventory.Allocations.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.ReservationId == reservation.Id)
                    .ConfigureAwait(false);
            long reservationsPendingOutbox = await reservations.OutboxMessages.AsNoTracking()
                .LongCountAsync(item => item.ProcessedAtUtc == null)
                .ConfigureAwait(false);
            long inventoryPendingOutbox = await inventory.OutboxMessages.AsNoTracking()
                .LongCountAsync(item => item.ProcessedAtUtc == null)
                .ConfigureAwait(false);
            var reservationsMessages = await reservations.OutboxMessages.AsNoTracking()
                .OrderBy(item => item.CreatedAtUtc)
                .Select(item => new { item.EventType, item.ProcessedAtUtc, item.Error })
                .ToListAsync()
                .ConfigureAwait(false);
            var inventoryMessages = await inventory.InboxMessages.AsNoTracking()
                .OrderBy(item => item.CreatedAtUtc)
                .Select(item => new { item.EventType, item.Handler, item.Status, item.LastError })
                .ToListAsync()
                .ConfigureAwait(false);
            string reservationsOutbox = string.Join(
                ",",
                reservationsMessages.Select(item =>
                    $"{item.EventType}:{(item.ProcessedAtUtc.HasValue ? "processed" : item.Error ?? "pending")}"));
            string inventoryInbox = string.Join(
                ",",
                inventoryMessages.Select(item =>
                    $"{item.EventType}/{item.Handler}:{item.Status}/{item.LastError ?? "no-error"}"));
            lastObservedState =
                $"receipt={receipt?.State.ToString() ?? "missing"}/{receipt?.RejectionReason ?? "no-error"}; " +
                $"dispatch={dispatch?.State.ToString() ?? "missing"}/{dispatch?.Kind.ToString() ?? "none"}/{dispatch?.ErrorCode ?? "no-error"}; " +
                $"link={link?.State.ToString() ?? "missing"}/revision={link?.LastAppliedReservationDetailsRevision?.ToString(CultureInfo.InvariantCulture) ?? "none"}; " +
                $"reservation={reservation?.Status.ToString() ?? "missing"}/revision={reservation?.DetailsRevision.ToString(CultureInfo.InvariantCulture) ?? "none"}/departure={reservation?.Departure.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "none"}/pending-amendment={reservation?.PendingAllocationAmendmentId?.ToString() ?? "none"}; " +
                $"allocation={allocation?.Status.ToString() ?? "missing"}/version={allocation?.Version.ToString(CultureInfo.InvariantCulture) ?? "none"}; " +
                $"pending-outbox=reservations:{reservationsPendingOutbox},inventory:{inventoryPendingOutbox}; " +
                $"reservations-outbox=[{reservationsOutbox}]; inventory-inbox=[{inventoryInbox}].";
            if (receipt?.State == ObservationReceiptState.Processed &&
                dispatch?.State == ReservationDispatchState.Applied &&
                dispatch.Kind == expectedKind &&
                link?.LastAppliedReservationDetailsRevision == expectedDetailsRevision &&
                reservation?.DetailsRevision == expectedDetailsRevision &&
                reservation.Departure == expectedDeparture &&
                reservation.Status == ReservationState.Confirmed &&
                !reservation.PendingAllocationAmendmentId.HasValue)
            {
                return;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"The durable observation did not complete the Ingestion-to-Reservations flow. Last state: {lastObservedState}");
    }

    private static async Task WaitForPendingProposalAsync(
        AuthTestApplication api,
        Guid receiptId,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using IServiceScope scope = api.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantId);
            IngestionDbContext ingestion = scope.ServiceProvider.GetRequiredService<IngestionDbContext>();
            ChangeProposal? proposal = await ingestion.ChangeProposals.AsNoTracking()
                .SingleOrDefaultAsync(item => item.ReceiptId == receiptId)
                .ConfigureAwait(false);
            ObservationReceipt? receipt = await ingestion.ObservationReceipts.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == receiptId)
                .ConfigureAwait(false);
            if (proposal?.State == ChangeProposalState.Pending && receipt?.State == ObservationReceiptState.Processed)
            {
                return;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new TimeoutException("The staff-conflicting observation did not become a pending proposal.");
    }

    private static async Task AssertStaffAuthorityPreservedAsync(AuthTestApplication api, Guid receiptId)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantId);
        IngestionDbContext ingestion = scope.ServiceProvider.GetRequiredService<IngestionDbContext>();
        ReservationsDbContext reservations = scope.ServiceProvider.GetRequiredService<ReservationsDbContext>();
        ChangeProposal proposal = await ingestion.ChangeProposals.AsNoTracking()
            .SingleAsync(item => item.ReceiptId == receiptId)
            .ConfigureAwait(false);
        ReservationDispatch dispatch = await ingestion.ReservationDispatches.AsNoTracking()
            .SingleAsync(item => item.ReceiptId == receiptId)
            .ConfigureAwait(false);
        ReservationSourceLink link = await ingestion.ReservationSourceLinks.AsNoTracking()
            .SingleAsync(item => item.ReservationId == proposal.ReservationId)
            .ConfigureAwait(false);
        Reservation reservation = await reservations.Reservations.AsNoTracking()
            .SingleAsync(item => item.Id == proposal.ReservationId)
            .ConfigureAwait(false);

        Assert.Equal(ChangeProposalState.Pending, proposal.State);
        Assert.Equal(ReservationDispatchState.ProposalRequired, dispatch.State);
        Assert.Equal(2, link.LastAppliedReservationDetailsRevision);
        Assert.Equal(3, reservation.DetailsRevision);
        Assert.Equal("Staff Corrected", reservation.PrimaryGuestName);
    }

    private static async Task<long> CountProcessedIngestionOutboxAsync(AuthTestApplication api)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantId);
        return await scope.ServiceProvider.GetRequiredService<IngestionDbContext>()
            .OutboxMessages.AsNoTracking()
            .LongCountAsync(item => item.ProcessedAtUtc != null)
            .ConfigureAwait(false);
    }

    private static async Task WaitForProcessedIngestionOutboxAsync(
        AuthTestApplication api,
        long minimumCount,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await CountProcessedIngestionOutboxAsync(api).ConfigureAwait(false) >= minimumCount)
            {
                return;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new TimeoutException("The Ingestion outbox did not publish the durable observation.");
    }

    private static byte[] Payload(
        long sourceSequence,
        string guestName,
        DateOnly? departure = null) => JsonSerializer.SerializeToUtf8Bytes(
        new
        {
            operation = "upsert",
            sourceSequence,
            arrival = new DateOnly(2026, 11, 1),
            departure = departure ?? new DateOnly(2026, 11, 3),
            inventoryUnitIds = new[] { RoomId },
            primaryGuestName = guestName,
            email = "adapter@example.test",
            phone = (string?)null,
            guestCount = 1,
            notes = (string?)null
        });
}
