namespace Integration.Tests;

using System.Data.Common;
using System.Diagnostics;
using BunkFy.Modules.Inventory.Application;
using BunkFy.Modules.Inventory.Application.Commands;
using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Application.Queries;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using BunkFy.Modules.Inventory.Persistence;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Application.Events.Infrastructure;
using Gma.Framework.Cqrs;
using Gma.Framework.Cqrs.Infrastructure;
using Gma.Framework.Messaging;
using Gma.Framework.Messaging.Infrastructure;
using Gma.Framework.Results;
using Gma.Framework.Scoping;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class InventoryManualBlockGroupConcurrencyIntegrationTests
{
    private const string TenantId =
        "a2300000-0000-0000-0000-000000000001";
    private const string OtherTenantId =
        "a2300000-0000-0000-0000-000000000002";
    private static readonly DateOnly Arrival = new(2026, 10, 1);
    private static readonly DateOnly Departure = new(2026, 10, 3);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Preview_is_repeatable_and_selection_fence_rejects_a_queued_topology_change()
    {
        await using PostgreSqlContainer postgreSql = await StartAsync(
            "bunkfy_inventory_group_selection_concurrency");
        PreviewCoordinateBarrier interceptor = new();
        await using ServiceProvider services = CreateProvider(
            postgreSql.GetConnectionString(),
            new TestScopeContext(TenantId),
            interceptor);
        await MigrateAsync(services).ConfigureAwait(false);

        Guid propertyId = Guid.Parse(
            "13000000-0000-0000-0000-000000000001");
        Guid eastRoomId = Guid.Parse(
            "23000000-0000-0000-0000-000000000001");
        Guid westRoomId = Guid.Parse(
            "23000000-0000-0000-0000-000000000002");
        await SeedPropertyAsync(
            services,
            propertyId,
            new RoomSeed(eastRoomId, "101", "East"),
            new RoomSeed(westRoomId, "102", "West"))
            .ConfigureAwait(false);

        InventoryBlockTarget east = new(
            InventoryBlockTargetKind.Building,
            BuildingLabel: "East");
        PreviewManualInventoryBlockGroupQuery query = new(
            propertyId,
            east,
            Arrival,
            Departure,
            "East wing maintenance");
        Result<ManualInventoryBlockGroupSelectionPreviewDto> before =
            await PreviewAsync(services, query).ConfigureAwait(false);
        Assert.True(before.IsSuccess, before.Error.Code);
        Assert.Equal(1, before.Value.AffectedBlockCount);
        Assert.NotNull(before.Value.SelectionDigest);

        interceptor.Arm();
        Task<Result<ManualInventoryBlockGroupSelectionPreviewDto>>
            pausedPreview = PreviewAsync(services, query);
        await interceptor.WaitUntilPausedAsync()
            .WaitAsync(TimeSpan.FromSeconds(15))
            .ConfigureAwait(false);
        try
        {
            await MoveRoomAsync(
                    services,
                    propertyId,
                    westRoomId,
                    "102",
                    "East",
                    sourceVersion: 2)
                .WaitAsync(TimeSpan.FromSeconds(15))
                .ConfigureAwait(false);
        }
        finally
        {
            interceptor.Continue();
        }

        Result<ManualInventoryBlockGroupSelectionPreviewDto> snapshot =
            await pausedPreview.WaitAsync(TimeSpan.FromSeconds(15))
                .ConfigureAwait(false);
        Assert.True(snapshot.IsSuccess, snapshot.Error.Code);
        Assert.Equal(1, snapshot.Value.AffectedBlockCount);
        Assert.Equal(
            before.Value.SelectionDigest,
            snapshot.Value.SelectionDigest);

        Result<ManualInventoryBlockGroupSelectionPreviewDto> afterMove =
            await PreviewAsync(services, query).ConfigureAwait(false);
        Assert.True(afterMove.IsSuccess, afterMove.Error.Code);
        Assert.Equal(2, afterMove.Value.AffectedBlockCount);
        Assert.NotEqual(
            before.Value.SelectionDigest,
            afterMove.Value.SelectionDigest);

        await MoveRoomAsync(
                services,
                propertyId,
                westRoomId,
                "102",
                "West",
                sourceVersion: 3)
            .ConfigureAwait(false);
        Result<ManualInventoryBlockGroupSelectionPreviewDto> stale =
            await PreviewAsync(services, query).ConfigureAwait(false);
        Assert.True(stale.IsSuccess, stale.Error.Code);
        Assert.Equal(1, stale.Value.AffectedBlockCount);
        Assert.NotNull(stale.Value.SelectionDigest);

        long revisionBeforeRace = await GetTenantRevisionAsync(services)
            .ConfigureAwait(false);
        int outboxBeforeRace = await GetOutboxCountAsync(services)
            .ConfigureAwait(false);
        await using NpgsqlConnection barrier = new(
            postgreSql.GetConnectionString());
        await barrier.OpenAsync().ConfigureAwait(false);
        await using NpgsqlTransaction barrierTransaction =
            await barrier.BeginTransactionAsync().ConfigureAwait(false);
        await LockPropertyAsync(
            barrier,
            barrierTransaction,
            propertyId).ConfigureAwait(false);

        Task topologyChange = MoveRoomAsync(
            services,
            propertyId,
            westRoomId,
            "102",
            "East",
            sourceVersion: 4);
        await Task.Delay(TimeSpan.FromMilliseconds(250))
            .ConfigureAwait(false);
        Assert.False(topologyChange.IsCompleted);

        CreateManualInventoryBlockGroupCommand staleCreate = new(
            Guid.NewGuid(),
            propertyId,
            east,
            Arrival,
            Departure,
            "East wing maintenance",
            stale.Value.SelectionDigest!,
            stale.Value.AffectedBlockCount!.Value,
            Confirmed: true,
            ActorId: "user:operator");
        Task<Result<ManualInventoryBlockGroupMutationReceiptDto>> create =
            SendAsync(services, staleCreate);
        await Task.Delay(TimeSpan.FromMilliseconds(250))
            .ConfigureAwait(false);
        Assert.False(create.IsCompleted);

        await barrierTransaction.CommitAsync().ConfigureAwait(false);
        await topologyChange.WaitAsync(TimeSpan.FromSeconds(15))
            .ConfigureAwait(false);
        Result<ManualInventoryBlockGroupMutationReceiptDto> rejected =
            await create.WaitAsync(TimeSpan.FromSeconds(15))
                .ConfigureAwait(false);
        Assert.Equal(
            InventoryApplicationErrors.BlockGroupSelectionMismatch,
            rejected.Error);

        using (IServiceScope verification = services.CreateScope())
        {
            InventoryDbContext dbContext = verification.ServiceProvider
                .GetRequiredService<InventoryDbContext>();
            Assert.Equal(
                0,
                await dbContext.ManualBlockGroups.CountAsync(group =>
                        group.PropertyId == propertyId)
                    .ConfigureAwait(false));
            Assert.Equal(
                0,
                await dbContext.ManualBlocks.CountAsync(block =>
                        block.PropertyId == propertyId)
                    .ConfigureAwait(false));
            Assert.Equal(
                0L,
                await dbContext.Database.SqlQuery<long>($"""
                        SELECT COUNT(*) AS "Value"
                        FROM inventory.management_operations
                        WHERE "ScopeId" = {TenantId}
                          AND "PropertyId" = {propertyId}
                        """).SingleAsync().ConfigureAwait(false));
        }

        Assert.Equal(
            revisionBeforeRace + 1,
            await GetTenantRevisionAsync(services).ConfigureAwait(false));
        Assert.Equal(
            outboxBeforeRace + 1,
            await GetOutboxCountAsync(services).ConfigureAwait(false));

        await AssertSelectionFenceProviderSemanticsAsync(
            services,
            postgreSql.GetConnectionString(),
            propertyId).ConfigureAwait(false);
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Overlapping_allocation_commits_and_block_group_returns_typed_conflict_without_partials()
    {
        await using PostgreSqlContainer postgreSql = await StartAsync(
            "bunkfy_inventory_group_allocation_concurrency");
        await using ServiceProvider services = CreateProvider(
            postgreSql.GetConnectionString(),
            new TestScopeContext(TenantId));
        await MigrateAsync(services).ConfigureAwait(false);

        Guid propertyId = Guid.Parse(
            "13000000-0000-0000-0000-000000000011");
        Guid roomId = Guid.Parse(
            "23000000-0000-0000-0000-000000000011");
        await SeedPropertyAsync(
            services,
            propertyId,
            new RoomSeed(roomId, "201", "Main"))
            .ConfigureAwait(false);
        InventoryBlockTarget target = new(
            InventoryBlockTargetKind.Room,
            RoomId: roomId);
        Result<ManualInventoryBlockGroupSelectionPreviewDto> preview =
            await PreviewAsync(
                services,
                new(
                    propertyId,
                    target,
                    Arrival,
                    Departure,
                    "Allocation overlap proof"))
                .ConfigureAwait(false);
        Assert.True(preview.IsSuccess, preview.Error.Code);
        Assert.Equal(1, preview.Value.AffectedBlockCount);
        long revisionBefore = await GetTenantRevisionAsync(services)
            .ConfigureAwait(false);

        using IServiceScope allocationScope = services.CreateScope();
        InventoryDbContext allocationDb = allocationScope.ServiceProvider
            .GetRequiredService<InventoryDbContext>();
        await using var allocationTransaction = await allocationDb.Database
            .BeginTransactionAsync().ConfigureAwait(false);
        IIntegrationEventHandler<InventoryAllocationRequestedIntegrationEvent>
            allocationHandler = ResolveInventoryHandler<
                InventoryAllocationRequestedIntegrationEvent>(
                allocationScope.ServiceProvider);
        Guid reservationId = Guid.NewGuid();
        Guid allocationRequestId = Guid.NewGuid();
        await allocationHandler.HandleAsync(
            new(
                Guid.NewGuid(),
                TenantId,
                DateTimeOffset.UtcNow,
                reservationId,
                allocationRequestId,
                propertyId,
                Arrival,
                Departure,
                [roomId]),
            CancellationToken.None).ConfigureAwait(false);
        await allocationDb.SaveChangesAsync().ConfigureAwait(false);

        CreateManualInventoryBlockGroupCommand command = new(
            Guid.NewGuid(),
            propertyId,
            target,
            Arrival,
            Departure,
            "Allocation overlap proof",
            preview.Value.SelectionDigest!,
            preview.Value.AffectedBlockCount!.Value,
            Confirmed: true,
            ActorId: "user:operator");
        Task<Result<ManualInventoryBlockGroupMutationReceiptDto>> create =
            SendAsync(services, command);
        await Task.Delay(TimeSpan.FromMilliseconds(300))
            .ConfigureAwait(false);
        Assert.False(create.IsCompleted);

        await allocationTransaction.CommitAsync().ConfigureAwait(false);
        Result<ManualInventoryBlockGroupMutationReceiptDto> result =
            await create.WaitAsync(TimeSpan.FromSeconds(20))
                .ConfigureAwait(false);
        Assert.Equal(
            InventoryApplicationErrors.BlockGroupSelectionMismatch,
            result.Error);

        using IServiceScope verification = services.CreateScope();
        InventoryDbContext dbContext = verification.ServiceProvider
            .GetRequiredService<InventoryDbContext>();
        InventoryAllocation allocation = await dbContext.Allocations
            .AsNoTracking()
            .SingleAsync(item =>
                item.AllocationRequestId == allocationRequestId)
            .ConfigureAwait(false);
        Assert.Equal(InventoryAllocationState.Active, allocation.Status);
        Assert.Equal(
            0,
            await dbContext.ManualBlockGroups.CountAsync(group =>
                    group.PropertyId == propertyId)
                .ConfigureAwait(false));
        Assert.Equal(
            0,
            await dbContext.ManualBlocks.CountAsync(block =>
                    block.PropertyId == propertyId)
                .ConfigureAwait(false));
        Assert.Equal(
            0L,
            await dbContext.Database.SqlQuery<long>($"""
                    SELECT COUNT(*) AS "Value"
                    FROM inventory.management_operations
                    WHERE "ScopeId" = {TenantId}
                      AND "PropertyId" = {propertyId}
                    """).SingleAsync().ConfigureAwait(false));
        Assert.Equal(
            revisionBefore + 1,
            await dbContext.Database.SqlQuery<long>($"""
                    SELECT "Revision" AS "Value"
                    FROM inventory.tenant_revisions
                    WHERE "ScopeId" = {TenantId}
                    """).SingleAsync().ConfigureAwait(false));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Sales_mode_and_retirement_cancellation_invalidate_queued_stale_confirmations()
    {
        await using PostgreSqlContainer postgreSql = await StartAsync(
            "bunkfy_inventory_group_eligibility_writers");
        string connectionString = postgreSql.GetConnectionString();
        await using ServiceProvider services = CreateProvider(
            connectionString,
            new TestScopeContext(TenantId));
        await MigrateAsync(services).ConfigureAwait(false);

        Guid salesPropertyId = Guid.Parse(
            "13000000-0000-0000-0000-000000000041");
        Guid salesRoomA = Guid.Parse(
            "23000000-0000-0000-0000-000000000041");
        Guid salesRoomB = Guid.Parse(
            "23000000-0000-0000-0000-000000000042");
        await SeedPropertyAsync(
            services,
            salesPropertyId,
            new RoomSeed(salesRoomA, "501", "Main"),
            new RoomSeed(
                salesRoomB,
                "502",
                "Main",
                RoomSalesMode.Unconfigured)).ConfigureAwait(false);
        PreviewManualInventoryBlockGroupQuery salesPreview = new(
            salesPropertyId,
            new(InventoryBlockTargetKind.Property),
            Arrival,
            Departure,
            "Sales mode fence proof");
        await AssertQueuedEligibilityWriterInvalidatesAsync(
            services,
            connectionString,
            salesPreview,
            async () =>
            {
                Result<RoomInventoryMutationReceiptDto> configured =
                    await SendAsync(
                        services,
                        new ConfigureRoomSalesModeCommand(
                            Guid.NewGuid(),
                            salesPropertyId,
                            salesRoomB,
                            InventorySalesMode.RoomLevel,
                            ExpectedVersion: 1,
                            ActorId: "user:operator"))
                        .ConfigureAwait(false);
                Assert.True(configured.IsSuccess, configured.Error.Code);
            }).ConfigureAwait(false);

        Guid retirementPropertyId = Guid.Parse(
            "13000000-0000-0000-0000-000000000051");
        Guid retirementRoomA = Guid.Parse(
            "23000000-0000-0000-0000-000000000051");
        Guid retirementRoomB = Guid.Parse(
            "23000000-0000-0000-0000-000000000052");
        await SeedPropertyAsync(
            services,
            retirementPropertyId,
            new RoomSeed(retirementRoomA, "601", "Main"),
            new RoomSeed(retirementRoomB, "602", "Main"))
            .ConfigureAwait(false);
        Guid topologyChangeId = Guid.NewGuid();
        using (IServiceScope seedScope = services.CreateScope())
        {
            InventoryDbContext dbContext = seedScope.ServiceProvider
                .GetRequiredService<InventoryDbContext>();
            RoomRetirementProcess retirement = RoomRetirementProcess.Create(
                topologyChangeId,
                TenantId,
                retirementPropertyId,
                retirementRoomB,
                "Synthetic active drain for cancellation race",
                "user:operator",
                DateTimeOffset.UtcNow).Value;
            InventoryPropertyTopology property = await dbContext
                .PropertyTopology
                .SingleAsync(item => item.Id == retirementPropertyId)
                .ConfigureAwait(false);
            property.AdvanceAvailabilitySelection();
            dbContext.RoomRetirements.Add(retirement);
            await dbContext.SaveChangesAsync().ConfigureAwait(false);
        }

        PreviewManualInventoryBlockGroupQuery retirementPreview = new(
            retirementPropertyId,
            new(InventoryBlockTargetKind.Property),
            Arrival,
            Departure,
            "Retirement cancellation fence proof");
        await AssertQueuedEligibilityWriterInvalidatesAsync(
            services,
            connectionString,
            retirementPreview,
            async () =>
            {
                Result<RoomRetirementDto> canceled = await SendAsync(
                    services,
                    new CancelRoomRetirementCommand(
                        Guid.NewGuid(),
                        retirementPropertyId,
                        topologyChangeId,
                        ExpectedVersion: 1,
                        Confirmed: true,
                        Reason: "Room remains in service",
                        CanceledBy: "user:operator"))
                    .ConfigureAwait(false);
                Assert.True(canceled.IsSuccess, canceled.Error.Code);
            }).ConfigureAwait(false);
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Distinct_replacements_and_replace_vs_release_serialize_without_deadlock_or_partial_graphs()
    {
        await using PostgreSqlContainer postgreSql = await StartAsync(
            "bunkfy_inventory_group_terminal_concurrency");
        await using ServiceProvider services = CreateProvider(
            postgreSql.GetConnectionString(),
            new TestScopeContext(TenantId));
        await MigrateAsync(services).ConfigureAwait(false);

        Guid replacePropertyId = Guid.Parse(
            "13000000-0000-0000-0000-000000000021");
        Guid replaceRoomA = Guid.Parse(
            "23000000-0000-0000-0000-000000000021");
        Guid replaceRoomB = Guid.Parse(
            "23000000-0000-0000-0000-000000000022");
        Guid replaceRoomC = Guid.Parse(
            "23000000-0000-0000-0000-000000000023");
        Guid terminalPropertyId = Guid.Parse(
            "13000000-0000-0000-0000-000000000031");
        Guid terminalRoomA = Guid.Parse(
            "23000000-0000-0000-0000-000000000031");
        Guid terminalRoomB = Guid.Parse(
            "23000000-0000-0000-0000-000000000032");
        await SeedPropertyAsync(
            services,
            replacePropertyId,
            new RoomSeed(replaceRoomA, "301", "Main"),
            new RoomSeed(replaceRoomB, "302", "Main"),
            new RoomSeed(replaceRoomC, "303", "Main"))
            .ConfigureAwait(false);
        await SeedPropertyAsync(
            services,
            terminalPropertyId,
            new RoomSeed(terminalRoomA, "401", "Main"),
            new RoomSeed(terminalRoomB, "402", "Main"))
            .ConfigureAwait(false);

        ManualInventoryBlockGroupMutationReceiptDto predecessor =
            await CreateGroupAsync(
                services,
                replacePropertyId,
                replaceRoomA,
                "Initial replacement race")
                .ConfigureAwait(false);
        Result<ManualInventoryBlockGroupSelectionPreviewDto> previewB =
            await PreviewReplacementAsync(
                services,
                replacePropertyId,
                predecessor.BlockGroupId,
                predecessor.Version!.Value,
                replaceRoomB,
                "Replacement B").ConfigureAwait(false);
        Result<ManualInventoryBlockGroupSelectionPreviewDto> previewC =
            await PreviewReplacementAsync(
                services,
                replacePropertyId,
                predecessor.BlockGroupId,
                predecessor.Version.Value,
                replaceRoomC,
                "Replacement C").ConfigureAwait(false);
        Assert.True(previewB.IsSuccess, previewB.Error.Code);
        Assert.True(previewC.IsSuccess, previewC.Error.Code);

        ReplaceManualInventoryBlockGroupCommand replaceB =
            CreateReplaceCommand(
                replacePropertyId,
                predecessor,
                replaceRoomB,
                "Replacement B",
                previewB.Value);
        ReplaceManualInventoryBlockGroupCommand replaceC =
            CreateReplaceCommand(
                replacePropertyId,
                predecessor,
                replaceRoomC,
                "Replacement C",
                previewC.Value);
        Result<ManualInventoryBlockGroupMutationReceiptDto>[] replacements =
            await Task.WhenAll(
                    SendAsync(services, replaceB),
                    SendAsync(services, replaceC))
                .WaitAsync(TimeSpan.FromSeconds(20))
                .ConfigureAwait(false);
        Result<ManualInventoryBlockGroupMutationReceiptDto> replacement =
            Assert.Single(replacements, item => item.IsSuccess);
        Result<ManualInventoryBlockGroupMutationReceiptDto> rejectedReplace =
            Assert.Single(replacements, item => item.IsFailure);
        Assert.Equal(
            InventoryApplicationErrors.VersionConflict,
            rejectedReplace.Error);
        Assert.NotEqual(
            predecessor.BlockGroupId,
            replacement.Value.ResultBlockGroupId);

        await AssertReplacedGraphAsync(
            services,
            replacePropertyId,
            predecessor.BlockGroupId,
            replacement.Value.ResultBlockGroupId).ConfigureAwait(false);

        ManualInventoryBlockGroupMutationReceiptDto terminalPredecessor =
            await CreateGroupAsync(
                services,
                terminalPropertyId,
                terminalRoomA,
                "Initial terminal race")
                .ConfigureAwait(false);
        Result<ManualInventoryBlockGroupSelectionPreviewDto> terminalPreview =
            await PreviewReplacementAsync(
                services,
                terminalPropertyId,
                terminalPredecessor.BlockGroupId,
                terminalPredecessor.Version!.Value,
                terminalRoomB,
                "Terminal replacement").ConfigureAwait(false);
        Assert.True(terminalPreview.IsSuccess, terminalPreview.Error.Code);

        ReplaceManualInventoryBlockGroupCommand terminalReplace =
            CreateReplaceCommand(
                terminalPropertyId,
                terminalPredecessor,
                terminalRoomB,
                "Terminal replacement",
                terminalPreview.Value);
        ReleaseManualInventoryBlockGroupCommand terminalRelease = new(
            Guid.NewGuid(),
            terminalPropertyId,
            terminalPredecessor.BlockGroupId,
            terminalPredecessor.Version.Value,
            Confirmed: true,
            ActorId: "user:operator");
        Task<Result<ManualInventoryBlockGroupMutationReceiptDto>> replaceTask =
            SendAsync(services, terminalReplace);
        Task<Result<ManualInventoryBlockGroupMutationReceiptDto>> releaseTask =
            SendAsync(services, terminalRelease);
        await Task.WhenAll(replaceTask, releaseTask)
            .WaitAsync(TimeSpan.FromSeconds(20))
            .ConfigureAwait(false);
        Result<ManualInventoryBlockGroupMutationReceiptDto>[] terminalResults =
            [await replaceTask, await releaseTask];
        Assert.Single(terminalResults, item => item.IsSuccess);
        Result<ManualInventoryBlockGroupMutationReceiptDto> terminalRejected =
            Assert.Single(terminalResults, item => item.IsFailure);
        Assert.Equal(
            InventoryApplicationErrors.VersionConflict,
            terminalRejected.Error);

        await AssertTerminalGraphAsync(
            services,
            terminalPropertyId,
            terminalPredecessor.BlockGroupId).ConfigureAwait(false);
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Five_hundred_and_one_member_target_is_bounded_and_has_zero_operational_effects()
    {
        await using PostgreSqlContainer postgreSql = await StartAsync(
            "bunkfy_inventory_group_501_boundary");
        await using ServiceProvider services = CreateProvider(
            postgreSql.GetConnectionString(),
            new TestScopeContext(TenantId));
        await MigrateAsync(services).ConfigureAwait(false);

        Guid propertyId = Guid.Parse(
            "13000000-0000-0000-0000-000000000061");
        RoomSeed[] rooms = Enumerable.Range(1, 501)
            .Select(index => new RoomSeed(
                IndexedRoomId(index),
                $"R{index:D3}",
                "Boundary"))
            .ToArray();
        await SeedPropertyAsync(services, propertyId, rooms)
            .ConfigureAwait(false);
        long revisionBefore = await GetTenantRevisionAsync(services)
            .ConfigureAwait(false);
        int outboxBefore = await GetOutboxCountAsync(services)
            .ConfigureAwait(false);

        Stopwatch timer = Stopwatch.StartNew();
        PreviewManualInventoryBlockGroupQuery query = new(
            propertyId,
            new(InventoryBlockTargetKind.Property),
            Arrival,
            Departure,
            "Bounded 501-unit refusal");
        Result<ManualInventoryBlockGroupSelectionPreviewDto> preview =
            await PreviewAsync(services, query).ConfigureAwait(false);
        Assert.True(preview.IsSuccess, preview.Error.Code);
        Assert.Equal(
            ManualInventoryBlockGroupPreviewStatus.TooLarge,
            preview.Value.Status);
        Assert.Null(preview.Value.AffectedBlockCount);
        Assert.Equal(501, preview.Value.AtLeastAffectedBlockCount);
        Assert.True(preview.Value.ExceedsMaximumAffectedBlockCount);
        Assert.True(preview.Value.HasMoreMembers);
        Assert.Null(preview.Value.SelectionDigest);

        Result<ManualInventoryBlockGroupMutationReceiptDto> rejected =
            await SendAsync(
                services,
                new CreateManualInventoryBlockGroupCommand(
                    Guid.NewGuid(),
                    propertyId,
                    query.Target,
                    query.Arrival,
                    query.Departure,
                    query.Reason,
                    new string('a', 64),
                    ExpectedAffectedBlockCount: 500,
                    Confirmed: true,
                    ActorId: "user:operator"))
                .ConfigureAwait(false);
        timer.Stop();
        Assert.Equal(
            InventoryApplicationErrors.BlockGroupTargetTooLarge,
            rejected.Error);
        Assert.True(
            timer.Elapsed < TimeSpan.FromSeconds(30),
            $"Bounded 501-unit preview and refusal took {timer.Elapsed}.");

        using IServiceScope verification = services.CreateScope();
        InventoryDbContext dbContext = verification.ServiceProvider
            .GetRequiredService<InventoryDbContext>();
        Assert.Equal(
            revisionBefore,
            await dbContext.Database.SqlQuery<long>($"""
                    SELECT "Revision" AS "Value"
                    FROM inventory.tenant_revisions
                    WHERE "ScopeId" = {TenantId}
                    """).SingleAsync().ConfigureAwait(false));
        Assert.Equal(
            outboxBefore,
            await dbContext.OutboxMessages.CountAsync().ConfigureAwait(false));
        Assert.Equal(
            0,
            await dbContext.ManualBlockGroups.CountAsync(group =>
                    group.PropertyId == propertyId)
                .ConfigureAwait(false));
        Assert.Equal(
            0,
            await dbContext.ManualBlocks.CountAsync(block =>
                    block.PropertyId == propertyId)
                .ConfigureAwait(false));
        Assert.Equal(
            0L,
            await dbContext.Database.SqlQuery<long>($"""
                    SELECT COUNT(*) AS "Value"
                    FROM inventory.management_operations
                    WHERE "ScopeId" = {TenantId}
                      AND "PropertyId" = {propertyId}
                    """).SingleAsync().ConfigureAwait(false));
    }

    private static async Task AssertSelectionFenceProviderSemanticsAsync(
        ServiceProvider services,
        string connectionString,
        Guid propertyId)
    {
        using (IServiceScope noTransactionScope = services.CreateScope())
        {
            IInventoryAvailabilitySelectionFence fence = noTransactionScope
                .ServiceProvider
                .GetRequiredService<IInventoryAvailabilitySelectionFence>();
            await Assert.ThrowsAsync<ArgumentException>(() =>
                fence.AcquireAsync(Guid.Empty, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fence.AcquireAsync(propertyId, CancellationToken.None));
        }

        using IServiceScope firstScope = services.CreateScope();
        using IServiceScope secondScope = services.CreateScope();
        InventoryDbContext firstDb = firstScope.ServiceProvider
            .GetRequiredService<InventoryDbContext>();
        InventoryDbContext secondDb = secondScope.ServiceProvider
            .GetRequiredService<InventoryDbContext>();
        await using var firstTransaction = await firstDb.Database
            .BeginTransactionAsync().ConfigureAwait(false);
        await using var secondTransaction = await secondDb.Database
            .BeginTransactionAsync().ConfigureAwait(false);
        IInventoryAvailabilitySelectionFence firstFence = firstScope
            .ServiceProvider
            .GetRequiredService<IInventoryAvailabilitySelectionFence>();
        IInventoryAvailabilitySelectionFence secondFence = secondScope
            .ServiceProvider
            .GetRequiredService<IInventoryAvailabilitySelectionFence>();
        await firstFence.AcquireAsync(propertyId, CancellationToken.None)
            .ConfigureAwait(false);
        Task queuedAcquire = secondFence.AcquireAsync(
            propertyId,
            CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(200))
            .ConfigureAwait(false);
        Assert.False(queuedAcquire.IsCompleted);
        await firstTransaction.CommitAsync().ConfigureAwait(false);
        await queuedAcquire.WaitAsync(TimeSpan.FromSeconds(10))
            .ConfigureAwait(false);
        await secondTransaction.RollbackAsync().ConfigureAwait(false);

        Guid missingPropertyId = Guid.NewGuid();
        using (IServiceScope missingScope = services.CreateScope())
        {
            InventoryDbContext dbContext = missingScope.ServiceProvider
                .GetRequiredService<InventoryDbContext>();
            await using var transaction = await dbContext.Database
                .BeginTransactionAsync().ConfigureAwait(false);
            IInventoryAvailabilitySelectionFence fence = missingScope
                .ServiceProvider
                .GetRequiredService<IInventoryAvailabilitySelectionFence>();
            await fence.AcquireAsync(
                missingPropertyId,
                CancellationToken.None).ConfigureAwait(false);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fence.AdvanceAsync(
                    missingPropertyId,
                    CancellationToken.None));
            await transaction.RollbackAsync().ConfigureAwait(false);
        }

        await using (ServiceProvider otherTenant = CreateProvider(
                         connectionString,
                         new TestScopeContext(OtherTenantId)))
        {
            using IServiceScope scope = otherTenant.CreateScope();
            InventoryDbContext dbContext = scope.ServiceProvider
                .GetRequiredService<InventoryDbContext>();
            await using var transaction = await dbContext.Database
                .BeginTransactionAsync().ConfigureAwait(false);
            IInventoryAvailabilitySelectionFence fence = scope.ServiceProvider
                .GetRequiredService<IInventoryAvailabilitySelectionFence>();
            await fence.AcquireAsync(propertyId, CancellationToken.None)
                .ConfigureAwait(false);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fence.AdvanceAsync(propertyId, CancellationToken.None));
            await transaction.RollbackAsync().ConfigureAwait(false);
        }

        await using (ServiceProvider disabled = CreateProvider(
                         connectionString,
                         new TestScopeContext(null, IsEnabled: false)))
        {
            using IServiceScope scope = disabled.CreateScope();
            IInventoryAvailabilitySelectionFence fence = scope.ServiceProvider
                .GetRequiredService<IInventoryAvailabilitySelectionFence>();
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fence.AcquireAsync(propertyId, CancellationToken.None));
        }

        await using (NpgsqlConnection connection = new(connectionString))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            await using NpgsqlTransaction transaction =
                await connection.BeginTransactionAsync().ConfigureAwait(false);
            await using NpgsqlCommand replicationMode = new(
                "SET LOCAL session_replication_role = replica;",
                connection,
                transaction);
            await replicationMode.ExecuteNonQueryAsync().ConfigureAwait(false);
            await using NpgsqlCommand command = new(
                """
                UPDATE inventory.property_topology
                SET "AvailabilitySelectionVersion" = 9223372036854775807
                WHERE "ScopeId" = @scope_id AND "Id" = @property_id;
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("scope_id", TenantId);
            command.Parameters.AddWithValue("property_id", propertyId);
            Assert.Equal(
                1,
                await command.ExecuteNonQueryAsync().ConfigureAwait(false));
            await transaction.CommitAsync().ConfigureAwait(false);
        }

        using (IServiceScope overflowScope = services.CreateScope())
        {
            InventoryDbContext dbContext = overflowScope.ServiceProvider
                .GetRequiredService<InventoryDbContext>();
            await using var transaction = await dbContext.Database
                .BeginTransactionAsync().ConfigureAwait(false);
            IInventoryAvailabilitySelectionFence fence = overflowScope
                .ServiceProvider
                .GetRequiredService<IInventoryAvailabilitySelectionFence>();
            await fence.AcquireAsync(propertyId, CancellationToken.None)
                .ConfigureAwait(false);
            InvalidOperationException exhausted =
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    fence.AdvanceAsync(propertyId, CancellationToken.None));
            Assert.Contains(
                "exhausted",
                exhausted.Message,
                StringComparison.OrdinalIgnoreCase);
            await transaction.RollbackAsync().ConfigureAwait(false);
        }
    }

    private static async Task AssertQueuedEligibilityWriterInvalidatesAsync(
        ServiceProvider services,
        string connectionString,
        PreviewManualInventoryBlockGroupQuery previewQuery,
        Func<Task> eligibilityWriter)
    {
        Result<ManualInventoryBlockGroupSelectionPreviewDto> stale =
            await PreviewAsync(services, previewQuery).ConfigureAwait(false);
        Assert.True(stale.IsSuccess, stale.Error.Code);
        Assert.Equal(1, stale.Value.AffectedBlockCount);
        Assert.NotNull(stale.Value.SelectionDigest);

        await using NpgsqlConnection barrier = new(connectionString);
        await barrier.OpenAsync().ConfigureAwait(false);
        await using NpgsqlTransaction barrierTransaction =
            await barrier.BeginTransactionAsync().ConfigureAwait(false);
        await LockPropertyAsync(
            barrier,
            barrierTransaction,
            previewQuery.PropertyId).ConfigureAwait(false);

        Task writer = eligibilityWriter();
        await Task.Delay(TimeSpan.FromMilliseconds(250))
            .ConfigureAwait(false);
        Assert.False(writer.IsCompleted);
        Task<Result<ManualInventoryBlockGroupMutationReceiptDto>> create =
            SendAsync(
                services,
                new CreateManualInventoryBlockGroupCommand(
                    Guid.NewGuid(),
                    previewQuery.PropertyId,
                    previewQuery.Target,
                    previewQuery.Arrival,
                    previewQuery.Departure,
                    previewQuery.Reason,
                    stale.Value.SelectionDigest!,
                    stale.Value.AffectedBlockCount!.Value,
                    Confirmed: true,
                    ActorId: "user:operator"));
        await Task.Delay(TimeSpan.FromMilliseconds(250))
            .ConfigureAwait(false);
        Assert.False(create.IsCompleted);

        await barrierTransaction.CommitAsync().ConfigureAwait(false);
        await writer.WaitAsync(TimeSpan.FromSeconds(15))
            .ConfigureAwait(false);
        Result<ManualInventoryBlockGroupMutationReceiptDto> result =
            await create.WaitAsync(TimeSpan.FromSeconds(15))
                .ConfigureAwait(false);
        Assert.Equal(
            InventoryApplicationErrors.BlockGroupSelectionMismatch,
            result.Error);

        using IServiceScope verification = services.CreateScope();
        InventoryDbContext dbContext = verification.ServiceProvider
            .GetRequiredService<InventoryDbContext>();
        Assert.Equal(
            0,
            await dbContext.ManualBlockGroups.CountAsync(group =>
                    group.PropertyId == previewQuery.PropertyId)
                .ConfigureAwait(false));
        Assert.Equal(
            0,
            await dbContext.ManualBlocks.CountAsync(block =>
                    block.PropertyId == previewQuery.PropertyId)
                .ConfigureAwait(false));
    }

    private static async Task AssertReplacedGraphAsync(
        ServiceProvider services,
        Guid propertyId,
        Guid predecessorId,
        Guid successorId)
    {
        using IServiceScope scope = services.CreateScope();
        InventoryDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<InventoryDbContext>();
        ManualInventoryBlockGroup[] groups = await dbContext
            .ManualBlockGroups
            .AsNoTracking()
            .Where(group => group.PropertyId == propertyId)
            .OrderBy(group => group.CreatedAtUtc)
            .ToArrayAsync().ConfigureAwait(false);
        Assert.Equal(2, groups.Length);
        ManualInventoryBlockGroup predecessor = groups.Single(group =>
            group.Id == predecessorId);
        ManualInventoryBlockGroup successor = groups.Single(group =>
            group.Id == successorId);
        Assert.Equal(
            ManualInventoryBlockGroupState.Replaced,
            predecessor.State);
        Assert.Equal(0, predecessor.ActiveBlockCount);
        Assert.Equal(predecessorId, successor.ReplacesGroupId);
        Assert.Equal(
            ManualInventoryBlockGroupState.Active,
            successor.State);
        Assert.Equal(1, successor.ActiveBlockCount);

        ManualInventoryBlock[] blocks = await dbContext.ManualBlocks
            .AsNoTracking()
            .Where(block => block.PropertyId == propertyId)
            .ToArrayAsync().ConfigureAwait(false);
        Assert.Equal(2, blocks.Length);
        Assert.Single(blocks, block =>
            block.Status == ManualInventoryBlockState.Active);
        Assert.Single(blocks, block =>
            block.Status == ManualInventoryBlockState.Released);
        Assert.Equal(
            2L,
            await dbContext.Database.SqlQuery<long>($"""
                    SELECT COUNT(*) AS "Value"
                    FROM inventory.management_operations
                    WHERE "ScopeId" = {TenantId}
                      AND "PropertyId" = {propertyId}
                    """).SingleAsync().ConfigureAwait(false));
    }

    private static async Task AssertTerminalGraphAsync(
        ServiceProvider services,
        Guid propertyId,
        Guid predecessorId)
    {
        using IServiceScope scope = services.CreateScope();
        InventoryDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<InventoryDbContext>();
        ManualInventoryBlockGroup predecessor = await dbContext
            .ManualBlockGroups
            .AsNoTracking()
            .SingleAsync(group => group.Id == predecessorId)
            .ConfigureAwait(false);
        ManualInventoryBlockGroup[] groups = await dbContext
            .ManualBlockGroups
            .AsNoTracking()
            .Where(group => group.PropertyId == propertyId)
            .ToArrayAsync().ConfigureAwait(false);
        ManualInventoryBlock[] blocks = await dbContext.ManualBlocks
            .AsNoTracking()
            .Where(block => block.PropertyId == propertyId)
            .ToArrayAsync().ConfigureAwait(false);
        Assert.Equal(0, predecessor.ActiveBlockCount);
        Assert.All(
            blocks.Where(block => block.BlockGroupId == predecessorId),
            block => Assert.Equal(
                ManualInventoryBlockState.Released,
                block.Status));
        if (predecessor.State == ManualInventoryBlockGroupState.Replaced)
        {
            Assert.Equal(2, groups.Length);
            ManualInventoryBlockGroup successor = Assert.Single(
                groups,
                group => group.Id != predecessorId);
            Assert.Equal(predecessorId, successor.ReplacesGroupId);
            Assert.Equal(1, successor.ActiveBlockCount);
            Assert.Equal(2, blocks.Length);
            Assert.Single(blocks, block =>
                block.Status == ManualInventoryBlockState.Active);
        }
        else
        {
            Assert.Equal(
                ManualInventoryBlockGroupState.Released,
                predecessor.State);
            Assert.Single(groups);
            Assert.Single(blocks);
        }

        Assert.Equal(
            2L,
            await dbContext.Database.SqlQuery<long>($"""
                    SELECT COUNT(*) AS "Value"
                    FROM inventory.management_operations
                    WHERE "ScopeId" = {TenantId}
                      AND "PropertyId" = {propertyId}
                    """).SingleAsync().ConfigureAwait(false));
    }

    private static ReplaceManualInventoryBlockGroupCommand
        CreateReplaceCommand(
            Guid propertyId,
            ManualInventoryBlockGroupMutationReceiptDto predecessor,
            Guid roomId,
            string reason,
            ManualInventoryBlockGroupSelectionPreviewDto preview) => new(
                Guid.NewGuid(),
                propertyId,
                predecessor.BlockGroupId,
                predecessor.Version!.Value,
                new(
                    InventoryBlockTargetKind.Room,
                    RoomId: roomId),
                Arrival,
                Departure,
                reason,
                preview.SelectionDigest!,
                preview.AffectedBlockCount!.Value,
                Confirmed: true,
                ActorId: "user:operator");

    private static async Task<ManualInventoryBlockGroupMutationReceiptDto>
        CreateGroupAsync(
            ServiceProvider services,
            Guid propertyId,
            Guid roomId,
            string reason)
    {
        InventoryBlockTarget target = new(
            InventoryBlockTargetKind.Room,
            RoomId: roomId);
        Result<ManualInventoryBlockGroupSelectionPreviewDto> preview =
            await PreviewAsync(
                services,
                new(
                    propertyId,
                    target,
                    Arrival,
                    Departure,
                    reason)).ConfigureAwait(false);
        Assert.True(preview.IsSuccess, preview.Error.Code);
        Result<ManualInventoryBlockGroupMutationReceiptDto> created =
            await SendAsync(
                services,
                new CreateManualInventoryBlockGroupCommand(
                    Guid.NewGuid(),
                    propertyId,
                    target,
                    Arrival,
                    Departure,
                    reason,
                    preview.Value.SelectionDigest!,
                    preview.Value.AffectedBlockCount!.Value,
                    Confirmed: true,
                    ActorId: "user:operator")).ConfigureAwait(false);
        Assert.True(created.IsSuccess, created.Error.Code);
        return created.Value;
    }

    private static Task<Result<ManualInventoryBlockGroupSelectionPreviewDto>>
        PreviewReplacementAsync(
            ServiceProvider services,
            Guid propertyId,
            Guid blockGroupId,
            long expectedVersion,
            Guid roomId,
            string reason) => PreviewAsync(
                services,
                new(
                    propertyId,
                    new(
                        InventoryBlockTargetKind.Room,
                        RoomId: roomId),
                    Arrival,
                    Departure,
                    reason,
                    blockGroupId,
                    expectedVersion));

    private static async Task SeedPropertyAsync(
        ServiceProvider services,
        Guid propertyId,
        params RoomSeed[] rooms)
    {
        using IServiceScope scope = services.CreateScope();
        InventoryDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<InventoryDbContext>();
        InventoryPropertyTopology property =
            InventoryPropertyTopology.Create(propertyId, TenantId);
        property.Apply(
            $"Concurrency property {propertyId:N}",
            $"concurrency-{propertyId:N}",
            "UTC",
            PropertyStatus.Active,
            sourceVersion: 1);
        List<object> entities = [property];
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        foreach (RoomSeed seed in rooms)
        {
            InventoryRoomTopology room = InventoryRoomTopology.Create(
                seed.RoomId,
                TenantId,
                propertyId);
            room.Apply(
                propertyId,
                seed.Name,
                seed.Building,
                "1",
                RoomStatus.Active,
                sourceVersion: 1);
            InventoryUnit unit = InventoryUnit.CreateRoom(
                seed.RoomId,
                TenantId,
                propertyId);
            unit.Apply(
                propertyId,
                seed.RoomId,
                bedId: null,
                InventoryUnitKind.Room,
                seed.Name,
                isTopologyActive: true,
                sourceVersion: 1);
            RoomInventoryConfiguration configuration =
                RoomInventoryConfiguration.Create(
                    seed.RoomId,
                    TenantId,
                    propertyId,
                    nowUtc).Value;
            if (seed.SalesMode != RoomSalesMode.Unconfigured)
            {
                Result configured = configuration.Configure(
                    seed.SalesMode,
                    expectedVersion: 1,
                    Guid.NewGuid(),
                    nowUtc,
                    "seed:concurrency-proof");
                Assert.True(configured.IsSuccess, configured.Error.Code);
                configuration.ClearDomainEvents();
            }
            entities.Add(room);
            entities.Add(unit);
            entities.Add(configuration);
        }

        dbContext.AddRange(entities);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    private static async Task MoveRoomAsync(
        ServiceProvider services,
        Guid propertyId,
        Guid roomId,
        string name,
        string building,
        long sourceVersion)
    {
        using IServiceScope scope = services.CreateScope();
        InventoryDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<InventoryDbContext>();
        await using var transaction = await dbContext.Database
            .BeginTransactionAsync().ConfigureAwait(false);
        IIntegrationEventHandler<RoomUpdatedIntegrationEvent> handler =
            ResolveInventoryHandler<RoomUpdatedIntegrationEvent>(
                scope.ServiceProvider);
        await handler.HandleAsync(
            new(
                Guid.NewGuid(),
                TenantId,
                DateTimeOffset.UtcNow,
                propertyId,
                roomId,
                name,
                building,
                "1",
                RoomStatus.Active,
                sourceVersion),
            CancellationToken.None).ConfigureAwait(false);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
    }

    private static async Task LockPropertyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid propertyId)
    {
        await using NpgsqlCommand command = new(
            """
            SELECT 1
            FROM inventory.property_topology
            WHERE "ScopeId" = @scope_id AND "Id" = @property_id
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("scope_id", TenantId);
        command.Parameters.AddWithValue("property_id", propertyId);
        Assert.Equal(1, await command.ExecuteScalarAsync().ConfigureAwait(false));
    }

    private static Guid IndexedRoomId(int index)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(index, 1);
        return Guid.Parse($"23000000-0000-0000-0001-{index:D12}");
    }

    private static async Task<long> GetTenantRevisionAsync(
        ServiceProvider services)
    {
        using IServiceScope scope = services.CreateScope();
        InventoryDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<InventoryDbContext>();
        return await dbContext.Database.SqlQuery<long>($"""
                SELECT "Revision" AS "Value"
                FROM inventory.tenant_revisions
                WHERE "ScopeId" = {TenantId}
                """).SingleAsync().ConfigureAwait(false);
    }

    private static async Task<int> GetOutboxCountAsync(
        ServiceProvider services)
    {
        using IServiceScope scope = services.CreateScope();
        InventoryDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<InventoryDbContext>();
        return await dbContext.OutboxMessages.CountAsync()
            .ConfigureAwait(false);
    }

    private static async Task MigrateAsync(ServiceProvider services)
    {
        using IServiceScope scope = services.CreateScope();
        InventoryDbContext dbContext = scope.ServiceProvider
            .GetRequiredService<InventoryDbContext>();
        await dbContext.Database.MigrateAsync().ConfigureAwait(false);
    }

    private static async Task<PostgreSqlContainer> StartAsync(string database)
    {
        PostgreSqlContainer postgreSql = new PostgreSqlBuilder(
                "postgres:16-alpine")
            .WithDatabase(database)
            .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);
        return postgreSql;
    }

    private static ServiceProvider CreateProvider(
        string connectionString,
        IScopeContext scopeContext,
        DbCommandInterceptor? interceptor = null)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.Configuration["Persistence:Provider"] = "PostgreSql";
        builder.Configuration["ConnectionStrings:PostgreSql"] =
            connectionString;
        builder.Services.AddSingleton(scopeContext);
        builder.Services.AddSingleton<IWorkspaceTerminationFenceReader>(
            OpenWorkspaceTerminationFenceReader.Instance);
        builder.AddApplicationEventsInfrastructure();
        builder.AddCqrsInfrastructure();
        builder.AddMessagingInfrastructure();
        builder.Services.AddInventoryApplication();
        builder.AddInventoryPersistence();
        if (interceptor is not null)
        {
            builder.Services.AddDbContext<InventoryDbContext>(options =>
                options.AddInterceptors(interceptor));
        }

        return builder.Services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });
    }

    private static async Task<Result<ManualInventoryBlockGroupSelectionPreviewDto>>
        PreviewAsync(
            ServiceProvider services,
            PreviewManualInventoryBlockGroupQuery query)
    {
        using IServiceScope scope = services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<IRequestDispatcher>()
            .QueryAsync(query, CancellationToken.None)
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
            ReplaceManualInventoryBlockGroupCommand command)
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

    private static async Task<Result<RoomRetirementDto>> SendAsync(
        ServiceProvider services,
        CancelRoomRetirementCommand command)
    {
        using IServiceScope scope = services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<IRequestDispatcher>()
            .SendAsync(command, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static IIntegrationEventHandler<TEvent>
        ResolveInventoryHandler<TEvent>(IServiceProvider services)
        where TEvent : IIntegrationEvent
    {
        IntegrationEventSubscription subscription = services
            .GetRequiredService<IIntegrationEventSubscriptionRegistry>()
            .Subscriptions
            .Single(item =>
                item.ConsumerModule == InventoryModuleMetadata.Name &&
                item.EventType == typeof(TEvent));
        return (IIntegrationEventHandler<TEvent>)services
            .GetRequiredService(subscription.HandlerType);
    }

    private sealed record RoomSeed(
        Guid RoomId,
        string Name,
        string Building,
        RoomSalesMode SalesMode = RoomSalesMode.RoomLevel);

    private sealed class TestScopeContext(
        string? scopeId,
        bool IsEnabled = true)
        : IScopeContext
    {
        public bool IsEnabled { get; } = IsEnabled;
        public string? ScopeId { get; } = scopeId;
    }

    private sealed class PreviewCoordinateBarrier : DbCommandInterceptor
    {
        private readonly Lock gate = new();
        private bool armed;
        private TaskCompletionSource<bool> reached = NewSignal();
        private TaskCompletionSource<bool> release = NewSignal();

        public void Arm()
        {
            lock (this.gate)
            {
                this.reached = NewSignal();
                this.release = NewSignal();
                this.armed = true;
            }
        }

        public Task<bool> WaitUntilPausedAsync()
        {
            lock (this.gate)
            {
                return this.reached.Task;
            }
        }

        public void Continue()
        {
            lock (this.gate)
            {
                this.release.TrySetResult(true);
            }
        }

        public override async ValueTask<InterceptionResult<DbDataReader>>
            ReaderExecutingAsync(
                DbCommand command,
                CommandEventData eventData,
                InterceptionResult<DbDataReader> result,
                CancellationToken cancellationToken = default)
        {
            TaskCompletionSource<bool>? reachedSignal = null;
            TaskCompletionSource<bool>? releaseSignal = null;
            lock (this.gate)
            {
                if (this.armed &&
                    eventData.Context is InventoryDbContext &&
                    IsSelectionCoordinateQuery(command.CommandText))
                {
                    this.armed = false;
                    reachedSignal = this.reached;
                    releaseSignal = this.release;
                }
            }

            if (reachedSignal is not null && releaseSignal is not null)
            {
                reachedSignal.TrySetResult(true);
                await releaseSignal.Task.WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            return await base.ReaderExecutingAsync(
                    command,
                    eventData,
                    result,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private static bool IsSelectionCoordinateQuery(string commandText) =>
            commandText.Contains(
                "AvailabilitySelectionVersion",
                StringComparison.Ordinal) &&
            commandText.Contains(
                "inventory_units",
                StringComparison.OrdinalIgnoreCase) &&
            commandText.Contains(
                "room_configurations",
                StringComparison.OrdinalIgnoreCase);

        private static TaskCompletionSource<bool> NewSignal() => new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
