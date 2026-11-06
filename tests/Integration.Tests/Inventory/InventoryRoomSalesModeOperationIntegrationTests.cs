namespace Integration.Tests;

using BunkFy.Modules.Inventory.Application;
using BunkFy.Modules.Inventory.Application.Commands;
using BunkFy.Modules.Inventory.Application.Queries;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using BunkFy.Modules.Inventory.Persistence;
using BunkFy.Modules.Properties.Contracts;
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
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class InventoryRoomSalesModeOperationIntegrationTests
{
    private const string PreviousMigration =
        "20260804114733_AddInventoryTenantDestructionLifecycle";
    private const string TenantId =
        "a9000000-0000-0000-0000-000000000001";
    private static readonly Guid PropertyId =
        Guid.Parse("79000000-0000-0000-0000-000000000001");
    private static readonly Guid RoomId =
        Guid.Parse("79000000-0000-0000-0000-000000000002");
    private static readonly Guid BedId =
        Guid.Parse("79000000-0000-0000-0000-000000000003");

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Migrated_management_mutations_converge_and_replay_honors_admission()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_inventory_room_mode_operation_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);

        await using ServiceProvider services = CreateProvider(
            postgreSql.GetConnectionString());
        await MigrateFromPreviousAndSeedAsync(services)
            .ConfigureAwait(false);

        Guid firstOperationId = Guid.NewGuid();
        ConfigureRoomSalesModeCommand first = new(
            firstOperationId,
            PropertyId,
            RoomId,
            InventorySalesMode.BedLevel,
            ExpectedVersion: 1,
            "user:operator");
        Result<RoomInventoryMutationReceiptDto>[] concurrent =
            await Task.WhenAll(
                SendAsync(services, first),
                SendAsync(services, first)).ConfigureAwait(false);
        Assert.All(
            concurrent,
            result => Assert.True(result.IsSuccess, result.Error.Code));
        Assert.Equal(concurrent[0].Value, concurrent[1].Value);
        Assert.Equal(2, concurrent[0].Value.Version);

        Result<RoomInventoryMutationReceiptDto> conflictingReuse =
            await SendAsync(
                services,
                first with
                {
                    SalesMode = InventorySalesMode.RoomLevel,
                    ExpectedVersion = 2
                }).ConfigureAwait(false);
        Assert.Equal(
            InventoryApplicationErrors.ManagementOperationConflict,
            conflictingReuse.Error);

        ConfigureRoomSalesModeCommand later = new(
            Guid.NewGuid(),
            PropertyId,
            RoomId,
            InventorySalesMode.RoomLevel,
            ExpectedVersion: 2,
            "user:operator");
        Result<RoomInventoryMutationReceiptDto> changed = await SendAsync(
            services,
            later).ConfigureAwait(false);
        Assert.True(changed.IsSuccess, changed.Error.Code);
        Assert.Equal(3, changed.Value.Version);

        Result<RoomInventoryMutationReceiptDto> immutableReplay =
            await SendAsync(services, first).ConfigureAwait(false);
        Assert.True(immutableReplay.IsSuccess, immutableReplay.Error.Code);
        Assert.Equal(concurrent[0].Value, immutableReplay.Value);

        Guid noChangeOperationId = Guid.NewGuid();
        Result<RoomInventoryMutationReceiptDto> noChange = await SendAsync(
            services,
            later with
            {
                OperationId = noChangeOperationId,
                ExpectedVersion = 3
            }).ConfigureAwait(false);
        Assert.True(noChange.IsSuccess, noChange.Error.Code);
        Assert.Equal(3, noChange.Value.Version);

        Guid[] blockOperationIds = await ExerciseManualBlockOperationsAsync(
            services).ConfigureAwait(false);

        await AssertPersistedResultsAsync(
            services,
            firstOperationId,
            later.OperationId,
            noChangeOperationId,
            blockOperationIds).ConfigureAwait(false);
        await AssertDatabaseConstraintsAsync(services).ConfigureAwait(false);

        await CloseTenantLifecycleAsync(services).ConfigureAwait(false);
        Result<RoomInventoryMutationReceiptDto> closedReplay =
            await SendAsync(services, first).ConfigureAwait(false);
        Assert.Equal(
            InventoryApplicationErrors.WorkspaceProcessingRestricted,
            closedReplay.Error);

        await AssertDowngradeRefusedAsync(services).ConfigureAwait(false);
    }

    private static async Task<Guid[]> ExerciseManualBlockOperationsAsync(
        ServiceProvider services)
    {
        Guid createOperationId = Guid.NewGuid();
        CreateManualInventoryBlockCommand create = new(
            createOperationId,
            PropertyId,
            RoomId,
            new DateOnly(2026, 10, 1),
            new DateOnly(2026, 10, 3),
            "  Boiler maintenance  ",
            "user:operator");
        Result<ManualInventoryBlockMutationReceiptDto>[] created =
            await Task.WhenAll(
                SendAsync(services, create),
                SendAsync(services, create)).ConfigureAwait(false);
        Assert.All(
            created,
            result => Assert.True(result.IsSuccess, result.Error.Code));
        Assert.Equal(created[0].Value, created[1].Value);
        Assert.Equal(ManualInventoryBlockStatus.Active, created[0].Value.Status);

        Result<ManualInventoryBlockMutationReceiptDto> normalizedReplay =
            await SendAsync(
                services,
                create with { Reason = "Boiler maintenance" })
                .ConfigureAwait(false);
        Assert.True(normalizedReplay.IsSuccess, normalizedReplay.Error.Code);
        Assert.Equal(created[0].Value, normalizedReplay.Value);

        Result<ManualInventoryBlockMutationReceiptDto> conflictingCreate =
            await SendAsync(
                services,
                create with { Reason = "Another reason" })
                .ConfigureAwait(false);
        Assert.Equal(
            InventoryApplicationErrors.ManagementOperationConflict,
            conflictingCreate.Error);

        Guid recoverableCreateOperationId = Guid.NewGuid();
        CreateManualInventoryBlockCommand recoverableCreate = create with
        {
            OperationId = recoverableCreateOperationId,
            Reason = "Recovered maintenance"
        };
        Result<ManualInventoryBlockMutationReceiptDto> blockedCreate =
            await SendAsync(services, recoverableCreate).ConfigureAwait(false);
        Assert.Equal(
            InventoryApplicationErrors.BlockOverlap,
            blockedCreate.Error);

        Guid releaseOperationId = Guid.NewGuid();
        ReleaseManualInventoryBlockCommand release = new(
            releaseOperationId,
            PropertyId,
            created[0].Value.BlockId,
            created[0].Value.Version,
            "user:operator");
        Result<ManualInventoryBlockMutationReceiptDto>[] released =
            await Task.WhenAll(
                SendAsync(services, release),
                SendAsync(services, release)).ConfigureAwait(false);
        Assert.All(
            released,
            result => Assert.True(result.IsSuccess, result.Error.Code));
        Assert.Equal(released[0].Value, released[1].Value);
        Assert.Equal(
            ManualInventoryBlockStatus.Released,
            released[0].Value.Status);

        Result<ManualInventoryBlockMutationReceiptDto> conflictingRelease =
            await SendAsync(
                services,
                release with { ExpectedVersion = released[0].Value.Version })
                .ConfigureAwait(false);
        Assert.Equal(
            InventoryApplicationErrors.ManagementOperationConflict,
            conflictingRelease.Error);

        Result<ManualInventoryBlockMutationReceiptDto> recoveredCreate =
            await SendAsync(services, recoverableCreate).ConfigureAwait(false);
        Assert.True(recoveredCreate.IsSuccess, recoveredCreate.Error.Code);

        Guid recoveredReleaseOperationId = Guid.NewGuid();
        Result<ManualInventoryBlockMutationReceiptDto> recoveredRelease =
            await SendAsync(
                services,
                new ReleaseManualInventoryBlockCommand(
                    recoveredReleaseOperationId,
                    PropertyId,
                    recoveredCreate.Value.BlockId,
                    recoveredCreate.Value.Version,
                    "user:operator"))
                .ConfigureAwait(false);
        Assert.True(recoveredRelease.IsSuccess, recoveredRelease.Error.Code);

        Guid groupCreateOperationId = Guid.NewGuid();
        InventoryBlockTarget groupTarget = new(InventoryBlockTargetKind.Property);
        Result<ManualInventoryBlockGroupSelectionPreviewDto> groupPreview =
            await SendAsync(
                services,
                new PreviewManualInventoryBlockGroupQuery(
                    PropertyId,
                    groupTarget,
                    new DateOnly(2026, 10, 5),
                    new DateOnly(2026, 10, 7),
                    "Property maintenance"))
                .ConfigureAwait(false);
        Assert.True(groupPreview.IsSuccess, groupPreview.Error.Code);
        Assert.Equal(ManualInventoryBlockGroupPreviewStatus.Ready, groupPreview.Value.Status);
        CreateManualInventoryBlockGroupCommand createGroup = new(
            groupCreateOperationId,
            PropertyId,
            groupTarget,
            new DateOnly(2026, 10, 5),
            new DateOnly(2026, 10, 7),
            "Property maintenance",
            groupPreview.Value.SelectionDigest!,
            groupPreview.Value.AffectedBlockCount!.Value,
            Confirmed: true,
            ActorId: "user:operator");
        Result<ManualInventoryBlockGroupMutationReceiptDto>[] groups =
            await Task.WhenAll(
                SendAsync(services, createGroup),
                SendAsync(services, createGroup)).ConfigureAwait(false);
        Assert.All(
            groups,
            result => Assert.True(result.IsSuccess, result.Error.Code));
        Assert.Equal(groups[0].Value, groups[1].Value);
        Assert.Equal(1, groups[0].Value.AffectedBlockCount);

        Result<ManualInventoryBlockGroupMutationReceiptDto> normalizedGroup =
            await SendAsync(
                services,
                createGroup with
                {
                    Target = new(
                        InventoryBlockTargetKind.Property,
                        BuildingLabel: "ignored")
                }).ConfigureAwait(false);
        Assert.True(normalizedGroup.IsSuccess, normalizedGroup.Error.Code);
        Assert.Equal(groups[0].Value, normalizedGroup.Value);

        Guid groupReleaseOperationId = Guid.NewGuid();
        ReleaseManualInventoryBlockGroupCommand releaseGroup = new(
            groupReleaseOperationId,
            PropertyId,
            groups[0].Value.BlockGroupId,
            groups[0].Value.Version!.Value,
            Confirmed: true,
            ActorId: "user:operator");
        Result<ManualInventoryBlockGroupMutationReceiptDto>[] releasedGroups =
            await Task.WhenAll(
                SendAsync(services, releaseGroup),
                SendAsync(services, releaseGroup)).ConfigureAwait(false);
        Assert.All(
            releasedGroups,
            result => Assert.True(result.IsSuccess, result.Error.Code));
        Assert.Equal(releasedGroups[0].Value, releasedGroups[1].Value);

        return
        [
            createOperationId,
            releaseOperationId,
            recoverableCreateOperationId,
            recoveredReleaseOperationId,
            groupCreateOperationId,
            groupReleaseOperationId
        ];
    }

    private static async Task MigrateFromPreviousAndSeedAsync(
        ServiceProvider services)
    {
        using IServiceScope scope = services.CreateScope();
        InventoryDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<InventoryDbContext>();
        IMigrator migrator = dbContext.Database.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration).ConfigureAwait(false);

        InventoryPropertyTopology property =
            InventoryPropertyTopology.Create(PropertyId, TenantId);
        property.Apply(
            "Operation House",
            "operation-house",
            "UTC",
            PropertyStatus.Active,
            sourceVersion: 1);
        InventoryRoomTopology room = InventoryRoomTopology.Create(
            RoomId,
            TenantId,
            PropertyId);
        room.Apply(
            PropertyId,
            "101",
            "Main",
            "1",
            RoomStatus.Active,
            sourceVersion: 1);
        InventoryBedTopology bed = InventoryBedTopology.Create(
            BedId,
            TenantId,
            PropertyId,
            RoomId);
        bed.Apply(
            PropertyId,
            RoomId,
            "A",
            BedStatus.Active,
            sourceVersion: 1);
        InventoryUnit roomUnit = InventoryUnit.CreateRoom(
            RoomId,
            TenantId,
            PropertyId);
        roomUnit.Apply(
            PropertyId,
            RoomId,
            null,
            InventoryUnitKind.Room,
            "101",
            isTopologyActive: true,
            sourceVersion: 1);
        InventoryUnit bedUnit = InventoryUnit.CreateBed(
            BedId,
            TenantId,
            PropertyId,
            RoomId);
        bedUnit.Apply(
            PropertyId,
            RoomId,
            BedId,
            InventoryUnitKind.Bed,
            "A",
            isTopologyActive: true,
            sourceVersion: 1);
        RoomInventoryConfiguration configuration =
            RoomInventoryConfiguration.Create(
                RoomId,
                TenantId,
                PropertyId,
                new DateTimeOffset(
                    2026,
                    8,
                    7,
                    20,
                    0,
                    0,
                    TimeSpan.Zero)).Value;

        // This is the frozen predecessor table, which predates the new epoch column.
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO inventory.property_topology (
                "Id", "ScopeId", "Name", "Code", "TimeZoneId", "Status",
                "SourceVersion", "DetailsVersion", "IsKnown")
            VALUES ({property.Id}, {property.ScopeId}, {property.Name}, {property.Code},
                    {property.TimeZoneId}, {(int)property.Status}, {property.SourceVersion},
                    {property.DetailsVersion}, {property.IsKnown});
            """);
        dbContext.AddRange(
            room,
            bed,
            roomUnit,
            bedUnit,
            configuration);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
        await migrator.MigrateAsync().ConfigureAwait(false);
    }

    private static async Task AssertPersistedResultsAsync(
        ServiceProvider services,
        Guid firstOperationId,
        Guid laterOperationId,
        Guid noChangeOperationId,
        IReadOnlyCollection<Guid> blockOperationIds)
    {
        using IServiceScope scope = services.CreateScope();
        InventoryDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<InventoryDbContext>();
        RoomInventoryConfiguration configuration = await dbContext
            .RoomConfigurations
            .AsNoTracking()
            .SingleAsync(item => item.Id == RoomId)
            .ConfigureAwait(false);
        Assert.Equal(RoomSalesMode.RoomLevel, configuration.SalesMode);
        Assert.Equal(3, configuration.Version);
        Assert.Equal(9, configuration.AvailabilityMutationVersion);

        Assert.Equal(
            3,
            await dbContext.Database.SqlQuery<long>($"""
                    SELECT COUNT(*) AS "Value"
                    FROM inventory.management_operations
                    WHERE "ScopeId" = {TenantId}
                      AND "ResourceKind" = 1
                      AND "ResourceId" = {RoomId}
                      AND "Id" IN (
                          {firstOperationId},
                          {laterOperationId},
                          {noChangeOperationId})
                    """).SingleAsync().ConfigureAwait(false));
        Assert.Equal(
            6,
            await dbContext.Database.SqlQuery<long>($"""
                    SELECT COUNT(*) AS "Value"
                    FROM inventory.management_operations
                    WHERE "ScopeId" = {TenantId}
                      AND "Kind" IN (2, 4, 12, 14)
                    """).SingleAsync().ConfigureAwait(false));
        Assert.Equal(6, blockOperationIds.Distinct().Count());
        Assert.Equal(
            2,
            await dbContext.OutboxMessages.CountAsync(message =>
                EF.Functions.Like(
                    message.EventType,
                    $"%{nameof(RoomSalesModeChangedIntegrationEvent)}%"))
                .ConfigureAwait(false));
        Assert.Equal(
            6,
            await dbContext.OutboxMessages.CountAsync(message =>
                EF.Functions.Like(
                    message.EventType,
                    "%ManualInventoryBlock%IntegrationEvent%"))
                .ConfigureAwait(false));
    }

    private static async Task AssertDatabaseConstraintsAsync(
        ServiceProvider services)
    {
        using IServiceScope scope = services.CreateScope();
        InventoryDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<InventoryDbContext>();
        PostgresException invalidFingerprint =
            await Assert.ThrowsAsync<PostgresException>(() =>
                dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO inventory.management_operations
                        ("Id", "ScopeId", "ResourceKind", "ResourceId",
                         "PropertyId", "Kind", "ExpectedVersion",
                         "RequestFingerprint", "ResultSalesMode",
                         "ResultVersion", "CompletedAtUtc")
                    VALUES
                        ({Guid.NewGuid()}, {TenantId}, 1, {RoomId},
                         {PropertyId}, 1, 3, {"not-a-sha256"}, 2, 3,
                         {DateTimeOffset.UtcNow})
                    """)).ConfigureAwait(false);
        Assert.Equal(PostgresErrorCodes.CheckViolation, invalidFingerprint.SqlState);

        PostgresException incompleteReceipt =
            await Assert.ThrowsAsync<PostgresException>(() =>
                dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO inventory.management_operations
                        ("Id", "ScopeId", "ResourceKind", "ResourceId",
                         "PropertyId", "Kind", "ExpectedVersion",
                         "RequestFingerprint", "ResultVersion",
                         "CompletedAtUtc")
                    VALUES
                        ({Guid.NewGuid()}, {TenantId}, 2, {PropertyId},
                         {PropertyId}, 2, 0, {new string('a', 64)}, 1,
                         {DateTimeOffset.UtcNow})
                    """)).ConfigureAwait(false);
        Assert.Equal(
            PostgresErrorCodes.CheckViolation,
            incompleteReceipt.SqlState);
    }

    private static async Task CloseTenantLifecycleAsync(
        ServiceProvider services)
    {
        using IServiceScope scope = services.CreateScope();
        InventoryDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<InventoryDbContext>();
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE inventory.tenant_revisions
                SET "Revision" = "Revision" + 1,
                    "LifecycleStatus" = 2,
                    "DestroyOperationId" = {Guid.NewGuid()},
                    "DestroyRequestSha256" = {new string('c', 64)},
                    "DestroyStartedAtUtc" = {DateTimeOffset.UtcNow}
                WHERE "ScopeId" = {TenantId}
                """).ConfigureAwait(false);
    }

    private static async Task AssertDowngradeRefusedAsync(
        ServiceProvider services)
    {
        using IServiceScope scope = services.CreateScope();
        InventoryDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<InventoryDbContext>();
        PostgresException failure = await Assert.ThrowsAsync<PostgresException>(
            () => dbContext.Database.GetService<IMigrator>()
                .MigrateAsync(PreviousMigration)).ConfigureAwait(false);
        Assert.Contains(
            "Cannot downgrade Inventory while",
            failure.MessageText,
            StringComparison.Ordinal);
    }

    private static async Task<Result<RoomInventoryMutationReceiptDto>>
        SendAsync(
            ServiceProvider services,
            ConfigureRoomSalesModeCommand command)
    {
        using IServiceScope scope = services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<IRequestDispatcher>()
            .SendAsync(command, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static async Task<Result<ManualInventoryBlockMutationReceiptDto>>
        SendAsync(
            ServiceProvider services,
            CreateManualInventoryBlockCommand command)
    {
        using IServiceScope scope = services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<IRequestDispatcher>()
            .SendAsync(command, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static async Task<Result<ManualInventoryBlockMutationReceiptDto>>
        SendAsync(
            ServiceProvider services,
            ReleaseManualInventoryBlockCommand command)
    {
        using IServiceScope scope = services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<IRequestDispatcher>()
            .SendAsync(command, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static async Task<Result<ManualInventoryBlockGroupMutationReceiptDto>>
        SendAsync(
            ServiceProvider services,
            CreateManualInventoryBlockGroupCommand command)
    {
        using IServiceScope scope = services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<IRequestDispatcher>()
            .SendAsync(command, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static async Task<Result<ManualInventoryBlockGroupMutationReceiptDto>>
        SendAsync(
            ServiceProvider services,
            ReleaseManualInventoryBlockGroupCommand command)
    {
        using IServiceScope scope = services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<IRequestDispatcher>()
            .SendAsync(command, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static async Task<Result<ManualInventoryBlockGroupSelectionPreviewDto>>
        SendAsync(
            ServiceProvider services,
            PreviewManualInventoryBlockGroupQuery query)
    {
        using IServiceScope scope = services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<IRequestDispatcher>()
            .QueryAsync(query, CancellationToken.None)
            .ConfigureAwait(false);
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
        builder.Services.AddInventoryApplication();
        builder.AddInventoryPersistence();
        return builder.Services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => TenantId;
    }
}
