namespace Integration.Tests;

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using BunkFy.Adapter.Abstractions;
using BunkFy.Adapter.Runtime;
using BunkFy.Adapters.FakeHttp;
using BunkFy.Adapters.Http;
using BunkFy.Adapters.ImapReservationMail;
using BunkFy.Adapters.JsonFileDrop;
using BunkFy.Parsers.ReservationMail;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Gma.Framework.Administration;
using Gma.Framework.Administration.Cli;
using Gma.Framework.Cqrs;
using Gma.Framework.Messaging;
using Gma.Framework.ModuleComposition;
using Gma.Framework.Results;
using Gma.Framework.Tenancy;
using Gma.Modules.Auth.Contracts;
using BunkFy.Modules.Ingestion.Application;
using BunkFy.Modules.Ingestion.Application.Adapters;
using BunkFy.Modules.Ingestion.Application.Commands;
using BunkFy.Modules.Ingestion.Application.Ports;
using BunkFy.Modules.Ingestion.Application.Queries;
using BunkFy.Modules.Ingestion.Contracts;
using BunkFy.Modules.Ingestion.Domain.Connections;
using BunkFy.Modules.Ingestion.Domain.Proposals;
using BunkFy.Modules.Ingestion.Domain.Receipts;
using BunkFy.Modules.Ingestion.Persistence;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using BunkFy.Modules.Properties.Contracts;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class IngestionOperationsIntegrationTests
{
    private const string TenantId = "a5000000-0000-0000-0000-000000000001";
    private const string OtherTenantId = "a5000000-0000-0000-0000-000000000002";
    private const string AccessKey = "minioadmin";
    private const string SecretKey = "minioadmin";
    private static readonly Guid PropertyId = Guid.Parse("91000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherPropertyId = Guid.Parse("91000000-0000-0000-0000-000000000002");
    private static readonly Guid PushConnectionId = Guid.Parse("91000000-0000-0000-0000-000000000003");
    private static readonly Guid RemoteConnectionId = Guid.Parse("91000000-0000-0000-0000-000000000004");
    private static readonly Guid OtherTenantPropertyId =
        Guid.Parse("91000000-0000-0000-0000-000000000005");
    private static readonly Guid OtherTenantPushConnectionId =
        Guid.Parse("91000000-0000-0000-0000-000000000006");

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Management_api_scopes_connection_lifecycle_and_operational_reads()
    {
        await using IContainer nats = AuthTestContainers.CreateNatsContainer();
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("bunkfy_ingestion_operations_tests")
            .Build();
        await using IContainer minio = ProviderTestContainers.Minio()
            .WithEnvironment("MINIO_ROOT_USER", AccessKey)
            .WithEnvironment("MINIO_ROOT_PASSWORD", SecretKey)
            .WithPortBinding(9000, assignRandomHostPort: true)
            .WithCommand("server", "/data", "--console-address", ":9001")
            .Build();
        await using IContainer redis = new ContainerBuilder("redis:7.4-alpine")
            .WithPortBinding(6379, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379))
            .Build();
        await Task.WhenAll(
                nats.StartAsync(),
                postgreSql.StartAsync(),
                minio.StartAsync(),
                redis.StartAsync())
            .ConfigureAwait(false);
        string connectionString = postgreSql.GetConnectionString();
        string minioEndpoint = $"localhost:{minio.GetMappedPublicPort(9000)}";
        string redisConnectionString =
            $"127.0.0.1:{redis.GetMappedPublicPort(6379)},abortConnect=false";
        string bucketName = $"bunkfy-ingestion-operations-{Guid.NewGuid():N}";
        await using AuthTestApplication api = new(
            "PostgreSql",
            connectionString,
            AuthTestContainers.GetNatsConnectionString(nats),
            minioEndpoint: minioEndpoint,
            minioAccessKey: AccessKey,
            minioSecretKey: SecretKey,
            minioBucketName: bucketName,
            minioCreateBucketIfMissing: true,
            adapterIngressRedisConnectionString: redisConnectionString);
        await api.MigratePropertiesAuthorizationDatabaseAsync().ConfigureAwait(false);
        await api.MigrateIngestionDatabaseAsync().ConfigureAwait(false);
        await using AdminCliTestApplication admin = new("PostgreSql", connectionString, includeIngestion: true);
        await admin.MigrateAsync().ConfigureAwait(false);
        await SeedPropertyProjectionAsync(api).ConfigureAwait(false);
        await ProveRetentionFenceConcurrencyAsync(api).ConfigureAwait(false);

        using HttpClient client = api.CreateClient();
        AuthTokensResponse tokens = await AuthApiClient.RegisterAsync(
            client,
            TenantId,
            "operator@ingestion.test").ConfigureAwait(false);
        Guid operatorId = GetSubjectId(tokens.AccessToken);
        await api.SeedOrganizationMembershipAsync(TenantId, operatorId).ConfigureAwait(false);
        Guid adminActorId = Guid.NewGuid();
        await GrantAccessAsync(admin, operatorId, adminActorId).ConfigureAwait(false);
        await using AdminApiTestApplication adminApi = new(
            "PostgreSql",
            connectionString,
            AuthTestContainers.GetNatsConnectionString(nats));
        using HttpClient adminClient = adminApi.CreateClient();
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            AdminApiTestApplication.CreateAccessTokenWithTenantClaim(adminActorId, TenantId));
        adminClient.DefaultRequestHeaders.Add("X-Tenant-Id", TenantId);
        AdminCliResult redactionConfirmationRequired = await admin.ExecuteAsync(
            "ingestion", "retention", "redact-reservation-history",
            "--actor", "owner",
            "--tenant", TenantId,
            "--requested-by", "owner");
        Assert.NotEqual(AdminExitCodes.Success, redactionConfirmationRequired.ExitCode);
        Assert.Contains(
            AdminErrors.ConfirmationRequired.Message,
            redactionConfirmationRequired.Error,
            StringComparison.Ordinal);
        AdminCliResult reprocessingConfirmationRequired = await admin.ExecuteAsync(
            "ingestion", "reprocessing", "enqueue",
            "--actor", "owner",
            "--tenant", TenantId,
            "--property-id", PropertyId.ToString("D"),
            "--source-receipt-id", Guid.NewGuid().ToString("D"),
            "--parser-type", ReservationMailParserDescriptor.ParserType);
        Assert.NotEqual(AdminExitCodes.Success, reprocessingConfirmationRequired.ExitCode);
        Assert.True(
            reprocessingConfirmationRequired.Error.Contains(
                AdminErrors.ConfirmationRequired.Message,
                StringComparison.Ordinal),
            reprocessingConfirmationRequired.Error);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        client.DefaultRequestHeaders.Add("X-Tenant-Id", TenantId);

        AdapterTypeCapabilityListResponse capabilities = await GetAsync<AdapterTypeCapabilityListResponse>(
            client,
            $"/api/ingestion/properties/{PropertyId:D}/adapter-types").ConfigureAwait(false);
        Assert.Equal(3, capabilities.AdapterTypes.Count);
        AdapterTypeCapabilityDto fakeHttp = Assert.Single(
            capabilities.AdapterTypes, capability => capability.AdapterType == "fake.http");
        Assert.Equal(
            [AdapterExecutionMode.Polling, AdapterExecutionMode.Push, AdapterExecutionMode.RemotePolling],
            fakeHttp.ExecutionModes);
        Assert.Equal(60, fakeHttp.MinimumPollingIntervalSeconds);
        Assert.Equal(300, fakeHttp.RecommendedPollingIntervalSeconds);
        AdapterTypeCapabilityDto fileDrop = Assert.Single(
            capabilities.AdapterTypes,
            capability => capability.AdapterType == JsonFileDropAdapterDescriptor.AdapterType);
        Assert.Equal(
            [AdapterExecutionMode.Polling, AdapterExecutionMode.Push, AdapterExecutionMode.RemotePolling],
            fileDrop.ExecutionModes);
        Assert.Equal(60, fileDrop.MinimumPollingIntervalSeconds);
        Assert.Equal(60, fileDrop.RecommendedPollingIntervalSeconds);
        AdapterTypeCapabilityDto imap = Assert.Single(
            capabilities.AdapterTypes,
            capability => capability.AdapterType == ImapReservationMailAdapterDescriptor.AdapterType);
        Assert.Equal(3, imap.ConfigurationSchemaVersion);
        Assert.Equal(
            [AdapterExecutionMode.Polling, AdapterExecutionMode.Push, AdapterExecutionMode.RemotePolling],
            imap.ExecutionModes);
        Assert.Equal(60, imap.MinimumPollingIntervalSeconds);
        Assert.Equal(300, imap.RecommendedPollingIntervalSeconds);

        ObservationParserCapabilityListResponse parserCapabilities =
            await GetAsync<ObservationParserCapabilityListResponse>(
                client,
                $"/api/ingestion/properties/{PropertyId:D}/parser-types").ConfigureAwait(false);
        ObservationParserCapabilityDto mailParser = Assert.Single(parserCapabilities.Parsers);
        Assert.Equal(ReservationMailParserDescriptor.ParserType, mailParser.ParserType);
        Assert.Equal(ReservationMailParserDescriptor.ParserVersion, mailParser.ParserVersion);
        Assert.Contains("mail.unparsed.v1", mailParser.SupportedSourceRecordTypes);

        using (HttpResponseMessage unsupportedMode = await client.PostAsJsonAsync(
                   $"/api/ingestion/properties/{PropertyId:D}/connections",
                   new
                   {
                       operationId = Guid.NewGuid(),
                       adapterType = "fake.http",
                       executionMode = AdapterExecutionMode.Continuous,
                       conflictPolicy = AdapterConflictPolicy.SuggestionsOnly,
                       configurationReference = "configuration://invalid"
                   }).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unsupportedMode.StatusCode);
        }

        Guid createOperationId = Guid.NewGuid();
        object createRequest = new
        {
            operationId = createOperationId,
            adapterType = "fake.http",
            executionMode = AdapterExecutionMode.Polling,
            conflictPolicy = AdapterConflictPolicy.SuggestionsOnly,
            configurationReference = "configuration://integration",
            secretReference = "secret://integration"
        };
        AdapterConnectionMutationReceiptDto[] concurrentCreateReceipts =
            await Task.WhenAll(
                PostAsync<AdapterConnectionMutationReceiptDto>(
                    client,
                    $"/api/ingestion/properties/{PropertyId:D}/connections",
                    createRequest),
                PostAsync<AdapterConnectionMutationReceiptDto>(
                    client,
                    $"/api/ingestion/properties/{PropertyId:D}/connections",
                    createRequest)).ConfigureAwait(false);
        AdapterConnectionMutationReceiptDto createdReceipt =
            concurrentCreateReceipts[0];
        Assert.All(
            concurrentCreateReceipts,
            receipt => Assert.Equal(createdReceipt, receipt));
        Assert.Equal(createOperationId, createdReceipt.ConnectionId);
        using (HttpResponseMessage changedReplay = await client.PostAsJsonAsync(
                   $"/api/ingestion/properties/{PropertyId:D}/connections",
                   new
                   {
                       operationId = createOperationId,
                       adapterType = "fake.http",
                       executionMode = AdapterExecutionMode.Polling,
                       conflictPolicy = AdapterConflictPolicy.SuggestionsOnly,
                       configurationReference = "configuration://changed",
                       secretReference = "secret://integration"
                   }).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.Conflict, changedReplay.StatusCode);
        }
        using (IServiceScope scope = api.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
                .SetTenant(TenantId);
            IngestionDbContext dbContext = scope.ServiceProvider
                .GetRequiredService<IngestionDbContext>();
            Assert.Equal(
                1,
                await dbContext.AdapterConnections.CountAsync(
                    connection => connection.Id == createOperationId)
                    .ConfigureAwait(false));
            Assert.Equal(
                1,
                await dbContext.ConnectionManagementOperations.CountAsync(
                    operation => operation.Id == createOperationId)
                    .ConfigureAwait(false));
        }
        Assert.Equal(AdapterConnectionStatus.Enabled, createdReceipt.Status);
        Assert.Equal(1, createdReceipt.Version);
        AdapterConnectionDto created = await GetAsync<AdapterConnectionDto>(
            client,
            $"/api/ingestion/properties/{PropertyId:D}/connections/{createdReceipt.ConnectionId:D}")
            .ConfigureAwait(false);
        Assert.Equal(createdReceipt.ConnectionId, created.ConnectionId);
        Assert.Equal(createdReceipt.Version, created.Version);
        Assert.True(created.HasSecretReference);

        AdapterConnectionListResponse connections = await GetAsync<AdapterConnectionListResponse>(
            client,
            $"/api/ingestion/properties/{PropertyId:D}/connections").ConfigureAwait(false);
        Assert.Equal(created.ConnectionId, Assert.Single(connections.Connections).ConnectionId);
        await SeedPushConnectionAsync(api).ConfigureAwait(false);
        CreateAdapterIngressCredentialResponse otherTenantCredential =
            await SeedOtherTenantIngressAsync(api).ConfigureAwait(false);
        await ProveAdapterIngressAsync(api, admin, client, otherTenantCredential)
            .ConfigureAwait(false);
        await SeedRemoteConnectionAsync(api).ConfigureAwait(false);
        await ProveRemoteAdapterLeasesAsync(api, client).ConfigureAwait(false);
        using (HttpResponseMessage connectionResponse = await client.GetAsync(
                   $"/api/ingestion/properties/{PropertyId:D}/connections/{created.ConnectionId:D}").ConfigureAwait(false))
        {
            string json = await connectionResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
            Assert.True(connectionResponse.IsSuccessStatusCode, json);
            using JsonDocument document = JsonDocument.Parse(json);
            Assert.False(document.RootElement.TryGetProperty("secretReference", out _));
            Assert.True(document.RootElement.GetProperty("hasSecretReference").GetBoolean());
            Assert.DoesNotContain("secret://integration", json, StringComparison.Ordinal);
        }

        Guid updateOperationId = Guid.NewGuid();
        object updateRequest = new
        {
            operationId = updateOperationId,
            executionMode = AdapterExecutionMode.Polling,
            conflictPolicy = AdapterConflictPolicy.SuggestionsOnly,
            configurationReference = "configuration://integration-updated",
            expectedVersion = created.Version
        };
        AdapterConnectionMutationReceiptDto[] concurrentUpdateReceipts =
            await Task.WhenAll(
                PutAsync<AdapterConnectionMutationReceiptDto>(
                    client,
                    $"/api/ingestion/properties/{PropertyId:D}/connections/{created.ConnectionId:D}",
                    updateRequest),
                PutAsync<AdapterConnectionMutationReceiptDto>(
                    client,
                    $"/api/ingestion/properties/{PropertyId:D}/connections/{created.ConnectionId:D}",
                    updateRequest)).ConfigureAwait(false);
        AdapterConnectionMutationReceiptDto keptSecretReceipt =
            concurrentUpdateReceipts[0];
        Assert.All(
            concurrentUpdateReceipts,
            receipt => Assert.Equal(keptSecretReceipt, receipt));
        using (HttpResponseMessage changedUpdateReplay = await client.PutAsJsonAsync(
                   $"/api/ingestion/properties/{PropertyId:D}/connections/{created.ConnectionId:D}",
                   new
                   {
                       operationId = updateOperationId,
                       executionMode = AdapterExecutionMode.Polling,
                       conflictPolicy = AdapterConflictPolicy.SuggestionsOnly,
                       configurationReference = "configuration://changed",
                       expectedVersion = created.Version
                   }).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.Conflict, changedUpdateReplay.StatusCode);
        }
        AdapterConnectionDto keptSecret = await GetAsync<AdapterConnectionDto>(
            client,
            $"/api/ingestion/properties/{PropertyId:D}/connections/{created.ConnectionId:D}")
            .ConfigureAwait(false);
        Assert.Equal(keptSecretReceipt.Version, keptSecret.Version);
        Assert.True(keptSecret.HasSecretReference);
        AdapterConnectionMutationReceiptDto clearedSecretReceipt = await PutAsync<AdapterConnectionMutationReceiptDto>(
            client,
            $"/api/ingestion/properties/{PropertyId:D}/connections/{created.ConnectionId:D}",
            new
            {
                operationId = Guid.NewGuid(),
                executionMode = AdapterExecutionMode.Polling,
                conflictPolicy = AdapterConflictPolicy.SuggestionsOnly,
                configurationReference = "configuration://integration-updated",
                clearSecretReference = true,
                expectedVersion = keptSecret.Version
            }).ConfigureAwait(false);
        AdapterConnectionDto clearedSecret = await GetAsync<AdapterConnectionDto>(
            client,
            $"/api/ingestion/properties/{PropertyId:D}/connections/{created.ConnectionId:D}")
            .ConfigureAwait(false);
        Assert.Equal(clearedSecretReceipt.Version, clearedSecret.Version);
        Assert.False(clearedSecret.HasSecretReference);
        using (IServiceScope scope = api.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
                .SetTenant(TenantId);
            IngestionDbContext dbContext = scope.ServiceProvider
                .GetRequiredService<IngestionDbContext>();
            Assert.Equal(
                1,
                await dbContext.ConnectionManagementOperations.CountAsync(
                    operation =>
                        operation.ConnectionId == created.ConnectionId &&
                        operation.Id == updateOperationId)
                    .ConfigureAwait(false));
        }

        using (HttpResponseMessage invalidSchedule = await client.PutAsJsonAsync(
                   $"/api/ingestion/properties/{PropertyId:D}/connections/{created.ConnectionId:D}/polling-schedule",
                   new
                   {
                       operationId = Guid.NewGuid(),
                       intervalSeconds = 59,
                       maxAttempts = 3,
                       expectedVersion = clearedSecret.Version
                   }).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalidSchedule.StatusCode);
        }

        Guid scheduleOperationId = Guid.NewGuid();
        object scheduleRequest = new
        {
            operationId = scheduleOperationId,
            intervalSeconds = 300,
            maxAttempts = 3,
            expectedVersion = clearedSecret.Version
        };
        AdapterConnectionMutationReceiptDto[] concurrentScheduleReceipts =
            await Task.WhenAll(
                PutAsync<AdapterConnectionMutationReceiptDto>(
                    client,
                    $"/api/ingestion/properties/{PropertyId:D}/connections/{created.ConnectionId:D}/polling-schedule",
                    scheduleRequest),
                PutAsync<AdapterConnectionMutationReceiptDto>(
                    client,
                    $"/api/ingestion/properties/{PropertyId:D}/connections/{created.ConnectionId:D}/polling-schedule",
                    scheduleRequest)).ConfigureAwait(false);
        AdapterConnectionMutationReceiptDto scheduledReceipt =
            concurrentScheduleReceipts[0];
        Assert.All(
            concurrentScheduleReceipts,
            receipt => Assert.Equal(scheduledReceipt, receipt));
        using (HttpResponseMessage changedScheduleReplay =
               await client.PutAsJsonAsync(
                   $"/api/ingestion/properties/{PropertyId:D}/connections/{created.ConnectionId:D}/polling-schedule",
                   new
                   {
                       operationId = scheduleOperationId,
                       intervalSeconds = 600,
                       maxAttempts = 3,
                       expectedVersion = clearedSecret.Version
                   }).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.Conflict, changedScheduleReplay.StatusCode);
        }
        AdapterConnectionDto scheduled = await GetAsync<AdapterConnectionDto>(
            client,
            $"/api/ingestion/properties/{PropertyId:D}/connections/{created.ConnectionId:D}")
            .ConfigureAwait(false);
        Assert.Equal(scheduledReceipt.Version, scheduled.Version);
        Assert.Equal(300, scheduled.PollingIntervalSeconds);
        Assert.Equal(3, scheduled.PollingScheduleMaxAttempts);
        Assert.NotNull(scheduled.PollingScheduleConfiguredAtUtc);

        await AssertScheduleDiscoveryAsync(api, created.ConnectionId, expectedCount: 1).ConfigureAwait(false);
        AdapterConnectionHealthDto scheduledHealth = await GetAsync<AdapterConnectionHealthDto>(
            client,
            $"/api/ingestion/properties/{PropertyId:D}/connections/{created.ConnectionId:D}/health")
            .ConfigureAwait(false);
        Assert.Equal(300, scheduledHealth.PollingIntervalSeconds);
        Assert.Equal(3, scheduledHealth.PollingScheduleMaxAttempts);
        Assert.Equal(
            scheduled.PollingScheduleConfiguredAtUtc!.Value,
            scheduledHealth.PollingScheduleConfiguredAtUtc!.Value,
            TimeSpan.FromMilliseconds(1));
        Assert.Equal(
            scheduled.PollingScheduleConfiguredAtUtc!.Value,
            scheduledHealth.NextRunExpectedAtUtc!.Value,
            TimeSpan.FromMilliseconds(1));
        Assert.True(scheduledHealth.RunExpected);

        AdapterConnectionMutationReceiptDto scheduleClearedReceipt =
            await PostAsync<AdapterConnectionMutationReceiptDto>(
                client,
                $"/api/ingestion/properties/{PropertyId:D}/connections/{created.ConnectionId:D}/polling-schedule/clear",
                new
                {
                    operationId = Guid.NewGuid(),
                    expectedVersion = scheduled.Version
                }).ConfigureAwait(false);
        AdapterConnectionDto scheduleCleared = await GetAsync<AdapterConnectionDto>(
            client,
            $"/api/ingestion/properties/{PropertyId:D}/connections/{created.ConnectionId:D}")
            .ConfigureAwait(false);
        Assert.Equal(scheduleClearedReceipt.Version, scheduleCleared.Version);
        Assert.Null(scheduleCleared.PollingIntervalSeconds);
        Assert.Null(scheduleCleared.PollingScheduleMaxAttempts);
        Assert.Null(scheduleCleared.PollingScheduleConfiguredAtUtc);
        await AssertScheduleDiscoveryAsync(api, created.ConnectionId, expectedCount: 0).ConfigureAwait(false);

        AdapterConnectionMutationReceiptDto rescheduled = await PutAsync<AdapterConnectionMutationReceiptDto>(
            client,
            $"/api/ingestion/properties/{PropertyId:D}/connections/{created.ConnectionId:D}/polling-schedule",
            new
            {
                operationId = Guid.NewGuid(),
                intervalSeconds = 300,
                maxAttempts = 4,
                expectedVersion = scheduleCleared.Version
            }).ConfigureAwait(false);

        (Guid receiptId, byte[] rawPayload) = await SubmitObservationAsync(api, created.ConnectionId).ConfigureAwait(false);
        AdapterConnectionHealthDto activeHealth = await GetAsync<AdapterConnectionHealthDto>(
            client,
            $"/api/ingestion/properties/{PropertyId:D}/connections/{created.ConnectionId:D}/health")
            .ConfigureAwait(false);
        Assert.Equal(AdapterConnectionOperationalState.ObservationsReceived, activeHealth.OperationalState);
        Assert.Equal(AdapterCapabilityStatus.Available, activeHealth.CapabilityStatus);
        Assert.Equal("fake.http", activeHealth.AdapterType);
        Assert.Equal(1, activeHealth.ProtocolVersion);
        Assert.Equal(1, activeHealth.ConfigurationSchemaVersion);
        Assert.Equal(1, activeHealth.PendingReceiptCount);
        Assert.NotNull(activeHealth.LastObservationReceivedAtUtc);
        Assert.Equal(300, activeHealth.PollingIntervalSeconds);
        Assert.Equal(4, activeHealth.PollingScheduleMaxAttempts);
        string rawPayloadPath =
            $"/api/ingestion/properties/{PropertyId:D}/receipts/{receiptId:D}/raw-payload";
        using (HttpResponseMessage deniedPayload = await client.GetAsync(rawPayloadPath).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.Forbidden, deniedPayload.StatusCode);
        }

        await GrantRawPayloadAccessAsync(admin).ConfigureAwait(false);
        using (HttpResponseMessage downloadedPayload = await client.GetAsync(rawPayloadPath).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.OK, downloadedPayload.StatusCode);
            Assert.Equal("application/octet-stream", downloadedPayload.Content.Headers.ContentType?.MediaType);
            Assert.Equal("attachment", downloadedPayload.Content.Headers.ContentDisposition?.DispositionType);
            Assert.True(downloadedPayload.Headers.CacheControl?.NoStore);
            Assert.True(downloadedPayload.Headers.TryGetValues("X-Content-Type-Options", out IEnumerable<string>? values));
            Assert.Contains("nosniff", values);
            Assert.Equal(rawPayload, await downloadedPayload.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
        }

        using (HttpResponseMessage crossPropertyPayload = await client.GetAsync(
                   $"/api/ingestion/properties/{OtherPropertyId:D}/receipts/{receiptId:D}/raw-payload").ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.Forbidden, crossPropertyPayload.StatusCode);
        }

        await PurgeRawPayloadAsync(api, admin, adminClient, receiptId).ConfigureAwait(false);
        using (HttpResponseMessage purgedPayload = await client.GetAsync(rawPayloadPath).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.Gone, purgedPayload.StatusCode);
        }
        AdapterConnectionHealthDto retainedHealth = await GetAsync<AdapterConnectionHealthDto>(
            client,
            $"/api/ingestion/properties/{PropertyId:D}/connections/{created.ConnectionId:D}/health")
            .ConfigureAwait(false);
        Assert.Equal(0, retainedHealth.PendingReceiptCount);
        Assert.Equal(1, retainedHealth.RejectedReceiptCount);
        Assert.Equal(0, retainedHealth.ExpiredRawPayloadCount);
        Assert.Equal(0, retainedHealth.ProtectedRawPayloadCount);
        Assert.Equal(0, retainedHealth.HeldExpiredRawPayloadCount);
        Assert.Equal(0, retainedHealth.PurgingRawPayloadCount);
        Assert.Equal(0, retainedHealth.DueSensitiveHistoryCount);
        Assert.Equal(0, retainedHealth.HeldDueSensitiveHistoryCount);
        Assert.Equal(1, retainedHealth.RedactedSensitiveHistoryCount);
        Assert.Equal(0, retainedHealth.ActiveLegalHoldCount);

        Guid disableOperationId = Guid.NewGuid();
        object disableRequest = new
        {
            operationId = disableOperationId,
            expectedVersion = rescheduled.Version
        };
        AdapterConnectionMutationReceiptDto[] concurrentDisableReceipts =
            await Task.WhenAll(
                PostAsync<AdapterConnectionMutationReceiptDto>(
                    client,
                    $"/api/ingestion/properties/{PropertyId:D}/connections/{created.ConnectionId:D}/disable",
                    disableRequest),
                PostAsync<AdapterConnectionMutationReceiptDto>(
                    client,
                    $"/api/ingestion/properties/{PropertyId:D}/connections/{created.ConnectionId:D}/disable",
                    disableRequest)).ConfigureAwait(false);
        AdapterConnectionMutationReceiptDto disabledReceipt =
            concurrentDisableReceipts[0];
        Assert.All(
            concurrentDisableReceipts,
            receipt => Assert.Equal(disabledReceipt, receipt));
        Assert.Equal(AdapterConnectionStatus.Disabled, disabledReceipt.Status);
        AdapterConnectionDto disabled = await GetAsync<AdapterConnectionDto>(
            client,
            $"/api/ingestion/properties/{PropertyId:D}/connections/{created.ConnectionId:D}")
            .ConfigureAwait(false);
        Assert.Equal(disabledReceipt.Version, disabled.Version);
        Assert.Equal(300, disabled.PollingIntervalSeconds);
        Assert.Equal(4, disabled.PollingScheduleMaxAttempts);
        await AssertScheduleDiscoveryAsync(api, created.ConnectionId, expectedCount: 0).ConfigureAwait(false);
        AdapterConnectionHealthDto disabledHealth = await GetAsync<AdapterConnectionHealthDto>(
            client,
            $"/api/ingestion/properties/{PropertyId:D}/connections/{created.ConnectionId:D}/health")
            .ConfigureAwait(false);
        Assert.Equal(AdapterConnectionOperationalState.Disabled, disabledHealth.OperationalState);
        Assert.Equal(300, disabledHealth.PollingIntervalSeconds);
        Assert.Equal(4, disabledHealth.PollingScheduleMaxAttempts);
        Assert.Null(disabledHealth.NextRunExpectedAtUtc);
        Assert.False(disabledHealth.RunExpected);

        Guid resetOperationId = Guid.NewGuid();
        object resetRequest = new
        {
            operationId = resetOperationId,
            expectedVersion = disabled.Version,
            confirmed = true
        };
        AdapterConnectionMutationReceiptDto reset = await PostAsync<AdapterConnectionMutationReceiptDto>(
            client,
            $"/api/ingestion/properties/{PropertyId:D}/connections/{created.ConnectionId:D}/reset-checkpoint",
            resetRequest).ConfigureAwait(false);
        AdapterConnectionMutationReceiptDto replayedReset = await PostAsync<AdapterConnectionMutationReceiptDto>(
            client,
            $"/api/ingestion/properties/{PropertyId:D}/connections/{created.ConnectionId:D}/reset-checkpoint",
            resetRequest).ConfigureAwait(false);
        Assert.Equal(disabled.Version, reset.Version);
        Assert.Equal(reset, replayedReset);
        using (IServiceScope scope = api.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
                .SetTenant(TenantId);
            IngestionDbContext dbContext = scope.ServiceProvider
                .GetRequiredService<IngestionDbContext>();
            Guid[] operationIds =
            [
                scheduleOperationId,
                disableOperationId,
                resetOperationId
            ];
            Assert.Equal(
                operationIds.Length,
                await dbContext.ConnectionManagementOperations.CountAsync(
                    operation =>
                        operation.ConnectionId == created.ConnectionId &&
                        operationIds.Contains(operation.Id))
                    .ConfigureAwait(false));
        }

        IngestionRunListResponse runs = await GetAsync<IngestionRunListResponse>(
            client,
            $"/api/ingestion/properties/{PropertyId:D}/runs?connectionId={created.ConnectionId:D}")
            .ConfigureAwait(false);
        ObservationReceiptListResponse receipts = await GetAsync<ObservationReceiptListResponse>(
            client,
            $"/api/ingestion/properties/{PropertyId:D}/receipts?connectionId={created.ConnectionId:D}")
            .ConfigureAwait(false);
        Assert.Empty(runs.Runs);
        Assert.Equal(receiptId, Assert.Single(receipts.Receipts).ReceiptId);

        await ProveJsonFileDropAdapterAsync(api, client).ConfigureAwait(false);

        using HttpResponseMessage crossProperty = await client.GetAsync(
            $"/api/ingestion/properties/{OtherPropertyId:D}/connections").ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.Forbidden, crossProperty.StatusCode);
    }

    private static async Task ProveJsonFileDropAdapterAsync(
        AuthTestApplication api,
        HttpClient managementClient)
    {
        AdapterConnectionMutationReceiptDto connection = await PostAsync<AdapterConnectionMutationReceiptDto>(
            managementClient,
            $"/api/ingestion/properties/{PropertyId:D}/connections",
            new
            {
                operationId = Guid.NewGuid(),
                adapterType = JsonFileDropAdapterDescriptor.AdapterType,
                executionMode = AdapterExecutionMode.Polling,
                conflictPolicy = AdapterConflictPolicy.SuggestionsOnly,
                configurationReference = "configuration://json-file-drop-integration"
            }).ConfigureAwait(false);

        string root = Path.Combine(
            Path.GetTempPath(),
            $"bunkfy-file-drop-integration-{Guid.NewGuid():N}");
        string pending = Path.Combine(root, connection.ConnectionId.ToString("N"), "pending");
        Directory.CreateDirectory(pending);
        string fileName = "20260712T120000Z-booking-43.json";
        string pendingPath = Path.Combine(pending, fileName);
        await File.WriteAllTextAsync(pendingPath, /*lang=json,strict*/ """
            {
              "schemaVersion": 1,
              "recordType": "operations.file-drop.v1",
              "externalRecordId": "booking-file-drop-43",
              "sourceRevision": "1",
              "sourceUpdatedAtUtc": "2026-07-12T11:59:00Z",
              "observedAtUtc": "2026-07-12T12:00:00Z",
              "payload": { "source": "json-file-drop", "booking": "booking-file-drop-43" }
            }
            """).ConfigureAwait(false);

        try
        {
            Guid taskRunId = Guid.NewGuid();
            const int taskAttempt = 1;
            using IServiceScope scope = api.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantId);
            IRequestDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IRequestDispatcher>();
            Result<AdapterRunStart> started = await dispatcher.SendAsync(
                new StartAdapterRunCommand(connection.ConnectionId, taskRunId, taskAttempt),
                CancellationToken.None).ConfigureAwait(false);
            Assert.True(started.IsSuccess, started.Error.Code);

            DateTimeOffset assignedAtUtc = DateTimeOffset.UtcNow;
            AdapterRunAssignment assignment = new(
                started.Value.RunId,
                Guid.NewGuid(),
                started.Value.ConnectionId,
                started.Value.ScopeId,
                started.Value.PropertyId,
                started.Value.AdapterType,
                started.Value.ExecutionMode,
                assignedAtUtc,
                assignedAtUtc.AddMinutes(5),
                started.Value.Checkpoint);
            IAdapterObservationSink sink = scope.ServiceProvider
                .GetRequiredService<IAdapterObservationSinkFactory>()
                .Create(assignment);
            ServiceCollection adapterServices = new();
            adapterServices.AddJsonFileDropAdapter(new JsonFileDropAdapterOptions(root));
            using ServiceProvider adapterProvider = adapterServices.BuildServiceProvider();
            IAdapterRunner runner = Assert.Single(adapterProvider.GetServices<IAdapterRunner>());
            using AdapterConfigurationMaterial material = new(
                schemaVersion: 1,
                "application/json",
                "{}"u8,
                secretContentType: null,
                []);

            AdapterRunCompletion completion = await runner.RunAsync(
                assignment,
                material,
                sink,
                CancellationToken.None).ConfigureAwait(false);
            Assert.Equal(AdapterRunOutcome.Succeeded, completion.Outcome);
            Assert.Equal(1, completion.AcceptedCount);
            Result<Unit> completed = await dispatcher.SendAsync(
                new CompleteAdapterRunCommand(
                    started.Value.RunId,
                    taskRunId,
                    taskAttempt,
                    completion.Outcome,
                    completion.ObservedCount,
                    completion.AcceptedCount,
                    completion.RejectedCount,
                    completion.AcceptedCheckpoint,
                    completion.ErrorCode),
                CancellationToken.None).ConfigureAwait(false);
            Assert.True(completed.IsSuccess, completed.Error.Code);

            ObservationReceiptListResponse receipts = await GetAsync<ObservationReceiptListResponse>(
                managementClient,
                $"/api/ingestion/properties/{PropertyId:D}/receipts?connectionId={connection.ConnectionId:D}")
                .ConfigureAwait(false);
            ObservationReceiptListItemDto receipt = Assert.Single(receipts.Receipts);
            Assert.Equal("booking-file-drop-43", receipt.ExternalId);
            Assert.False(File.Exists(pendingPath));
            Assert.True(File.Exists(Path.Combine(
                root,
                connection.ConnectionId.ToString("N"),
                "processed",
                fileName)));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static Task SeedPropertyProjectionAsync(AuthTestApplication api) =>
        SeedPropertyProjectionAsync(
            api,
            TenantId,
            PropertyId,
            "Operations House",
            "operations");

    private static async Task SeedPropertyProjectionAsync(
        AuthTestApplication api,
        string tenantId,
        Guid propertyId,
        string propertyName,
        string propertyCode)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(tenantId);
        IntegrationEventSubscription subscription = scope.ServiceProvider
            .GetRequiredService<IIntegrationEventSubscriptionRegistry>()
            .Subscriptions.Single(item => item.ConsumerModule == IngestionModuleMetadata.Name &&
                                          item.EventType == typeof(PropertyCreatedIntegrationEvent));
        var handler = (IIntegrationEventHandler<PropertyCreatedIntegrationEvent>)scope.ServiceProvider
            .GetRequiredService(subscription.HandlerType);
        IngestionDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<IngestionDbContext>();
        await ModuleTransactionIntegrationTestData.ExecuteAsync(
            dbContext,
            token => handler.HandleAsync(
                new(Guid.NewGuid(), tenantId, DateTimeOffset.UtcNow, propertyId, propertyName, propertyCode,
                    "UTC", PropertyStatus.Active, 1),
                token)).ConfigureAwait(false);
        await CountryPolicyIntegrationTestData.ApplyActivationAsync(
            scope.ServiceProvider,
            IngestionModuleMetadata.Name,
            tenantId,
            propertyId,
            2).ConfigureAwait(false);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task GrantAccessAsync(
        AdminCliTestApplication admin,
        Guid operatorId,
        Guid adminActorId)
    {
        await AssertAdminSuccessAsync(admin.ExecuteAsync("admin", "bootstrap", "--actor", "owner", "--yes"));
        await AssertAdminSuccessAsync(admin.ExecuteAsync(
            "admin", "roles", "create", "--actor", "owner", "--name", "ingestion-operator"));
        foreach (string permission in new[]
                 {
                     IngestionAdminPermissionCodes.Read,
                     IngestionAdminPermissionCodes.ConnectionsManage,
                     IngestionAdminPermissionCodes.CredentialsManage,
                     IngestionAdminPermissionCodes.RetentionManage,
                     IngestionAdminPermissionCodes.ReprocessingManage,
                     IngestionAdminPermissionCodes.LegalHoldsManage
                 })
        {
            await AssertAdminSuccessAsync(admin.ExecuteAsync(
                "admin", "roles", "grant", "--actor", "owner", "--role", "ingestion-operator",
                "--permission", permission));
        }

        await AssertAdminSuccessAsync(admin.ExecuteAsync(
            "admin", "roles", "assign", "--actor", "owner", "--target-kind", "user",
            "--target-id", operatorId.ToString("D"), "--role", "ingestion-operator",
            "--scope", $"tenant:{TenantId}/property:{PropertyId:D}"));
        await AssertAdminSuccessAsync(admin.ExecuteAsync(
            "admin", "roles", "assign", "--actor", "owner", "--target-kind", "admin-actor",
            "--target-id", adminActorId.ToString("D"), "--role", "ingestion-operator",
            "--scope", $"tenant:{TenantId}"));
        await AssertAdminSuccessAsync(admin.ExecuteAsync(
            "admin", "roles", "create", "--actor", "owner", "--name", "ingestion-ingress-controller"));
        await AssertAdminSuccessAsync(admin.ExecuteAsync(
            "admin", "roles", "grant", "--actor", "owner", "--role", "ingestion-ingress-controller",
            "--permission", IngestionAdminPermissionCodes.IngressControlManage));
        await AssertAdminSuccessAsync(admin.ExecuteAsync(
            "admin", "roles", "assign", "--actor", "owner", "--target-kind", "user",
            "--target-id", operatorId.ToString("D"), "--role", "ingestion-ingress-controller",
            "--scope", $"tenant:{TenantId}"));
    }

    private static async Task GrantRawPayloadAccessAsync(AdminCliTestApplication admin) =>
        await AssertAdminSuccessAsync(admin.ExecuteAsync(
            "admin", "roles", "grant", "--actor", "owner", "--role", "ingestion-operator",
            "--permission", IngestionAdminPermissionCodes.RawPayloadsRead));

    private static async Task SeedPushConnectionAsync(AuthTestApplication api)
    {
        await SeedPushConnectionAsync(
            api,
            TenantId,
            PropertyId,
            PushConnectionId).ConfigureAwait(false);
    }

    private static async Task SeedPushConnectionAsync(
        AuthTestApplication api,
        string tenantId,
        Guid propertyId,
        Guid connectionId)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(tenantId);
        IngestionDbContext dbContext = scope.ServiceProvider.GetRequiredService<IngestionDbContext>();
        AdapterConnection connection = AdapterConnection.Create(
            connectionId,
            tenantId,
            propertyId,
            FakeHttpAdapterDescriptor.Value.AdapterType,
            AdapterExecutionMode.Push,
            IngestionConflictPolicy.SuggestionsOnly,
            "configuration://integration-push",
            secretReference: null,
            DateTimeOffset.UtcNow).Value;
        dbContext.AdapterConnections.Add(connection);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task<CreateAdapterIngressCredentialResponse> SeedOtherTenantIngressAsync(
        AuthTestApplication api)
    {
        await SeedPropertyProjectionAsync(
            api,
            OtherTenantId,
            OtherTenantPropertyId,
            "Other Tenant House",
            "other-tenant").ConfigureAwait(false);
        await SeedPushConnectionAsync(
            api,
            OtherTenantId,
            OtherTenantPropertyId,
            OtherTenantPushConnectionId).ConfigureAwait(false);

        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(OtherTenantId);
        Result<CreateAdapterIngressCredentialResponse> created = await scope.ServiceProvider
            .GetRequiredService<IRequestDispatcher>()
            .SendAsync(
                new CreateAdapterIngressCredentialCommand(
                    Guid.NewGuid(),
                    OtherTenantPropertyId,
                    OtherTenantPushConnectionId,
                    "other tenant ingress",
                    ExpiresAtUtc: null,
                    CreatedBy: "integration:other-tenant"),
                CancellationToken.None)
            .ConfigureAwait(false);
        Assert.True(created.IsSuccess, created.Error.Message);
        return created.Value;
    }

    private static async Task SeedRemoteConnectionAsync(AuthTestApplication api)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantId);
        IngestionDbContext dbContext = scope.ServiceProvider.GetRequiredService<IngestionDbContext>();
        AdapterConnection connection = AdapterConnection.Create(
            RemoteConnectionId,
            TenantId,
            PropertyId,
            FakeHttpAdapterDescriptor.Value.AdapterType,
            AdapterExecutionMode.RemotePolling,
            IngestionConflictPolicy.SuggestionsOnly,
            "configuration://integration-remote",
            secretReference: null,
            DateTimeOffset.UtcNow).Value;
        dbContext.AdapterConnections.Add(connection);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task<int> CountConnectionManagementOperationsAsync(
        AuthTestApplication api,
        Guid connectionId,
        IngestionConnectionManagementMutationKind kind)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>()
            .SetTenant(TenantId);
        IngestionDbContext dbContext =
            scope.ServiceProvider.GetRequiredService<IngestionDbContext>();
        return await dbContext.ConnectionManagementOperations
            .AsNoTracking()
            .CountAsync(operation =>
                operation.ConnectionId == connectionId &&
                operation.Kind == kind)
            .ConfigureAwait(false);
    }

    private static async Task ProveRemoteAdapterLeasesAsync(
        AuthTestApplication api,
        HttpClient managementClient)
    {
        string credentialsPath =
            $"/api/ingestion/properties/{PropertyId:D}/connections/{RemoteConnectionId:D}/credentials";
        CreateAdapterIngressCredentialResponse credential =
            await PostAsync<CreateAdapterIngressCredentialResponse>(
                managementClient,
                credentialsPath,
                new
                {
                    operationId = Guid.NewGuid(),
                    label = "remote poller"
                }).ConfigureAwait(false);
        Assert.Equal(
            AdapterIngressCredentialIssuanceOutcome.Issued,
            credential.Outcome);
        string credentialToken = Assert.IsType<string>(credential.Token);
        using HttpClient adapterHttp = api.CreateClient();
        AdapterHttpIngressClient remoteClient = CreateAdapterClient(
            adapterHttp, credentialToken, RemoteConnectionId);
        Guid workerId = Guid.Parse("93000000-0000-0000-0000-000000000001");
        RemoteLeasedAdapterCycleRunner cycle = new(
            new RemoteIntegrationRunner(),
            remoteClient,
            new StandaloneMaterialProvider(),
            new AdapterRuntimeIdentity(
                TenantId,
                PropertyId,
                RemoteConnectionId,
                FakeHttpAdapterDescriptor.Value.AdapterType,
                TimeSpan.FromMinutes(5)),
            workerId,
            TimeSpan.FromSeconds(30));

        AdapterRunCompletion runtimeCompletion;
        try
        {
            runtimeCompletion = await cycle.RunAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            Assert.Fail($"Remote adapter cycle request failed with HTTP status {exception.StatusCode?.ToString() ?? "none"}.");
            throw;
        }
        Assert.Equal(AdapterRunOutcome.Succeeded, runtimeCompletion.Outcome);
        Assert.Equal("remote-cursor-1", runtimeCompletion.AcceptedCheckpoint);
        AdapterConnectionDto afterRuntime = await GetAsync<AdapterConnectionDto>(
            managementClient,
            $"/api/ingestion/properties/{PropertyId:D}/connections/{RemoteConnectionId:D}")
            .ConfigureAwait(false);
        Assert.Equal("remote-cursor-1", afterRuntime.Checkpoint);

        AdapterRemoteLeaseClaimRequest firstClaim = new(
            Guid.NewGuid(),
            Guid.Parse("93000000-0000-0000-0000-000000000002"),
            FakeHttpAdapterDescriptor.Value.AdapterType,
            FakeHttpAdapterDescriptor.Value.ProtocolVersion,
            FakeHttpAdapterDescriptor.Value.ConfigurationSchemaVersion,
            RequestedLeaseSeconds: 60);
        AdapterRemoteLeaseClaimRequest secondClaim = firstClaim with
        {
            ClaimId = Guid.NewGuid(),
            WorkerId = Guid.Parse("93000000-0000-0000-0000-000000000003")
        };
        Task<HttpResponseMessage> firstTask = SendAdapterControlAsync(
            adapterHttp, RemoteConnectionId, credentialToken, "remote-leases/claim", firstClaim);
        Task<HttpResponseMessage> secondTask = SendAdapterControlAsync(
            adapterHttp, RemoteConnectionId, credentialToken, "remote-leases/claim", secondClaim);
        HttpResponseMessage[] claims = await Task.WhenAll(firstTask, secondTask).ConfigureAwait(false);
        try
        {
            HttpResponseMessage winnerResponse = Assert.Single(claims, response => response.IsSuccessStatusCode);
            Assert.Single(claims, response => response.StatusCode == HttpStatusCode.Conflict);
            AdapterRemoteLeaseClaimResponse winner = (await winnerResponse.Content
                .ReadFromJsonAsync<AdapterRemoteLeaseClaimResponse>().ConfigureAwait(false))!;
            Assert.Equal("remote-cursor-1", winner.Assignment.Checkpoint);
            Guid winningWorker = claims[0].IsSuccessStatusCode
                ? firstClaim.WorkerId
                : secondClaim.WorkerId;
            AdapterRemoteLeaseProof staleProof = new(
                winner.Assignment.RunId,
                winner.Assignment.LeaseId,
                winner.LeaseEpoch,
                winningWorker);

            using (IServiceScope scope = api.Services.CreateScope())
            {
                IngestionDbContext dbContext = scope.ServiceProvider.GetRequiredService<IngestionDbContext>();
                DateTimeOffset expiredAt = DateTimeOffset.UtcNow.AddSeconds(-1);
                await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE ingestion.adapter_connections
                    SET "RemoteLeaseExpiresAtUtc" = {expiredAt}
                    WHERE "Id" = {RemoteConnectionId};
                    """).ConfigureAwait(false);
                await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE ingestion.runs
                    SET "RemoteLeaseExpiresAtUtc" = {expiredAt}
                    WHERE "Id" = {winner.Assignment.RunId};
                    """).ConfigureAwait(false);
            }

            AdapterRemoteLeaseClaimRequest takeoverRequest = firstClaim with
            {
                ClaimId = Guid.NewGuid(),
                WorkerId = Guid.Parse("93000000-0000-0000-0000-000000000004")
            };
            using HttpResponseMessage takeoverResponse = await SendAdapterControlAsync(
                adapterHttp,
                RemoteConnectionId,
                credentialToken,
                "remote-leases/claim",
                takeoverRequest).ConfigureAwait(false);
            AdapterRemoteLeaseClaimResponse takeover =
                await ReadSuccessAsync<AdapterRemoteLeaseClaimResponse>(takeoverResponse).ConfigureAwait(false);
            Assert.True(takeover.LeaseEpoch > winner.LeaseEpoch);
            Assert.Equal("remote-cursor-1", takeover.Assignment.Checkpoint);

            byte[] stalePayload = CreateCanonicalReservationPayload(sourceSequence: 4);
            AdapterObservedRecord staleRecord = CreateIngressRecord(
                Guid.Parse("92000000-0000-0000-0000-000000000004"),
                stalePayload,
                "remote-stale-4");
            using HttpResponseMessage staleSubmission = await SendAdapterControlAsync(
                adapterHttp,
                RemoteConnectionId,
                credentialToken,
                "remote-leases/observations",
                new AdapterRemoteObservationSubmissionRequest(
                    staleProof,
                    [AdapterIngressObservationRequest.FromRecord(staleRecord)],
                    "stale-checkpoint")).ConfigureAwait(false);
            AdapterRemoteObservationSubmissionResponse staleResult =
                await ReadSuccessAsync<AdapterRemoteObservationSubmissionResponse>(staleSubmission)
                    .ConfigureAwait(false);
            Assert.Equal(
                AdapterObservationDisposition.Rejected,
                Assert.Single(staleResult.Acknowledgement.Results).Disposition);
            Assert.False(staleResult.Acknowledgement.CheckpointAccepted);

            AdapterRemoteLeaseProof takeoverProof = new(
                takeover.Assignment.RunId,
                takeover.Assignment.LeaseId,
                takeover.LeaseEpoch,
                takeoverRequest.WorkerId);
            using HttpResponseMessage completed = await SendAdapterControlAsync(
                adapterHttp,
                RemoteConnectionId,
                credentialToken,
                "remote-leases/complete",
                new AdapterRemoteRunCompletionRequest(
                    takeoverProof,
                    AdapterRunOutcome.Succeeded,
                    0,
                    0,
                    0,
                    "remote-cursor-1",
                    ErrorCode: null)).ConfigureAwait(false);
            _ = await ReadSuccessAsync<AdapterRemoteRunCompletionResponse>(completed).ConfigureAwait(false);
        }
        finally
        {
            foreach (HttpResponseMessage response in claims)
            {
                response.Dispose();
            }
        }

        using HttpResponseMessage directPushBypass = await SendIngressAsync(
            adapterHttp,
            TenantId,
            RemoteConnectionId,
            credentialToken,
            CreateIngressSubmission(Guid.NewGuid(), "{}"u8.ToArray(), "remote-bypass"))
            .ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.Unauthorized, directPushBypass.StatusCode);

        IngestionRunListResponse remoteRuns = await GetAsync<IngestionRunListResponse>(
            managementClient,
            $"/api/ingestion/properties/{PropertyId:D}/runs?connectionId={RemoteConnectionId:D}")
            .ConfigureAwait(false);
        Assert.Equal(3, remoteRuns.Runs.Count);
        Assert.Equal(2, remoteRuns.Runs.Count(run => run.Status == IngestionRunStatus.Succeeded));
        List<IngestionRunDto> remoteRunDetails = [];
        foreach (IngestionRunListItemDto run in remoteRuns.Runs)
        {
            remoteRunDetails.Add(await GetAsync<IngestionRunDto>(
                managementClient,
                $"/api/ingestion/properties/{PropertyId:D}/runs/{run.RunId:D}").ConfigureAwait(false));
        }

        Assert.All(remoteRunDetails, run =>
            Assert.Equal(IngestionRunExecutionKindDto.RemoteLease, run.ExecutionKind));
        IngestionRunDto heartbeatRun = Assert.Single(remoteRunDetails, run => run.ObservedCount == 1);
        Assert.True(heartbeatRun.Version >= 3);
        IngestionRunDto expiredRun = Assert.Single(
            remoteRunDetails,
            run => run.Status == IngestionRunStatus.Failed);
        Assert.Equal("ingestion.remote-lease-expired", expiredRun.ErrorCode);
    }

    private static async Task ProveAdapterIngressAsync(
        AuthTestApplication api,
        AdminCliTestApplication admin,
        HttpClient managementClient,
        CreateAdapterIngressCredentialResponse otherTenantCredential)
    {
        string credentialsPath =
            $"/api/ingestion/properties/{PropertyId:D}/connections/{PushConnectionId:D}/credentials";
        Guid primaryIssueOperationId = Guid.NewGuid();
        object primaryIssueRequest = new
        {
            operationId = primaryIssueOperationId,
            label = "integration primary"
        };
        Task<HttpResponseMessage>[] issueRequests =
        [
            managementClient.PostAsJsonAsync(
                credentialsPath,
                primaryIssueRequest),
            managementClient.PostAsJsonAsync(
                credentialsPath,
                primaryIssueRequest)
        ];
        HttpResponseMessage[] issueResponses =
            await Task.WhenAll(issueRequests).ConfigureAwait(false);
        CreateAdapterIngressCredentialResponse[] issueResults;
        try
        {
            issueResults = await Task.WhenAll(issueResponses.Select(
                ReadSuccessAsync<CreateAdapterIngressCredentialResponse>))
                .ConfigureAwait(false);
            Assert.All(
                issueResponses,
                response => Assert.True(response.Headers.CacheControl?.NoStore));
        }
        finally
        {
            foreach (HttpResponseMessage response in issueResponses)
            {
                response.Dispose();
            }
        }

        CreateAdapterIngressCredentialResponse primary = Assert.Single(
            issueResults,
            result => result.Outcome ==
                AdapterIngressCredentialIssuanceOutcome.Issued);
        CreateAdapterIngressCredentialResponse primaryReplay = Assert.Single(
            issueResults,
            result => result.Outcome ==
                AdapterIngressCredentialIssuanceOutcome.AlreadyIssued);
        string primaryToken = Assert.IsType<string>(primary.Token);
        Assert.Null(primaryReplay.Token);
        Assert.Equal(primary.Credential, primaryReplay.Credential);
        Assert.StartsWith("bfi_v1_", primaryToken, StringComparison.Ordinal);
        Assert.Equal(
            1,
            await CountConnectionManagementOperationsAsync(
                api,
                PushConnectionId,
                IngestionConnectionManagementMutationKind
                    .AdapterIngressCredentialCreate).ConfigureAwait(false));
        Assert.Equal(FakeHttpAdapterDescriptor.Value.AdapterType, primary.Credential.AdapterType);
        Assert.Equal(1, primary.Credential.AdapterProtocolVersion);
        Assert.Equal(1, primary.Credential.ConfigurationSchemaVersion);
        Assert.Equal(FakeHttpAdapterDescriptor.Value.AdapterType, primary.Credential.SourceSystem);
        AdapterIngressCredentialListResponse initialList = await GetAsync<AdapterIngressCredentialListResponse>(
            managementClient, credentialsPath).ConfigureAwait(false);
        AdapterIngressCredentialListItemDto listedPrimary = Assert.Single(initialList.Credentials);
        string listedJson = JsonSerializer.Serialize(initialList);
        Assert.DoesNotContain(primaryToken, listedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("secretHash", listedJson, StringComparison.OrdinalIgnoreCase);
        AdminCliResult cliList = await admin.ExecuteAsync(
            "ingestion", "credentials", "list",
            "--actor", "owner",
            "--tenant", TenantId,
            "--property-id", PropertyId.ToString("D"),
            "--connection-id", PushConnectionId.ToString("D"),
            "--output", "json");
        Assert.Equal(AdminExitCodes.Success, cliList.ExitCode);
        Assert.DoesNotContain(primaryToken, cliList.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("secretHash", cliList.Output, StringComparison.OrdinalIgnoreCase);

        byte[] payload = CreateCanonicalReservationPayload(sourceSequence: 1);
        Guid operationId = Guid.Parse("92000000-0000-0000-0000-000000000001");
        object submission = CreateIngressSubmission(operationId, payload, "remote-push-1");
        AdapterObservedRecord observedRecord = CreateIngressRecord(operationId, payload, "remote-push-1");
        using HttpClient ingressClient = api.CreateClient();
        AdapterHttpIngressClient otherTenantIngress = CreateAdapterClient(
            ingressClient,
            Assert.IsType<string>(otherTenantCredential.Token),
            OtherTenantId,
            OtherTenantPushConnectionId);

        using (HttpResponseMessage missing = await SendIngressAsync(
                   ingressClient, TenantId, PushConnectionId, token: null, submission).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        }
        using (HttpResponseMessage staffJwt = await managementClient.PostAsJsonAsync(
                   $"/api/ingestion/adapter-ingress/connections/{PushConnectionId:D}/observations",
                   submission).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, staffJwt.StatusCode);
        }
        using (HttpResponseMessage wrongTenant = await SendIngressAsync(
                   ingressClient, "tenant-other", PushConnectionId, primaryToken, submission).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, wrongTenant.StatusCode);
        }
        using (HttpResponseMessage wrongConnection = await SendIngressAsync(
                   ingressClient, TenantId, Guid.NewGuid(), primaryToken, submission).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, wrongConnection.StatusCode);
        }

        AdapterHttpIngressClient primaryIngress = CreateIngressClient(ingressClient, primaryToken);
        AdapterIngressSubmissionResponse accepted = await primaryIngress.SubmitAsync(
            [observedRecord], CancellationToken.None).ConfigureAwait(false);
        Assert.Equal(AdapterObservationDisposition.Accepted, Assert.Single(accepted.Results).Disposition);
        AdapterIngressSubmissionResponse duplicate = await primaryIngress.SubmitAsync(
            [observedRecord], CancellationToken.None).ConfigureAwait(false);
        Assert.Equal(AdapterObservationDisposition.Duplicate, Assert.Single(duplicate.Results).Disposition);
        ObservationReceiptListResponse ingressReceipts = await GetAsync<ObservationReceiptListResponse>(
            managementClient,
            $"/api/ingestion/properties/{PropertyId:D}/receipts?connectionId={PushConnectionId:D}")
            .ConfigureAwait(false);
        ObservationReceiptListItemDto ingressReceiptItem = Assert.Single(
            ingressReceipts.Receipts,
            receipt => receipt.ExternalId == "remote-push-1");
        ObservationReceiptDto ingressReceipt = await GetAsync<ObservationReceiptDto>(
            managementClient,
            $"/api/ingestion/properties/{PropertyId:D}/receipts/{ingressReceiptItem.ReceiptId:D}")
            .ConfigureAwait(false);
        Assert.Equal(primary.Credential.CredentialId, ingressReceipt.IngressCredentialId);
        Assert.Equal(primary.Credential.AdapterType, ingressReceipt.AdapterType);
        Assert.Equal(primary.Credential.AdapterProtocolVersion, ingressReceipt.AdapterProtocolVersion);
        Assert.Equal(primary.Credential.ConfigurationSchemaVersion, ingressReceipt.ConfigurationSchemaVersion);
        Assert.Equal(primary.Credential.SourceSystem, ingressReceipt.SourceSystem);
        Assert.Equal(primary.Credential.CreatedBy, ingressReceipt.CustomerOwner);

        CreateAdapterIngressCredentialResponse secondary = await PostAsync<CreateAdapterIngressCredentialResponse>(
            managementClient,
            credentialsPath,
            new
            {
                operationId = Guid.NewGuid(),
                label = "integration rotated"
            }).ConfigureAwait(false);
        Assert.Equal(
            AdapterIngressCredentialIssuanceOutcome.Issued,
            secondary.Outcome);
        string secondaryToken = Assert.IsType<string>(secondary.Token);
        Assert.NotEqual(primaryToken, secondaryToken);

        Guid primaryRevokeOperationId = Guid.NewGuid();
        object primaryRevokeRequest = new
        {
            operationId = primaryRevokeOperationId,
            expectedVersion = primary.Credential.Version
        };
        Task<HttpResponseMessage>[] revokeRequests =
        [
            managementClient.PostAsJsonAsync(
                $"{credentialsPath}/{primary.Credential.CredentialId:D}/revoke",
                primaryRevokeRequest),
            managementClient.PostAsJsonAsync(
                $"{credentialsPath}/{primary.Credential.CredentialId:D}/revoke",
                primaryRevokeRequest)
        ];
        HttpResponseMessage[] revokeResponses =
            await Task.WhenAll(revokeRequests).ConfigureAwait(false);
        AdapterIngressCredentialMutationReceiptDto[] revokeResults;
        try
        {
            revokeResults = await Task.WhenAll(revokeResponses.Select(
                ReadSuccessAsync<AdapterIngressCredentialMutationReceiptDto>))
                .ConfigureAwait(false);
        }
        finally
        {
            foreach (HttpResponseMessage response in revokeResponses)
            {
                response.Dispose();
            }
        }

        Assert.Equal(revokeResults[0], revokeResults[1]);
        Assert.All(
            revokeResults,
            revoked => Assert.Equal(
                AdapterIngressCredentialStatus.Revoked,
                revoked.Status));
        Assert.Equal(
            1,
            await CountConnectionManagementOperationsAsync(
                api,
                PushConnectionId,
                IngestionConnectionManagementMutationKind
                    .AdapterIngressCredentialRevoke).ConfigureAwait(false));
        using (HttpResponseMessage revokedIngress = await SendIngressAsync(
                   ingressClient, TenantId, PushConnectionId, primaryToken, submission).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, revokedIngress.StatusCode);
        }
        AdapterHttpIngressClient secondaryIngress = CreateIngressClient(ingressClient, secondaryToken);
        AdapterObservationResult otherTenantAfterRevocation = await SubmitCanonicalAsync(
            otherTenantIngress,
            sourceSequence: 10,
            "other-after-credential-revocation").ConfigureAwait(false);
        Assert.Equal(AdapterObservationDisposition.Accepted, otherTenantAfterRevocation.Disposition);

        AdapterIngressTenantControlDto initialTenantControl =
            await GetAsync<AdapterIngressTenantControlDto>(
                managementClient,
                "/api/ingestion/adapter-ingress-control").ConfigureAwait(false);
        Assert.False(initialTenantControl.IsSuspended);
        Assert.Equal(0, initialTenantControl.Version);
        AdapterIngressTenantControlDto suspended = await PostAsync<AdapterIngressTenantControlDto>(
            managementClient,
            "/api/ingestion/adapter-ingress-control/suspend",
            new
            {
                expectedVersion = initialTenantControl.Version,
                reasonCode = "security.drill"
            }).ConfigureAwait(false);
        Assert.True(suspended.IsSuspended);
        Assert.Equal(1, suspended.Version);

        AdapterObservationResult tenantStopped = await SubmitCanonicalAsync(
            secondaryIngress,
            sourceSequence: 11,
            "tenant-stopped").ConfigureAwait(false);
        Assert.Equal(AdapterObservationDisposition.Rejected, tenantStopped.Disposition);
        Assert.Equal(
            AdapterErrorCode(IngestionApplicationErrors.AdapterIngressTenantSuspended),
            tenantStopped.ErrorCode);
        AdapterObservationResult otherTenantStillActive = await SubmitCanonicalAsync(
            otherTenantIngress,
            sourceSequence: 12,
            "other-while-tenant-stopped").ConfigureAwait(false);
        Assert.Equal(AdapterObservationDisposition.Accepted, otherTenantStillActive.Disposition);

        AdminCliResult globalStatus = await admin.ExecuteAsync(
            "ingestion", "ingress-control", "status",
            "--actor", "owner",
            "--output", "json");
        Assert.Equal(AdminExitCodes.Success, globalStatus.ExitCode);
        AdminCliResult globalConfirmationRequired = await admin.ExecuteAsync(
            "ingestion", "ingress-control", "stop",
            "--actor", "owner",
            "--expected-version", "0",
            "--reason-code", "incident.drill");
        Assert.NotEqual(AdminExitCodes.Success, globalConfirmationRequired.ExitCode);
        Assert.Contains(
            AdminErrors.ConfirmationRequired.Message,
            globalConfirmationRequired.Error,
            StringComparison.Ordinal);
        await AssertAdminSuccessAsync(admin.ExecuteAsync(
            "ingestion", "ingress-control", "stop",
            "--actor", "owner",
            "--expected-version", "0",
            "--reason-code", "incident.drill",
            "--yes"));

        AdapterObservationResult globalStoppedTenant = await SubmitCanonicalAsync(
            secondaryIngress,
            sourceSequence: 13,
            "global-stopped-tenant").ConfigureAwait(false);
        AdapterObservationResult globalStoppedOther = await SubmitCanonicalAsync(
            otherTenantIngress,
            sourceSequence: 14,
            "global-stopped-other").ConfigureAwait(false);
        Assert.Equal(AdapterObservationDisposition.Rejected, globalStoppedTenant.Disposition);
        Assert.Equal(AdapterObservationDisposition.Rejected, globalStoppedOther.Disposition);
        Assert.Equal(
            AdapterErrorCode(IngestionApplicationErrors.AdapterIngressGloballyStopped),
            globalStoppedTenant.ErrorCode);
        Assert.Equal(
            AdapterErrorCode(IngestionApplicationErrors.AdapterIngressGloballyStopped),
            globalStoppedOther.ErrorCode);

        await AssertAdminSuccessAsync(admin.ExecuteAsync(
            "ingestion", "ingress-control", "resume",
            "--actor", "owner",
            "--expected-version", "1",
            "--reason-code", "incident.resolved",
            "--yes"));
        AdapterObservationResult otherTenantRestored = await SubmitCanonicalAsync(
            otherTenantIngress,
            sourceSequence: 15,
            "other-after-global-resume").ConfigureAwait(false);
        Assert.Equal(AdapterObservationDisposition.Accepted, otherTenantRestored.Disposition);
        AdapterObservationResult tenantStillSuspended = await SubmitCanonicalAsync(
            secondaryIngress,
            sourceSequence: 16,
            "tenant-after-global-resume").ConfigureAwait(false);
        Assert.Equal(AdapterObservationDisposition.Rejected, tenantStillSuspended.Disposition);
        Assert.Equal(
            AdapterErrorCode(IngestionApplicationErrors.AdapterIngressTenantSuspended),
            tenantStillSuspended.ErrorCode);

        AdapterIngressTenantControlDto resumed = await PostAsync<AdapterIngressTenantControlDto>(
            managementClient,
            "/api/ingestion/adapter-ingress-control/resume",
            new
            {
                expectedVersion = suspended.Version,
                reasonCode = "security.drill-complete"
            }).ConfigureAwait(false);
        Assert.False(resumed.IsSuspended);
        Assert.Equal(2, resumed.Version);
        AdapterObservationResult tenantRestored = await SubmitCanonicalAsync(
            secondaryIngress,
            sourceSequence: 17,
            "tenant-after-resume").ConfigureAwait(false);
        Assert.Equal(AdapterObservationDisposition.Accepted, tenantRestored.Disposition);
        await ProveProviderLossIsolationAsync(
            api,
            managementClient,
            secondaryToken).ConfigureAwait(false);

        MemoryCheckpointLease runtimeCheckpoint = new(PushConnectionId);
        StandaloneAdapterCycleRunner standaloneCycle = new(
            new StandaloneIntegrationRunner(),
            secondaryIngress,
            runtimeCheckpoint,
            new StandaloneMaterialProvider(),
            new AdapterRuntimeIdentity(
                TenantId,
                PropertyId,
                PushConnectionId,
                FakeHttpAdapterDescriptor.Value.AdapterType,
                TimeSpan.FromMinutes(5)));
        AdapterRunCompletion standaloneCompletion = await standaloneCycle.RunAsync(CancellationToken.None)
            .ConfigureAwait(false);
        Assert.Equal(AdapterRunOutcome.Succeeded, standaloneCompletion.Outcome);
        Assert.Equal("standalone-cursor-3", runtimeCheckpoint.Checkpoint);
        Assert.Equal(1, runtimeCheckpoint.Generation);
        ObservationReceiptListResponse standaloneReceipts = await GetAsync<ObservationReceiptListResponse>(
            managementClient,
            $"/api/ingestion/properties/{PropertyId:D}/receipts?connectionId={PushConnectionId:D}")
            .ConfigureAwait(false);
        Assert.Contains(standaloneReceipts.Receipts, receipt =>
            receipt.ExternalId == "remote-standalone-3");

        AdapterConnectionMutationReceiptDto disabled = await PostAsync<AdapterConnectionMutationReceiptDto>(
            managementClient,
            $"/api/ingestion/properties/{PropertyId:D}/connections/{PushConnectionId:D}/disable",
            new
            {
                operationId = Guid.NewGuid(),
                expectedVersion = 1L
            }).ConfigureAwait(false);
        Assert.Equal(AdapterConnectionStatus.Disabled, disabled.Status);
        AdapterObservedRecord disabledRecord = CreateIngressRecord(
            Guid.Parse("92000000-0000-0000-0000-000000000002"), payload, "remote-push-2");
        using HttpResponseMessage disabledResponse = await SendIngressAsync(
            ingressClient,
            TenantId,
            PushConnectionId,
            secondaryToken,
            CreateIngressSubmission(
                disabledRecord.OperationId,
                disabledRecord.Payload.ToArray(),
                disabledRecord.ExternalRecordId)).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.Unauthorized, disabledResponse.StatusCode);

        AdapterIngressCredentialListResponse finalList = await GetAsync<AdapterIngressCredentialListResponse>(
            managementClient, credentialsPath).ConfigureAwait(false);
        Assert.Equal(2, finalList.Credentials.Count);
        Assert.False(finalList.HasMore);
        Assert.Contains(finalList.Credentials, credential =>
            credential.CredentialId == listedPrimary.CredentialId && credential.LastAuthenticatedAtUtc.HasValue);

        Guid secondaryRevokeOperationId = Guid.NewGuid();
        AdminCliResult confirmationRequired = await admin.ExecuteAsync(
            "ingestion", "credentials", "revoke",
            "--actor", "owner",
            "--tenant", TenantId,
            "--operation-id", secondaryRevokeOperationId.ToString("D"),
            "--property-id", PropertyId.ToString("D"),
            "--connection-id", PushConnectionId.ToString("D"),
            "--credential-id", secondary.Credential.CredentialId.ToString("D"),
            "--expected-version", secondary.Credential.Version.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        Assert.NotEqual(AdminExitCodes.Success, confirmationRequired.ExitCode);
        Assert.Contains(AdminErrors.ConfirmationRequired.Message, confirmationRequired.Error, StringComparison.Ordinal);
        await AssertAdminSuccessAsync(admin.ExecuteAsync(
            "ingestion", "credentials", "revoke",
            "--actor", "owner",
            "--tenant", TenantId,
            "--operation-id", secondaryRevokeOperationId.ToString("D"),
            "--property-id", PropertyId.ToString("D"),
            "--connection-id", PushConnectionId.ToString("D"),
            "--credential-id", secondary.Credential.CredentialId.ToString("D"),
            "--expected-version", secondary.Credential.Version.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            "--yes"));
        Assert.Equal(0, await admin.CountAuditEntriesContainingAsync(primaryToken).ConfigureAwait(false));
        Assert.Equal(0, await admin.CountAuditEntriesContainingAsync(secondaryToken).ConfigureAwait(false));
    }

    private static async Task ProveProviderLossIsolationAsync(
        AuthTestApplication api,
        HttpClient managementClient,
        string token)
    {
        using var unavailableApi = api.WithUnavailableAdapterIngressProvider();
        using HttpClient unavailableIngressHttp = unavailableApi.CreateClient();
        AdapterHttpIngressClient unavailableIngress = CreateIngressClient(
            unavailableIngressHttp,
            token);
        AdapterObservationResult denied = await SubmitCanonicalAsync(
            unavailableIngress,
            sourceSequence: 18,
            "provider-unavailable").ConfigureAwait(false);

        Assert.Equal(AdapterObservationDisposition.Rejected, denied.Disposition);
        Assert.Equal(
            AdapterErrorCode(IngestionApplicationErrors.AdapterIngressControlUnavailable),
            denied.ErrorCode);

        using HttpClient staffClient = unavailableApi.CreateClient();
        staffClient.DefaultRequestHeaders.Authorization =
            managementClient.DefaultRequestHeaders.Authorization;
        staffClient.DefaultRequestHeaders.Add("X-Tenant-Id", TenantId);
        using HttpResponseMessage staffResponse = await staffClient.GetAsync(
            $"/api/ingestion/properties/{PropertyId:D}/connections").ConfigureAwait(false);
        Assert.True(
            staffResponse.IsSuccessStatusCode,
            await staffResponse.Content.ReadAsStringAsync().ConfigureAwait(false));
    }

    private static object CreateIngressSubmission(Guid operationId, byte[] payload, string externalId) => new
    {
        records = new[]
        {
            new
            {
                operationId,
                recordType = "reservation.v1",
                externalRecordId = externalId,
                sourceRevision = "1",
                sourceUpdatedAtUtc = DateTimeOffset.UtcNow,
                observedAtUtc = DateTimeOffset.UtcNow,
                contentType = "application/json",
                payload,
                contentSha256 = AdapterPayloadHash.ComputeSha256(payload)
            }
        }
    };

    private static AdapterObservedRecord CreateIngressRecord(
        Guid operationId,
        byte[] payload,
        string externalId) => new(
            operationId,
            "reservation.v1",
            externalId,
            "1",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "application/json",
            payload,
            AdapterPayloadHash.ComputeSha256(payload));

    private static AdapterHttpIngressClient CreateIngressClient(HttpClient client, string token) =>
        CreateAdapterClient(client, token, TenantId, PushConnectionId);

    private static async Task<AdapterObservationResult> SubmitCanonicalAsync(
        AdapterHttpIngressClient client,
        long sourceSequence,
        string externalId)
    {
        byte[] payload = CreateCanonicalReservationPayload(sourceSequence);
        AdapterIngressSubmissionResponse response = await client.SubmitAsync(
            [CreateIngressRecord(Guid.NewGuid(), payload, externalId)],
            CancellationToken.None).ConfigureAwait(false);
        return Assert.Single(response.Results);
    }

    private static byte[] CreateCanonicalReservationPayload(long sourceSequence) =>
        Encoding.UTF8.GetBytes($$"""
            {"operation":"upsert","sourceSequence":{{sourceSequence}},"arrival":"2026-08-01","departure":"2026-08-03","inventoryUnitIds":["20000000-0000-0000-0000-000000000001"],"primaryGuestName":"Integration Guest","email":"guest@example.test","phone":null,"guestCount":1,"notes":null}
            """);

    private static string AdapterErrorCode(Error error) => error.Code.ToLowerInvariant();

    private static AdapterHttpIngressClient CreateAdapterClient(
        HttpClient client,
        string token,
        Guid connectionId) =>
        CreateAdapterClient(client, token, TenantId, connectionId);

    private static AdapterHttpIngressClient CreateAdapterClient(
        HttpClient client,
        string token,
        string tenantId,
        Guid connectionId) => new(
        client,
        new AdapterHttpIngressOptions(
            client.BaseAddress!,
            tenantId,
            connectionId,
            maxAttempts: 2,
            retryBaseDelay: TimeSpan.FromMilliseconds(10),
            retryMaxDelay: TimeSpan.FromMilliseconds(10),
            retryJitterFactor: 0,
            allowInsecureLoopback: true),
        new StaticAdapterIngressTokenProvider(token));

    private sealed class RemoteIntegrationRunner : IAdapterRunner
    {
        private static readonly byte[] Payload = CreateCanonicalReservationPayload(sourceSequence: 5);

        public AdapterDescriptor Descriptor => FakeHttpAdapterDescriptor.Value;

        public async Task<AdapterRunCompletion> RunAsync(
            AdapterRunAssignment assignment,
            AdapterConfigurationMaterial material,
            IAdapterObservationSink sink,
            CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(12), cancellationToken).ConfigureAwait(false);
            AdapterObservedRecord record = new(
                Guid.Parse("92000000-0000-0000-0000-000000000005"),
                "reservation.v1",
                "remote-leased-5",
                "5",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                "application/json",
                Payload,
                AdapterPayloadHash.ComputeSha256(Payload));
            AdapterObservationAcknowledgement acknowledgement = await sink.SubmitAsync(
                new AdapterObservationSubmission(
                    assignment.RunId,
                    assignment.LeaseId,
                    [record],
                    "  remote-cursor-1  "),
                cancellationToken).ConfigureAwait(false);
            Assert.True(acknowledgement.CheckpointAccepted);
            Assert.Equal("remote-cursor-1", acknowledgement.AcceptedCheckpoint);
            Assert.Equal(AdapterObservationDisposition.Accepted, Assert.Single(acknowledgement.Results).Disposition);
            return new AdapterRunCompletion(
                assignment.RunId,
                assignment.LeaseId,
                AdapterRunOutcome.Succeeded,
                1,
                1,
                0,
                acknowledgement.AcceptedCheckpoint,
                errorCode: null,
                errorMessage: null);
        }
    }

    private sealed class StandaloneIntegrationRunner : IAdapterRunner
    {
        private static readonly byte[] Payload = CreateCanonicalReservationPayload(sourceSequence: 3);

        public static AdapterObservedRecord Record { get; } = new(
            Guid.Parse("92000000-0000-0000-0000-000000000003"),
            "reservation.v1",
            "remote-standalone-3",
            "3",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "application/json",
            Payload,
            AdapterPayloadHash.ComputeSha256(Payload));

        public AdapterDescriptor Descriptor => FakeHttpAdapterDescriptor.Value;

        public async Task<AdapterRunCompletion> RunAsync(
            AdapterRunAssignment assignment,
            AdapterConfigurationMaterial material,
            IAdapterObservationSink sink,
            CancellationToken cancellationToken)
        {
            AdapterObservationAcknowledgement acknowledgement = await sink.SubmitAsync(
                new AdapterObservationSubmission(
                    assignment.RunId,
                    assignment.LeaseId,
                    [Record],
                    "standalone-cursor-3"),
                cancellationToken).ConfigureAwait(false);
            AdapterObservationResult result = Assert.Single(acknowledgement.Results);
            Assert.Equal(AdapterObservationDisposition.Accepted, result.Disposition);
            Assert.True(acknowledgement.CheckpointAccepted);
            return new AdapterRunCompletion(
                assignment.RunId,
                assignment.LeaseId,
                AdapterRunOutcome.Succeeded,
                observedCount: 1,
                acceptedCount: 1,
                rejectedCount: 0,
                acknowledgement.AcceptedCheckpoint,
                errorCode: null,
                errorMessage: null);
        }
    }

    private sealed class StandaloneMaterialProvider : IAdapterRuntimeMaterialProvider
    {
        public Task<AdapterConfigurationMaterial> ResolveAsync(
            AdapterRuntimeIdentity identity,
            int configurationSchemaVersion,
            CancellationToken cancellationToken) => Task.FromResult(new AdapterConfigurationMaterial(
            configurationSchemaVersion,
            "application/json",
            "{}"u8));
    }

    private sealed class MemoryCheckpointLease(Guid connectionId) : IAdapterCheckpointLease
    {
        public Guid ConnectionId { get; } = connectionId;
        public string? Checkpoint { get; private set; }
        public long Generation { get; private set; }

        public Task SaveAsync(
            string checkpoint,
            DateTimeOffset updatedAtUtc,
            CancellationToken cancellationToken)
        {
            this.Checkpoint = checkpoint;
            this.Generation++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task<HttpResponseMessage> SendIngressAsync(
        HttpClient client,
        string tenantId,
        Guid connectionId,
        string? token,
        object submission)
    {
        HttpRequestMessage request = new(
            HttpMethod.Post,
            $"/api/ingestion/adapter-ingress/connections/{connectionId:D}/observations")
        {
            Content = JsonContent.Create(submission)
        };
        request.Headers.Add("X-Tenant-Id", tenantId);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("BunkFy-Adapter", token);
        }

        return await client.SendAsync(request).ConfigureAwait(false);
    }

    private static async Task<HttpResponseMessage> SendAdapterControlAsync<TRequest>(
        HttpClient client,
        Guid connectionId,
        string token,
        string action,
        TRequest content)
    {
        HttpRequestMessage request = new(
            HttpMethod.Post,
            $"/api/ingestion/adapter-ingress/connections/{connectionId:D}/{action}")
        {
            Content = JsonContent.Create(content)
        };
        request.Headers.Add("X-Tenant-Id", TenantId);
        request.Headers.Authorization = new AuthenticationHeaderValue("BunkFy-Adapter", token);
        return await client.SendAsync(request).ConfigureAwait(false);
    }

    private static async Task AssertScheduleDiscoveryAsync(
        AuthTestApplication api,
        Guid connectionId,
        int expectedCount)
    {
        using IServiceScope scope = api.Services.CreateScope();
        IReadOnlyList<AdapterPollingScheduleDefinition> schedules = await scope.ServiceProvider
            .GetRequiredService<IAdapterPollingScheduleReader>()
            .ListActiveAsync(CancellationToken.None).ConfigureAwait(false);
        AdapterPollingScheduleDefinition[] matching = schedules
            .Where(item => item.ConnectionId == connectionId)
            .ToArray();
        Assert.Equal(expectedCount, matching.Length);
        if (expectedCount == 1)
        {
            AdapterPollingScheduleDefinition schedule = Assert.Single(matching);
            Assert.Equal(TenantId, schedule.ScopeId);
            Assert.Equal(300, schedule.IntervalSeconds);
            Assert.InRange(schedule.MaxAttempts, 3, 4);
        }
    }

    private static async Task<(Guid ReceiptId, byte[] Payload)> SubmitObservationAsync(
        AuthTestApplication api,
        Guid connectionId)
    {
        byte[] payload = Encoding.UTF8.GetBytes(/*lang=json,strict*/ "{\"reservation\":\"booking-operations-42\"}");
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantId);
        Result<AdapterObservationResult> result = await scope.ServiceProvider
            .GetRequiredService<IRequestDispatcher>()
            .SendAsync(
                new ReceiveObservationCommand(
                    connectionId,
                    RunId: null,
                    Guid.NewGuid(),
                    "operations.raw.v1",
                    "booking-operations-42",
                    "1",
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    "application/json",
                    payload,
                    AdapterPayloadHash.ComputeSha256(payload)),
                CancellationToken.None).ConfigureAwait(false);
        Assert.True(result.IsSuccess, result.Error.Code);
        Assert.NotNull(result.Value.ReceiptId);
        return (result.Value.ReceiptId.Value, payload);
    }

    private static async Task PurgeRawPayloadAsync(
        AuthTestApplication api,
        AdminCliTestApplication admin,
        HttpClient adminClient,
        Guid receiptId)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantId);
        IngestionDbContext dbContext = scope.ServiceProvider.GetRequiredService<IngestionDbContext>();
        ObservationReceipt receipt = await dbContext.ObservationReceipts.SingleAsync(
            item => item.Id == receiptId).ConfigureAwait(false);
        Assert.True(receipt.Reject("retention integration test", DateTimeOffset.UtcNow).IsSuccess);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
        dbContext.ChangeTracker.Clear();
        await dbContext.ObservationReceipts
            .Where(item => item.Id == receiptId)
            .ExecuteUpdateAsync(update => update.SetProperty(
                item => item.RawPayloadRetainUntilUtc,
                DateTimeOffset.UtcNow.AddMinutes(-1))).ConfigureAwait(false);

        Guid claimId = Guid.NewGuid();
        IRequestDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IRequestDispatcher>();
        DateTimeOffset proposalCreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-3);
        ChangeProposal proposal = ChangeProposal.Create(
            Guid.NewGuid(), TenantId, receipt.PropertyId, receipt.ConnectionId, receipt.Id,
            Guid.NewGuid(), receipt.RawPayloadFileId, 1, "retention-test", /*lang=json,strict*/ "{\"guest\":\"Sensitive\"}",
            proposalCreatedAtUtc).Value;
        dbContext.ChangeProposals.Add(proposal);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);

        Result<IReadOnlyList<RawPayloadPurgeCandidate>> pendingClaim = await dispatcher.SendAsync(
            new ClaimExpiredRawPayloadsCommand(
                Guid.NewGuid(),
                BatchSize: 10,
                PurgeExpiredRawPayloadsPayload.DefaultStaleClaimMinutes),
            CancellationToken.None).ConfigureAwait(false);
        Assert.True(pendingClaim.IsSuccess, pendingClaim.Error.Code);
        Assert.Empty(pendingClaim.Value);

        Assert.True(proposal.BeginApply(
            "staff:retention-test",
            Guid.NewGuid(),
            proposal.Version,
            proposalCreatedAtUtc.AddDays(1)).IsSuccess);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
        Result<IReadOnlyList<RawPayloadPurgeCandidate>> applyingClaim = await dispatcher.SendAsync(
            new ClaimExpiredRawPayloadsCommand(
                Guid.NewGuid(),
                BatchSize: 10,
                PurgeExpiredRawPayloadsPayload.DefaultStaleClaimMinutes),
            CancellationToken.None).ConfigureAwait(false);
        Assert.True(applyingClaim.IsSuccess, applyingClaim.Error.Code);
        Assert.Empty(applyingClaim.Value);

        DateTimeOffset proposalCompletedAtUtc = proposalCreatedAtUtc.AddDays(1).AddMinutes(1);
        Assert.True(proposal.MarkFailed(
            "Retention integration test",
            proposal.Version,
            proposalCompletedAtUtc.AddDays(1),
            proposalCompletedAtUtc).IsSuccess);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);

        LegalHoldDto firstPlaced = await PostAsync<LegalHoldDto>(
            adminClient,
            $"/api/admin/ingestion/properties/{receipt.PropertyId:D}/legal-holds",
            new { reason = "Regulatory matter 2026-01" }).ConfigureAwait(false);
        Result<LegalHoldDto> firstHold = Result.Success(firstPlaced);
        Result<LegalHoldDto> secondHold = await dispatcher.SendAsync(
            new PlaceLegalHoldCommand(
                receipt.PropertyId,
                "Litigation matter 2026-02",
                "integration:legal"),
            CancellationToken.None).ConfigureAwait(false);
        Assert.True(firstHold.IsSuccess, firstHold.Error.Code);
        Assert.True(secondHold.IsSuccess, secondHold.Error.Code);
        Result<LegalHoldListResponse> activeHolds = await dispatcher.QueryAsync(
            new ListLegalHoldsQuery(
                receipt.PropertyId,
                LegalHoldStatus.Active,
                Page: 1,
                PageSize: 20),
            CancellationToken.None).ConfigureAwait(false);
        Assert.True(activeHolds.IsSuccess, activeHolds.Error.Code);
        Assert.Equal(2, activeHolds.Value.TotalCount);
        LegalHoldListResponse adminHoldList = await GetAsync<LegalHoldListResponse>(
            adminClient,
            $"/api/admin/ingestion/properties/{receipt.PropertyId:D}/legal-holds?status=Active")
            .ConfigureAwait(false);
        Assert.Equal(2, adminHoldList.TotalCount);
        LegalHoldDto adminHoldDetail = await GetAsync<LegalHoldDto>(
            adminClient,
            $"/api/admin/ingestion/properties/{receipt.PropertyId:D}/legal-holds/{firstHold.Value.HoldId:D}")
            .ConfigureAwait(false);
        Assert.Equal("Regulatory matter 2026-01", adminHoldDetail.Reason);
        LegalHoldListResponse crossPropertyHolds = await GetAsync<LegalHoldListResponse>(
            adminClient,
            $"/api/admin/ingestion/properties/{OtherPropertyId:D}/legal-holds").ConfigureAwait(false);
        Assert.Empty(crossPropertyHolds.LegalHolds);
        Assert.Equal(0, crossPropertyHolds.TotalCount);
        AdminCliResult legalHoldList = await admin.ExecuteAsync(
            "ingestion", "legal-holds", "list",
            "--actor", "owner",
            "--tenant", TenantId,
            "--output", "json",
            "--property-id", receipt.PropertyId.ToString("D"));
        Assert.True(
            legalHoldList.ExitCode == AdminExitCodes.Success,
            $"ExitCode={legalHoldList.ExitCode}{Environment.NewLine}{legalHoldList.Output}{Environment.NewLine}{legalHoldList.Error}");
        Assert.Contains(firstHold.Value.HoldId.ToString("D"), legalHoldList.Output, StringComparison.OrdinalIgnoreCase);

        Result<IReadOnlyList<RawPayloadPurgeCandidate>> heldClaim = await dispatcher.SendAsync(
            new ClaimExpiredRawPayloadsCommand(
                Guid.NewGuid(),
                BatchSize: 10,
                PurgeExpiredRawPayloadsPayload.DefaultStaleClaimMinutes),
            CancellationToken.None).ConfigureAwait(false);
        Result<SensitiveHistoryRedactionBatchResult> heldRedaction = await dispatcher.SendAsync(
            new RedactExpiredSensitiveHistoryCommand(BatchSize: 10),
            CancellationToken.None).ConfigureAwait(false);
        Assert.True(heldClaim.IsSuccess, heldClaim.Error.Code);
        Assert.Empty(heldClaim.Value);
        Assert.True(heldRedaction.IsSuccess, heldRedaction.Error.Code);
        Assert.Equal(new SensitiveHistoryRedactionBatchResult(0, 0), heldRedaction.Value);
        Result<AdapterConnectionHealthDto> heldHealth = await dispatcher.QueryAsync(
            new GetAdapterConnectionHealthQuery(receipt.PropertyId, receipt.ConnectionId),
            CancellationToken.None).ConfigureAwait(false);
        Assert.True(heldHealth.IsSuccess, heldHealth.Error.Code);
        Assert.Equal(1, heldHealth.Value.HeldExpiredRawPayloadCount);
        Assert.Equal(1, heldHealth.Value.HeldDueSensitiveHistoryCount);
        Assert.Equal(2, heldHealth.Value.ActiveLegalHoldCount);
        Assert.Equal(0, heldHealth.Value.ExpiredRawPayloadCount);
        Assert.Equal(0, heldHealth.Value.DueSensitiveHistoryCount);

        Result<LegalHoldDto> firstReleased = await dispatcher.SendAsync(
            new ReleaseLegalHoldCommand(
                receipt.PropertyId,
                firstHold.Value.HoldId,
                firstHold.Value.Version,
                "Regulatory matter closed",
                "integration:legal"),
            CancellationToken.None).ConfigureAwait(false);
        Assert.True(firstReleased.IsSuccess, firstReleased.Error.Code);
        Result<IReadOnlyList<RawPayloadPurgeCandidate>> stillHeld = await dispatcher.SendAsync(
            new ClaimExpiredRawPayloadsCommand(
                Guid.NewGuid(),
                BatchSize: 10,
                PurgeExpiredRawPayloadsPayload.DefaultStaleClaimMinutes),
            CancellationToken.None).ConfigureAwait(false);
        Assert.True(stillHeld.IsSuccess, stillHeld.Error.Code);
        Assert.Empty(stillHeld.Value);

        AdminCliResult releaseConfirmationRequired = await admin.ExecuteAsync(
            "ingestion", "legal-holds", "release",
            "--actor", "owner",
            "--tenant", TenantId,
            "--property-id", receipt.PropertyId.ToString("D"),
            "--hold-id", secondHold.Value.HoldId.ToString("D"),
            "--expected-version", secondHold.Value.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--reason", "Litigation matter closed");
        Assert.NotEqual(AdminExitCodes.Success, releaseConfirmationRequired.ExitCode);
        Assert.Contains(
            AdminErrors.ConfirmationRequired.Message,
            releaseConfirmationRequired.Error,
            StringComparison.Ordinal);
        using (HttpResponseMessage releaseWithoutConfirmation = await adminClient.PostAsJsonAsync(
                   $"/api/admin/ingestion/properties/{receipt.PropertyId:D}/legal-holds/{secondHold.Value.HoldId:D}/release",
                   new
                   {
                       expectedVersion = secondHold.Value.Version,
                       releaseReason = "Litigation matter closed",
                       confirmed = false
                   }).ConfigureAwait(false))
        {
            Assert.Equal(HttpStatusCode.BadRequest, releaseWithoutConfirmation.StatusCode);
            Assert.Contains(
                AdminErrors.ConfirmationRequired.Code,
                await releaseWithoutConfirmation.Content.ReadAsStringAsync().ConfigureAwait(false),
                StringComparison.Ordinal);
        }
        LegalHoldDto secondReleased = await PostAsync<LegalHoldDto>(
            adminClient,
            $"/api/admin/ingestion/properties/{receipt.PropertyId:D}/legal-holds/{secondHold.Value.HoldId:D}/release",
            new
            {
                expectedVersion = secondHold.Value.Version,
                releaseReason = "Litigation matter closed",
                confirmed = true
            }).ConfigureAwait(false);
        Assert.Equal(LegalHoldStatus.Released, secondReleased.Status);
        dbContext.ChangeTracker.Clear();

        Result<IReadOnlyList<RawPayloadPurgeCandidate>> claimed = await dispatcher.SendAsync(
            new ClaimExpiredRawPayloadsCommand(
                claimId,
                BatchSize: 10,
                PurgeExpiredRawPayloadsPayload.DefaultStaleClaimMinutes),
            CancellationToken.None).ConfigureAwait(false);
        Assert.True(claimed.IsSuccess, claimed.Error.Code);
        RawPayloadPurgeCandidate candidate = Assert.Single(claimed.Value);
        Result<LegalHoldDto> tooLateHold = await dispatcher.SendAsync(
            new PlaceLegalHoldCommand(
                receipt.PropertyId,
                "Hold attempted after deletion claim",
                "integration:legal"),
            CancellationToken.None).ConfigureAwait(false);
        Assert.Equal(IngestionApplicationErrors.LegalHoldPurgeInProgress, tooLateHold.Error);
        IRawPayloadStore rawPayloads = scope.ServiceProvider.GetRequiredService<IRawPayloadStore>();
        _ = await rawPayloads.DeleteAsync(
            candidate.RawPayloadFileId,
            TenantId,
            candidate.ConnectionId,
            CancellationToken.None).ConfigureAwait(false);
        Assert.Null(await rawPayloads.ReadAsync(
            candidate.RawPayloadFileId,
            TenantId,
            candidate.ConnectionId,
            CancellationToken.None).ConfigureAwait(false));
        Result<Unit> completed = await dispatcher.SendAsync(
            new CompleteRawPayloadPurgeCommand(candidate.ReceiptId, claimId),
            CancellationToken.None).ConfigureAwait(false);
        Assert.True(completed.IsSuccess, completed.Error.Code);

        Result<SensitiveHistoryRedactionBatchResult> redacted = await dispatcher.SendAsync(
            new RedactExpiredSensitiveHistoryCommand(BatchSize: 10),
            CancellationToken.None).ConfigureAwait(false);
        Assert.True(redacted.IsSuccess, redacted.Error.Code);
        Assert.Equal(1, redacted.Value.ProposalCount);
        Assert.Equal(0, redacted.Value.DispatchCount);
        dbContext.ChangeTracker.Clear();
        ChangeProposal redactedProposal = await dbContext.ChangeProposals.SingleAsync(
            item => item.Id == proposal.Id).ConfigureAwait(false);
        Assert.Null(redactedProposal.Diff);
        Assert.Equal("retention-test", redactedProposal.ReasonCode);
        Assert.NotNull(redactedProposal.SensitiveDataRedactedAtUtc);
        Result<ChangeProposalDto> redactedRead = await dispatcher.QueryAsync(
            new GetChangeProposalQuery(receipt.PropertyId, proposal.Id),
            CancellationToken.None).ConfigureAwait(false);
        Assert.True(redactedRead.IsSuccess, redactedRead.Error.Code);
        Assert.Equal(SensitiveHistoryStatus.Redacted, redactedRead.Value.SensitiveHistoryStatus);
        Assert.Equal("retention-test", redactedRead.Value.ReasonCode);
        Assert.Null(redactedRead.Value.Diff);
        Result<LegalHoldListResponse> releasedHolds = await dispatcher.QueryAsync(
            new ListLegalHoldsQuery(
                receipt.PropertyId,
                LegalHoldStatus.Released,
                Page: 1,
                PageSize: 20),
            CancellationToken.None).ConfigureAwait(false);
        Assert.True(releasedHolds.IsSuccess, releasedHolds.Error.Code);
        Assert.Equal(2, releasedHolds.Value.TotalCount);
    }

    private static async Task ProveRetentionFenceConcurrencyAsync(AuthTestApplication api)
    {
        using IServiceScope firstScope = api.Services.CreateScope();
        using IServiceScope secondScope = api.Services.CreateScope();
        firstScope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantId);
        secondScope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(TenantId);
        IRetentionFenceRepository firstFence = firstScope.ServiceProvider
            .GetRequiredService<IRetentionFenceRepository>();
        IRetentionFenceRepository secondFence = secondScope.ServiceProvider
            .GetRequiredService<IRetentionFenceRepository>();
        Assert.True(await firstFence.TryAdvanceAsync(PropertyId, CancellationToken.None).ConfigureAwait(false));
        Assert.True(await secondFence.TryAdvanceAsync(PropertyId, CancellationToken.None).ConfigureAwait(false));

        await firstScope.ServiceProvider.GetRequiredService<IngestionDbContext>()
            .SaveChangesAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            secondScope.ServiceProvider.GetRequiredService<IngestionDbContext>()
                .SaveChangesAsync());
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string path)
    {
        using HttpResponseMessage response = await client.GetAsync(path).ConfigureAwait(false);
        return await ReadSuccessAsync<T>(response).ConfigureAwait(false);
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string path, object body)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync(path, body).ConfigureAwait(false);
        return await ReadSuccessAsync<T>(response).ConfigureAwait(false);
    }

    private static async Task<T> PutAsync<T>(HttpClient client, string path, object body)
    {
        using HttpResponseMessage response = await client.PutAsJsonAsync(path, body).ConfigureAwait(false);
        return await ReadSuccessAsync<T>(response).ConfigureAwait(false);
    }

    private static async Task<T> ReadSuccessAsync<T>(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.IsSuccessStatusCode, $"Expected success but received {(int)response.StatusCode}. Body: {body}");
        T? value = await response.Content.ReadFromJsonAsync<T>().ConfigureAwait(false);
        return Assert.IsType<T>(value);
    }

    private static async Task AssertAdminSuccessAsync(Task<AdminCliResult> resultTask)
    {
        AdminCliResult result = await resultTask.ConfigureAwait(false);
        Assert.True(result.ExitCode is AdminExitCodes.Success,
            $"ExitCode={result.ExitCode}{Environment.NewLine}{result.Output}{Environment.NewLine}{result.Error}");
    }

    private static Guid GetSubjectId(string accessToken)
    {
        JwtSecurityToken token = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
        string? subject = token.Claims.FirstOrDefault(claim =>
            claim.Type is ClaimTypes.NameIdentifier or "nameid" or "sub")?.Value;
        Assert.True(Guid.TryParse(subject, out Guid id));
        return id;
    }
}
