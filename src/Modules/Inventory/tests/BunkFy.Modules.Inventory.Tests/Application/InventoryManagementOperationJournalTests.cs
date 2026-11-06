namespace BunkFy.Modules.Inventory.Tests.Application;

using BunkFy.Modules.Inventory.Application;
using BunkFy.Modules.Inventory.Application.Handlers;
using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using Xunit;

[Trait("Category", "Unit")]
public sealed class InventoryManagementOperationJournalTests
{
    private static readonly Guid PropertyId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(
        2026,
        8,
        9,
        1,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public async Task Block_creation_replays_the_typed_immutable_receipt()
    {
        RecordingRepository repository = new();
        InventoryManagementOperationJournal journal = new(repository);
        Guid operationId = Guid.NewGuid();
        ManualInventoryBlock block = CreateBlock();
        ManualInventoryBlockGroup group = ManualInventoryBlockGroup.Create(
            block.BlockGroupId,
            block.ScopeId,
            PropertyId,
            ManualInventoryBlockGroupTargetKind.Unit,
            null,
            null,
            null,
            block.InventoryUnitId,
            block.Arrival,
            block.Departure,
            block.Reason,
            new string('a', 64),
            new string('b', 64),
            ManualInventoryBlockGroup.CurrentMembershipDigestVersion,
            1,
            null,
            Now,
            "user:operator").Value;
        ManualInventoryBlockCreationResult creation = new(
            group,
            [block]);
        string fingerprint = new('a', 64);

        ManualInventoryBlockMutationReceiptDto recorded = await journal
            .RecordBlockCreateAsync(
                creation,
                operationId,
                fingerprint,
                Now,
                CancellationToken.None);
        InventoryManagementReplayDecision<
            ManualInventoryBlockMutationReceiptDto> exact = await journal
                .InspectBlockCreateAsync(
                    PropertyId,
                    operationId,
                    fingerprint,
                    CancellationToken.None);
        InventoryManagementReplayDecision<
            ManualInventoryBlockMutationReceiptDto> conflict = await journal
                .InspectBlockCreateAsync(
                    PropertyId,
                    operationId,
                    new string('b', 64),
                    CancellationToken.None);

        Assert.True(exact.ToResult().IsSuccess);
        Assert.Equal(recorded, exact.ToResult().Value);
        Assert.Equal(
            InventoryApplicationErrors.ManagementOperationConflict,
            conflict.ToResult().Error);
        InventoryManagementOperationRecord stored =
            Assert.Single(repository.Added);
        Assert.Equal(
            InventoryManagementResourceKind.Property,
            stored.ResourceKind);
        Assert.Equal(
            InventoryManagementMutationKind.ManualBlockCreate,
            stored.Kind);
        Assert.Equal(fingerprint, stored.RequestFingerprint);
        Assert.Equal(ManualInventoryBlockStatus.Active, stored.ResultBlockStatus);
    }

    [Fact]
    public async Task Block_release_and_group_release_keep_distinct_receipts()
    {
        RecordingRepository repository = new();
        InventoryManagementOperationJournal journal = new(repository);
        ManualInventoryBlock block = CreateBlock();
        Assert.True(block.Release(1, Guid.NewGuid(), Now, "user:operator").IsSuccess);
        Guid blockOperationId = Guid.NewGuid();
        Guid groupOperationId = Guid.NewGuid();

        ManualInventoryBlockMutationReceiptDto released = await journal
            .RecordBlockReleaseAsync(
                block,
                blockOperationId,
                1,
                new string('c', 64),
                Now,
                CancellationToken.None);
        ManualInventoryBlockGroupMutationReceiptDto releasedGroup =
            await journal.RecordBlockGroupReleaseAsync(
                block.ScopeId,
                new(block.BlockGroupId, PropertyId, AffectedBlockCount: 7),
                groupOperationId,
                new string('d', 64),
                Now,
                CancellationToken.None);

        Assert.Equal(
            released,
            (await journal.InspectBlockReleaseAsync(
                PropertyId,
                block.Id,
                blockOperationId,
                1,
                new string('c', 64),
                CancellationToken.None)).ToResult().Value);
        Assert.Equal(
            releasedGroup,
            (await journal.InspectBlockGroupReleaseAsync(
                PropertyId,
                block.BlockGroupId,
                groupOperationId,
                new string('d', 64),
                CancellationToken.None)).ToResult().Value);
        Assert.Equal(2, repository.Added.Count);
    }

    [Fact]
    public void Hierarchical_target_normalization_discards_irrelevant_values()
    {
        Assert.True(InventoryBlockTargetNormalizer.TryNormalize(
            new(
                InventoryBlockTargetKind.Floor,
                BuildingLabel: "  Main  ",
                FloorLabel: "  2  ",
                RoomId: Guid.NewGuid(),
                InventoryUnitId: Guid.NewGuid()),
            out InventoryBlockTarget floor));
        Assert.Equal("Main", floor.BuildingLabel);
        Assert.Equal("2", floor.FloorLabel);
        Assert.Null(floor.RoomId);
        Assert.Null(floor.InventoryUnitId);
    }

    private static ManualInventoryBlock CreateBlock() =>
        ManualInventoryBlock.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "tenant-a",
            PropertyId,
            Guid.NewGuid(),
            new DateOnly(2026, 9, 1),
            new DateOnly(2026, 9, 3),
            "Maintenance",
            Guid.NewGuid(),
            Now,
            "user:operator").Value;

    private sealed class RecordingRepository
        : IInventoryManagementOperationRepository
    {
        private readonly Dictionary<
            (InventoryManagementResourceKind, Guid, Guid),
            InventoryManagementOperationRecord> operations = [];

        public List<InventoryManagementOperationRecord> Added { get; } = [];

        public Task<InventoryManagementOperationRecord?> GetAsync(
            InventoryManagementResourceKind resourceKind,
            Guid resourceId,
            Guid operationId,
            CancellationToken cancellationToken)
        {
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
}
