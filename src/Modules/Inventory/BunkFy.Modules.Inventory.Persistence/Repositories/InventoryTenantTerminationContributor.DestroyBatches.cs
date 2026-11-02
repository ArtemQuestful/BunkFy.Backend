namespace BunkFy.Modules.Inventory.Persistence.Repositories;

using System.Globalization;
using BunkFy.Modules.Inventory.Persistence.TenantTermination;
using Gma.Framework.Messaging.Infrastructure;
using Microsoft.EntityFrameworkCore;

internal sealed partial class InventoryTenantTerminationContributor
{
    private Task<bool> RemoveCurrentStageAsync(
        InventoryTenantDestroyOperation operation,
        string tenantId,
        CancellationToken cancellationToken) =>
        operation.Stage switch
        {
            InventoryTenantDestroyStage.OutboxMessages =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.OutboxMessages.Where(
                        message => message.ScopeId == tenantId),
                    message => message.Id,
                    cancellationToken),
            InventoryTenantDestroyStage.InboxMessages =>
                this.RemoveBatchAsync(
                    operation,
                    dbContext.InboxMessages
                        .Where(message => message.ScopeId == tenantId)
                        .OrderBy(message => message.Id)
                        .ThenBy(message => message.Handler),
                    message =>
                        $"{message.Id:N}|{LengthPrefixed(message.Handler)}",
                    cancellationToken),
            InventoryTenantDestroyStage
                .AllocationAnonymisationRestoreReceipts =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.AllocationAnonymisationRestoreReceipts,
                    receipt => receipt.Id,
                    cancellationToken),
            InventoryTenantDestroyStage.AllocationAnonymisationReceipts =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.AllocationAnonymisationReceipts,
                    receipt => receipt.Id,
                    cancellationToken),
            InventoryTenantDestroyStage.AllocationAnonymisationTombstones =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.AllocationAnonymisationTombstones,
                    tombstone => tombstone.Id,
                    cancellationToken),
            InventoryTenantDestroyStage.AllocationAmendmentDecisions =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.AllocationAmendmentDecisions,
                    decision => decision.Id,
                    cancellationToken),
            InventoryTenantDestroyStage.AllocationUnits =>
                this.RemoveBatchAsync(
                    operation,
                    dbContext.AllocationUnits
                        .OrderBy(unit => unit.AllocationId)
                        .ThenBy(unit => unit.Id),
                    unit => $"{unit.AllocationId:N}|{unit.Id:N}",
                    cancellationToken),
            InventoryTenantDestroyStage.Allocations =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.Allocations,
                    allocation => allocation.Id,
                    cancellationToken),
            InventoryTenantDestroyStage.ManualBlocks =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.ManualBlocks,
                    block => block.Id,
                    cancellationToken),
            InventoryTenantDestroyStage.ManualBlockGroups =>
                this.RemoveManualBlockGroupBatchAsync(
                    operation,
                    tenantId,
                    cancellationToken),
            InventoryTenantDestroyStage.AllocationOperationLocks =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.AllocationOperationLocks,
                    resourceLock => resourceLock.Id,
                    cancellationToken),
            InventoryTenantDestroyStage.BedRetirements =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.BedRetirements,
                    retirement => retirement.Id,
                    cancellationToken),
            InventoryTenantDestroyStage.RoomRetirements =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.RoomRetirements,
                    retirement => retirement.Id,
                    cancellationToken),
            InventoryTenantDestroyStage.ManagementOperations =>
                this.RemoveBatchAsync(
                    operation,
                    dbContext.ManagementOperations
                        .OrderBy(item => item.ResourceKind)
                        .ThenBy(item => item.ResourceId)
                        .ThenBy(item => item.Id),
                    item => $"{(int)item.ResourceKind}|" +
                        $"{item.ResourceId:N}|{item.Id:N}",
                    cancellationToken),
            InventoryTenantDestroyStage.RoomConfigurations =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.RoomConfigurations,
                    configuration => configuration.Id,
                    cancellationToken),
            InventoryTenantDestroyStage.InventoryUnits =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.InventoryUnits,
                    unit => unit.Id,
                    cancellationToken),
            InventoryTenantDestroyStage.BedTopology =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.BedTopology,
                    bed => bed.Id,
                    cancellationToken),
            InventoryTenantDestroyStage.RoomTopology =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.RoomTopology,
                    room => room.Id,
                    cancellationToken),
            InventoryTenantDestroyStage.PropertyTopology =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.PropertyTopology,
                    property => property.Id,
                    cancellationToken),
            InventoryTenantDestroyStage.ProjectionRebuildCheckpoints =>
                this.RemoveBatchAsync(
                    operation,
                    dbContext.ProjectionRebuildCheckpoints
                        .OrderBy(checkpoint => checkpoint.ProjectionName)
                        .ThenBy(checkpoint => checkpoint.RunId),
                    checkpoint =>
                        $"{LengthPrefixed(checkpoint.ProjectionName)}|" +
                        $"{checkpoint.RunId:N}",
                    cancellationToken),
            _ => throw new InvalidDataException(
                "The Inventory tenant destruction stage is invalid.")
        };

    private Task<bool> RemoveManualBlockGroupBatchAsync(
        InventoryTenantDestroyOperation operation,
        string tenantId,
        CancellationToken cancellationToken) =>
        this.RemoveBatchAsync(
            operation,
            dbContext.ManualBlockGroups
                .Where(group =>
                    group.ScopeId == tenantId &&
                    !dbContext.ManualBlockGroups.Any(successor =>
                        successor.ScopeId == tenantId &&
                        successor.PropertyId == group.PropertyId &&
                        successor.ReplacesGroupId == group.Id))
                .OrderBy(group => group.PropertyId)
                .ThenBy(group => group.Id),
            group => $"{group.PropertyId:N}|{group.Id:N}",
            completeWhenExhausted: false,
            cancellationToken);

    private Task<bool> RemoveGuidBatchAsync<TEntity>(
        InventoryTenantDestroyOperation operation,
        IQueryable<TEntity> source,
        System.Linq.Expressions.Expression<Func<TEntity, Guid>> idSelector,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        Func<TEntity, Guid> getId = idSelector.Compile();
        return this.RemoveBatchAsync(
            operation,
            source.OrderBy(idSelector),
            entity => getId(entity).ToString("N"),
            cancellationToken);
    }

    private Task<bool> RemoveBatchAsync<TEntity>(
        InventoryTenantDestroyOperation operation,
        IQueryable<TEntity> source,
        Func<TEntity, string> keySelector,
        CancellationToken cancellationToken)
        where TEntity : class => this.RemoveBatchAsync(
            operation,
            source,
            keySelector,
            completeWhenExhausted: true,
            cancellationToken);

    private async Task<bool> RemoveBatchAsync<TEntity>(
        InventoryTenantDestroyOperation operation,
        IQueryable<TEntity> source,
        Func<TEntity, string> keySelector,
        bool completeWhenExhausted,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        TEntity[] loaded = await source
            .Take(operation.BatchSize + 1)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (loaded.Length == 0)
        {
            EnsureStageAdvanced(operation, clock.UtcNow);
            return false;
        }

        TEntity[] selected = loaded.Take(operation.BatchSize).ToArray();
        string[] keys = selected.Select(keySelector).ToArray();
        dbContext.RemoveRange(selected);
        EnsureBatchRecorded(
            operation,
            keys,
            completeWhenExhausted && loaded.Length <= operation.BatchSize,
            clock.UtcNow);
        return true;
    }

    private async Task<bool> HasRemainingOwnerRecordsAsync(
        string tenantId,
        CancellationToken cancellationToken) =>
        await dbContext.OutboxMessages.AnyAsync(
            message => message.ScopeId == tenantId,
            cancellationToken).ConfigureAwait(false) ||
        await dbContext.InboxMessages.AnyAsync(
            message => message.ScopeId == tenantId,
            cancellationToken).ConfigureAwait(false) ||
        await dbContext.AllocationAnonymisationRestoreReceipts
            .AnyAsync(cancellationToken).ConfigureAwait(false) ||
        await dbContext.AllocationAnonymisationReceipts
            .AnyAsync(cancellationToken).ConfigureAwait(false) ||
        await dbContext.AllocationAnonymisationTombstones
            .AnyAsync(cancellationToken).ConfigureAwait(false) ||
        await dbContext.AllocationAmendmentDecisions
            .AnyAsync(cancellationToken).ConfigureAwait(false) ||
        await dbContext.AllocationUnits.AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.Allocations.AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.ManualBlocks.AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.ManualBlockGroups.AnyAsync(
            group => group.ScopeId == tenantId,
            cancellationToken).ConfigureAwait(false) ||
        await dbContext.AllocationOperationLocks.AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.BedRetirements.AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.RoomRetirements.AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.ManagementOperations.AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.RoomConfigurations.AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.InventoryUnits.AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.BedTopology.AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.RoomTopology.AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.PropertyTopology.AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.ProjectionRebuildCheckpoints
            .AnyAsync(cancellationToken).ConfigureAwait(false);

    private static void EnsureBatchRecorded(
        InventoryTenantDestroyOperation operation,
        string[] keys,
        bool stageCompleted,
        DateTimeOffset recordedAtUtc)
    {
        InventoryTenantDestroyStage stage = operation.Stage;
        string canonicalKeys = string.Concat(keys.Select(LengthPrefixed));
        string keysSha256 = InventoryTenantLifecycleHashes.Sha256(
            "bunkfy-inventory-tenant-destroy-keys/v1|" +
            $"{(int)stage}|{keys.Length.ToString(CultureInfo.InvariantCulture)}|" +
            canonicalKeys);
        if (!operation.RecordBatch(
                stage,
                keys.Length,
                keysSha256,
                stageCompleted,
                recordedAtUtc))
        {
            throw new InvalidDataException(
                "Inventory tenant destruction batch progress is invalid.");
        }
    }

    private static void EnsureStageAdvanced(
        InventoryTenantDestroyOperation operation,
        DateTimeOffset recordedAtUtc)
    {
        if (!operation.AdvanceEmptyStage(recordedAtUtc))
        {
            throw new InvalidDataException(
                "Inventory tenant destruction stage progress is invalid.");
        }
    }

    private static string LengthPrefixed(string value) =>
        $"{value.Length.ToString(CultureInfo.InvariantCulture)}:{value}";
}
