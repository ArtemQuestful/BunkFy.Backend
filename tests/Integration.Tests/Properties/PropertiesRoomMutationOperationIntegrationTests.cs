namespace Integration.Tests;

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

public sealed class PropertiesRoomMutationOperationIntegrationTests
{
    private const string PreviousMigration =
        "20260807193618_WidenPropertyLifecycleMutationOperations";
    private const string TenantId =
        "ac000000-0000-0000-0000-000000000001";

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Migrated_room_mutations_converge_and_replay_honors_admission()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_properties_room_operation_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);

        await using ServiceProvider services = CreateProvider(
            postgreSql.GetConnectionString());
        Guid legacyPropertyId = Guid.NewGuid();
        Guid legacyOperationId = Guid.NewGuid();
        await MigrateFromPreviousAsync(
            services,
            legacyPropertyId,
            legacyOperationId).ConfigureAwait(false);
        await AssertLegacyOperationBackfilledAsync(
            services,
            legacyPropertyId,
            legacyOperationId).ConfigureAwait(false);

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

        Guid createOperationId = Guid.NewGuid();
        CreateRoomCommand create = new(
            createOperationId,
            propertyId,
            ExpectedPropertyVersion: 1,
            "101",
            "Main",
            "1");
        Result<RoomMutationReceiptDto>[] concurrentCreation =
            await Task.WhenAll(
                SendAsync(services, create),
                SendAsync(services, create with
                {
                    Name = " 101 ",
                    BuildingLabel = " Main ",
                    FloorLabel = " 1 "
                })).ConfigureAwait(false);

        Assert.All(
            concurrentCreation,
            result => Assert.True(result.IsSuccess, result.Error.Code));
        Assert.Equal(concurrentCreation[0].Value, concurrentCreation[1].Value);
        Guid roomId = concurrentCreation[0].Value.RoomId;
        Assert.Equal(1, concurrentCreation[0].Value.Version);

        Result<RoomMutationReceiptDto> changedCreationReuse = await SendAsync(
            services,
            create with { Name = "102" }).ConfigureAwait(false);
        Assert.Equal(
            PropertiesApplicationErrors.ManagementOperationConflict,
            changedCreationReuse.Error);

        Guid correctedCreateOperationId = Guid.NewGuid();
        CreateRoomCommand duplicate = new(
            correctedCreateOperationId,
            propertyId,
            ExpectedPropertyVersion: 2,
            "101",
            null,
            null);
        Result<RoomMutationReceiptDto> duplicateResult = await SendAsync(
            services,
            duplicate).ConfigureAwait(false);
        Assert.Equal(
            PropertiesDomainErrors.RoomAlreadyExists,
            duplicateResult.Error);

        Result<RoomMutationReceiptDto> correctedCreation = await SendAsync(
            services,
            duplicate with { Name = "102" }).ConfigureAwait(false);
        Assert.True(
            correctedCreation.IsSuccess,
            correctedCreation.Error.Code);

        Guid noChangeOperationId = Guid.NewGuid();
        Result<RoomMutationReceiptDto> noChange = await SendAsync(
            services,
            new UpdateRoomCommand(
                noChangeOperationId,
                propertyId,
                roomId,
                ExpectedVersion: 1,
                " 101 ",
                "Main",
                "1")).ConfigureAwait(false);
        Assert.True(noChange.IsSuccess, noChange.Error.Code);
        Assert.Equal(1, noChange.Value.Version);

        Guid updateOperationId = Guid.NewGuid();
        UpdateRoomCommand update = new(
            updateOperationId,
            propertyId,
            roomId,
            ExpectedVersion: 1,
            "101 East",
            "Main",
            "1");
        Result<RoomMutationReceiptDto>[] concurrentUpdate =
            await Task.WhenAll(
                SendAsync(services, update),
                SendAsync(services, update with
                {
                    Name = " 101 East ",
                    BuildingLabel = " Main ",
                    FloorLabel = " 1 "
                })).ConfigureAwait(false);

        Assert.All(
            concurrentUpdate,
            result => Assert.True(result.IsSuccess, result.Error.Code));
        Assert.Equal(concurrentUpdate[0].Value, concurrentUpdate[1].Value);
        Assert.Equal(2, concurrentUpdate[0].Value.Version);

        Result<RoomMutationReceiptDto> changedUpdateReuse = await SendAsync(
            services,
            update with { Name = "Changed reuse" }).ConfigureAwait(false);
        Assert.Equal(
            PropertiesApplicationErrors.ManagementOperationConflict,
            changedUpdateReuse.Error);

        Result<RoomMutationReceiptDto> laterUpdate = await SendAsync(
            services,
            new UpdateRoomCommand(
                Guid.NewGuid(),
                propertyId,
                roomId,
                ExpectedVersion: 2,
                "101 West",
                "Annex",
                "1")).ConfigureAwait(false);
        Assert.True(laterUpdate.IsSuccess, laterUpdate.Error.Code);
        Assert.Equal(3, laterUpdate.Value.Version);

        Result<RoomMutationReceiptDto> immutableReplay = await SendAsync(
            services,
            update).ConfigureAwait(false);
        Assert.True(immutableReplay.IsSuccess, immutableReplay.Error.Code);
        Assert.Equal(2, immutableReplay.Value.Version);

        await AssertPersistedCardinalityAsync(
            services,
            propertyId,
            roomId).ConfigureAwait(false);

        await CloseTenantLifecycleAsync(
            services,
            Guid.NewGuid()).ConfigureAwait(false);
        Result<RoomMutationReceiptDto> closedReplay = await SendAsync(
            services,
            update).ConfigureAwait(false);
        Assert.Equal(
            PropertiesApplicationErrors.WorkspaceProcessingRestricted,
            closedReplay.Error);
    }

    private static async Task MigrateFromPreviousAsync(
        ServiceProvider services,
        Guid legacyPropertyId,
        Guid legacyOperationId)
    {
        using IServiceScope scope = services.CreateScope();
        PropertiesDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<PropertiesDbContext>();
        IMigrator migrator = dbContext.Database.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration).ConfigureAwait(false);

        Property legacyProperty = Property.Create(
            legacyPropertyId,
            TenantId,
            "Legacy House",
            "legacy-house",
            "UTC",
            Guid.NewGuid(),
            new DateTimeOffset(
                2026,
                8,
                7,
                19,
                0,
                0,
                TimeSpan.Zero)).Value;
        dbContext.Properties.Add(legacyProperty);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO properties.property_mutation_operations
                    ("Id", "ScopeId", "PropertyId", "Kind",
                     "ExpectedVersion", "RequestFingerprint",
                     "ResultStatus", "ResultProcessingStatus",
                     "ResultVersion", "CompletedAtUtc")
                VALUES
                    ({legacyOperationId}, {TenantId}, {legacyPropertyId}, 1,
                     1, {new string('a', 64)}, 1, 1, 1,
                     {new DateTimeOffset(
                         2026,
                         8,
                         7,
                         19,
                         5,
                         0,
                         TimeSpan.Zero)})
                """).ConfigureAwait(false);

        await migrator.MigrateAsync().ConfigureAwait(false);
    }

    private static async Task AssertLegacyOperationBackfilledAsync(
        ServiceProvider services,
        Guid legacyPropertyId,
        Guid legacyOperationId)
    {
        using IServiceScope scope = services.CreateScope();
        PropertiesDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<PropertiesDbContext>();
        int resourceKind = await dbContext.Database.SqlQuery<int>($"""
                SELECT "ResourceKind" AS "Value"
                FROM properties.property_mutation_operations
                WHERE "ScopeId" = {TenantId}
                  AND "Id" = {legacyOperationId}
                """).SingleAsync().ConfigureAwait(false);
        Guid resourceId = await dbContext.Database.SqlQuery<Guid>($"""
                SELECT "ResourceId" AS "Value"
                FROM properties.property_mutation_operations
                WHERE "ScopeId" = {TenantId}
                  AND "Id" = {legacyOperationId}
                """).SingleAsync().ConfigureAwait(false);
        long resourceVersion = await dbContext.Database.SqlQuery<long>($"""
                SELECT "ResultResourceVersion" AS "Value"
                FROM properties.property_mutation_operations
                WHERE "ScopeId" = {TenantId}
                  AND "Id" = {legacyOperationId}
                """).SingleAsync().ConfigureAwait(false);

        Assert.Equal(1, resourceKind);
        Assert.Equal(legacyPropertyId, resourceId);
        Assert.Equal(1, resourceVersion);
    }

    private static async Task AssertPersistedCardinalityAsync(
        ServiceProvider services,
        Guid propertyId,
        Guid roomId)
    {
        using IServiceScope scope = services.CreateScope();
        PropertiesDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<PropertiesDbContext>();
        Assert.Equal(
            2,
            await dbContext.Rooms.CountAsync(room =>
                room.PropertyId == propertyId).ConfigureAwait(false));
        Assert.Equal(
            3,
            await dbContext.Rooms
                .Where(room => room.Id == roomId)
                .Select(room => room.Version)
                .SingleAsync().ConfigureAwait(false));
        Assert.Equal(
            2,
            await CountOperationsAsync(
                dbContext,
                propertyId,
                resourceKind: 1,
                kind: 5,
                resourceId: propertyId).ConfigureAwait(false));
        Assert.Equal(
            3,
            await CountOperationsAsync(
                dbContext,
                propertyId,
                resourceKind: 2,
                kind: 6,
                resourceId: roomId).ConfigureAwait(false));
        Assert.Equal(
            2,
            await dbContext.OutboxMessages.CountAsync(message =>
                EF.Functions.Like(
                    message.EventType,
                    $"%{nameof(RoomCreatedIntegrationEvent)}%"))
                .ConfigureAwait(false));
        Assert.Equal(
            2,
            await dbContext.OutboxMessages.CountAsync(message =>
                EF.Functions.Like(
                    message.EventType,
                    $"%{nameof(RoomUpdatedIntegrationEvent)}%"))
                .ConfigureAwait(false));
    }

    private static Task<long> CountOperationsAsync(
        PropertiesDbContext dbContext,
        Guid propertyId,
        int resourceKind,
        int kind,
        Guid resourceId) =>
        dbContext.Database.SqlQuery<long>($"""
                SELECT COUNT(*) AS "Value"
                FROM properties.property_mutation_operations
                WHERE "ScopeId" = {TenantId}
                  AND "PropertyId" = {propertyId}
                  AND "ResourceKind" = {resourceKind}
                  AND "ResourceId" = {resourceId}
                  AND "Kind" = {kind}
                """).SingleAsync();

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
                        21,
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
        builder.Services.AddSingleton<IScopeContext>(
            new TestScopeContext());
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
