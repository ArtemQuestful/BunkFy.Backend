namespace BunkFy.Modules.Inventory.Tests;

using BunkFy.Modules.Inventory.Application.Handlers;
using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Xunit;

[Trait("Category", "Unit")]
public sealed class InventoryRetirementAutoAdvanceTests
{
    private const string TenantId = "tenant-a";
    private static readonly Guid PropertyId = Guid.Parse(
        "10000000-0000-0000-0000-000000000001");
    private static readonly Guid RoomId = Guid.Parse(
        "20000000-0000-0000-0000-000000000001");
    private static readonly Guid BedId = Guid.Parse(
        "30000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset Now = new(
        2026,
        8,
        11,
        8,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public async Task Bed_auto_advance_honors_cancellation_that_wins_the_process_lock()
    {
        BedRetirementProcess process = BedRetirementProcess.Create(
            Guid.NewGuid(),
            TenantId,
            PropertyId,
            RoomId,
            BedId,
            "Replace bed",
            "user:operator",
            Now).Value;
        RecordingManagementLock operationLock = new();
        CancelingBedRepository retirements = new(process);
        TestIdGenerator ids = new();
        BedRetirementCoordinator coordinator = new(
            new InventoryManagementMutationCoordinator(
                operationLock,
                new TestScopeContext()),
            retirements,
            new UnusedAvailabilityRepository(),
            new TestBusinessDateProvider(),
            new TestClock(),
            ids);

        await coordinator.TryAdvanceForUnitsAsync(
            PropertyId,
            [BedId],
            excludedAllocationId: null,
            excludedBlockIds: [],
            CancellationToken.None);

        Assert.Equal(InventoryRetirementProcessState.Canceled, process.State);
        Assert.Equal(1, retirements.ReloadCount);
        Assert.Equal(0, ids.CallCount);
        Assert.Equal(["BedRetirement"], operationLock.ResourceKinds);
    }

    [Fact]
    public async Task Room_auto_advance_honors_cancellation_that_wins_the_process_lock()
    {
        RoomRetirementProcess process = RoomRetirementProcess.Create(
            Guid.NewGuid(),
            TenantId,
            PropertyId,
            RoomId,
            "Repurpose room",
            "user:operator",
            Now).Value;
        RecordingManagementLock operationLock = new();
        CancelingRoomRepository retirements = new(process);
        TestIdGenerator ids = new();
        RoomRetirementCoordinator coordinator = new(
            new InventoryManagementMutationCoordinator(
                operationLock,
                new TestScopeContext()),
            retirements,
            new UnusedAvailabilityRepository(),
            new TestBusinessDateProvider(),
            new TestClock(),
            ids);

        await coordinator.TryAdvanceForUnitsAsync(
            PropertyId,
            [BedId],
            excludedAllocationId: null,
            excludedBlockIds: [],
            CancellationToken.None);

        Assert.Equal(InventoryRetirementProcessState.Canceled, process.State);
        Assert.Equal(1, retirements.ReloadCount);
        Assert.Equal(0, ids.CallCount);
        Assert.Equal(["RoomRetirement"], operationLock.ResourceKinds);
    }

    private sealed class CancelingBedRepository(BedRetirementProcess process)
        : IBedRetirementRepository
    {
        public int ReloadCount { get; private set; }

        public Task<BedRetirementProcess?> GetAsync(
            Guid propertyId,
            Guid topologyChangeId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Guid?> GetTopologyChangeIdByBedAsync(
            Guid propertyId,
            Guid bedId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BedRetirementProcess?> GetByBedAsync(
            Guid propertyId,
            Guid bedId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyCollection<BedRetirementProcess>> ListActiveForUnitsAsync(
            Guid propertyId,
            IReadOnlyCollection<Guid> inventoryUnitIds,
            CancellationToken cancellationToken) => Task.FromResult<
                IReadOnlyCollection<BedRetirementProcess>>([process]);

        public Task AddAsync(
            BedRetirementProcess value,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ReloadAsync(
            BedRetirementProcess value,
            CancellationToken cancellationToken)
        {
            this.ReloadCount++;
            Assert.True(value.Cancel(
                value.Version,
                "Keep bed in service",
                "user:manager",
                Now.AddMinutes(1)).IsSuccess);
            return Task.CompletedTask;
        }
    }

    private sealed class CancelingRoomRepository(RoomRetirementProcess process)
        : IRoomRetirementRepository
    {
        public int ReloadCount { get; private set; }

        public Task<RoomRetirementProcess?> GetAsync(
            Guid propertyId,
            Guid topologyChangeId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Guid?> GetTopologyChangeIdByRoomAsync(
            Guid propertyId,
            Guid roomId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RoomRetirementProcess?> GetByRoomAsync(
            Guid propertyId,
            Guid roomId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyCollection<RoomRetirementProcess>> ListActiveForUnitsAsync(
            Guid propertyId,
            IReadOnlyCollection<Guid> inventoryUnitIds,
            CancellationToken cancellationToken) => Task.FromResult<
                IReadOnlyCollection<RoomRetirementProcess>>([process]);

        public Task AddAsync(
            RoomRetirementProcess value,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ReloadAsync(
            RoomRetirementProcess value,
            CancellationToken cancellationToken)
        {
            this.ReloadCount++;
            Assert.True(value.Cancel(
                value.Version,
                "Keep room in service",
                "user:manager",
                Now.AddMinutes(1)).IsSuccess);
            return Task.CompletedTask;
        }
    }

    private sealed class UnusedAvailabilityRepository
        : IInventoryAvailabilityRepository
    {
        public Task<InventoryAvailabilityContextSnapshot> GetContextAsync(
            Guid propertyId,
            IReadOnlyCollection<Guid> inventoryUnitIds,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<InventoryAvailabilityConflictSnapshot> GetConflictsAsync(
            Guid propertyId,
            IReadOnlyCollection<Guid> conflictUnitIds,
            DateOnly arrival,
            DateOnly departure,
            Guid? excludedAllocationId,
            IReadOnlyCollection<Guid> excludedBlockIds,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RoomInventoryImpactSnapshot?> GetRoomImpactAsync(
            Guid propertyId,
            Guid roomId,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BedRetirementImpactSnapshot?> GetBedRetirementImpactAsync(
            Guid propertyId,
            Guid roomId,
            Guid bedId,
            Guid? excludedAllocationId,
            IReadOnlyCollection<Guid> excludedBlockIds,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task TouchUnitsAsync(
            Guid propertyId,
            IReadOnlyCollection<Guid> inventoryUnitIds,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingManagementLock : IInventoryManagementLock
    {
        public List<string> ResourceKinds { get; } = [];

        public Task AcquireResourceAsync(
            string tenantId,
            InventoryManagementResourceKind resourceKind,
            Guid resourceId,
            CancellationToken cancellationToken)
        {
            Assert.Equal(TenantId, tenantId);
            this.ResourceKinds.Add(resourceKind.ToString());
            return Task.CompletedTask;
        }

        public Task AcquireOperationAsync(
            string tenantId,
            InventoryManagementResourceKind resourceKind,
            Guid resourceId,
            Guid operationId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => TenantId;
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class TestBusinessDateProvider : IInventoryBusinessDateProvider
    {
        public Task<DateOnly?> GetAsync(
            Guid propertyId,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken) => Task.FromResult<DateOnly?>(
                DateOnly.FromDateTime(nowUtc.UtcDateTime));
    }

    private sealed class TestIdGenerator : IIdGenerator
    {
        public int CallCount { get; private set; }

        public Guid NewId()
        {
            this.CallCount++;
            return Guid.NewGuid();
        }
    }
}
