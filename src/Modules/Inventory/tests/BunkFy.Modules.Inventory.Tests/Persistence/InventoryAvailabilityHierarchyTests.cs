namespace BunkFy.Modules.Inventory.Tests;

using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using BunkFy.Modules.Inventory.Persistence;
using BunkFy.Modules.Inventory.Persistence.Repositories;
using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Scoping;
using Gma.Framework.Messaging;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Microsoft.EntityFrameworkCore;
using Xunit;

[Trait("Category", "Unit")]
public sealed class InventoryAvailabilityHierarchyTests
{
    [Fact]
    public async Task Room_claim_conflicts_with_beds_but_beds_remain_independent()
    {
        await using InventoryDbContext dbContext = CreateDbContext();
        (Guid propertyId, Guid roomId, Guid bedA, Guid bedB) = SeedTopology(dbContext);
        InventoryAllocation allocation = InventoryAllocation.CreateAccepted(
            Guid.NewGuid(),
            "tenant-a",
            Guid.NewGuid(),
            Guid.NewGuid(),
            propertyId,
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 3),
            [bedA],
            Now).Value;
        dbContext.Allocations.Add(allocation);
        await dbContext.SaveChangesAsync();
        InventoryAvailabilityRepository availability = CreateAvailability(dbContext);

        Assert.True(await HasAllocationConflictAsync(availability, propertyId, roomId));
        Assert.True(await HasAllocationConflictAsync(availability, propertyId, bedA));
        Assert.False(await HasAllocationConflictAsync(availability, propertyId, bedB));
    }

    [Fact]
    public async Task Room_block_conflicts_with_each_bed()
    {
        await using InventoryDbContext dbContext = CreateDbContext();
        (Guid propertyId, Guid roomId, Guid bedA, Guid bedB) = SeedTopology(dbContext);
        ManualInventoryBlock block = ManualInventoryBlock.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "tenant-a",
            propertyId,
            roomId,
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 3),
            "Maintenance",
            Guid.NewGuid(),
            Now).Value;
        dbContext.ManualBlocks.Add(block);
        await dbContext.SaveChangesAsync();
        InventoryAvailabilityRepository availability = CreateAvailability(dbContext);

        Assert.True(await HasManualBlockConflictAsync(availability, propertyId, bedA));
        Assert.True(await HasManualBlockConflictAsync(availability, propertyId, bedB));
    }

    [Fact]
    public async Task Room_retirement_drains_every_room_unit_from_new_sales()
    {
        await using InventoryDbContext dbContext = CreateDbContext();
        (Guid propertyId, Guid roomId, Guid bedA, Guid bedB) = SeedTopology(dbContext);
        RoomRetirementProcess process = RoomRetirementProcess.Create(
            Guid.NewGuid(),
            "tenant-a",
            propertyId,
            roomId,
            "Permanent closure",
            "user:operator-a",
            Now).Value;
        dbContext.RoomRetirements.Add(process);
        await dbContext.SaveChangesAsync();
        InventoryAvailabilityRepository availability = CreateAvailability(dbContext);

        InventoryAvailabilityContextSnapshot context = await availability.GetContextAsync(
            propertyId,
            [roomId, bedA, bedB],
            CancellationToken.None);

        Assert.Equal(3, context.Units.Count);
        Assert.All(context.Units, unit => Assert.False(unit.IsSellable));
    }

    [Fact]
    public async Task Room_impact_returns_a_bounded_reservation_sample()
    {
        await using InventoryDbContext dbContext = CreateDbContext();
        (Guid propertyId, Guid roomId, Guid bedA, _) = SeedTopology(dbContext);
        for (int index = 0; index < InventoryImpactLimits.AffectedReservationSampleSize + 5; index++)
        {
            dbContext.Allocations.Add(InventoryAllocation.CreateAccepted(
                Guid.NewGuid(),
                "tenant-a",
                Guid.NewGuid(),
                Guid.NewGuid(),
                propertyId,
                new DateOnly(2026, 8, 1).AddDays(index * 2),
                new DateOnly(2026, 8, 2).AddDays(index * 2),
                [bedA],
                Now).Value);
        }

        await dbContext.SaveChangesAsync();
        InventoryAvailabilityRepository availability = CreateAvailability(dbContext);

        RoomInventoryImpactSnapshot impact = Assert.IsType<RoomInventoryImpactSnapshot>(
            await availability.GetRoomImpactAsync(propertyId, roomId, CancellationToken.None));

        Assert.Equal(InventoryImpactLimits.AffectedReservationSampleSize + 5, impact.ActiveAllocationCount);
        Assert.Equal(InventoryImpactLimits.AffectedReservationSampleSize, impact.AffectedReservationIds.Count);
        Assert.True(impact.AffectedReservationIdsTruncated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Touch_publishes_exact_distinct_post_mutation_definitions_with_current_sellability(bool draining)
    {
        await using InventoryDbContext dbContext = CreateDbContext();
        (Guid propertyId, Guid roomId, Guid bedA, Guid bedB) = SeedTopology(dbContext);
        if (draining)
        {
            dbContext.RoomRetirements.Add(RoomRetirementProcess.Create(
                Guid.NewGuid(), "tenant-a", propertyId, roomId, "Synthetic closure", "user:operator", Now).Value);
        }
        RecordingOutbox outbox = new();
        InventoryAvailabilityRepository availability = new(
            dbContext, new InventoryTopologyRepository(dbContext), outbox, new TestClock(), new TestIds());

        await availability.TouchUnitsAsync(propertyId, [bedA, bedB, bedA], CancellationToken.None);

        Assert.Equal(new[] { bedA, bedB }.Order(), outbox.Events.Select(item => item.InventoryUnitId));
        Assert.All(outbox.Events, item =>
        {
            Assert.Equal(2, item.UnitVersion);
            Assert.Equal(2, item.ConfigurationVersion);
            Assert.True(item.IsTopologyActive);
            Assert.Equal(!draining, item.IsSellable);
        });
        Assert.Equal(1, (await dbContext.InventoryUnits.SingleAsync(unit => unit.Id == roomId)).AvailabilityMutationVersion);
        Assert.Equal(3, (await dbContext.RoomConfigurations.SingleAsync()).AvailabilityMutationVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_unit_or_configuration_publishes_nothing_and_does_not_touch_versions(bool missingConfiguration)
    {
        await using InventoryDbContext dbContext = CreateDbContext();
        (Guid propertyId, _, Guid bedA, _) = SeedTopology(dbContext);
        if (missingConfiguration)
        {
            dbContext.RoomConfigurations.Remove(await dbContext.RoomConfigurations.SingleAsync());
            await dbContext.SaveChangesAsync();
        }
        RecordingOutbox outbox = new();
        InventoryAvailabilityRepository availability = new(
            dbContext, new InventoryTopologyRepository(dbContext), outbox, new TestClock(), new TestIds());
        Guid[] ids = missingConfiguration ? [bedA] : [bedA, Guid.NewGuid()];

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            availability.TouchUnitsAsync(propertyId, ids, CancellationToken.None));

        Assert.Empty(outbox.Events);
        Assert.All(await dbContext.InventoryUnits.ToArrayAsync(), unit => Assert.Equal(1, unit.AvailabilityMutationVersion));
    }

    private static async Task<bool> HasAllocationConflictAsync(
        InventoryAvailabilityRepository availability,
        Guid propertyId,
        Guid requestedUnitId)
    {
        InventoryAvailabilityContextSnapshot context = await availability.GetContextAsync(
            propertyId,
            [requestedUnitId],
            CancellationToken.None);
        InventoryAvailabilityConflictSnapshot conflicts = await availability.GetConflictsAsync(
            propertyId,
            context.ConflictUnitIds,
            new(2026, 8, 2),
            new(2026, 8, 4),
            excludedAllocationId: null,
            excludedBlockIds: [],
            CancellationToken.None);
        return conflicts.HasActiveAllocationConflict;
    }

    private static async Task<bool> HasManualBlockConflictAsync(
        InventoryAvailabilityRepository availability,
        Guid propertyId,
        Guid requestedUnitId)
    {
        InventoryAvailabilityContextSnapshot context = await availability.GetContextAsync(
            propertyId,
            [requestedUnitId],
            CancellationToken.None);
        InventoryAvailabilityConflictSnapshot conflicts = await availability.GetConflictsAsync(
            propertyId,
            context.ConflictUnitIds,
            new(2026, 8, 2),
            new(2026, 8, 4),
            excludedAllocationId: null,
            excludedBlockIds: [],
            CancellationToken.None);
        return conflicts.HasManualBlockConflict;
    }

    private static (Guid PropertyId, Guid RoomId, Guid BedA, Guid BedB) SeedTopology(InventoryDbContext dbContext)
    {
        Guid propertyId = Guid.NewGuid();
        Guid roomId = Guid.NewGuid();
        Guid bedA = Guid.NewGuid();
        Guid bedB = Guid.NewGuid();
        InventoryPropertyTopology property = InventoryPropertyTopology.Create(propertyId, "tenant-a");
        property.Apply("Hostel", "hostel", "UTC", PropertyStatus.Active, 1);
        InventoryRoomTopology room = InventoryRoomTopology.Create(roomId, "tenant-a", propertyId);
        room.Apply(propertyId, "101", null, null, RoomStatus.Active, 1);
        InventoryUnit roomUnit = InventoryUnit.CreateRoom(roomId, "tenant-a", propertyId);
        roomUnit.Apply(propertyId, roomId, null, InventoryUnitKind.Room, "101", true, 1);
        InventoryUnit bedUnitA = InventoryUnit.CreateBed(bedA, "tenant-a", propertyId, roomId);
        bedUnitA.Apply(propertyId, roomId, bedA, InventoryUnitKind.Bed, "A", true, 1);
        InventoryUnit bedUnitB = InventoryUnit.CreateBed(bedB, "tenant-a", propertyId, roomId);
        bedUnitB.Apply(propertyId, roomId, bedB, InventoryUnitKind.Bed, "B", true, 1);
        RoomInventoryConfiguration configuration = RoomInventoryConfiguration.Create(
            roomId,
            "tenant-a",
            propertyId,
            Now).Value;
        configuration.Configure(RoomSalesMode.BedLevel, 1, Guid.NewGuid(), Now);
        configuration.ClearDomainEvents();
        dbContext.AddRange(property, room, roomUnit, bedUnitA, bedUnitB, configuration);
        dbContext.SaveChanges();
        return (propertyId, roomId, bedA, bedB);
    }

    private static InventoryAvailabilityRepository CreateAvailability(InventoryDbContext dbContext) =>
        new(dbContext, new InventoryTopologyRepository(dbContext), new ReadOnlyOutboxRegistry(), new TestClock(), new TestIds());

    private sealed class ReadOnlyOutboxRegistry : IOutboxWriterRegistry
    {
        public IOutboxWriter GetRequired(string moduleName) =>
            throw new InvalidOperationException("Read-only availability queries must not publish.");
    }

    private sealed class RecordingOutbox : IOutboxWriter, IOutboxWriterRegistry
    {
        public string ModuleName => InventoryModuleMetadata.Name;
        public List<InventoryUnitDefinitionChangedIntegrationEvent> Events { get; } = [];
        public IOutboxWriter GetRequired(string moduleName) => this;

        public Task EnqueueAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken)
            where TEvent : IIntegrationEvent
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.Events.Add(Assert.IsType<InventoryUnitDefinitionChangedIntegrationEvent>(integrationEvent));
            return Task.CompletedTask;
        }
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class TestIds : IIdGenerator
    {
        public Guid NewId() => Guid.NewGuid();
    }

    private static InventoryDbContext CreateDbContext()
    {
        DbContextOptions<InventoryDbContext> options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase($"inventory-hierarchy-{Guid.NewGuid():N}")
            .Options;
        return new InventoryDbContext(options, new TestScopeContext());
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }

    private static readonly DateTimeOffset Now = new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
}
