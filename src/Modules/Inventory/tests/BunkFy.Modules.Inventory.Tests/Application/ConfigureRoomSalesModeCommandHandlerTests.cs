namespace BunkFy.Modules.Inventory.Tests;

using BunkFy.Modules.Inventory.Application;
using BunkFy.Modules.Inventory.Application.Commands;
using BunkFy.Modules.Inventory.Application.Handlers;
using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using BunkFy.Modules.Inventory.Domain.Errors;
using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ConfigureRoomSalesModeCommandHandlerTests
{
    private static readonly Guid PropertyId =
        Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid RoomId =
        Guid.Parse("20000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task Exact_replay_returns_the_immutable_receipt_after_later_changes()
    {
        Harness harness = CreateHarness();
        Guid firstOperationId = Guid.NewGuid();

        Result<RoomInventoryMutationReceiptDto> first = await harness.HandleAsync(
            new(
                firstOperationId,
                PropertyId,
                RoomId,
                InventorySalesMode.RoomLevel,
                1));
        Result<RoomInventoryMutationReceiptDto> later = await harness.HandleAsync(
            new(
                Guid.NewGuid(),
                PropertyId,
                RoomId,
                InventorySalesMode.BedLevel,
                2));
        int generatedBeforeReplay = harness.Ids.CallCount;
        int selectionLocksBeforeReplay = harness.SelectionFence.AcquireCount;
        int selectionVersionAdvancesBeforeReplay = harness.SelectionFence.AdvanceCount;

        Result<RoomInventoryMutationReceiptDto> replay = await harness.HandleAsync(
            new(
                firstOperationId,
                PropertyId,
                RoomId,
                InventorySalesMode.RoomLevel,
                1));

        Assert.True(first.IsSuccess);
        Assert.True(later.IsSuccess);
        Assert.True(replay.IsSuccess);
        Assert.Equal(first.Value, replay.Value);
        Assert.Equal(InventorySalesMode.RoomLevel, replay.Value.SalesMode);
        Assert.Equal(2, replay.Value.Version);
        Assert.Equal(RoomSalesMode.BedLevel, harness.Configuration.SalesMode);
        Assert.Equal(3, harness.Configuration.Version);
        Assert.Equal(generatedBeforeReplay, harness.Ids.CallCount);
        Assert.Equal(selectionLocksBeforeReplay + 1, harness.SelectionFence.AcquireCount);
        Assert.Equal(selectionVersionAdvancesBeforeReplay, harness.SelectionFence.AdvanceCount);
        Assert.Equal(2, harness.Configuration.DomainEvents.Count);
    }

    [Fact]
    public async Task Changed_reuse_of_an_operation_id_fails_closed()
    {
        Harness harness = CreateHarness();
        Guid operationId = Guid.NewGuid();
        Result<RoomInventoryMutationReceiptDto> first = await harness.HandleAsync(
            new(
                operationId,
                PropertyId,
                RoomId,
                InventorySalesMode.RoomLevel,
                1));

        Result<RoomInventoryMutationReceiptDto> changed = await harness.HandleAsync(
            new(
                operationId,
                PropertyId,
                RoomId,
                InventorySalesMode.BedLevel,
                1));

        Assert.True(first.IsSuccess);
        Assert.Equal(
            InventoryApplicationErrors.ManagementOperationConflict,
            changed.Error);
        Assert.Equal(RoomSalesMode.RoomLevel, harness.Configuration.SalesMode);
        Assert.Equal(2, harness.Configuration.Version);
        Assert.Single(harness.Operations.Added);
        Assert.Equal(1, harness.Ids.CallCount);
    }

    [Fact]
    public async Task No_op_records_a_receipt_without_event_or_version_advance()
    {
        Harness harness = CreateHarness();
        Result<RoomInventoryMutationReceiptDto> initial = await harness.HandleAsync(
            new(
                Guid.NewGuid(),
                PropertyId,
                RoomId,
                InventorySalesMode.RoomLevel,
                1));
        int eventsBefore = harness.Configuration.DomainEvents.Count;
        int idsBefore = harness.Ids.CallCount;

        Result<RoomInventoryMutationReceiptDto> noOp = await harness.HandleAsync(
            new(
                Guid.NewGuid(),
                PropertyId,
                RoomId,
                InventorySalesMode.RoomLevel,
                2));

        Assert.True(initial.IsSuccess);
        Assert.True(noOp.IsSuccess);
        Assert.Equal(2, noOp.Value.Version);
        Assert.Equal(2, harness.Configuration.Version);
        Assert.Equal(eventsBefore, harness.Configuration.DomainEvents.Count);
        Assert.Equal(idsBefore, harness.Ids.CallCount);
        Assert.Equal(2, harness.Operations.Added.Count);
        Assert.Equal(1, harness.SelectionFence.AdvanceCount);
    }

    [Fact]
    public async Task Failed_claim_check_does_not_bind_the_operation_id()
    {
        Harness harness = CreateHarness(activeAllocationCount: 1);
        Guid operationId = Guid.NewGuid();
        ConfigureRoomSalesModeCommand command = new(
            operationId,
            PropertyId,
            RoomId,
            InventorySalesMode.BedLevel,
            1);

        Result<RoomInventoryMutationReceiptDto> blocked =
            await harness.HandleAsync(command);
        harness.Availability.ActiveAllocationCount = 0;
        Result<RoomInventoryMutationReceiptDto> retry =
            await harness.HandleAsync(command);

        Assert.Equal(InventoryApplicationErrors.RoomHasActiveClaims, blocked.Error);
        Assert.True(retry.IsSuccess);
        Assert.Single(harness.Operations.Added);
        Assert.Equal(1, harness.Ids.CallCount);
        Assert.Equal(1, harness.SelectionFence.AdvanceCount);
    }

    [Fact]
    public async Task Stale_attempt_does_not_bind_or_consume_an_event_id()
    {
        Harness harness = CreateHarness();

        Result<RoomInventoryMutationReceiptDto> result = await harness.HandleAsync(
            new(
                Guid.NewGuid(),
                PropertyId,
                RoomId,
                InventorySalesMode.RoomLevel,
                2));

        Assert.Equal(InventoryDomainErrors.VersionConflict, result.Error);
        Assert.Empty(harness.Operations.Added);
        Assert.Equal(0, harness.Ids.CallCount);
        Assert.Empty(harness.Configuration.DomainEvents);
    }

    [Fact]
    public async Task Admission_and_room_lock_happen_before_replay_lookup()
    {
        List<string> trace = [];
        Harness harness = CreateHarness(trace: trace);
        Guid operationId = Guid.NewGuid();
        ConfigureRoomSalesModeCommand command = new(
            operationId,
            PropertyId,
            RoomId,
            InventorySalesMode.RoomLevel,
            1);
        Assert.True((await harness.HandleAsync(command)).IsSuccess);
        trace.Clear();

        Assert.True((await harness.HandleAsync(command)).IsSuccess);

        Assert.Equal(["selection-lock", "room-lock", "journal-read"], trace);
    }

    [Fact]
    public async Task Invalid_operation_id_is_rejected_before_locking()
    {
        Harness harness = CreateHarness();

        Result<RoomInventoryMutationReceiptDto> result = await harness.HandleAsync(
            new(
                Guid.Empty,
                PropertyId,
                RoomId,
                InventorySalesMode.RoomLevel,
                1));

        Assert.Equal(
            InventoryApplicationErrors.ManagementOperationInvalid,
            result.Error);
        Assert.Equal(0, harness.Lock.CallCount);
        Assert.Equal(0, harness.SelectionFence.AcquireCount);
        Assert.Empty(harness.Operations.Added);
    }

    [Fact]
    public async Task Bed_level_requires_an_active_bed()
    {
        Harness harness = CreateHarness(activeBedCount: 0);

        Result<RoomInventoryMutationReceiptDto> result = await harness.HandleAsync(
            new(
                Guid.NewGuid(),
                PropertyId,
                RoomId,
                InventorySalesMode.BedLevel,
                1));

        Assert.Equal(InventoryDomainErrors.BedLevelRequiresBeds, result.Error);
        Assert.Equal(RoomSalesMode.Unconfigured, harness.Configuration.SalesMode);
        Assert.Empty(harness.Operations.Added);
    }

    [Fact]
    public async Task Retired_room_cannot_be_configured()
    {
        Harness harness = CreateHarness(status: RoomStatus.Retired);

        Result<RoomInventoryMutationReceiptDto> result = await harness.HandleAsync(
            new(
                Guid.NewGuid(),
                PropertyId,
                RoomId,
                InventorySalesMode.RoomLevel,
                1));

        Assert.Equal(InventoryDomainErrors.RoomRetired, result.Error);
        Assert.Equal(RoomSalesMode.Unconfigured, harness.Configuration.SalesMode);
        Assert.Empty(harness.Operations.Added);
    }

    private static Harness CreateHarness(
        RoomStatus status = RoomStatus.Active,
        int activeBedCount = 2,
        int activeAllocationCount = 0,
        List<string>? trace = null)
    {
        RoomInventoryConfiguration configuration =
            RoomInventoryConfiguration.Create(
                RoomId,
                "tenant-a",
                PropertyId,
                TestClock.Now).Value;
        RecordingManagementOperationRepository operations = new(trace);
        RecordingRoomLock operationLock = new(trace);
        MutableAvailabilityRepository availability = new(
            activeAllocationCount);
        RecordingSelectionFence selectionFence = new(trace);
        TestIdGenerator ids = new();
        ConfigureRoomSalesModeCommandHandler handler = new(
            new InventoryManagementMutationCoordinator(
                operationLock,
                new TestScopeContext()),
            new InventoryManagementOperationJournal(operations),
            new FakeTopologyRepository(status, activeBedCount),
            new FakeConfigurationRepository(configuration),
            availability,
            selectionFence,
            new TestBusinessDateProvider(),
            new TestClock(),
            ids);
        return new(
            handler,
            configuration,
            operations,
            operationLock,
            availability,
            selectionFence,
            ids);
    }

    private sealed record Harness(
        ConfigureRoomSalesModeCommandHandler Handler,
        RoomInventoryConfiguration Configuration,
        RecordingManagementOperationRepository Operations,
        RecordingRoomLock Lock,
        MutableAvailabilityRepository Availability,
        RecordingSelectionFence SelectionFence,
        TestIdGenerator Ids)
    {
        public Task<Result<RoomInventoryMutationReceiptDto>> HandleAsync(
            ConfigureRoomSalesModeCommand command) => this.Handler.HandleAsync(
                command,
                CancellationToken.None);
    }

    private sealed class RecordingManagementOperationRepository(
        List<string>? trace)
        : IInventoryManagementOperationRepository
    {
        private readonly Dictionary<
            (InventoryManagementResourceKind Kind, Guid ResourceId, Guid OperationId),
            InventoryManagementOperationRecord> operations = [];

        public List<InventoryManagementOperationRecord> Added { get; } = [];

        public Task<InventoryManagementOperationRecord?> GetAsync(
            InventoryManagementResourceKind resourceKind,
            Guid resourceId,
            Guid operationId,
            CancellationToken cancellationToken)
        {
            trace?.Add("journal-read");
            this.operations.TryGetValue(
                (resourceKind, resourceId, operationId),
                out InventoryManagementOperationRecord? operation);
            return Task.FromResult(operation);
        }

        public Task AddAsync(
            InventoryManagementOperationRecord operation,
            CancellationToken cancellationToken)
        {
            this.operations.Add(
                (operation.ResourceKind,
                 operation.ResourceId,
                 operation.OperationId),
                operation);
            this.Added.Add(operation);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRoomLock(List<string>? trace)
        : IInventoryManagementLock
    {
        public int CallCount { get; private set; }

        public Task AcquireResourceAsync(
            string tenantId,
            InventoryManagementResourceKind resourceKind,
            Guid resourceId,
            CancellationToken cancellationToken)
        {
            Assert.Equal("tenant-a", tenantId);
            Assert.Equal(InventoryManagementResourceKind.Room, resourceKind);
            Assert.Equal(RoomId, resourceId);
            this.CallCount++;
            trace?.Add("room-lock");
            return Task.CompletedTask;
        }

        public Task AcquireOperationAsync(
            string tenantId,
            InventoryManagementResourceKind resourceKind,
            Guid resourceId,
            Guid operationId,
            CancellationToken cancellationToken) => throw new InvalidOperationException(
                "Room sales mode does not acquire an operation-only lock.");
    }

    private sealed class RecordingSelectionFence(List<string>? trace)
        : IInventoryAvailabilitySelectionFence
    {
        public int AcquireCount { get; private set; }
        public int AdvanceCount { get; private set; }

        public Task AcquireAsync(Guid propertyId, CancellationToken cancellationToken)
        {
            Assert.Equal(PropertyId, propertyId);
            this.AcquireCount++;
            trace?.Add("selection-lock");
            return Task.CompletedTask;
        }

        public Task AdvanceAsync(Guid propertyId, CancellationToken cancellationToken)
        {
            Assert.Equal(PropertyId, propertyId);
            this.AdvanceCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class MutableAvailabilityRepository(int activeAllocationCount)
        : IInventoryAvailabilityRepository
    {
        public int ActiveAllocationCount { get; set; } = activeAllocationCount;

        public Task<RoomInventoryImpactSnapshot?> GetRoomImpactAsync(
            Guid propertyId,
            Guid roomId,
            CancellationToken cancellationToken) =>
            Task.FromResult<RoomInventoryImpactSnapshot?>(
                propertyId == PropertyId && roomId == RoomId
                    ? new(
                        this.ActiveAllocationCount,
                        0,
                        0,
                        0,
                        this.ActiveAllocationCount == 0
                            ? []
                            : [Guid.NewGuid()],
                        false)
                    : null);

        public Task<InventoryAvailabilityContextSnapshot> GetContextAsync(
            Guid propertyId,
            IReadOnlyCollection<Guid> inventoryUnitIds,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<InventoryAvailabilityConflictSnapshot> GetConflictsAsync(
            Guid propertyId,
            IReadOnlyCollection<Guid> conflictUnitIds,
            DateOnly arrival,
            DateOnly departure,
            Guid? excludedAllocationId,
            IReadOnlyCollection<Guid> excludedBlockIds,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<BedRetirementImpactSnapshot?> GetBedRetirementImpactAsync(
            Guid propertyId,
            Guid roomId,
            Guid bedId,
            Guid? excludedAllocationId,
            IReadOnlyCollection<Guid> excludedBlockIds,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task TouchUnitsAsync(
            Guid propertyId,
            IReadOnlyCollection<Guid> inventoryUnitIds,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeTopologyRepository(
        RoomStatus status,
        int activeBedCount)
        : IInventoryTopologyRepository
    {
        public Task<bool> ApplyPropertyAsync(
            InventoryPropertyTopologyWriteModel property,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> ApplyRoomAsync(
            InventoryRoomTopologyWriteModel room,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> ApplyBedAsync(
            InventoryBedTopologyWriteModel bed,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<InventoryRoomTopologySnapshot?> GetRoomAsync(
            Guid propertyId,
            Guid roomId,
            CancellationToken cancellationToken) =>
            Task.FromResult<InventoryRoomTopologySnapshot?>(
                propertyId == PropertyId && roomId == RoomId
                    ? new(
                        PropertyId,
                        RoomId,
                        status,
                        activeBedCount)
                    : null);

        public Task<IReadOnlyCollection<InventoryUnitDefinitionSnapshot>>
            GetUnitDefinitionsAsync(
            Guid propertyId,
            Guid? roomId,
            Guid? inventoryUnitId,
            bool touchVersions,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyCollection<
                InventoryUnitDefinitionSnapshot>>([]);
    }

    private sealed class FakeConfigurationRepository(
        RoomInventoryConfiguration configuration)
        : IRoomInventoryConfigurationRepository
    {
        public Task<bool> EnsureAsync(
            string scopeId,
            Guid propertyId,
            Guid roomId,
            DateTimeOffset createdAtUtc,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<RoomInventoryConfiguration?> GetAsync(
            Guid propertyId,
            Guid roomId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                propertyId == PropertyId && roomId == RoomId
                    ? configuration
                    : null);
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }

    private sealed class TestClock : ISystemClock
    {
        public static DateTimeOffset Now { get; } =
            new(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);

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
            return Guid.CreateVersion7();
        }
    }
}
