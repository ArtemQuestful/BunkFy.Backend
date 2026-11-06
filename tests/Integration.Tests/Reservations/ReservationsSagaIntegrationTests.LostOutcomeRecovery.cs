namespace Integration.Tests;

using System.Net;
using System.Text.Json;
using BunkFy.Host.Worker;
using BunkFy.Modules.Reservations.Application;
using BunkFy.Modules.Reservations.Application.Commands;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Persistence;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Persistence;
using BunkFy.Modules.Workspaces.Contracts;
using DotNet.Testcontainers.Containers;
using Gma.Framework.Application.Events.Infrastructure;
using Gma.Framework.Cqrs;
using Gma.Framework.Cqrs.Infrastructure;
using Gma.Framework.Messaging.Infrastructure;
using Gma.Framework.Messaging.Nats;
using Gma.Framework.ModuleComposition;
using Gma.Framework.Naming;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Gma.Framework.Tenancy;
using Gma.Modules.Auth.Contracts;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

public sealed partial class ReservationsSagaIntegrationTests
{
    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Restart_and_reconcile_recover_a_lost_stay_amendment_outcome_once()
    {
        await using IContainer nats = AuthTestContainers.CreateNatsContainer();
        await nats.StartAsync().ConfigureAwait(false);
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_reservations_lost_outcome_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);

        string connectionString = postgreSql.GetConnectionString();
        string natsConnectionString =
            AuthTestContainers.GetNatsConnectionString(nats);
        await using (AuthTestApplication migrationApi = new(
                         "PostgreSql",
                         connectionString,
                         natsConnectionString,
                         disableOutboxPublisher: true))
        {
            await migrationApi.MigrateGuestDataRightsAuthorizationDatabaseAsync()
                .ConfigureAwait(false);
            await migrationApi.MigrateStaffAuthorizationDatabaseAsync()
                .ConfigureAwait(false);
            await migrationApi.MigrateIngestionDatabaseAsync()
                .ConfigureAwait(false);
        }

        await using AdminCliTestApplication admin = new(
            "PostgreSql",
            connectionString);
        await admin.MigrateAsync().ConfigureAwait(false);
        await using AuthTestApplication api = new(
            "PostgreSql",
            connectionString,
            natsConnectionString,
            disableOutboxPublisher: false);
        using HttpClient client = api.CreateClient();

        using IHost initialWorker = CreateWorker(
            connectionString,
            natsConnectionString);
        IHost? inventoryOnlyWorker = null;
        IHost? recoveryWorker = null;
        bool initialWorkerStopped = false;
        bool inventoryOnlyWorkerStopped = false;
        bool recoveryWorkerStopped = false;
        await initialWorker.StartAsync().ConfigureAwait(false);
        try
        {
            await SeedInventoryAsync(api).ConfigureAwait(false);
            await SeedGovernedPropertyProjectionsAsync(api)
                .ConfigureAwait(false);
            await WaitForSellableProjectionAsync(
                api,
                TimeSpan.FromSeconds(20)).ConfigureAwait(false);

            AuthTokensResponse tokens = await AuthApiClient.RegisterAsync(
                client,
                TenantId,
                "recovery-operator@reservations.test")
                .ConfigureAwait(false);
            Guid operatorId = GetSubjectId(tokens.AccessToken);
            await api.SeedOrganizationMembershipAsync(TenantId, operatorId)
                .ConfigureAwait(false);
            await GrantReservationsAccessAsync(admin, operatorId)
                .ConfigureAwait(false);

            ReservationMutationReceiptDto created =
                await CreateReservationAsync(
                    client,
                    tokens.AccessToken,
                    new DateOnly(2026, 11, 1),
                    new DateOnly(2026, 11, 3),
                    "Recovery Guest").ConfigureAwait(false);
            ReservationDto confirmed = await WaitForStatusAsync(
                client,
                tokens.AccessToken,
                created.ReservationId,
                ReservationStatus.Confirmed,
                TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            Assert.NotNull(confirmed.AllocationId);

            await initialWorker.StopAsync().ConfigureAwait(false);
            initialWorkerStopped = true;

            Guid operationId = Guid.NewGuid();
            ReservationStayAmendmentReceiptDto pending;
            using (HttpResponseMessage response = await SendAsync(
                       client,
                       HttpMethod.Put,
                       $"/api/reservations/properties/{PropertyId:D}/{created.ReservationId:D}/stay-amendments/{operationId:D}",
                       tokens.AccessToken,
                       new
                       {
                           arrival = confirmed.Arrival,
                           departure = confirmed.Departure.AddDays(1),
                           expectedArrivalTime = new TimeOnly(16, 0),
                           expectedDepartureTime = new TimeOnly(9, 0),
                           inventoryUnitIds = new[] { ReplacementRoomId },
                           expectedDetailsRevision =
                               confirmed.DetailsRevision
                       }).ConfigureAwait(false))
            {
                pending =
                    await ReadSuccessAsync<ReservationStayAmendmentReceiptDto>(
                            response)
                        .ConfigureAwait(false);
            }

            Assert.Equal(
                ReservationStayAmendmentOutcome.Pending,
                pending.Outcome);
            Guid inventoryRequestId = await GetStayInventoryRequestIdAsync(
                api,
                created.ReservationId,
                operationId).ConfigureAwait(false);
            Assert.NotEqual(operationId, inventoryRequestId);
            ReservationDto oldTruth = await WaitForStatusAsync(
                client,
                tokens.AccessToken,
                created.ReservationId,
                ReservationStatus.Confirmed,
                TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            Assert.Equal(confirmed.Arrival, oldTruth.Arrival);
            Assert.Equal(confirmed.Departure, oldTruth.Departure);
            Assert.Equal(confirmed.ExpectedArrivalTime, oldTruth.ExpectedArrivalTime);
            Assert.Equal(confirmed.ExpectedDepartureTime, oldTruth.ExpectedDepartureTime);
            Assert.Equal(confirmed.AllocationId, oldTruth.AllocationId);
            Assert.Equal(confirmed.AllocationVersion, oldTruth.AllocationVersion);
            Assert.Equal([RoomId], oldTruth.InventoryUnitIds);

            inventoryOnlyWorker = CreateInventoryOnlyWorker(
                connectionString,
                natsConnectionString);
            await inventoryOnlyWorker.StartAsync().ConfigureAwait(false);
            InventoryMutationSnapshot firstMutation =
                await WaitForInventoryMutationAsync(
                    connectionString,
                    confirmed.AllocationId.Value,
                    inventoryRequestId,
                    TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            Assert.Equal(confirmed.Departure.AddDays(1), firstMutation.Departure);
            Assert.Equal(confirmed.AllocationVersion + 1, firstMutation.Version);
            Assert.Equal(firstMutation.Version, firstMutation.DecisionAllocationVersion);
            Assert.Equal(1, await CountInventoryAmendmentDecisionsAsync(
                api,
                inventoryRequestId).ConfigureAwait(false));
            Guid[] initialOutcomeIds = await WaitForInventoryOutcomeOutboxAsync(
                api,
                expectedCount: 1,
                TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            Assert.Equal([inventoryRequestId], initialOutcomeIds);

            await inventoryOnlyWorker.StopAsync().ConfigureAwait(false);
            inventoryOnlyWorkerStopped = true;

            // The Reservations durable consumer was offline for this outcome.
            // Purging its exact subject models an outcome lost at the broker boundary.
            long purged = await PurgeConfirmedAmendmentOutcomesAsync(
                natsConnectionString).ConfigureAwait(false);
            Assert.Equal(1, purged);
            Assert.Equal(
                0,
                await CountReservationsAmendmentOutcomeInboxAsync(api)
                    .ConfigureAwait(false));
            ReservationStayAmendmentReceiptDto stillPending =
                await GetStayAmendmentAsync(
                    client,
                    tokens.AccessToken,
                    created.ReservationId,
                    operationId).ConfigureAwait(false);
            Assert.Equal(
                ReservationStayAmendmentOutcome.Pending,
                stillPending.Outcome);

            Assert.NotNull(stillPending.NextRecoveryEligibleAtUtc);
            FixedRecoveryClock recoveryClock = new(
                stillPending.NextRecoveryEligibleAtUtc.Value.AddSeconds(1));
            ReservationStayAmendmentReceiptDto reconciled;
            await using (ServiceProvider reconciliationServices =
                         CreateReconciliationServices(
                             connectionString,
                             recoveryClock))
            {
                using IServiceScope scope = reconciliationServices.CreateScope();
                Result<ReservationStayAmendmentReceiptDto> result =
                    await scope.ServiceProvider
                        .GetRequiredService<IRequestDispatcher>()
                        .SendAsync(
                            new ReconcileReservationStayAmendmentCommand(
                                PropertyId,
                                created.ReservationId,
                                operationId,
                                stillPending.OperationVersion,
                                "user:recovery-test"),
                            CancellationToken.None)
                        .ConfigureAwait(false);
                Assert.True(result.IsSuccess, result.Error.Code);
                reconciled = result.Value;
            }

            Assert.Equal(
                ReservationStayAmendmentOutcome.Pending,
                reconciled.Outcome);
            Assert.Equal(1, reconciled.ReconciliationCount);
            Assert.Equal(
                stillPending.OperationVersion + 1,
                reconciled.OperationVersion);
            Guid[] requestIds = await WaitForReservationAmendmentRequestsAsync(
                api,
                expectedCount: 2,
                TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            Assert.Equal(
                [inventoryRequestId, inventoryRequestId],
                requestIds);

            recoveryWorker = CreateRecoveryWorker(
                connectionString,
                natsConnectionString,
                recoveryClock);
            await recoveryWorker.StartAsync().ConfigureAwait(false);
            ReservationStayAmendmentReceiptDto applied =
                await WaitForStayAmendmentAsync(
                    client,
                    tokens.AccessToken,
                    created.ReservationId,
                    operationId,
                    ReservationStayAmendmentOutcome.Applied,
                    TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            Assert.Equal(confirmed.Departure.AddDays(1), applied.Target!.Departure);

            InventoryMutationSnapshot afterRecovery =
                await ReadInventoryMutationAsync(
                    connectionString,
                    confirmed.AllocationId.Value,
                    inventoryRequestId).ConfigureAwait(false);
            Assert.Equal(firstMutation, afterRecovery);
            Assert.Equal(1, await CountInventoryAmendmentDecisionsAsync(
                api,
                inventoryRequestId).ConfigureAwait(false));
            Assert.Equal(
                [inventoryRequestId, inventoryRequestId],
                await WaitForInventoryOutcomeOutboxAsync(
                    api,
                    expectedCount: 2,
                    TimeSpan.FromSeconds(20)).ConfigureAwait(false));
            Assert.Equal(
                1,
                await WaitForReservationsAmendmentOutcomeInboxAsync(
                    api,
                    TimeSpan.FromSeconds(20)).ConfigureAwait(false));

            await recoveryWorker.StopAsync().ConfigureAwait(false);
            recoveryWorkerStopped = true;
        }
        finally
        {
            if (recoveryWorker is not null)
            {
                if (!recoveryWorkerStopped)
                {
                    await recoveryWorker.StopAsync().ConfigureAwait(false);
                }

                recoveryWorker.Dispose();
            }

            if (inventoryOnlyWorker is not null)
            {
                if (!inventoryOnlyWorkerStopped)
                {
                    await inventoryOnlyWorker.StopAsync().ConfigureAwait(false);
                }

                inventoryOnlyWorker.Dispose();
            }

            if (!initialWorkerStopped)
            {
                await initialWorker.StopAsync().ConfigureAwait(false);
            }
        }
    }

    private static IHost CreateInventoryOnlyWorker(
        string connectionString,
        string natsConnectionString) => CreateSelectiveWorker(
        connectionString,
        natsConnectionString,
        includeReservations: false,
        clock: null);

    private static IHost CreateRecoveryWorker(
        string connectionString,
        string natsConnectionString,
        ISystemClock clock) => CreateSelectiveWorker(
        connectionString,
        natsConnectionString,
        includeReservations: true,
        clock);

    private static IHost CreateSelectiveWorker(
        string connectionString,
        string natsConnectionString,
        bool includeReservations,
        ISystemClock? clock)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder([]);
        builder.Environment.EnvironmentName = "Integration";
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ApplicationIdentity:DisplayName"] =
                    "BunkFy Reservation Recovery Inventory Worker",
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
                ["Worker:Modules:Reservations"] =
                    includeReservations.ToString(),
                ["Worker:Modules:Guests"] =
                    includeReservations.ToString(),
                ["Tasks:Worker:Enabled"] = "false"
            });
        if (clock is not null)
        {
            builder.Services.AddSingleton(clock);
        }

        builder.AddWorkerHost();
        CountryPolicyIntegrationTestData.InstallRegistry(builder.Services);
        builder.ValidateModuleComposition();
        return builder.Build();
    }

    private static ServiceProvider CreateReconciliationServices(
        string connectionString,
        ISystemClock clock)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder([]);
        builder.Configuration["ApplicationIdentity:DisplayName"] =
            "BunkFy Reservation Recovery Dispatcher";
        builder.Configuration["ApplicationIdentity:Namespace"] = "bunkfy";
        builder.Configuration["Persistence:Provider"] = "PostgreSql";
        builder.Configuration["ConnectionStrings:PostgreSql"] =
            connectionString;
        builder.Services.AddSingleton<IScopeContext>(
            RecoveryScopeContext.Instance);
        builder.Services.AddSingleton(clock);
        builder.Services.AddSingleton<IIdGenerator, RecoveryIdGenerator>();
        builder.Services.AddSingleton<IWorkspaceTerminationFenceReader>(
            OpenWorkspaceTerminationFenceReader.Instance);
        builder.AddApplicationEventsInfrastructure();
        builder.AddCqrsInfrastructure();
        builder.AddMessagingInfrastructure();
        builder.Services.AddReservationsApplication();
        builder.AddReservationsPersistence();
        return builder.Services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });
    }

    private static async Task<InventoryMutationSnapshot>
        WaitForInventoryMutationAsync(
            string connectionString,
            Guid allocationId,
            Guid inventoryRequestId,
            TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            InventoryMutationSnapshot? snapshot =
                await TryReadInventoryMutationAsync(
                    connectionString,
                    allocationId,
                    inventoryRequestId).ConfigureAwait(false);
            if (snapshot is not null)
            {
                return snapshot;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Inventory did not commit amendment decision '{inventoryRequestId:D}'.");
    }

    private static async Task<InventoryMutationSnapshot>
        ReadInventoryMutationAsync(
            string connectionString,
            Guid allocationId,
            Guid inventoryRequestId) =>
        await TryReadInventoryMutationAsync(
                connectionString,
                allocationId,
                inventoryRequestId)
            .ConfigureAwait(false) ??
        throw new InvalidOperationException(
            $"Inventory amendment decision '{inventoryRequestId:D}' was not found.");

    private static async Task<InventoryMutationSnapshot?>
        TryReadInventoryMutationAsync(
            string connectionString,
            Guid allocationId,
            Guid inventoryRequestId)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using NpgsqlCommand command = new("""
            SELECT
                allocation."Version",
                allocation."Arrival",
                allocation."Departure",
                allocation.xmin::text::bigint,
                decision."AllocationVersion",
                decision.xmin::text::bigint
            FROM inventory.allocations allocation
            JOIN inventory.allocation_amendment_decisions decision
              ON decision."ScopeId" = allocation."ScopeId"
             AND decision."AllocationId" = allocation."Id"
             AND decision."Id" = @inventoryRequestId
            WHERE allocation."ScopeId" = @tenantId
              AND allocation."Id" = @allocationId
            """, connection);
        command.Parameters.AddWithValue("tenantId", TenantId);
        command.Parameters.AddWithValue("allocationId", allocationId);
        command.Parameters.AddWithValue(
            "inventoryRequestId",
            inventoryRequestId);
        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync()
            .ConfigureAwait(false);
        if (!await reader.ReadAsync().ConfigureAwait(false))
        {
            return null;
        }

        InventoryMutationSnapshot snapshot = new(
            reader.GetInt64(0),
            reader.GetFieldValue<DateOnly>(1),
            reader.GetFieldValue<DateOnly>(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5));
        Assert.False(await reader.ReadAsync().ConfigureAwait(false));
        return snapshot;
    }

    private static async Task<Guid[]> WaitForInventoryOutcomeOutboxAsync(
        AuthTestApplication api,
        int expectedCount,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        Guid[] last = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            using IServiceScope scope = api.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
                .SetTenant(TenantId);
            InventoryDbContext inventory = scope.ServiceProvider
                .GetRequiredService<InventoryDbContext>();
            var messages = await inventory.OutboxMessages
                .AsNoTracking()
                .Where(message =>
                    message.ScopeId == TenantId &&
                    message.EventType == typeof(
                        InventoryAllocationAmendmentConfirmedIntegrationEvent)
                        .FullName)
                .OrderBy(message => message.CreatedAtUtc)
                .Select(message => new
                {
                    message.Payload,
                    message.ProcessedAtUtc
                })
                .ToArrayAsync()
                .ConfigureAwait(false);
            last = messages
                .Select(message => ReadInventoryRequestId(message.Payload))
                .ToArray();
            if (messages.Length == expectedCount &&
                messages.All(message => message.ProcessedAtUtc.HasValue))
            {
                return last;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Inventory outcome outbox did not reach {expectedCount} published messages. Last count: {last.Length}.");
    }

    private static async Task<Guid[]> WaitForReservationAmendmentRequestsAsync(
        AuthTestApplication api,
        int expectedCount,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        Guid[] last = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            using IServiceScope scope = api.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
                .SetTenant(TenantId);
            ReservationsDbContext reservations = scope.ServiceProvider
                .GetRequiredService<ReservationsDbContext>();
            var messages = await reservations.OutboxMessages
                .AsNoTracking()
                .Where(message =>
                    message.ScopeId == TenantId &&
                    message.EventType == typeof(
                        InventoryAllocationAmendmentRequestedIntegrationEvent)
                        .FullName)
                .OrderBy(message => message.CreatedAtUtc)
                .Select(message => new
                {
                    message.Payload,
                    message.ProcessedAtUtc
                })
                .ToArrayAsync()
                .ConfigureAwait(false);
            last = messages
                .Select(message => ReadInventoryRequestId(message.Payload))
                .ToArray();
            if (messages.Length == expectedCount &&
                messages.All(message => message.ProcessedAtUtc.HasValue))
            {
                return last;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Reservations request outbox did not reach {expectedCount} published messages. Last count: {last.Length}.");
    }

    private static Guid ReadInventoryRequestId(string payload)
    {
        using JsonDocument document = JsonDocument.Parse(payload);
        return document.RootElement
            .GetProperty("amendmentRequestId")
            .GetGuid();
    }

    private static async Task<long> PurgeConfirmedAmendmentOutcomesAsync(
        string natsConnectionString)
    {
        await using NatsConnection connection = new(new NatsOpts
        {
            Url = natsConnectionString
        });
        NatsJSContext jetStream = new(connection);
        StreamPurgeResponse response = await jetStream.PurgeStreamAsync(
            ApplicationNamespaces.CreateStreamName("bunkfy"),
            new StreamPurgeRequest
            {
                Filter = InventoryIntegrationSubjects
                    .CreateAllocationAmendmentConfirmed("bunkfy")
            },
            CancellationToken.None).ConfigureAwait(false);
        return response.Purged;
    }

    private static async Task<int>
        CountReservationsAmendmentOutcomeInboxAsync(
            AuthTestApplication api)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
            .SetTenant(TenantId);
        ReservationsDbContext reservations = scope.ServiceProvider
            .GetRequiredService<ReservationsDbContext>();
        return await reservations.InboxMessages
            .AsNoTracking()
            .CountAsync(message =>
                message.ScopeId == TenantId &&
                message.Handler == ReservationsModuleMetadata
                    .AllocationAmendmentConfirmedHandlerName)
            .ConfigureAwait(false);
    }

    private static async Task<int>
        WaitForReservationsAmendmentOutcomeInboxAsync(
            AuthTestApplication api,
            TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(timeout);
        int count = 0;
        while (DateTimeOffset.UtcNow < deadline)
        {
            count = await CountReservationsAmendmentOutcomeInboxAsync(api)
                .ConfigureAwait(false);
            if (count == 1)
            {
                return count;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Reservations did not consume exactly one recovered outcome. Last count: {count}.");
    }

    private static async Task<ReservationStayAmendmentReceiptDto>
        GetStayAmendmentAsync(
            HttpClient client,
            string accessToken,
            Guid reservationId,
            Guid operationId)
    {
        using HttpResponseMessage response = await SendAsync(
            client,
            HttpMethod.Get,
            $"/api/reservations/properties/{PropertyId:D}/{reservationId:D}/stay-amendments/{operationId:D}",
            accessToken).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadSuccessAsync<ReservationStayAmendmentReceiptDto>(
                response)
            .ConfigureAwait(false);
    }

    private sealed record InventoryMutationSnapshot(
        long Version,
        DateOnly Arrival,
        DateOnly Departure,
        long AllocationXmin,
        long DecisionAllocationVersion,
        long DecisionXmin);

    private sealed class FixedRecoveryClock(DateTimeOffset utcNow)
        : ISystemClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class RecoveryScopeContext : IScopeContext
    {
        public static RecoveryScopeContext Instance { get; } = new();

        public bool IsEnabled => true;
        public string ScopeId => TenantId;
    }

    private sealed class RecoveryIdGenerator : IIdGenerator
    {
        public Guid NewId() => Guid.NewGuid();
    }
}
