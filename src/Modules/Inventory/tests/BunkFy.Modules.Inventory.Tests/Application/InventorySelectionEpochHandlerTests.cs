namespace BunkFy.Modules.Inventory.Tests.Application;

using BunkFy.Modules.Inventory.Application.Handlers;
using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Messaging;
using Gma.Framework.Runtime.Identity;
using Xunit;

[Trait("Category", "Unit")]
public sealed class InventorySelectionEpochHandlerTests
{
    private static readonly Guid PropertyId =
        Guid.Parse("81000000-0000-0000-0000-000000000001");
    private static readonly Guid RoomId =
        Guid.Parse("81000000-0000-0000-0000-000000000002");

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task Room_topology_update_advances_epoch_only_for_an_accepted_change(
        bool projectionChanged,
        int expectedAdvanceCount)
    {
        List<string> trace = [];
        RecordingTopologyRepository topology = new(projectionChanged, trace);
        RecordingSelectionFence selectionFence = new(trace);
        RoomUpdatedTopologyHandler handler = new(
            topology,
            new ExistingConfigurationRepository(),
            selectionFence,
            new InventoryUnitDefinitionPublisher(
                topology,
                new NoOpOutboxRegistry(),
                new TestIdGenerator()));

        await handler.HandleAsync(
            new RoomUpdatedIntegrationEvent(
                Guid.NewGuid(),
                "tenant-a",
                new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero),
                PropertyId,
                RoomId,
                "101",
                "A",
                "1",
                RoomStatus.Active,
                roomVersion: 7),
            CancellationToken.None);

        Assert.Equal(1, selectionFence.AcquireCount);
        Assert.Equal(expectedAdvanceCount, selectionFence.AdvanceCount);
        Assert.Equal(
            projectionChanged
                ? ["selection", "apply", "advance"]
                : ["selection", "apply"],
            trace);
    }

    private sealed class RecordingTopologyRepository(
        bool projectionChanged,
        List<string> trace) : IInventoryTopologyRepository
    {
        public Task<bool> ApplyRoomAsync(
            InventoryRoomTopologyWriteModel room,
            CancellationToken cancellationToken)
        {
            Assert.Equal(PropertyId, room.PropertyId);
            Assert.Equal(RoomId, room.RoomId);
            trace.Add("apply");
            return Task.FromResult(projectionChanged);
        }

        public Task<IReadOnlyCollection<InventoryUnitDefinitionSnapshot>> GetUnitDefinitionsAsync(
            Guid propertyId,
            Guid? roomId,
            Guid? inventoryUnitId,
            bool touchVersions,
            CancellationToken cancellationToken)
        {
            Assert.Equal(PropertyId, propertyId);
            Assert.Equal(RoomId, roomId);
            Assert.Null(inventoryUnitId);
            Assert.True(touchVersions);
            return Task.FromResult<IReadOnlyCollection<InventoryUnitDefinitionSnapshot>>([]);
        }

        public Task<bool> ApplyPropertyAsync(
            InventoryPropertyTopologyWriteModel property,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> ApplyBedAsync(
            InventoryBedTopologyWriteModel bed,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<InventoryRoomTopologySnapshot?> GetRoomAsync(
            Guid propertyId,
            Guid roomId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ExistingConfigurationRepository : IRoomInventoryConfigurationRepository
    {
        public Task<bool> EnsureAsync(
            string scopeId,
            Guid propertyId,
            Guid roomId,
            DateTimeOffset createdAtUtc,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<BunkFy.Modules.Inventory.Domain.Aggregates.RoomInventoryConfiguration?> GetAsync(
            Guid propertyId,
            Guid roomId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingSelectionFence(List<string> trace)
        : IInventoryAvailabilitySelectionFence
    {
        public int AcquireCount { get; private set; }
        public int AdvanceCount { get; private set; }

        public Task AcquireAsync(Guid propertyId, CancellationToken cancellationToken)
        {
            Assert.Equal(PropertyId, propertyId);
            this.AcquireCount++;
            trace.Add("selection");
            return Task.CompletedTask;
        }

        public Task AdvanceAsync(Guid propertyId, CancellationToken cancellationToken)
        {
            Assert.Equal(PropertyId, propertyId);
            this.AdvanceCount++;
            trace.Add("advance");
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpOutboxRegistry : IOutboxWriterRegistry
    {
        public IOutboxWriter GetRequired(string moduleName)
        {
            Assert.Equal(InventoryModuleMetadata.Name, moduleName);
            return new NoOpOutbox();
        }
    }

    private sealed class NoOpOutbox : IOutboxWriter
    {
        public string ModuleName => InventoryModuleMetadata.Name;

        public Task EnqueueAsync<TEvent>(
            TEvent integrationEvent,
            CancellationToken cancellationToken)
            where TEvent : IIntegrationEvent => Task.CompletedTask;
    }

    private sealed class TestIdGenerator : IIdGenerator
    {
        public Guid NewId() => Guid.NewGuid();
    }
}
