namespace BunkFy.Modules.Inventory.Tests;

using BunkFy.Modules.Inventory.Application;
using BunkFy.Modules.Inventory.Application.Commands;
using BunkFy.Modules.Inventory.Application.Handlers;
using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Contracts;
using Gma.Framework.Results;
using Gma.Framework.Scoping;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ManualInventoryBlockCommandHandlerTests
{
    private const string TenantId = "tenant-a";
    private static readonly Guid PropertyId = Guid.NewGuid();
    private static readonly Guid UnitId = Guid.NewGuid();
    private static readonly Guid BlockId = Guid.NewGuid();
    private static readonly Guid BlockGroupId = Guid.NewGuid();
    private static readonly DateOnly Arrival = new(2026, 9, 1);
    private static readonly DateOnly Departure = new(2026, 9, 3);
    private static readonly DateTimeOffset CompletedAtUtc = new(
        2026,
        8,
        9,
        2,
        0,
        0,
        TimeSpan.Zero);
    private const string SelectionDigest =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task Exact_replays_lock_then_return_without_domain_reads()
    {
        RecordingRepository operations = new();
        RecordingLock operationLock = new();
        InventoryManagementMutationCoordinator mutations = new(
            operationLock,
            new TestScopeContext());
        InventoryManagementOperationJournal journal = new(operations);

        Guid createOperationId = Guid.NewGuid();
        InventoryBlockTarget unitTarget = new(
            InventoryBlockTargetKind.Unit,
            InventoryUnitId: UnitId);
        string createFingerprint = InventoryManagementMutationFingerprint
            .ComputeManualBlockCreate(
                PropertyId,
                unitTarget,
                Arrival,
                Departure,
                "Maintenance",
                group: false);
        ManualInventoryBlockMutationReceiptDto createReceipt = new(
            BlockId,
            BlockGroupId,
            PropertyId,
            ManualInventoryBlockStatus.Active,
            1);
        operations.Seed(InventoryManagementOperationRecord.ForBlock(
            createOperationId,
            TenantId,
            InventoryManagementResourceKind.Property,
            PropertyId,
            InventoryManagementMutationKind.ManualBlockCreate,
            0,
            createFingerprint,
            createReceipt,
            CompletedAtUtc));

        Guid groupCreateOperationId = Guid.NewGuid();
        InventoryBlockTarget groupTarget = new(
            InventoryBlockTargetKind.Floor,
            BuildingLabel: "Main",
            FloorLabel: "2");
        string groupCreateFingerprint =
            InventoryManagementMutationFingerprint.ComputeManualBlockGroupCreateV2(
                PropertyId,
                groupTarget,
                Arrival,
                Departure,
                "Deep clean",
                SelectionDigest,
                3);
        ManualInventoryBlockGroupMutationReceiptDto groupCreateReceipt = new(
            BlockGroupId,
            PropertyId,
            3,
            ManualInventoryBlockGroupStatus.Active,
            1,
            ReleasedBlockCount: 0,
            CreatedBlockCount: 3,
            TotalBlockCount: 3,
            ActiveBlockCount: 3,
            AlreadyReleasedBlockCount: 0,
            MembershipDigest: SelectionDigest);
        operations.Seed(InventoryManagementOperationRecord.ForBlockGroupV2(
            groupCreateOperationId,
            TenantId,
            InventoryManagementResourceKind.Property,
            PropertyId,
            InventoryManagementMutationKind.ManualBlockGroupCreateV2,
            0,
            groupCreateFingerprint,
            groupCreateReceipt,
            CompletedAtUtc));

        Guid releaseOperationId = Guid.NewGuid();
        string releaseFingerprint = InventoryManagementMutationFingerprint
            .ComputeManualBlockRelease(PropertyId, BlockId, 1);
        ManualInventoryBlockMutationReceiptDto releaseReceipt = createReceipt with
        {
            Status = ManualInventoryBlockStatus.Released,
            Version = 2
        };
        operations.Seed(InventoryManagementOperationRecord.ForBlock(
            releaseOperationId,
            TenantId,
            InventoryManagementResourceKind.Block,
            BlockId,
            InventoryManagementMutationKind.ManualBlockRelease,
            1,
            releaseFingerprint,
            releaseReceipt,
            CompletedAtUtc));

        Guid groupReleaseOperationId = Guid.NewGuid();
        string groupReleaseFingerprint =
            InventoryManagementMutationFingerprint
                .ComputeManualBlockGroupReleaseV2(PropertyId, BlockGroupId, 1);
        ManualInventoryBlockGroupMutationReceiptDto groupReleaseReceipt = new(
            BlockGroupId,
            PropertyId,
            3,
            ManualInventoryBlockGroupStatus.Released,
            2,
            ReleasedBlockCount: 3,
            CreatedBlockCount: 0,
            TotalBlockCount: 3,
            ActiveBlockCount: 0,
            AlreadyReleasedBlockCount: 0,
            MembershipDigest: SelectionDigest);
        operations.Seed(InventoryManagementOperationRecord.ForBlockGroupV2(
            groupReleaseOperationId,
            TenantId,
            InventoryManagementResourceKind.BlockGroup,
            BlockGroupId,
            InventoryManagementMutationKind.ManualBlockGroupReleaseV2,
            1,
            groupReleaseFingerprint,
            groupReleaseReceipt,
            CompletedAtUtc));

        CreateManualInventoryBlockCommandHandler createHandler = new(
            mutations,
            journal,
            null!,
            null!);
        CreateManualInventoryBlockGroupCommandHandler groupCreateHandler = new(
            mutations,
            journal,
            null!,
            null!);
        ReleaseManualInventoryBlockCommandHandler releaseHandler = new(
            mutations,
            journal,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!);
        ReleaseManualInventoryBlockGroupCommandHandler groupReleaseHandler =
            new(
                mutations,
                journal,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!);

        Result<ManualInventoryBlockMutationReceiptDto> created =
            await createHandler.HandleAsync(
                new(
                    createOperationId,
                    PropertyId,
                    UnitId,
                    Arrival,
                    Departure,
                    "  Maintenance  "),
                CancellationToken.None);
        Result<ManualInventoryBlockGroupMutationReceiptDto> groupCreated =
            await groupCreateHandler.HandleAsync(
                new(
                    groupCreateOperationId,
                    PropertyId,
                    new(
                        InventoryBlockTargetKind.Floor,
                        BuildingLabel: "  Main  ",
                        FloorLabel: "  2  ",
                        RoomId: Guid.NewGuid()),
                    Arrival,
                    Departure,
                    " Deep clean ",
                    SelectionDigest,
                    3,
                    true),
                CancellationToken.None);
        Result<ManualInventoryBlockMutationReceiptDto> released =
            await releaseHandler.HandleAsync(
                new(
                    releaseOperationId,
                    PropertyId,
                    BlockId,
                    1),
                CancellationToken.None);
        Result<ManualInventoryBlockGroupMutationReceiptDto> groupReleased =
            await groupReleaseHandler.HandleAsync(
                new(
                    groupReleaseOperationId,
                    PropertyId,
                    BlockGroupId,
                    1,
                    true),
                CancellationToken.None);

        Assert.Equal(createReceipt, created.Value);
        Assert.Equal(groupCreateReceipt, groupCreated.Value);
        Assert.Equal(releaseReceipt, released.Value);
        Assert.Equal(groupReleaseReceipt, groupReleased.Value);
        Assert.Equal(
            [
                $"operation:Property:{PropertyId:N}:{createOperationId:N}",
                $"operation:Property:{PropertyId:N}:{groupCreateOperationId:N}",
                $"resource:Block:{BlockId:N}",
                $"resource:BlockGroup:{BlockGroupId:N}"
            ],
            operationLock.Calls);
        Assert.Equal(4, operations.ReadCount);
    }

    [Fact]
    public async Task Changed_create_reuse_conflicts_before_domain_reads()
    {
        RecordingRepository operations = new();
        RecordingLock operationLock = new();
        Guid operationId = Guid.NewGuid();
        InventoryBlockTarget target = new(
            InventoryBlockTargetKind.Unit,
            InventoryUnitId: UnitId);
        operations.Seed(InventoryManagementOperationRecord.ForBlock(
            operationId,
            TenantId,
            InventoryManagementResourceKind.Property,
            PropertyId,
            InventoryManagementMutationKind.ManualBlockCreate,
            0,
            InventoryManagementMutationFingerprint.ComputeManualBlockCreate(
                PropertyId,
                target,
                Arrival,
                Departure,
                "Maintenance",
                group: false),
            new(
                BlockId,
                BlockGroupId,
                PropertyId,
                ManualInventoryBlockStatus.Active,
                1),
            CompletedAtUtc));
        CreateManualInventoryBlockCommandHandler handler = new(
            new InventoryManagementMutationCoordinator(
                operationLock,
                new TestScopeContext()),
            new InventoryManagementOperationJournal(operations),
            null!,
            null!);

        Result<ManualInventoryBlockMutationReceiptDto> result =
            await handler.HandleAsync(
                new(
                    operationId,
                    PropertyId,
                    UnitId,
                    Arrival,
                    Departure,
                    "Different reason"),
                CancellationToken.None);

        Assert.Equal(
            InventoryApplicationErrors.ManagementOperationConflict,
            result.Error);
        Assert.Single(operationLock.Calls);
        Assert.Equal(1, operations.ReadCount);
    }

    private sealed class RecordingRepository
        : IInventoryManagementOperationRepository
    {
        private readonly Dictionary<
            (InventoryManagementResourceKind, Guid, Guid),
            InventoryManagementOperationRecord> operations = [];

        public int ReadCount { get; private set; }

        public void Seed(InventoryManagementOperationRecord operation) =>
            this.operations.Add(
                (operation.ResourceKind,
                 operation.ResourceId,
                 operation.OperationId),
                operation);

        public Task<InventoryManagementOperationRecord?> GetAsync(
            InventoryManagementResourceKind resourceKind,
            Guid resourceId,
            Guid operationId,
            CancellationToken cancellationToken)
        {
            this.ReadCount++;
            this.operations.TryGetValue(
                (resourceKind, resourceId, operationId),
                out InventoryManagementOperationRecord? operation);
            return Task.FromResult(operation);
        }

        public Task AddAsync(
            InventoryManagementOperationRecord operation,
            CancellationToken cancellationToken) => throw new InvalidOperationException(
                "An exact replay must not append another receipt.");
    }

    private sealed class RecordingLock : IInventoryManagementLock
    {
        public List<string> Calls { get; } = [];

        public Task AcquireResourceAsync(
            string tenantId,
            InventoryManagementResourceKind resourceKind,
            Guid resourceId,
            CancellationToken cancellationToken)
        {
            Assert.Equal(TenantId, tenantId);
            this.Calls.Add($"resource:{resourceKind}:{resourceId:N}");
            return Task.CompletedTask;
        }

        public Task AcquireOperationAsync(
            string tenantId,
            InventoryManagementResourceKind resourceKind,
            Guid resourceId,
            Guid operationId,
            CancellationToken cancellationToken)
        {
            Assert.Equal(TenantId, tenantId);
            this.Calls.Add(
                $"operation:{resourceKind}:{resourceId:N}:{operationId:N}");
            return Task.CompletedTask;
        }
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => TenantId;
    }
}
