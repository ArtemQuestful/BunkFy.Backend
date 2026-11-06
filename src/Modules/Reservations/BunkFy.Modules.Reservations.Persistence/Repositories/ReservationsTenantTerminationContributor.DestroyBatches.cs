namespace BunkFy.Modules.Reservations.Persistence.Repositories;

using System.Globalization;
using BunkFy.Modules.Reservations.Domain.DataRights;
using BunkFy.Modules.Reservations.Domain.GuestRecords;
using BunkFy.Modules.Reservations.Domain.Retention;
using BunkFy.Modules.Reservations.Domain.StayAmendments;
using BunkFy.Modules.Reservations.Persistence.TenantTermination;
using Gma.Framework.Messaging.Infrastructure;
using Microsoft.EntityFrameworkCore;

internal sealed partial class ReservationsTenantTerminationContributor
{
    private Task<bool> RemoveCurrentStageAsync(
        ReservationsTenantDestroyOperation operation,
        string tenantId,
        CancellationToken cancellationToken) =>
        operation.Stage switch
        {
            ReservationsTenantDestroyStage.OutboxMessages =>
                this.RemoveBatchAsync(
                    operation,
                    dbContext.OutboxMessages
                        .Where(message => message.ScopeId == tenantId)
                        .OrderBy(message => message.Id),
                    message => message.Id.ToString("N"),
                    cancellationToken),
            ReservationsTenantDestroyStage.InboxMessages =>
                this.RemoveBatchAsync(
                    operation,
                    dbContext.InboxMessages
                        .Where(message => message.ScopeId == tenantId)
                        .OrderBy(message => message.Id)
                        .ThenBy(message => message.Handler),
                    message =>
                        $"{message.Id:N}|{LengthPrefixed(message.Handler)}",
                    cancellationToken),
            ReservationsTenantDestroyStage.RetentionAnonymisationReceipts =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.RetentionAnonymisationReceipts,
                    receipt => receipt.Id,
                    cancellationToken),
            ReservationsTenantDestroyStage.AnonymisationReceipts =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.AnonymisationReceipts,
                    receipt => receipt.Id,
                    cancellationToken),
            ReservationsTenantDestroyStage.AnonymisationRestoreReceipts =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.AnonymisationRestoreReceipts,
                    receipt => receipt.Id,
                    cancellationToken),
            ReservationsTenantDestroyStage.AnonymisationTombstones =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.AnonymisationTombstones,
                    tombstone => tombstone.Id,
                    cancellationToken),
            ReservationsTenantDestroyStage.DataHoldReceipts =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.DataHoldReceipts,
                    receipt => receipt.Id,
                    cancellationToken),
            ReservationsTenantDestroyStage.ProcessingRestrictionReceipts =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.ProcessingRestrictionReceipts,
                    receipt => receipt.Id,
                    cancellationToken),
            ReservationsTenantDestroyStage.DataRightsCorrectionReceipts =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.DataRightsCorrectionReceipts,
                    receipt => receipt.Id,
                    cancellationToken),
            ReservationsTenantDestroyStage.ArrivalReminders =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.ArrivalReminders,
                    reminder => reminder.Id,
                    cancellationToken),
            ReservationsTenantDestroyStage.ExternalOperations =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.ExternalOperations
                        .Where(item => item.ScopeId == tenantId),
                    externalOperation => externalOperation.Id,
                    cancellationToken),
            ReservationsTenantDestroyStage.ManagementOperations =>
                this.RemoveManagementOperationBatchAsync(
                    operation,
                    tenantId,
                    cancellationToken),
            ReservationsTenantDestroyStage.DetailsHistory =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.ReservationDetailsHistory
                        .Where(item => item.ScopeId == tenantId),
                    entry => entry.Id,
                    cancellationToken),
            ReservationsTenantDestroyStage.DataHolds =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.DataHolds,
                    hold => hold.Id,
                    cancellationToken),
            ReservationsTenantDestroyStage.ProcessingRestrictions =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.ProcessingRestrictions,
                    restriction => restriction.Id,
                    cancellationToken),
            ReservationsTenantDestroyStage.RetentionExecutions =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.RetentionExecutions,
                    execution => execution.Id,
                    cancellationToken),
            ReservationsTenantDestroyStage.ReservationGuests =>
                this.RemoveReservationGuestRecordsBatchAsync(
                    operation,
                    cancellationToken),
            ReservationsTenantDestroyStage.RequestedInventoryUnits =>
                this.RemoveBatchAsync(
                    operation,
                    dbContext.RequestedInventoryUnits
                        .OrderBy(unit => unit.ReservationId)
                        .ThenBy(unit => unit.Id),
                    unit => $"{unit.ReservationId:N}|{unit.Id:N}",
                    cancellationToken),
            ReservationsTenantDestroyStage.Reservations =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.Reservations,
                    reservation => reservation.Id,
                    cancellationToken),
            ReservationsTenantDestroyStage.InventoryAllocationUnits =>
                this.RemoveBatchAsync(
                    operation,
                    dbContext.InventoryAllocationUnitProjections
                        .OrderBy(unit => unit.AllocationId)
                        .ThenBy(unit => unit.Id),
                    unit => $"{unit.AllocationId:N}|{unit.Id:N}",
                    cancellationToken),
            ReservationsTenantDestroyStage.InventoryAllocations =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.InventoryAllocationProjections,
                    allocation => allocation.Id,
                    cancellationToken),
            ReservationsTenantDestroyStage.InventoryBlocks =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.InventoryBlockProjections,
                    block => block.Id,
                    cancellationToken),
            ReservationsTenantDestroyStage.InventoryUnits =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.InventoryUnitProjections,
                    unit => unit.Id,
                    cancellationToken),
            ReservationsTenantDestroyStage
                .GuestProcessingRestrictionProjections =>
                this.RemoveBatchAsync(
                    operation,
                    dbContext.GuestProcessingRestrictionProjections
                        .OrderBy(projection => projection.PropertyId)
                        .ThenBy(projection => projection.GuestId),
                    projection =>
                        $"{projection.PropertyId:N}|{projection.GuestId:N}",
                    cancellationToken),
            ReservationsTenantDestroyStage.GuestProfileProjections =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.GuestProfileProjections
                        .Where(item => item.ScopeId == tenantId),
                    profile => profile.Id,
                    cancellationToken),
            ReservationsTenantDestroyStage.ProcessingRestrictionProjections =>
                this.RemoveBatchAsync(
                    operation,
                    dbContext.ProcessingRestrictionProjections
                        .OrderBy(projection => projection.PropertyId)
                        .ThenBy(projection => projection.ReservationId),
                    projection =>
                        $"{projection.PropertyId:N}|" +
                        $"{projection.ReservationId:N}",
                    cancellationToken),
            ReservationsTenantDestroyStage.PropertyProjections =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.PropertyProjections,
                    property => property.Id,
                    cancellationToken),
            ReservationsTenantDestroyStage.ProjectionRebuildCheckpoints =>
                this.RemoveBatchAsync(
                    operation,
                    dbContext.ProjectionRebuildCheckpoints
                        .OrderBy(checkpoint => checkpoint.ProjectionName)
                        .ThenBy(checkpoint => checkpoint.RunId),
                    checkpoint =>
                        $"{LengthPrefixed(checkpoint.ProjectionName)}|" +
                        $"{checkpoint.RunId:N}",
                    cancellationToken),
            ReservationsTenantDestroyStage.RetentionSweepCheckpoints =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.RetentionSweepCheckpoints,
                    checkpoint => checkpoint.Id,
                    cancellationToken),
            ReservationsTenantDestroyStage.OperationLocks =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.OperationLocks,
                    resourceLock => resourceLock.Id,
                    cancellationToken),
            _ => throw new InvalidDataException(
                "The Reservations tenant destruction stage is invalid.")
        };

    private Task<bool> RemoveGuidBatchAsync<TEntity>(
        ReservationsTenantDestroyOperation operation,
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

    private async Task<bool> RemoveManagementOperationBatchAsync(
        ReservationsTenantDestroyOperation operation,
        string tenantId,
        CancellationToken cancellationToken)
    {
        ReservationStayAmendmentOperation[] children = await dbContext
            .StayAmendmentOperations
            .Where(item => item.ScopeId == tenantId)
            .OrderBy(item => item.ReservationId)
            .ThenBy(item => item.Id)
            .Take(operation.BatchSize + 1)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (children.Length > 0)
        {
            ReservationStayAmendmentOperation[] selected = children
                .Take(operation.BatchSize)
                .ToArray();
            dbContext.StayAmendmentOperations.RemoveRange(selected);
            EnsureBatchRecorded(
                operation,
                selected
                    .Select(item =>
                        $"stay-amendment:{item.ReservationId:N}|{item.Id:N}")
                    .ToArray(),
                stageCompleted: false,
                clock.UtcNow);
            return true;
        }

        return await this.RemoveBatchAsync(
            operation,
            dbContext.ManagementOperations
                .Where(item => item.ScopeId == tenantId)
                .OrderBy(item => item.ReservationId)
                .ThenBy(item => item.Id),
            item => $"management:{item.ReservationId:N}|{item.Id:N}",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> RemoveReservationGuestRecordsBatchAsync(
        ReservationsTenantDestroyOperation operation,
        CancellationToken cancellationToken)
    {
        ReservationGuestRecordLinkProcess[] processes = await dbContext
            .GuestRecordLinkProcesses
            .OrderBy(process => process.Id)
            .Take(operation.BatchSize + 1)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (processes.Length > 0)
        {
            ReservationGuestRecordLinkProcess[] selected = processes
                .Take(operation.BatchSize)
                .ToArray();
            dbContext.GuestRecordLinkProcesses.RemoveRange(selected);
            EnsureBatchRecorded(
                operation,
                selected.Select(process => $"process:{process.Id:N}").ToArray(),
                stageCompleted: false,
                clock.UtcNow);
            return true;
        }

        return await this.RemoveBatchAsync(
            operation,
            dbContext.ReservationGuests
                .OrderBy(guest => guest.ReservationId)
                .ThenBy(guest => guest.Id),
            guest => $"{guest.ReservationId:N}|{guest.Id:N}",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> RemoveBatchAsync<TEntity>(
        ReservationsTenantDestroyOperation operation,
        IQueryable<TEntity> source,
        Func<TEntity, string> keySelector,
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
            loaded.Length <= operation.BatchSize,
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
        await dbContext.RetentionAnonymisationReceipts.AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.AnonymisationReceipts.AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.AnonymisationRestoreReceipts.AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.AnonymisationTombstones.AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.DataHoldReceipts.AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.ProcessingRestrictionReceipts.AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.DataRightsCorrectionReceipts.AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.ArrivalReminders.AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.ExternalOperations.AnyAsync(
            item => item.ScopeId == tenantId,
            cancellationToken).ConfigureAwait(false) ||
        await dbContext.StayAmendmentOperations.AnyAsync(
            item => item.ScopeId == tenantId,
            cancellationToken).ConfigureAwait(false) ||
        await dbContext.ManagementOperations.AnyAsync(
            item => item.ScopeId == tenantId,
            cancellationToken).ConfigureAwait(false) ||
        await dbContext.ReservationDetailsHistory.AnyAsync(
            item => item.ScopeId == tenantId,
            cancellationToken).ConfigureAwait(false) ||
        await dbContext.DataHolds.AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.ProcessingRestrictions.AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.RetentionExecutions.AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.GuestRecordLinkProcesses.AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.ReservationGuests.AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.RequestedInventoryUnits.AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.Reservations.AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.InventoryAllocationUnitProjections
            .AnyAsync(cancellationToken).ConfigureAwait(false) ||
        await dbContext.InventoryAllocationProjections
            .AnyAsync(cancellationToken).ConfigureAwait(false) ||
        await dbContext.InventoryBlockProjections.AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.InventoryUnitProjections.AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.GuestProcessingRestrictionProjections
            .AnyAsync(cancellationToken).ConfigureAwait(false) ||
        await dbContext.GuestProfileProjections.AnyAsync(
            item => item.ScopeId == tenantId,
            cancellationToken).ConfigureAwait(false) ||
        await dbContext.ProcessingRestrictionProjections
            .AnyAsync(cancellationToken).ConfigureAwait(false) ||
        await dbContext.PropertyProjections.AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.ProjectionRebuildCheckpoints
            .AnyAsync(cancellationToken).ConfigureAwait(false) ||
        await dbContext.RetentionSweepCheckpoints.AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.OperationLocks.AnyAsync(cancellationToken)
            .ConfigureAwait(false);

    private static void EnsureBatchRecorded(
        ReservationsTenantDestroyOperation operation,
        string[] keys,
        bool stageCompleted,
        DateTimeOffset recordedAtUtc)
    {
        ReservationsTenantDestroyStage stage = operation.Stage;
        string canonicalKeys = string.Concat(
            keys.Select(LengthPrefixed));
        string keysSha256 = ReservationsTenantLifecycleHashes.Sha256(
            "bunkfy-reservations-tenant-destroy-keys/v1|" +
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
                "Reservations tenant destruction batch progress is invalid.");
        }
    }

    private static void EnsureStageAdvanced(
        ReservationsTenantDestroyOperation operation,
        DateTimeOffset recordedAtUtc)
    {
        if (!operation.AdvanceEmptyStage(recordedAtUtc))
        {
            throw new InvalidDataException(
                "Reservations tenant destruction stage progress is invalid.");
        }
    }

    private static string LengthPrefixed(string value) =>
        $"{value.Length.ToString(CultureInfo.InvariantCulture)}:{value}";
}
