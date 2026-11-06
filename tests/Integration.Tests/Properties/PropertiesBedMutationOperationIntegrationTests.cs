namespace Integration.Tests;

using System.Text.Json;
using BunkFy.Modules.Properties.Application;
using BunkFy.Modules.Properties.Application.Commands;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Domain.Errors;
using BunkFy.Modules.Properties.Persistence;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Application.Events.Infrastructure;
using Gma.Framework.Cqrs;
using Gma.Framework.Cqrs.Infrastructure;
using Gma.Framework.Messaging.Infrastructure;
using Gma.Framework.Results;
using Gma.Framework.Scoping;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class PropertiesBedMutationOperationIntegrationTests
{
    private const string PreviousMigration =
        "20260807202103_AddResourceAwareRoomMutationOperations";
    private const string TenantId =
        "ad000000-0000-0000-0000-000000000001";

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Migrated_bed_mutations_converge_and_replay_honors_admission()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_properties_bed_operation_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);

        await using ServiceProvider services = CreateProvider(
            postgreSql.GetConnectionString());
        Guid legacyOperationId = await MigrateFromPreviousAsync(services)
            .ConfigureAwait(false);
        await AssertLegacyReceiptPreservedAsync(services, legacyOperationId)
            .ConfigureAwait(false);

        Guid propertyId = Guid.NewGuid();
        Result<PropertyMutationReceiptDto> propertyCreated = await SendAsync(
            services,
            new CreatePropertyCommand(
                propertyId,
                "Harbour House",
                "harbour-house",
                "UTC",
                "system:integration-test")).ConfigureAwait(false);
        Assert.True(propertyCreated.IsSuccess, propertyCreated.Error.Code);

        Guid roomOperationId = Guid.NewGuid();
        Result<RoomMutationReceiptDto> roomCreated = await SendAsync(
            services,
            new CreateRoomCommand(
                roomOperationId,
                propertyId,
                ExpectedPropertyVersion: 1,
                "101",
                "Main",
                "1")).ConfigureAwait(false);
        Assert.True(roomCreated.IsSuccess, roomCreated.Error.Code);
        Guid roomId = roomCreated.Value.RoomId;

        Guid singleOperationId = Guid.NewGuid();
        AddBedCommand single = new(
            singleOperationId,
            propertyId,
            roomId,
            ExpectedRoomVersion: 1,
            " A ");
        Result<BedMutationReceiptDto>[] concurrentSingle =
            await Task.WhenAll(
                SendAsync(services, single),
                SendAsync(services, single with { Label = "A" }))
                .ConfigureAwait(false);
        Assert.All(
            concurrentSingle,
            result => Assert.True(result.IsSuccess, result.Error.Code));
        Assert.Equal(concurrentSingle[0].Value, concurrentSingle[1].Value);
        Guid firstBedId = concurrentSingle[0].Value.BedId;
        Assert.Equal(2, concurrentSingle[0].Value.RoomVersion);

        Guid correctedOperationId = Guid.NewGuid();
        AddBedCommand duplicate = new(
            correctedOperationId,
            propertyId,
            roomId,
            ExpectedRoomVersion: 2,
            "A");
        Result<BedMutationReceiptDto> duplicateResult = await SendAsync(
            services,
            duplicate).ConfigureAwait(false);
        Assert.Equal(PropertiesDomainErrors.BedAlreadyExists, duplicateResult.Error);
        Result<BedMutationReceiptDto> corrected = await SendAsync(
            services,
            duplicate with { Label = "B" }).ConfigureAwait(false);
        Assert.True(corrected.IsSuccess, corrected.Error.Code);
        Assert.Equal(3, corrected.Value.RoomVersion);

        Guid batchOperationId = Guid.NewGuid();
        AddBedsCommand batch = new(
            batchOperationId,
            propertyId,
            roomId,
            ExpectedRoomVersion: 3,
            [" C ", "D"]);
        Result<BedBatchMutationReceiptDto>[] concurrentBatch =
            await Task.WhenAll(
                SendAsync(services, batch),
                SendAsync(services, batch with { Labels = ["C", "D"] }))
                .ConfigureAwait(false);
        Assert.All(
            concurrentBatch,
            result => Assert.True(result.IsSuccess, result.Error.Code));
        Assert.Equal(concurrentBatch[0].Value, concurrentBatch[1].Value);
        Assert.Equal(2, concurrentBatch[0].Value.AffectedBedCount);
        Assert.Equal(5, concurrentBatch[0].Value.RoomVersion);

        Guid noChangeOperationId = Guid.NewGuid();
        Result<BedMutationReceiptDto> noChange = await SendAsync(
            services,
            new UpdateBedCommand(
                noChangeOperationId,
                propertyId,
                roomId,
                firstBedId,
                ExpectedRoomVersion: 5,
                " A ")).ConfigureAwait(false);
        Assert.True(noChange.IsSuccess, noChange.Error.Code);
        Assert.Equal(1, noChange.Value.Version);
        Assert.Equal(5, noChange.Value.RoomVersion);

        Guid updateOperationId = Guid.NewGuid();
        UpdateBedCommand update = new(
            updateOperationId,
            propertyId,
            roomId,
            firstBedId,
            ExpectedRoomVersion: 5,
            " A1 ");
        Result<BedMutationReceiptDto>[] concurrentUpdate =
            await Task.WhenAll(
                SendAsync(services, update),
                SendAsync(services, update with { Label = "A1" }))
                .ConfigureAwait(false);
        Assert.All(
            concurrentUpdate,
            result => Assert.True(result.IsSuccess, result.Error.Code));
        Assert.Equal(concurrentUpdate[0].Value, concurrentUpdate[1].Value);
        Assert.Equal(2, concurrentUpdate[0].Value.Version);
        Assert.Equal(6, concurrentUpdate[0].Value.RoomVersion);

        Result<BedMutationReceiptDto> laterUpdate = await SendAsync(
            services,
            new UpdateBedCommand(
                Guid.NewGuid(),
                propertyId,
                roomId,
                firstBedId,
                ExpectedRoomVersion: 6,
                "A2")).ConfigureAwait(false);
        Assert.True(laterUpdate.IsSuccess, laterUpdate.Error.Code);
        Assert.Equal(3, laterUpdate.Value.Version);
        Assert.Equal(7, laterUpdate.Value.RoomVersion);

        Result<BedMutationReceiptDto> immutableReplay = await SendAsync(
            services,
            update).ConfigureAwait(false);
        Assert.True(immutableReplay.IsSuccess, immutableReplay.Error.Code);
        Assert.Equal(concurrentUpdate[0].Value, immutableReplay.Value);

        await AssertPersistedResultsAsync(
            services,
            propertyId,
            roomId,
            singleOperationId,
            batchOperationId,
            noChangeOperationId,
            updateOperationId,
            firstBedId).ConfigureAwait(false);

        await CloseTenantLifecycleAsync(services, Guid.NewGuid())
            .ConfigureAwait(false);
        Result<BedMutationReceiptDto> closedReplay = await SendAsync(
            services,
            update).ConfigureAwait(false);
        Assert.Equal(
            PropertiesApplicationErrors.WorkspaceProcessingRestricted,
            closedReplay.Error);
    }

    private static async Task<Guid> MigrateFromPreviousAsync(
        ServiceProvider services)
    {
        using IServiceScope scope = services.CreateScope();
        PropertiesDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<PropertiesDbContext>();
        IMigrator migrator = dbContext.Database.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration).ConfigureAwait(false);

        Guid propertyId = Guid.NewGuid();
        Guid roomId = Guid.NewGuid();
        Guid operationId = Guid.NewGuid();
        Property property = Property.Create(
            propertyId,
            TenantId,
            "Legacy House",
            "legacy-house",
            "UTC",
            Guid.NewGuid(),
            new DateTimeOffset(
                2026,
                8,
                7,
                20,
                0,
                0,
                TimeSpan.Zero)).Value;
        dbContext.Properties.Add(property);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO properties.property_mutation_operations
                    ("Id", "ScopeId", "PropertyId", "ResourceKind",
                     "ResourceId", "Kind", "ExpectedVersion",
                     "RequestFingerprint", "ResultStatus",
                     "ResultProcessingStatus", "ResultRoomId",
                     "ResultRoomStatus", "ResultVersion",
                     "ResultResourceVersion", "CompletedAtUtc")
                VALUES
                    ({operationId}, {TenantId}, {propertyId}, 2,
                     {roomId}, 6, 1, {new string('a', 64)}, NULL,
                     NULL, {roomId}, 1, 2, 2,
                     {new DateTimeOffset(
                         2026,
                         8,
                         7,
                         20,
                         5,
                         0,
                         TimeSpan.Zero)})
                """).ConfigureAwait(false);

        await migrator.MigrateAsync().ConfigureAwait(false);
        return operationId;
    }

    private static async Task AssertLegacyReceiptPreservedAsync(
        ServiceProvider services,
        Guid operationId)
    {
        using IServiceScope scope = services.CreateScope();
        PropertiesDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<PropertiesDbContext>();
        long count = await dbContext.Database.SqlQuery<long>($"""
                SELECT COUNT(*) AS "Value"
                FROM properties.property_mutation_operations
                WHERE "ScopeId" = {TenantId}
                  AND "Id" = {operationId}
                  AND "Kind" = 6
                  AND "ResultBedId" IS NULL
                  AND "ResultBedStatus" IS NULL
                  AND "ResultAffectedBedCount" IS NULL
                """).SingleAsync().ConfigureAwait(false);

        Assert.Equal(1, count);
    }

    private static async Task AssertPersistedResultsAsync(
        ServiceProvider services,
        Guid propertyId,
        Guid roomId,
        Guid singleOperationId,
        Guid batchOperationId,
        Guid noChangeOperationId,
        Guid updateOperationId,
        Guid firstBedId)
    {
        using IServiceScope scope = services.CreateScope();
        PropertiesDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<PropertiesDbContext>();

        Assert.Equal(
            4,
            await dbContext.Database.SqlQuery<long>($"""
                    SELECT COUNT(*) AS "Value"
                    FROM properties.beds
                    WHERE "ScopeId" = {TenantId}
                      AND "PropertyId" = {propertyId}
                      AND "RoomId" = {roomId}
                    """).SingleAsync().ConfigureAwait(false));
        Assert.Equal(
            7,
            await dbContext.Rooms
                .Where(room => room.Id == roomId)
                .Select(room => room.Version)
                .SingleAsync().ConfigureAwait(false));
        Assert.Equal(
            2,
            await CountOperationsAsync(
                dbContext,
                propertyId,
                roomId,
                kind: 7).ConfigureAwait(false));
        Assert.Equal(
            1,
            await CountOperationsAsync(
                dbContext,
                propertyId,
                roomId,
                kind: 8).ConfigureAwait(false));
        Assert.Equal(
            3,
            await CountOperationsAsync(
                dbContext,
                propertyId,
                roomId,
                kind: 9).ConfigureAwait(false));

        JsonElement single = await ReadOperationAsync(
            dbContext,
            singleOperationId).ConfigureAwait(false);
        Assert.Equal(7, single.GetProperty("kind").GetInt32());
        Assert.Equal(2, single.GetProperty("resourceKind").GetInt32());
        Assert.Equal(roomId, single.GetProperty("resourceId").GetGuid());
        Assert.Equal(firstBedId, single.GetProperty("resultBedId").GetGuid());
        Assert.Equal(1, single.GetProperty("resultBedStatus").GetInt32());
        Assert.Equal(2, single.GetProperty("resultResourceVersion").GetInt64());

        JsonElement batch = await ReadOperationAsync(
            dbContext,
            batchOperationId).ConfigureAwait(false);
        Assert.Equal(8, batch.GetProperty("kind").GetInt32());
        Assert.Equal(2, batch.GetProperty("resultAffectedBedCount").GetInt32());
        Assert.Equal(5, batch.GetProperty("resultVersion").GetInt64());
        Assert.Equal(5, batch.GetProperty("resultResourceVersion").GetInt64());

        JsonElement noChange = await ReadOperationAsync(
            dbContext,
            noChangeOperationId).ConfigureAwait(false);
        Assert.Equal(1, noChange.GetProperty("resultVersion").GetInt64());
        Assert.Equal(5, noChange.GetProperty("resultResourceVersion").GetInt64());

        JsonElement update = await ReadOperationAsync(
            dbContext,
            updateOperationId).ConfigureAwait(false);
        Assert.Equal(2, update.GetProperty("resultVersion").GetInt64());
        Assert.Equal(6, update.GetProperty("resultResourceVersion").GetInt64());

        Assert.Equal(
            4,
            await dbContext.OutboxMessages.CountAsync(message =>
                EF.Functions.Like(
                    message.EventType,
                    $"%{nameof(BedAddedIntegrationEvent)}%"))
                .ConfigureAwait(false));
        Assert.Equal(
            2,
            await dbContext.OutboxMessages.CountAsync(message =>
                EF.Functions.Like(
                    message.EventType,
                    $"%{nameof(BedUpdatedIntegrationEvent)}%"))
                .ConfigureAwait(false));
    }

    private static Task<long> CountOperationsAsync(
        PropertiesDbContext dbContext,
        Guid propertyId,
        Guid roomId,
        int kind) => dbContext.Database.SqlQuery<long>($"""
            SELECT COUNT(*) AS "Value"
            FROM properties.property_mutation_operations
            WHERE "ScopeId" = {TenantId}
              AND "PropertyId" = {propertyId}
              AND "ResourceKind" = 2
              AND "ResourceId" = {roomId}
              AND "Kind" = {kind}
            """).SingleAsync();

    private static async Task<JsonElement> ReadOperationAsync(
        PropertiesDbContext dbContext,
        Guid operationId)
    {
        string json = await dbContext.Database.SqlQuery<string>($"""
                SELECT jsonb_build_object(
                    'kind', "Kind",
                    'resourceKind', "ResourceKind",
                    'resourceId', "ResourceId",
                    'resultBedId', "ResultBedId",
                    'resultBedStatus', "ResultBedStatus",
                    'resultAffectedBedCount', "ResultAffectedBedCount",
                    'resultVersion', "ResultVersion",
                    'resultResourceVersion', "ResultResourceVersion")::text
                    AS "Value"
                FROM properties.property_mutation_operations
                WHERE "ScopeId" = {TenantId}
                  AND "Id" = {operationId}
                """).SingleAsync().ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async Task<Result<TResponse>> SendAsync<TResponse>(
        ServiceProvider services,
        ICommand<TResponse> command)
    {
        using IServiceScope scope = services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<IRequestDispatcher>()
            .SendAsync(command, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static async Task CloseTenantLifecycleAsync(
        ServiceProvider services,
        Guid destroyOperationId)
    {
        using IServiceScope scope = services.CreateScope();
        PropertiesDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<PropertiesDbContext>();
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE properties.tenant_revisions
                SET "Revision" = "Revision" + 1,
                    "LifecycleStatus" = 2,
                    "DestroyOperationId" = {destroyOperationId},
                    "DestroyRequestSha256" = {new string('c', 64)},
                    "DestroyStartedAtUtc" = {new DateTimeOffset(
                        2026,
                        8,
                        7,
                        22,
                        0,
                        0,
                        TimeSpan.Zero)}
                WHERE "ScopeId" = {TenantId}
                """).ConfigureAwait(false);
    }

    private static ServiceProvider CreateProvider(string connectionString)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Configuration["Persistence:Provider"] = "PostgreSql";
        builder.Configuration["ConnectionStrings:PostgreSql"] =
            connectionString;
        builder.Services.AddSingleton<IScopeContext>(new TestScopeContext());
        builder.Services.AddSingleton<IWorkspaceTerminationFenceReader>(
            OpenWorkspaceTerminationFenceReader.Instance);
        builder.AddApplicationEventsInfrastructure();
        builder.AddCqrsInfrastructure();
        builder.AddMessagingInfrastructure();
        builder.Services.AddPropertiesApplication();
        builder.AddPropertiesPersistence();
        return builder.Services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => TenantId;
    }
}
