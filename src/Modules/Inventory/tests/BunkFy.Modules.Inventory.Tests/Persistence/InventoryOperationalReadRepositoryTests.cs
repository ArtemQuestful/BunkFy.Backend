namespace BunkFy.Modules.Inventory.Tests.Persistence;

using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using BunkFy.Modules.Inventory.Persistence;
using BunkFy.Modules.Inventory.Persistence.Repositories;
using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Pagination;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Xunit;

[Trait("Category", "Unit")]
public sealed class InventoryOperationalReadRepositoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Room_directory_uses_bounded_lookahead()
    {
        await using InventoryDbContext dbContext = CreateDbContext();
        Guid propertyId = SeedRooms(dbContext, "103", "101", "102");
        InventoryReadRepository repository = new(dbContext);

        RoomInventoryListResponse first = await repository.ListRoomsAsync(
            propertyId,
            new PageRequest(1, 2),
            CancellationToken.None);
        RoomInventoryListResponse last = await repository.ListRoomsAsync(
            propertyId,
            new PageRequest(2, 2),
            CancellationToken.None);

        Assert.Equal(["101", "102"], first.Rooms.Select(room => room.RoomName));
        Assert.True(first.HasMore);
        Assert.Equal(["103"], last.Rooms.Select(room => room.RoomName));
        Assert.False(last.HasMore);
    }

    [Fact]
    public async Task Block_directory_uses_bounded_lookahead()
    {
        await using InventoryDbContext dbContext = CreateDbContext();
        Guid propertyId = SeedRooms(dbContext, "101");
        Guid unitId = await dbContext.InventoryUnits.Select(unit => unit.Id).SingleAsync();
        foreach (int offset in new[] { 2, 0, 1 })
        {
            dbContext.ManualBlocks.Add(ManualInventoryBlock.Create(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "tenant-a",
                propertyId,
                unitId,
                new DateOnly(2026, 8, 10).AddDays(offset),
                new DateOnly(2026, 8, 11).AddDays(offset),
                $"Maintenance {offset}",
                Guid.NewGuid(),
                Now).Value);
        }

        await dbContext.SaveChangesAsync();
        ManualInventoryBlockRepository repository = new(dbContext);

        ManualInventoryBlockListResponse first = await repository.ListAsync(
            propertyId,
            inventoryUnitId: null,
            includeReleased: false,
            overlapsFrom: null,
            overlapsTo: null,
            new PageRequest(1, 2),
            CancellationToken.None);
        ManualInventoryBlockListResponse last = await repository.ListAsync(
            propertyId,
            inventoryUnitId: null,
            includeReleased: false,
            overlapsFrom: null,
            overlapsTo: null,
            new PageRequest(2, 2),
            CancellationToken.None);

        Assert.Equal(2, first.Blocks.Count);
        Assert.True(first.HasMore);
        Assert.Single(last.Blocks);
        Assert.False(last.HasMore);
    }

    [Fact]
    public async Task Block_directory_can_be_bounded_to_the_visible_schedule()
    {
        await using InventoryDbContext dbContext = CreateDbContext();
        Guid propertyId = SeedRooms(dbContext, "101");
        Guid unitId = await dbContext.InventoryUnits.Select(unit => unit.Id).SingleAsync();
        foreach ((DateOnly arrival, string reason) in new[]
                 {
                     (new DateOnly(2026, 7, 28), "Before"),
                     (new DateOnly(2026, 7, 31), "Spanning"),
                     (new DateOnly(2026, 8, 4), "Inside"),
                     (new DateOnly(2026, 8, 12), "After")
                 })
        {
            dbContext.ManualBlocks.Add(ManualInventoryBlock.Create(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "tenant-a",
                propertyId,
                unitId,
                arrival,
                arrival.AddDays(2),
                reason,
                Guid.NewGuid(),
                Now).Value);
        }

        await dbContext.SaveChangesAsync();
        ManualInventoryBlockRepository repository = new(dbContext);

        ManualInventoryBlockListResponse result = await repository.ListAsync(
            propertyId,
            inventoryUnitId: null,
            includeReleased: false,
            overlapsFrom: new DateOnly(2026, 8, 1),
            overlapsTo: new DateOnly(2026, 8, 10),
            new PageRequest(1, 20),
            CancellationToken.None);

        Assert.Equal(["Spanning", "Inside"], result.Blocks.Select(block => block.Reason));
        Assert.False(result.HasMore);
    }

    private static Guid SeedRooms(InventoryDbContext dbContext, params string[] names)
    {
        Guid propertyId = Guid.NewGuid();
        InventoryPropertyTopology property = InventoryPropertyTopology.Create(propertyId, "tenant-a");
        property.Apply("Hostel", "hostel", "UTC", PropertyStatus.Active, 1);
        dbContext.PropertyTopology.Add(property);

        foreach (string name in names)
        {
            Guid roomId = Guid.NewGuid();
            InventoryRoomTopology room = InventoryRoomTopology.Create(roomId, "tenant-a", propertyId);
            room.Apply(propertyId, name, null, null, RoomStatus.Active, 1);
            InventoryUnit unit = InventoryUnit.CreateRoom(roomId, "tenant-a", propertyId);
            unit.Apply(propertyId, roomId, null, InventoryUnitKind.Room, name, true, 1);
            RoomInventoryConfiguration configuration = RoomInventoryConfiguration.Create(
                roomId,
                "tenant-a",
                propertyId,
                Now).Value;
            configuration.Configure(RoomSalesMode.RoomLevel, 1, Guid.NewGuid(), Now);
            configuration.ClearDomainEvents();
            dbContext.AddRange(room, unit, configuration);
        }

        dbContext.SaveChanges();
        return propertyId;
    }

    private static InventoryDbContext CreateDbContext()
    {
        DbContextOptions<InventoryDbContext> options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase($"inventory-operational-read-{Guid.NewGuid():N}")
            .Options;
        return new InventoryDbContext(options, new TestScopeContext());
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }
}
