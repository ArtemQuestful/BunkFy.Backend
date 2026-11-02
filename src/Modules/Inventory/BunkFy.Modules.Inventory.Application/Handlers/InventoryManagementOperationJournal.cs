namespace BunkFy.Modules.Inventory.Application.Handlers;

using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using Gma.Framework.Results;

internal sealed class InventoryManagementOperationJournal(
    IInventoryManagementOperationRepository operations)
{
    public Task<InventoryManagementReplayDecision<
        RoomInventoryMutationReceiptDto>> InspectRoomAsync(
        Guid propertyId,
        Guid roomId,
        Guid operationId,
        long expectedVersion,
        string fingerprint,
        CancellationToken cancellationToken) => this.InspectAsync(
            propertyId,
            InventoryManagementResourceKind.Room,
            roomId,
            operationId,
            InventoryManagementMutationKind.RoomSalesModeConfiguration,
            expectedVersion,
            fingerprint,
            static operation => operation.ToRoomReceipt(),
            cancellationToken);

    public Task<InventoryManagementReplayDecision<
        ManualInventoryBlockMutationReceiptDto>> InspectBlockCreateAsync(
        Guid propertyId,
        Guid operationId,
        string fingerprint,
        CancellationToken cancellationToken) => this.InspectAsync(
            propertyId,
            InventoryManagementResourceKind.Property,
            propertyId,
            operationId,
            InventoryManagementMutationKind.ManualBlockCreate,
            0,
            fingerprint,
            static operation => operation.ToBlockReceipt(),
            cancellationToken);

    public Task<InventoryManagementReplayDecision<
        ManualInventoryBlockGroupMutationReceiptDto>>
        InspectBlockGroupCreateAsync(
        Guid propertyId,
        Guid operationId,
        string fingerprint,
        CancellationToken cancellationToken) => this.InspectAsync(
            propertyId,
            InventoryManagementResourceKind.Property,
            propertyId,
            operationId,
            InventoryManagementMutationKind.ManualBlockGroupCreate,
            0,
            fingerprint,
            static operation => operation.ToBlockGroupReceipt(),
            cancellationToken);

    public Task<InventoryManagementReplayDecision<
        ManualInventoryBlockGroupMutationReceiptDto>>
        InspectBlockGroupCreateV2Async(
        Guid propertyId,
        Guid operationId,
        string fingerprint,
        CancellationToken cancellationToken) => this.InspectAsync(
            propertyId,
            InventoryManagementResourceKind.Property,
            propertyId,
            operationId,
            InventoryManagementMutationKind.ManualBlockGroupCreateV2,
            0,
            fingerprint,
            static operation => operation.ToBlockGroupReceipt(),
            cancellationToken);

    public Task<InventoryManagementReplayDecision<
        ManualInventoryBlockMutationReceiptDto>> InspectBlockReleaseAsync(
        Guid propertyId,
        Guid blockId,
        Guid operationId,
        long expectedVersion,
        string fingerprint,
        CancellationToken cancellationToken) => this.InspectAsync(
            propertyId,
            InventoryManagementResourceKind.Block,
            blockId,
            operationId,
            InventoryManagementMutationKind.ManualBlockRelease,
            expectedVersion,
            fingerprint,
            static operation => operation.ToBlockReceipt(),
            cancellationToken);

    public Task<InventoryManagementReplayDecision<
        ManualInventoryBlockGroupMutationReceiptDto>>
        InspectBlockGroupReleaseAsync(
        Guid propertyId,
        Guid blockGroupId,
        Guid operationId,
        string fingerprint,
        CancellationToken cancellationToken) => this.InspectAsync(
            propertyId,
            InventoryManagementResourceKind.BlockGroup,
            blockGroupId,
            operationId,
            InventoryManagementMutationKind.ManualBlockGroupRelease,
            0,
            fingerprint,
            static operation => operation.ToBlockGroupReceipt(),
            cancellationToken);

    public Task<InventoryManagementReplayDecision<
        ManualInventoryBlockGroupMutationReceiptDto>> InspectBlockGroupReplaceAsync(
        Guid propertyId,
        Guid blockGroupId,
        Guid operationId,
        long expectedVersion,
        string fingerprint,
        CancellationToken cancellationToken) => this.InspectAsync(
            propertyId,
            InventoryManagementResourceKind.BlockGroup,
            blockGroupId,
            operationId,
            InventoryManagementMutationKind.ManualBlockGroupReplace,
            expectedVersion,
            fingerprint,
            static operation => operation.ToBlockGroupReceipt(),
            cancellationToken);

    public Task<InventoryManagementReplayDecision<
        ManualInventoryBlockGroupMutationReceiptDto>> InspectBlockGroupReleaseV2Async(
        Guid propertyId,
        Guid blockGroupId,
        Guid operationId,
        long expectedVersion,
        string fingerprint,
        CancellationToken cancellationToken) => this.InspectAsync(
            propertyId,
            InventoryManagementResourceKind.BlockGroup,
            blockGroupId,
            operationId,
            InventoryManagementMutationKind.ManualBlockGroupReleaseV2,
            expectedVersion,
            fingerprint,
            static operation => operation.ToBlockGroupReceipt(),
            cancellationToken);

    public Task<InventoryManagementReplayDecision<
        InventoryRetirementOperationPointer>>
        InspectBedRetirementRequestAsync(
        Guid propertyId,
        Guid bedId,
        Guid operationId,
        string fingerprint,
        CancellationToken cancellationToken) => this.InspectAsync(
            propertyId,
            InventoryManagementResourceKind.InventoryUnit,
            bedId,
            operationId,
            InventoryManagementMutationKind.BedRetirementRequest,
            0,
            fingerprint,
            static operation => operation.ToRetirementPointer(),
            cancellationToken);

    public Task<InventoryManagementReplayDecision<
        InventoryRetirementOperationPointer>> InspectBedRetirementRetryAsync(
        Guid propertyId,
        Guid topologyChangeId,
        Guid operationId,
        long expectedVersion,
        string fingerprint,
        CancellationToken cancellationToken) => this.InspectAsync(
            propertyId,
            InventoryManagementResourceKind.BedRetirement,
            topologyChangeId,
            operationId,
            InventoryManagementMutationKind.BedRetirementRetry,
            expectedVersion,
            fingerprint,
            static operation => operation.ToRetirementPointer(),
            cancellationToken);

    public Task<InventoryManagementReplayDecision<
        InventoryRetirementOperationPointer>> InspectBedRetirementCancellationAsync(
        Guid propertyId,
        Guid topologyChangeId,
        Guid operationId,
        long expectedVersion,
        string fingerprint,
        CancellationToken cancellationToken) => this.InspectAsync(
            propertyId,
            InventoryManagementResourceKind.BedRetirement,
            topologyChangeId,
            operationId,
            InventoryManagementMutationKind.BedRetirementCancellation,
            expectedVersion,
            fingerprint,
            static operation => operation.ToRetirementPointer(),
            cancellationToken);

    public Task<InventoryManagementReplayDecision<
        InventoryRetirementOperationPointer>>
        InspectRoomRetirementRequestAsync(
        Guid propertyId,
        Guid roomId,
        Guid operationId,
        string fingerprint,
        CancellationToken cancellationToken) => this.InspectAsync(
            propertyId,
            InventoryManagementResourceKind.Room,
            roomId,
            operationId,
            InventoryManagementMutationKind.RoomRetirementRequest,
            0,
            fingerprint,
            static operation => operation.ToRetirementPointer(),
            cancellationToken);

    public Task<InventoryManagementReplayDecision<
        InventoryRetirementOperationPointer>> InspectRoomRetirementRetryAsync(
        Guid propertyId,
        Guid topologyChangeId,
        Guid operationId,
        long expectedVersion,
        string fingerprint,
        CancellationToken cancellationToken) => this.InspectAsync(
            propertyId,
            InventoryManagementResourceKind.RoomRetirement,
            topologyChangeId,
            operationId,
            InventoryManagementMutationKind.RoomRetirementRetry,
            expectedVersion,
            fingerprint,
            static operation => operation.ToRetirementPointer(),
            cancellationToken);

    public Task<InventoryManagementReplayDecision<
        InventoryRetirementOperationPointer>> InspectRoomRetirementCancellationAsync(
        Guid propertyId,
        Guid topologyChangeId,
        Guid operationId,
        long expectedVersion,
        string fingerprint,
        CancellationToken cancellationToken) => this.InspectAsync(
            propertyId,
            InventoryManagementResourceKind.RoomRetirement,
            topologyChangeId,
            operationId,
            InventoryManagementMutationKind.RoomRetirementCancellation,
            expectedVersion,
            fingerprint,
            static operation => operation.ToRetirementPointer(),
            cancellationToken);

    private async Task<InventoryManagementReplayDecision<TReceipt>>
        InspectAsync<TReceipt>(
        Guid propertyId,
        InventoryManagementResourceKind resourceKind,
        Guid resourceId,
        Guid operationId,
        InventoryManagementMutationKind kind,
        long expectedVersion,
        string fingerprint,
        Func<InventoryManagementOperationRecord, TReceipt> receiptFactory,
        CancellationToken cancellationToken)
        where TReceipt : class
    {
        InventoryManagementOperationRecord? existing =
            await operations.GetAsync(
                resourceKind,
                resourceId,
                operationId,
                cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return InventoryManagementReplayDecision<TReceipt>.Missing;
        }

        return existing.Matches(
            kind,
            propertyId,
            resourceKind,
            resourceId,
            expectedVersion,
            fingerprint)
            ? InventoryManagementReplayDecision<TReceipt>.Exact(
                receiptFactory(existing))
            : InventoryManagementReplayDecision<TReceipt>.Conflict;
    }

    public async Task<RoomInventoryMutationReceiptDto> RecordRoomAsync(
        RoomInventoryConfiguration configuration,
        Guid operationId,
        long expectedVersion,
        string fingerprint,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        InventorySalesMode salesMode = configuration.SalesMode switch
        {
            RoomSalesMode.RoomLevel => InventorySalesMode.RoomLevel,
            RoomSalesMode.BedLevel => InventorySalesMode.BedLevel,
            _ => InventorySalesMode.Unknown
        };
        RoomInventoryMutationReceiptDto receipt = new(
            configuration.PropertyId,
            configuration.Id,
            salesMode,
            configuration.Version);
        await operations.AddAsync(
            InventoryManagementOperationRecord.ForRoom(
                operationId,
                configuration.ScopeId,
                configuration.PropertyId,
                configuration.Id,
                expectedVersion,
                fingerprint,
                receipt,
                completedAtUtc),
            cancellationToken).ConfigureAwait(false);
        return receipt;
    }

    public Task RecordBedRetirementRequestAsync(
        BedRetirementProcess process,
        Guid operationId,
        string fingerprint,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken) => this.RecordRetirementAsync(
            process.ScopeId,
            process.PropertyId,
            InventoryManagementResourceKind.InventoryUnit,
            process.BedId,
            InventoryManagementMutationKind.BedRetirementRequest,
            expectedVersion: 0,
            fingerprint,
            process.Id,
            process.Version,
            operationId,
            completedAtUtc,
            cancellationToken);

    public Task RecordBedRetirementRetryAsync(
        BedRetirementProcess process,
        Guid operationId,
        long expectedVersion,
        string fingerprint,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken) => this.RecordRetirementAsync(
            process.ScopeId,
            process.PropertyId,
            InventoryManagementResourceKind.BedRetirement,
            process.Id,
            InventoryManagementMutationKind.BedRetirementRetry,
            expectedVersion,
            fingerprint,
            process.Id,
            process.Version,
            operationId,
            completedAtUtc,
            cancellationToken);

    public Task RecordBedRetirementCancellationAsync(
        BedRetirementProcess process,
        Guid operationId,
        long expectedVersion,
        string fingerprint,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken) => this.RecordRetirementAsync(
            process.ScopeId,
            process.PropertyId,
            InventoryManagementResourceKind.BedRetirement,
            process.Id,
            InventoryManagementMutationKind.BedRetirementCancellation,
            expectedVersion,
            fingerprint,
            process.Id,
            process.Version,
            operationId,
            completedAtUtc,
            cancellationToken);

    public Task RecordRoomRetirementRequestAsync(
        RoomRetirementProcess process,
        Guid operationId,
        string fingerprint,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken) => this.RecordRetirementAsync(
            process.ScopeId,
            process.PropertyId,
            InventoryManagementResourceKind.Room,
            process.RoomId,
            InventoryManagementMutationKind.RoomRetirementRequest,
            expectedVersion: 0,
            fingerprint,
            process.Id,
            process.Version,
            operationId,
            completedAtUtc,
            cancellationToken);

    public Task RecordRoomRetirementRetryAsync(
        RoomRetirementProcess process,
        Guid operationId,
        long expectedVersion,
        string fingerprint,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken) => this.RecordRetirementAsync(
            process.ScopeId,
            process.PropertyId,
            InventoryManagementResourceKind.RoomRetirement,
            process.Id,
            InventoryManagementMutationKind.RoomRetirementRetry,
            expectedVersion,
            fingerprint,
            process.Id,
            process.Version,
            operationId,
            completedAtUtc,
            cancellationToken);

    public Task RecordRoomRetirementCancellationAsync(
        RoomRetirementProcess process,
        Guid operationId,
        long expectedVersion,
        string fingerprint,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken) => this.RecordRetirementAsync(
            process.ScopeId,
            process.PropertyId,
            InventoryManagementResourceKind.RoomRetirement,
            process.Id,
            InventoryManagementMutationKind.RoomRetirementCancellation,
            expectedVersion,
            fingerprint,
            process.Id,
            process.Version,
            operationId,
            completedAtUtc,
            cancellationToken);

    private Task RecordRetirementAsync(
        string scopeId,
        Guid propertyId,
        InventoryManagementResourceKind resourceKind,
        Guid resourceId,
        InventoryManagementMutationKind kind,
        long expectedVersion,
        string fingerprint,
        Guid topologyChangeId,
        long resultVersion,
        Guid operationId,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken) => operations.AddAsync(
            InventoryManagementOperationRecord.ForRetirement(
                operationId,
                scopeId,
                propertyId,
                resourceKind,
                resourceId,
                kind,
                expectedVersion,
                fingerprint,
                topologyChangeId,
                resultVersion,
                completedAtUtc),
            cancellationToken);

    public async Task<ManualInventoryBlockMutationReceiptDto>
        RecordBlockCreateAsync(
        ManualInventoryBlockCreationResult result,
        Guid operationId,
        string fingerprint,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken)
    {
        ManualInventoryBlock block = result.Blocks.Single();
        ManualInventoryBlockMutationReceiptDto receipt =
            block.ToMutationReceipt();
        await operations.AddAsync(
            InventoryManagementOperationRecord.ForBlock(
                operationId,
                block.ScopeId,
                InventoryManagementResourceKind.Property,
                block.PropertyId,
                InventoryManagementMutationKind.ManualBlockCreate,
                0,
                fingerprint,
                receipt,
                completedAtUtc),
            cancellationToken).ConfigureAwait(false);
        return receipt;
    }

    public async Task<ManualInventoryBlockGroupMutationReceiptDto>
        RecordBlockGroupCreateAsync(
        ManualInventoryBlockCreationResult result,
        Guid operationId,
        string fingerprint,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken)
    {
        ManualInventoryBlock first = result.Blocks.First();
        ManualInventoryBlockGroupMutationReceiptDto receipt =
            result.ToMutationReceipt();
        await operations.AddAsync(
            InventoryManagementOperationRecord.ForBlockGroup(
                operationId,
                first.ScopeId,
                InventoryManagementResourceKind.Property,
                first.PropertyId,
                InventoryManagementMutationKind.ManualBlockGroupCreate,
                fingerprint,
                receipt,
                completedAtUtc),
            cancellationToken).ConfigureAwait(false);
        return receipt;
    }

    public Task<ManualInventoryBlockGroupMutationReceiptDto> RecordBlockGroupCreateV2Async(
        ManualInventoryBlockCreationResult result,
        Guid operationId,
        string fingerprint,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken) => this.RecordBlockGroupV2Async(
            result.Group.ScopeId,
            InventoryManagementResourceKind.Property,
            result.Group.PropertyId,
            InventoryManagementMutationKind.ManualBlockGroupCreateV2,
            expectedVersion: 0,
            result.ToMutationReceipt(),
            operationId,
            fingerprint,
            completedAtUtc,
            cancellationToken);

    public async Task<ManualInventoryBlockMutationReceiptDto>
        RecordBlockReleaseAsync(
        ManualInventoryBlock block,
        Guid operationId,
        long expectedVersion,
        string fingerprint,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(block);
        ManualInventoryBlockMutationReceiptDto receipt =
            block.ToMutationReceipt();
        await operations.AddAsync(
            InventoryManagementOperationRecord.ForBlock(
                operationId,
                block.ScopeId,
                InventoryManagementResourceKind.Block,
                block.Id,
                InventoryManagementMutationKind.ManualBlockRelease,
                expectedVersion,
                fingerprint,
                receipt,
                completedAtUtc),
            cancellationToken).ConfigureAwait(false);
        return receipt;
    }

    public async Task<ManualInventoryBlockGroupMutationReceiptDto>
        RecordBlockGroupReleaseAsync(
        string scopeId,
        ManualInventoryBlockGroupMutationReceiptDto receipt,
        Guid operationId,
        string fingerprint,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken)
    {
        await operations.AddAsync(
            InventoryManagementOperationRecord.ForBlockGroup(
                operationId,
                scopeId,
                InventoryManagementResourceKind.BlockGroup,
                receipt.BlockGroupId,
                InventoryManagementMutationKind.ManualBlockGroupRelease,
                fingerprint,
                receipt,
                completedAtUtc),
            cancellationToken).ConfigureAwait(false);
        return receipt;
    }

    public Task<ManualInventoryBlockGroupMutationReceiptDto> RecordBlockGroupReplaceAsync(
        string scopeId,
        Guid requestedBlockGroupId,
        long expectedVersion,
        ManualInventoryBlockGroupMutationReceiptDto receipt,
        Guid operationId,
        string fingerprint,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken) => this.RecordBlockGroupV2Async(
            scopeId,
            InventoryManagementResourceKind.BlockGroup,
            requestedBlockGroupId,
            InventoryManagementMutationKind.ManualBlockGroupReplace,
            expectedVersion,
            receipt,
            operationId,
            fingerprint,
            completedAtUtc,
            cancellationToken);

    public Task<ManualInventoryBlockGroupMutationReceiptDto> RecordBlockGroupReleaseV2Async(
        string scopeId,
        long expectedVersion,
        ManualInventoryBlockGroupMutationReceiptDto receipt,
        Guid operationId,
        string fingerprint,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken) => this.RecordBlockGroupV2Async(
            scopeId,
            InventoryManagementResourceKind.BlockGroup,
            receipt.BlockGroupId,
            InventoryManagementMutationKind.ManualBlockGroupReleaseV2,
            expectedVersion,
            receipt,
            operationId,
            fingerprint,
            completedAtUtc,
            cancellationToken);

    private async Task<ManualInventoryBlockGroupMutationReceiptDto> RecordBlockGroupV2Async(
        string scopeId,
        InventoryManagementResourceKind resourceKind,
        Guid resourceId,
        InventoryManagementMutationKind kind,
        long expectedVersion,
        ManualInventoryBlockGroupMutationReceiptDto receipt,
        Guid operationId,
        string fingerprint,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken)
    {
        await operations.AddAsync(
            InventoryManagementOperationRecord.ForBlockGroupV2(
                operationId,
                scopeId,
                resourceKind,
                resourceId,
                kind,
                expectedVersion,
                fingerprint,
                receipt,
                completedAtUtc),
            cancellationToken).ConfigureAwait(false);
        return receipt;
    }
}

internal readonly record struct InventoryManagementReplayDecision<TReceipt>(
    InventoryManagementReplayOutcome Outcome,
    TReceipt? Receipt)
    where TReceipt : class
{
    public static InventoryManagementReplayDecision<TReceipt> Missing => new(
        InventoryManagementReplayOutcome.Missing,
        null);

    public static InventoryManagementReplayDecision<TReceipt> Conflict => new(
        InventoryManagementReplayOutcome.Conflict,
        null);

    public static InventoryManagementReplayDecision<TReceipt> Exact(
        TReceipt receipt) => new(
            InventoryManagementReplayOutcome.Exact,
            receipt);

    public bool Exists => this.Outcome !=
        InventoryManagementReplayOutcome.Missing;

    public Result<TReceipt> ToResult() => this.Outcome switch
    {
        InventoryManagementReplayOutcome.Exact when this.Receipt is not null =>
            Result.Success(this.Receipt),
        InventoryManagementReplayOutcome.Conflict =>
            Result.Failure<TReceipt>(
                InventoryApplicationErrors.ManagementOperationConflict),
        _ => throw new InvalidOperationException(
            "A missing Inventory management operation has no replay result.")
    };
}

internal enum InventoryManagementReplayOutcome
{
    Missing = 0,
    Exact = 1,
    Conflict = 2
}
