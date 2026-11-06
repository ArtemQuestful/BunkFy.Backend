namespace BunkFy.Modules.Retention.Persistence.Repositories;

using System.Globalization;
using BunkFy.Modules.Retention.Persistence.TenantTermination;
using Gma.Framework.Messaging.Infrastructure;
using Microsoft.EntityFrameworkCore;

internal sealed partial class RetentionTenantTerminationContributor
{
    private Task<bool> RemoveCurrentStageAsync(
        RetentionTenantDestroyOperation operation,
        string tenantId,
        CancellationToken cancellationToken) =>
        operation.Stage switch
        {
            RetentionTenantDestroyStage.OutboxMessages =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.OutboxMessages.Where(
                        message => message.ScopeId == tenantId),
                    message => message.Id,
                    cancellationToken),
            RetentionTenantDestroyStage.InboxMessages =>
                this.RemoveBatchAsync(
                    operation,
                    dbContext.InboxMessages
                        .Where(message => message.ScopeId == tenantId)
                        .OrderBy(message => message.Id)
                        .ThenBy(message => message.Handler),
                    message =>
                        $"{message.Id:N}|{LengthPrefixed(message.Handler)}",
                    cancellationToken),
            RetentionTenantDestroyStage.RunRetryRequests =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.RunRetryRequests.Where(
                        request => request.ScopeId == tenantId),
                    request => request.Id,
                    cancellationToken),
            RetentionTenantDestroyStage.ScheduleStates =>
                this.RemoveBatchAsync(
                    operation,
                    dbContext.ScheduleStates
                        .Where(state => state.ScopeId == tenantId)
                        .OrderBy(state => state.OwnerKey)
                        .ThenBy(state => state.DataClassKey)
                        .ThenBy(state => state.TargetKey)
                        .ThenBy(state => state.ExecutionPolicyVersion),
                    state =>
                        $"{LengthPrefixed(state.OwnerKey)}|" +
                        $"{LengthPrefixed(state.DataClassKey)}|" +
                        $"{LengthPrefixed(state.TargetKey)}|" +
                        state.ExecutionPolicyVersion.ToString(
                            CultureInfo.InvariantCulture),
                    cancellationToken),
            RetentionTenantDestroyStage.Executions =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.Executions.Where(
                        execution => execution.ScopeId == tenantId),
                    execution => execution.Id,
                    cancellationToken),
            RetentionTenantDestroyStage.PropertyProjections =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.PropertyProjections.Where(
                        property => property.ScopeId == tenantId),
                    property => property.Id,
                    cancellationToken),
            RetentionTenantDestroyStage.TenantProjections =>
                this.RemoveBatchAsync(
                    operation,
                    dbContext.TenantProjections
                        .Where(tenant => tenant.ScopeId == tenantId)
                        .OrderBy(tenant => tenant.ScopeId),
                    tenant => LengthPrefixed(tenant.ScopeId),
                    cancellationToken),
            _ => throw new InvalidDataException(
                "The Retention tenant destruction stage is invalid.")
        };

    private Task<bool> RemoveGuidBatchAsync<TEntity>(
        RetentionTenantDestroyOperation operation,
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

    private async Task<bool> RemoveBatchAsync<TEntity>(
        RetentionTenantDestroyOperation operation,
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
        await dbContext.RunRetryRequests.AnyAsync(
            request => request.ScopeId == tenantId,
            cancellationToken).ConfigureAwait(false) ||
        await dbContext.ScheduleStates.AnyAsync(
            state => state.ScopeId == tenantId,
            cancellationToken).ConfigureAwait(false) ||
        await dbContext.Executions.AnyAsync(
            execution => execution.ScopeId == tenantId,
            cancellationToken).ConfigureAwait(false) ||
        await dbContext.PropertyProjections.AnyAsync(
            property => property.ScopeId == tenantId,
            cancellationToken).ConfigureAwait(false) ||
        await dbContext.TenantProjections.AnyAsync(
            tenant => tenant.ScopeId == tenantId,
            cancellationToken).ConfigureAwait(false);

    private static void EnsureBatchRecorded(
        RetentionTenantDestroyOperation operation,
        string[] keys,
        bool stageCompleted,
        DateTimeOffset recordedAtUtc)
    {
        RetentionTenantDestroyStage stage = operation.Stage;
        string canonicalKeys = string.Concat(keys.Select(LengthPrefixed));
        string keysSha256 = RetentionTenantLifecycleHashes.Sha256(
            "bunkfy-retention-tenant-destroy-keys/v1|" +
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
                "Retention tenant destruction batch progress is invalid.");
        }
    }

    private static void EnsureStageAdvanced(
        RetentionTenantDestroyOperation operation,
        DateTimeOffset recordedAtUtc)
    {
        if (!operation.AdvanceEmptyStage(recordedAtUtc))
        {
            throw new InvalidDataException(
                "Retention tenant destruction stage progress is invalid.");
        }
    }

    private static string LengthPrefixed(string value) =>
        $"{value.Length.ToString(CultureInfo.InvariantCulture)}:{value}";
}
