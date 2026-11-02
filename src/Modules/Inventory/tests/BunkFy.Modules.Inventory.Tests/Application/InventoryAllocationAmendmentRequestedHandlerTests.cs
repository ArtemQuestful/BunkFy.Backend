namespace BunkFy.Modules.Inventory.Tests;

using Gma.Framework.Messaging;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using BunkFy.Modules.Inventory.Application.Handlers;
using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using Gma.Framework.Scoping;
using Xunit;

[Trait("Category", "Unit")]
public sealed class InventoryAllocationAmendmentRequestedHandlerTests
{
    private const string ScopeId = "tenant-a";
    private static readonly DateTimeOffset Now = new(2026, 7, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid PropertyId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid ReservationId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid AllocationId = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid UnitId = Guid.Parse("40000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task Exact_replay_republishes_the_decision_without_mutating_twice()
    {
        InventoryAllocation allocation = InventoryAllocation.CreateAccepted(
            AllocationId,
            ScopeId,
            ReservationId,
            Guid.NewGuid(),
            PropertyId,
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 3),
            [UnitId],
            Now).Value;
        FakeAllocationRepository allocations = new(allocation);
        FakeAvailabilityRepository availability = new();
        FakeDecisionRepository decisions = new();
        RecordingOutbox outbox = new();
        InventoryManagementMutationCoordinator retirementMutations =
            CreateRetirementMutationCoordinator();
        BedRetirementCoordinator bedRetirements = new(
            retirementMutations,
            new FakeBedRetirementRepository(),
            availability,
            new TestBusinessDateProvider(),
            new TestClock(),
            new TestIdGenerator());
        RoomRetirementCoordinator roomRetirements = new(
            retirementMutations,
            new FakeRoomRetirementRepository(),
            availability,
            new TestBusinessDateProvider(),
            new TestClock(),
            new TestIdGenerator());
        InventoryAllocationAmendmentRequestedHandler handler = new(
            allocations,
            availability,
            new InventoryRetirementCoordinator(bedRetirements, roomRetirements),
            decisions,
            new InventoryAllocationMutationCoordinator(
                new NoopAllocationOperationLock(),
                new TestScopeContext()),
            new RecordingOutboxRegistry(outbox),
            new TestClock(),
            new TestIdGenerator());
        Guid amendmentId = Guid.NewGuid();
        InventoryAllocationAmendmentRequestedIntegrationEvent request = Request(
            amendmentId,
            new DateOnly(2026, 8, 4));

        await handler.HandleAsync(request, CancellationToken.None);
        await handler.HandleAsync(request, CancellationToken.None);
        await handler.HandleAsync(Request(amendmentId, new DateOnly(2026, 8, 5)), CancellationToken.None);

        Assert.Equal(2, allocation.Version);
        Assert.Equal(new DateOnly(2026, 8, 4), allocation.Departure);
        Assert.Equal(1, availability.TouchCount);
        InventoryAllocationAmendmentDecisionRecord decision = Assert.Single(decisions.Items.Values);
        Assert.True(decision.Confirmed);
        Assert.Equal(2, decision.AllocationVersion);
        Assert.Collection(
            outbox.Events,
            item => Assert.Equal(2, Assert.IsType<InventoryAllocationAmendmentConfirmedIntegrationEvent>(item).AllocationVersion),
            item => Assert.Equal(2, Assert.IsType<InventoryAllocationAmendmentConfirmedIntegrationEvent>(item).AllocationVersion),
            item => Assert.Equal(
                InventoryAllocationRejectionReason.RequestMismatch,
                Assert.IsType<InventoryAllocationAmendmentRejectedIntegrationEvent>(item).Reason));
    }

    [Fact]
    public async Task Existing_decision_is_read_only_after_allocation_lock_and_reload()
    {
        InventoryAllocation allocation = InventoryAllocation.CreateAccepted(
            AllocationId,
            ScopeId,
            ReservationId,
            Guid.NewGuid(),
            PropertyId,
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 3),
            [UnitId],
            Now).Value;
        FakeAvailabilityRepository availability = new();
        FakeDecisionRepository decisions = new();
        Guid amendmentId = Guid.NewGuid();
        InventoryAllocationAmendmentRequestedIntegrationEvent request =
            Request(amendmentId, new DateOnly(2026, 8, 4));
        await CreateHandler(
            new FakeAllocationRepository(allocation),
            availability,
            decisions,
            new NoopAllocationOperationLock(),
            new RecordingOutbox()).HandleAsync(
                request,
                CancellationToken.None);

        List<string> calls = [];
        decisions.Calls = calls;
        await CreateHandler(
            new FakeAllocationRepository(allocation, calls),
            availability,
            decisions,
            new RecordingAllocationOperationLock(calls),
            new RecordingOutbox()).HandleAsync(
                request,
                CancellationToken.None);

        Assert.Equal(
            ["allocation-lock", "allocation-read", "decision-read"],
            calls);
    }

    [Fact]
    public async Task Anonymised_allocation_does_not_recreate_amendment_decision()
    {
        InventoryAllocation allocation = InventoryAllocation.CreateAccepted(
            AllocationId,
            ScopeId,
            ReservationId,
            Guid.NewGuid(),
            PropertyId,
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 3),
            [UnitId],
            Now).Value;
        Assert.True(allocation.Release(
            Guid.NewGuid(),
            allocation.Version,
            Now).IsSuccess);
        Assert.True(allocation.Anonymise(
            allocation.Version,
            Guid.NewGuid(),
            Now).IsSuccess);
        FakeAvailabilityRepository availability = new();
        FakeDecisionRepository decisions = new();
        RecordingOutbox outbox = new();

        await CreateHandler(
            new FakeAllocationRepository(allocation),
            availability,
            decisions,
            new NoopAllocationOperationLock(),
            outbox).HandleAsync(
                Request(Guid.NewGuid(), new DateOnly(2026, 8, 4)),
                CancellationToken.None);

        Assert.Empty(decisions.Items);
        Assert.Empty(outbox.Events);
        Assert.Equal(0, availability.TouchCount);
    }

    private static InventoryAllocationAmendmentRequestedHandler CreateHandler(
        IInventoryAllocationRepository allocations,
        FakeAvailabilityRepository availability,
        FakeDecisionRepository decisions,
        IInventoryAllocationOperationLock operationLock,
        RecordingOutbox outbox)
    {
        InventoryManagementMutationCoordinator retirementMutations =
            CreateRetirementMutationCoordinator();
        BedRetirementCoordinator bedRetirements = new(
            retirementMutations,
            new FakeBedRetirementRepository(),
            availability,
            new TestBusinessDateProvider(),
            new TestClock(),
            new TestIdGenerator());
        RoomRetirementCoordinator roomRetirements = new(
            retirementMutations,
            new FakeRoomRetirementRepository(),
            availability,
            new TestBusinessDateProvider(),
            new TestClock(),
            new TestIdGenerator());
        return new(
            allocations,
            availability,
            new InventoryRetirementCoordinator(
                bedRetirements,
                roomRetirements),
            decisions,
            new InventoryAllocationMutationCoordinator(
                operationLock,
                new TestScopeContext()),
            new RecordingOutboxRegistry(outbox),
            new TestClock(),
            new TestIdGenerator());
    }

    private static InventoryManagementMutationCoordinator
        CreateRetirementMutationCoordinator() => new(
            new NoopInventoryManagementLock(),
            new TestScopeContext());

    private static InventoryAllocationAmendmentRequestedIntegrationEvent Request(
        Guid amendmentId,
        DateOnly departure) => new(
        Guid.NewGuid(),
        ScopeId,
        Now,
        amendmentId,
        AllocationId,
        ReservationId,
        PropertyId,
        expectedAllocationVersion: 1,
        new DateOnly(2026, 8, 1),
        departure,
        [UnitId]);

    private sealed class FakeAllocationRepository(
        InventoryAllocation allocation,
        List<string>? calls = null) : IInventoryAllocationRepository
    {
        public Task<InventoryAllocation?> GetByRequestAsync(
            Guid allocationRequestId,
            CancellationToken cancellationToken) => Task.FromResult<InventoryAllocation?>(null);

        public Task<InventoryAllocation?> GetByReservationAsync(
            Guid reservationId,
            CancellationToken cancellationToken) => Task.FromResult<InventoryAllocation?>(allocation);

        public Task<InventoryAllocation?> GetAsync(
            Guid allocationId,
            CancellationToken cancellationToken)
        {
            calls?.Add("allocation-read");
            return Task.FromResult<InventoryAllocation?>(
                allocation.Id == allocationId ? allocation : null);
        }

        public Task AddAsync(InventoryAllocation value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

    }

    private sealed class FakeAvailabilityRepository : IInventoryAvailabilityRepository
    {
        public int TouchCount { get; private set; }

        public Task<InventoryAvailabilityContextSnapshot> GetContextAsync(
            Guid propertyId,
            IReadOnlyCollection<Guid> inventoryUnitIds,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InventoryAllocationUnitSnapshot[] units = propertyId == PropertyId
                ? inventoryUnitIds.Select(id => new InventoryAllocationUnitSnapshot(id, true, true)).ToArray()
                : [];
            return Task.FromResult(new InventoryAvailabilityContextSnapshot(units, inventoryUnitIds));
        }

        public Task<InventoryAvailabilityConflictSnapshot> GetConflictsAsync(
            Guid propertyId,
            IReadOnlyCollection<Guid> conflictUnitIds,
            DateOnly arrival,
            DateOnly departure,
            Guid? excludedAllocationId,
            IReadOnlyCollection<Guid> excludedBlockIds,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool invalid =
                propertyId != PropertyId ||
                conflictUnitIds.Count == 0 ||
                arrival >= departure ||
                excludedAllocationId != AllocationId ||
                excludedBlockIds.Count != 0;
            return Task.FromResult(new InventoryAvailabilityConflictSnapshot(invalid, invalid));
        }

        public Task<RoomInventoryImpactSnapshot?> GetRoomImpactAsync(
            Guid propertyId,
            Guid roomId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<RoomInventoryImpactSnapshot?>(
                propertyId == PropertyId && roomId != Guid.Empty ? new(0, 0, 0, 0, [], false) : null);
        }

        public Task<BedRetirementImpactSnapshot?> GetBedRetirementImpactAsync(
            Guid propertyId,
            Guid roomId,
            Guid bedId,
            Guid? excludedAllocationId,
            IReadOnlyCollection<Guid> excludedBlockIds,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<BedRetirementImpactSnapshot?>(
                propertyId == PropertyId && roomId != Guid.Empty && bedId != Guid.Empty &&
                excludedBlockIds.Count == 0
                    ? new(0, 0, [], false)
                    : null);
        }

        public Task TouchUnitsAsync(
            Guid propertyId,
            IReadOnlyCollection<Guid> inventoryUnitIds,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(PropertyId, propertyId);
            Assert.NotEmpty(inventoryUnitIds);
            this.TouchCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeBedRetirementRepository : IBedRetirementRepository
    {
        public Task<BedRetirementProcess?> GetAsync(
            Guid propertyId,
            Guid topologyChangeId,
            CancellationToken cancellationToken) => Task.FromResult<BedRetirementProcess?>(null);

        public Task<Guid?> GetTopologyChangeIdByBedAsync(
            Guid propertyId,
            Guid bedId,
            CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);

        public Task<BedRetirementProcess?> GetByBedAsync(
            Guid propertyId,
            Guid bedId,
            CancellationToken cancellationToken) => Task.FromResult<BedRetirementProcess?>(null);

        public Task<IReadOnlyCollection<BedRetirementProcess>> ListActiveForUnitsAsync(
            Guid propertyId,
            IReadOnlyCollection<Guid> inventoryUnitIds,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyCollection<BedRetirementProcess>>([]);

        public Task AddAsync(BedRetirementProcess process, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReloadAsync(
            BedRetirementProcess process,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeRoomRetirementRepository : IRoomRetirementRepository
    {
        public Task<RoomRetirementProcess?> GetAsync(
            Guid propertyId,
            Guid topologyChangeId,
            CancellationToken cancellationToken) => Task.FromResult<RoomRetirementProcess?>(null);

        public Task<Guid?> GetTopologyChangeIdByRoomAsync(
            Guid propertyId,
            Guid roomId,
            CancellationToken cancellationToken) => Task.FromResult<Guid?>(null);

        public Task<RoomRetirementProcess?> GetByRoomAsync(
            Guid propertyId,
            Guid roomId,
            CancellationToken cancellationToken) => Task.FromResult<RoomRetirementProcess?>(null);

        public Task<IReadOnlyCollection<RoomRetirementProcess>> ListActiveForUnitsAsync(
            Guid propertyId,
            IReadOnlyCollection<Guid> inventoryUnitIds,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyCollection<RoomRetirementProcess>>([]);

        public Task AddAsync(RoomRetirementProcess process, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task ReloadAsync(
            RoomRetirementProcess process,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeDecisionRepository : IInventoryAllocationAmendmentDecisionRepository
    {
        public Dictionary<Guid, InventoryAllocationAmendmentDecisionRecord> Items { get; } = [];
        public List<string>? Calls { get; set; }

        public Task<InventoryAllocationAmendmentDecisionRecord?> GetAsync(
            Guid amendmentRequestId,
            CancellationToken cancellationToken)
        {
            this.Calls?.Add("decision-read");
            return Task.FromResult(
                this.Items.GetValueOrDefault(amendmentRequestId));
        }

        public Task AddAsync(
            InventoryAllocationAmendmentDecisionRecord decision,
            CancellationToken cancellationToken)
        {
            this.Items.Add(decision.AmendmentRequestId, decision);
            return Task.CompletedTask;
        }
    }

    private sealed class NoopAllocationOperationLock
        : IInventoryAllocationOperationLock
    {
        public Task<bool> TryAcquireExistingAsync(
            string tenantId,
            Guid allocationId,
            CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task AcquireCoordinateAsync(
            string tenantId,
            Guid allocationId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingAllocationOperationLock(
        List<string> calls) : IInventoryAllocationOperationLock
    {
        public Task<bool> TryAcquireExistingAsync(
            string tenantId,
            Guid allocationId,
            CancellationToken cancellationToken)
        {
            calls.Add("allocation-lock");
            return Task.FromResult(true);
        }

        public Task AcquireCoordinateAsync(
            string tenantId,
            Guid allocationId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => InventoryAllocationAmendmentRequestedHandlerTests.ScopeId;
    }

    private sealed class NoopInventoryManagementLock
        : IInventoryManagementLock
    {
        public Task AcquireResourceAsync(
            string tenantId,
            InventoryManagementResourceKind resourceKind,
            Guid resourceId,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AcquireOperationAsync(
            string tenantId,
            InventoryManagementResourceKind resourceKind,
            Guid resourceId,
            Guid operationId,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingOutbox : IOutboxWriter
    {
        public string ModuleName => InventoryModuleMetadata.Name;
        public List<IIntegrationEvent> Events { get; } = [];

        public Task EnqueueAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken)
            where TEvent : IIntegrationEvent
        {
            this.Events.Add(integrationEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingOutboxRegistry(RecordingOutbox outbox) : IOutboxWriterRegistry
    {
        public IOutboxWriter GetRequired(string moduleName) => outbox;
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
        public Guid NewId() => Guid.NewGuid();
    }
}
