namespace BunkFy.Modules.Properties.Persistence.Repositories;

using System.Globalization;
using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Domain.Entities;
using BunkFy.Modules.Properties.Domain.ValueObjects;
using BunkFy.Modules.Properties.Persistence.TenantTermination;
using Gma.Framework.Messaging.Infrastructure;
using Microsoft.EntityFrameworkCore;

internal sealed partial class PropertiesTenantTerminationContributor
{
    private Task<bool> RemoveCurrentStageAsync(
        PropertiesTenantDestroyOperation operation,
        string tenantId,
        CancellationToken cancellationToken) =>
        operation.Stage switch
        {
            PropertiesTenantDestroyStage.OutboxMessages =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.OutboxMessages.Where(
                        message => message.ScopeId == tenantId),
                    message => message.Id,
                    cancellationToken),
            PropertiesTenantDestroyStage.InboxMessages =>
                this.RemoveBatchAsync(
                    operation,
                    dbContext.InboxMessages
                        .Where(message => message.ScopeId == tenantId)
                        .OrderBy(message => message.Id)
                        .ThenBy(message => message.Handler),
                    message =>
                        $"{message.Id:N}|{LengthPrefixed(message.Handler)}",
                    cancellationToken),
            PropertiesTenantDestroyStage.GovernanceRevisions =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.GovernanceRevisions.Where(
                        revision => revision.ScopeId == tenantId),
                    revision => revision.Id,
                    cancellationToken),
            PropertiesTenantDestroyStage.GovernanceAcknowledgements =>
                this.RemoveGovernanceAcknowledgementBatchAsync(
                    operation,
                    cancellationToken),
            PropertiesTenantDestroyStage.Beds =>
                this.RemoveBedBatchAsync(operation, cancellationToken),
            PropertiesTenantDestroyStage.Rooms =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.Rooms.Where(
                        room => room.ScopeId == tenantId),
                    room => room.Id,
                    cancellationToken),
            // The append-only ledger guard reads durable Stage 12. Persist an empty
            // Stage 11 -> 12 transition before selecting protected ledger rows.
            PropertiesTenantDestroyStage.PropertyMutationOperations =>
                this.RemoveBatchAsync(
                    operation,
                    dbContext.PropertyMutationOperations
                        .OrderBy(item => item.PropertyId)
                        .ThenBy(item => item.ResourceKind)
                        .ThenBy(item => item.ResourceId)
                        .ThenBy(item => item.Id),
                    item => $"{item.PropertyId:N}|" +
                        $"{(int)item.ResourceKind}|{item.ResourceId:N}|" +
                        $"{item.Id:N}",
                    cancellationToken,
                    stopAfterEmptyStageAdvance: true),
            PropertiesTenantDestroyStage.PropertyTimeZoneOperations =>
                this.RemovePropertyTimeZoneOperationBatchAsync(
                    operation,
                    tenantId,
                    cancellationToken),
            PropertiesTenantDestroyStage.Properties =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.Properties.Where(
                        property => property.ScopeId == tenantId),
                    property => property.Id,
                    cancellationToken),
            PropertiesTenantDestroyStage.PropertyOperationLocks =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.PropertyOperationLocks.Where(
                        resourceLock => resourceLock.ScopeId == tenantId),
                    resourceLock => resourceLock.Id,
                    cancellationToken),
            PropertiesTenantDestroyStage.RoomOperationLocks =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.RoomOperationLocks.Where(
                        resourceLock => resourceLock.ScopeId == tenantId),
                    resourceLock => resourceLock.Id,
                    cancellationToken),
            _ => throw new InvalidDataException(
                "The Properties tenant destruction stage is invalid.")
        };

    private Task<bool> RemoveGuidBatchAsync<TEntity>(
        PropertiesTenantDestroyOperation operation,
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

    private async Task<bool> RemovePropertyTimeZoneOperationBatchAsync(
        PropertiesTenantDestroyOperation operation,
        string tenantId,
        CancellationToken cancellationToken)
    {
        PropertyTimeZoneOperation[] loaded = await dbContext
            .PropertyTimeZoneOperations
            .Where(item => item.ScopeId == tenantId)
            .OrderBy(item => item.PropertyId)
            .ThenBy(item => item.OperationId)
            .Take(operation.BatchSize + 1)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (loaded.Length == 0)
        {
            EnsureStageAdvanced(operation, clock.UtcNow);
            return false;
        }

        PropertyTimeZoneOperation[] selected = loaded
            .Take(operation.BatchSize)
            .ToArray();
        dbContext.RemoveRange(selected);
        EnsureBatchRecorded(
            operation,
            selected.Select(item =>
                $"{item.PropertyId:N}|{item.OperationId:N}").ToArray(),
            stageCompleted: false,
            clock.UtcNow);
        return true;
    }

    private async Task<bool> RemoveBatchAsync<TEntity>(
        PropertiesTenantDestroyOperation operation,
        IQueryable<TEntity> source,
        Func<TEntity, string> keySelector,
        CancellationToken cancellationToken,
        bool stopAfterEmptyStageAdvance = false)
        where TEntity : class
    {
        TEntity[] loaded = await source
            .Take(operation.BatchSize + 1)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (loaded.Length == 0)
        {
            EnsureStageAdvanced(operation, clock.UtcNow);
            return stopAfterEmptyStageAdvance;
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

    private async Task<bool> RemoveGovernanceAcknowledgementBatchAsync(
        PropertiesTenantDestroyOperation operation,
        CancellationToken cancellationToken)
    {
        GovernanceAcknowledgementRemovalRow[] loaded = await (
            from property in dbContext.Properties.AsNoTracking()
            where property.ScopeId == operation.ScopeId
            from acknowledgement in property.GovernanceAcknowledgements
            orderby property.Id,
                acknowledgement.AcknowledgementId,
                acknowledgement.AcknowledgementVersion
            select new GovernanceAcknowledgementRemovalRow(
                property.Id,
                acknowledgement.AcknowledgementId,
                acknowledgement.AcknowledgementVersion))
            .Take(operation.BatchSize + 1)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (loaded.Length == 0)
        {
            EnsureStageAdvanced(operation, clock.UtcNow);
            return false;
        }

        GovernanceAcknowledgementRemovalRow[] selected = loaded
            .Take(operation.BatchSize)
            .ToArray();
        Guid[] propertyIds = selected
            .Select(row => row.PropertyId)
            .Distinct()
            .ToArray();
        Property[] owners = await dbContext.Properties
            .Include(property => property.GovernanceAcknowledgements)
            .Where(property => propertyIds.Contains(property.Id))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        HashSet<GovernanceAcknowledgementRemovalRow> selectedKeys =
            selected.ToHashSet();
        PropertyGovernanceAcknowledgement[] entities = owners
            .SelectMany(property =>
                property.GovernanceAcknowledgements.Select(acknowledgement =>
                    new
                    {
                        Entity = acknowledgement,
                        Key = new GovernanceAcknowledgementRemovalRow(
                            property.Id,
                            acknowledgement.AcknowledgementId,
                            acknowledgement.AcknowledgementVersion)
                    }))
            .Where(candidate => selectedKeys.Contains(candidate.Key))
            .Select(candidate => candidate.Entity)
            .ToArray();
        if (entities.Length != selected.Length)
        {
            throw new InvalidDataException(
                "Properties governance acknowledgement batch changed during selection.");
        }

        dbContext.RemoveRange(entities);
        EnsureBatchRecorded(
            operation,
            selected.Select(row => row.ProofKey).ToArray(),
            loaded.Length <= operation.BatchSize,
            clock.UtcNow);
        return true;
    }

    private async Task<bool> RemoveBedBatchAsync(
        PropertiesTenantDestroyOperation operation,
        CancellationToken cancellationToken)
    {
        BedRemovalRow[] loaded = await (
            from room in dbContext.Rooms.AsNoTracking()
            where room.ScopeId == operation.ScopeId
            from bed in room.Beds
            orderby room.Id, bed.Id
            select new BedRemovalRow(room.Id, bed.Id))
            .Take(operation.BatchSize + 1)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (loaded.Length == 0)
        {
            EnsureStageAdvanced(operation, clock.UtcNow);
            return false;
        }

        BedRemovalRow[] selected = loaded.Take(operation.BatchSize).ToArray();
        Guid[] roomIds = selected
            .Select(row => row.RoomId)
            .Distinct()
            .ToArray();
        Room[] owners = await dbContext.Rooms
            .Include(room => room.Beds)
            .Where(room => roomIds.Contains(room.Id))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        HashSet<BedRemovalRow> selectedKeys = selected.ToHashSet();
        Bed[] entities = owners
            .SelectMany(room => room.Beds)
            .Where(bed => selectedKeys.Contains(
                new BedRemovalRow(bed.RoomId, bed.Id)))
            .ToArray();
        if (entities.Length != selected.Length)
        {
            throw new InvalidDataException(
                "Properties bed batch changed during selection.");
        }

        dbContext.RemoveRange(entities);
        EnsureBatchRecorded(
            operation,
            selected.Select(row => row.ProofKey).ToArray(),
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
        await dbContext.GovernanceRevisions.AnyAsync(
            revision => revision.ScopeId == tenantId,
            cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.Properties
            .Where(property => property.ScopeId == tenantId)
            .SelectMany(property => property.GovernanceAcknowledgements)
            .AnyAsync(cancellationToken).ConfigureAwait(false) ||
        await dbContext.Rooms
            .Where(room => room.ScopeId == tenantId)
            .SelectMany(room => room.Beds)
            .AnyAsync(cancellationToken).ConfigureAwait(false) ||
        await dbContext.Rooms.AnyAsync(
            room => room.ScopeId == tenantId,
            cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.PropertyMutationOperations.AnyAsync(
            operation => operation.ScopeId == tenantId,
            cancellationToken).ConfigureAwait(false) ||
        await dbContext.PropertyTimeZoneOperations.AnyAsync(
            operation => operation.ScopeId == tenantId,
            cancellationToken).ConfigureAwait(false) ||
        await dbContext.Properties.AnyAsync(
            property => property.ScopeId == tenantId,
            cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.PropertyOperationLocks.AnyAsync(
            resourceLock => resourceLock.ScopeId == tenantId,
            cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.RoomOperationLocks.AnyAsync(
            resourceLock => resourceLock.ScopeId == tenantId,
            cancellationToken)
            .ConfigureAwait(false);

    private static void EnsureBatchRecorded(
        PropertiesTenantDestroyOperation operation,
        string[] keys,
        bool stageCompleted,
        DateTimeOffset recordedAtUtc)
    {
        PropertiesTenantDestroyStage stage = operation.Stage;
        string canonicalKeys = string.Concat(keys.Select(LengthPrefixed));
        string keysSha256 = PropertiesTenantLifecycleHashes.Sha256(
            "bunkfy-properties-tenant-destroy-keys/v1|" +
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
                "Properties tenant destruction batch progress is invalid.");
        }
    }

    private static void EnsureStageAdvanced(
        PropertiesTenantDestroyOperation operation,
        DateTimeOffset recordedAtUtc)
    {
        if (!operation.AdvanceEmptyStage(recordedAtUtc))
        {
            throw new InvalidDataException(
                "Properties tenant destruction stage progress is invalid.");
        }
    }

    private static string LengthPrefixed(string value) =>
        $"{value.Length.ToString(CultureInfo.InvariantCulture)}:{value}";

    private sealed record GovernanceAcknowledgementRemovalRow(
        Guid PropertyId,
        string AcknowledgementId,
        int AcknowledgementVersion)
    {
        public string ProofKey =>
            $"{this.PropertyId:N}|{LengthPrefixed(this.AcknowledgementId)}|" +
            this.AcknowledgementVersion.ToString(CultureInfo.InvariantCulture);
    }

    private sealed record BedRemovalRow(Guid RoomId, Guid BedId)
    {
        public string ProofKey => $"{this.RoomId:N}|{this.BedId:N}";
    }
}
