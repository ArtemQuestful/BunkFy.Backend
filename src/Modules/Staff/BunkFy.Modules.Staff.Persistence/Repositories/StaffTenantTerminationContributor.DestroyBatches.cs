namespace BunkFy.Modules.Staff.Persistence.Repositories;

using System.Globalization;
using BunkFy.Modules.Staff.Persistence.TenantTermination;
using Gma.Framework.Messaging.Infrastructure;
using Microsoft.EntityFrameworkCore;

internal sealed partial class StaffTenantTerminationContributor
{
    private Task<bool> RemoveCurrentStageAsync(
        StaffTenantDestroyOperation operation,
        string tenantId,
        CancellationToken cancellationToken) =>
        operation.Stage switch
        {
            StaffTenantDestroyStage.OutboxMessages =>
                this.RemoveBatchAsync(
                    operation,
                    dbContext.OutboxMessages
                        .Where(message => message.ScopeId == tenantId)
                        .OrderBy(message => message.Id),
                    message => message.Id.ToString("N"),
                    cancellationToken),
            StaffTenantDestroyStage.InboxMessages =>
                this.RemoveBatchAsync(
                    operation,
                    dbContext.InboxMessages
                        .Where(message => message.ScopeId == tenantId)
                        .OrderBy(message => message.Id)
                        .ThenBy(message => message.Handler),
                    message =>
                        $"{message.Id:N}|{LengthPrefixed(message.Handler)}",
                    cancellationToken),
            StaffTenantDestroyStage.RetentionAnonymisationReceipts =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.RetentionAnonymisationReceipts,
                    receipt => receipt.Id,
                    cancellationToken),
            StaffTenantDestroyStage.AnonymisationReceipts =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.AnonymisationReceipts,
                    receipt => receipt.Id,
                    cancellationToken),
            StaffTenantDestroyStage.AnonymisationRestoreReceipts =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.AnonymisationRestoreReceipts,
                    receipt => receipt.Id,
                    cancellationToken),
            StaffTenantDestroyStage.AnonymisationTombstones =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.AnonymisationTombstones,
                    tombstone => tombstone.Id,
                    cancellationToken),
            StaffTenantDestroyStage.DataHoldReceipts =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.DataHoldReceipts,
                    receipt => receipt.Id,
                    cancellationToken),
            StaffTenantDestroyStage.EmploymentGovernanceChangeReceipts =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.EmploymentGovernanceChangeReceipts,
                    receipt => receipt.Id,
                    cancellationToken),
            StaffTenantDestroyStage.ProcessingRestrictionReceipts =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.ProcessingRestrictionReceipts,
                    receipt => receipt.Id,
                    cancellationToken),
            StaffTenantDestroyStage.DataRightsCorrectionReceipts =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.DataRightsCorrectionReceipts,
                    receipt => receipt.Id,
                    cancellationToken),
            StaffTenantDestroyStage.PropertyAssignments =>
                this.RemoveBatchAsync(
                    operation,
                    dbContext.PropertyAssignments
                        .OrderBy(assignment => assignment.StaffMemberId)
                        .ThenBy(assignment => assignment.Id),
                    assignment =>
                        $"{assignment.StaffMemberId:N}|{assignment.Id:N}",
                    cancellationToken),
            StaffTenantDestroyStage.DataHolds =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.DataHolds,
                    hold => hold.Id,
                    cancellationToken),
            StaffTenantDestroyStage.ProcessingRestrictions =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.ProcessingRestrictions,
                    restriction => restriction.Id,
                    cancellationToken),
            StaffTenantDestroyStage.EmploymentGovernance =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.EmploymentGovernance,
                    governance => governance.Id,
                    cancellationToken),
            StaffTenantDestroyStage.RetentionExecutions =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.RetentionExecutions,
                    execution => execution.Id,
                    cancellationToken),
            StaffTenantDestroyStage.OperationLocks =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.OperationLocks,
                    resourceLock => resourceLock.Id,
                    cancellationToken),
            StaffTenantDestroyStage.IdentityProvisioningAnchorResolutions =>
                this.RemoveBatchAsync(
                    operation,
                    dbContext.IdentityProvisioningAnchorResolutions
                        .OrderBy(resolution => resolution.SourceKind)
                        .ThenBy(resolution => resolution.SourceId),
                    resolution =>
                        $"{(int)resolution.SourceKind}|" +
                        $"{resolution.SourceId:N}|" +
                        $"{resolution.StaffMemberId:N}",
                    cancellationToken),
            StaffTenantDestroyStage.IdentityProvisioningAnchors =>
                this.RemoveBatchAsync(
                    operation,
                    dbContext.IdentityProvisioningAnchors
                        .OrderBy(anchor => anchor.SourceKind)
                        .ThenBy(anchor => anchor.SourceId),
                    anchor =>
                        $"{(int)anchor.SourceKind}|{anchor.SourceId:N}",
                    cancellationToken),
            StaffTenantDestroyStage.StaffMembers =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.StaffMembers,
                    member => member.Id,
                    cancellationToken),
            StaffTenantDestroyStage.ProcessingRestrictionProjections =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.ProcessingRestrictionProjections,
                    projection => projection.StaffMemberId,
                    cancellationToken),
            StaffTenantDestroyStage.PropertyProjections =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.PropertyProjections,
                    property => property.Id,
                    cancellationToken),
            StaffTenantDestroyStage.ProjectionRebuildCheckpoints =>
                this.RemoveBatchAsync(
                    operation,
                    dbContext.ProjectionRebuildCheckpoints
                        .OrderBy(checkpoint => checkpoint.ProjectionName)
                        .ThenBy(checkpoint => checkpoint.RunId),
                    checkpoint =>
                        $"{LengthPrefixed(checkpoint.ProjectionName)}|" +
                        $"{checkpoint.RunId:N}",
                    cancellationToken),
            StaffTenantDestroyStage.RetentionSweepCheckpoints =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.RetentionSweepCheckpoints,
                    checkpoint => checkpoint.Id,
                    cancellationToken),
            StaffTenantDestroyStage.MemberMutationOperations =>
                this.RemoveBatchAsync(
                    operation,
                    dbContext.MemberMutationOperations
                        .OrderBy(item => item.StaffMemberId)
                        .ThenBy(item => item.Id),
                    item => $"{item.StaffMemberId:N}|{item.Id:N}",
                    cancellationToken),
            _ => throw new InvalidDataException(
                "The Staff tenant destruction stage is invalid.")
        };

    private Task<bool> RemoveGuidBatchAsync<TEntity>(
        StaffTenantDestroyOperation operation,
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
        StaffTenantDestroyOperation operation,
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
        await dbContext.RetentionAnonymisationReceipts
            .AnyAsync(cancellationToken).ConfigureAwait(false) ||
        await dbContext.AnonymisationReceipts.AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.AnonymisationRestoreReceipts
            .AnyAsync(cancellationToken).ConfigureAwait(false) ||
        await dbContext.AnonymisationTombstones.AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.DataHoldReceipts.AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.EmploymentGovernanceChangeReceipts
            .AnyAsync(cancellationToken).ConfigureAwait(false) ||
        await dbContext.ProcessingRestrictionReceipts
            .AnyAsync(cancellationToken).ConfigureAwait(false) ||
        await dbContext.DataRightsCorrectionReceipts
            .AnyAsync(cancellationToken).ConfigureAwait(false) ||
        await dbContext.PropertyAssignments.AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.DataHolds.AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.ProcessingRestrictions.AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.EmploymentGovernance.AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.RetentionExecutions.AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.OperationLocks.AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.IdentityProvisioningAnchorResolutions
            .AnyAsync(cancellationToken).ConfigureAwait(false) ||
        await dbContext.IdentityProvisioningAnchors
            .AnyAsync(cancellationToken).ConfigureAwait(false) ||
        await dbContext.StaffMembers.AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.ProcessingRestrictionProjections
            .AnyAsync(cancellationToken).ConfigureAwait(false) ||
        await dbContext.PropertyProjections.AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.ProjectionRebuildCheckpoints
            .AnyAsync(cancellationToken).ConfigureAwait(false) ||
        await dbContext.RetentionSweepCheckpoints.AnyAsync(cancellationToken)
            .ConfigureAwait(false) ||
        await dbContext.MemberMutationOperations.AnyAsync(cancellationToken)
            .ConfigureAwait(false);

    private static void EnsureBatchRecorded(
        StaffTenantDestroyOperation operation,
        string[] keys,
        bool stageCompleted,
        DateTimeOffset recordedAtUtc)
    {
        StaffTenantDestroyStage stage = operation.Stage;
        string canonicalKeys = string.Concat(keys.Select(LengthPrefixed));
        string keysSha256 = StaffTenantLifecycleHashes.Sha256(
            "bunkfy-staff-tenant-destroy-keys/v1|" +
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
                "Staff tenant destruction batch progress is invalid.");
        }
    }

    private static void EnsureStageAdvanced(
        StaffTenantDestroyOperation operation,
        DateTimeOffset recordedAtUtc)
    {
        if (!operation.AdvanceEmptyStage(recordedAtUtc))
        {
            throw new InvalidDataException(
                "Staff tenant destruction stage progress is invalid.");
        }
    }

    private static string LengthPrefixed(string value) =>
        $"{value.Length.ToString(CultureInfo.InvariantCulture)}:{value}";
}
