namespace BunkFy.Modules.Workspaces.Persistence.TenantTermination;

using System.Globalization;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Domain.DataRights;
using BunkFy.Modules.Workspaces.Domain.Termination;
using Microsoft.EntityFrameworkCore;

internal sealed partial class WorkspaceTenantDestructionOwner
{
    private Task<bool> RemoveCurrentStageAsync(
        WorkspaceTenantDestroyOperation operation,
        string tenantId,
        CancellationToken cancellationToken) =>
        operation.Stage switch
        {
            WorkspaceTenantDestroyStage.OutboxMessages =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.OutboxMessages
                        .IgnoreQueryFilters()
                        .Where(message => message.ScopeId == tenantId),
                    message => message.Id,
                    cancellationToken),
            WorkspaceTenantDestroyStage.InboxMessages =>
                this.RemoveBatchAsync(
                    operation,
                    dbContext.InboxMessages
                        .IgnoreQueryFilters()
                        .Where(message => message.ScopeId == tenantId)
                        .OrderBy(message => message.Id)
                        .ThenBy(message => message.Handler),
                    message =>
                        $"{message.Id:N}|{LengthPrefixed(message.Handler)}",
                    cancellationToken),
            WorkspaceTenantDestroyStage.ProjectionRebuildCheckpoints =>
                this.RemoveBatchAsync(
                    operation,
                    dbContext.ProjectionRebuildCheckpoints
                        .IgnoreQueryFilters()
                        .Where(checkpoint => checkpoint.ScopeId == tenantId)
                        .OrderBy(checkpoint => checkpoint.RunId)
                        .ThenBy(checkpoint => checkpoint.ProjectionName),
                    checkpoint =>
                        $"{checkpoint.RunId:N}|" +
                        LengthPrefixed(checkpoint.ProjectionName),
                    cancellationToken),
            WorkspaceTenantDestroyStage.PropertyProjections =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.PropertyProjections
                        .IgnoreQueryFilters()
                        .Where(projection => projection.ScopeId == tenantId),
                    projection => projection.Id,
                    cancellationToken),
            WorkspaceTenantDestroyStage.OnboardingCorrectionReceipts =>
                this.RemoveOnboardingReceiptBatchAsync(
                    operation,
                    tenantId,
                    cancellationToken),
            WorkspaceTenantDestroyStage.OnboardingRestrictionReceipts =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.StaffOnboardingProcessingRestrictionReceipts
                        .IgnoreQueryFilters()
                        .Where(receipt => receipt.ScopeId == tenantId),
                    receipt => receipt.Id,
                    cancellationToken),
            WorkspaceTenantDestroyStage.OnboardingRestrictionProjections =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.StaffOnboardingProcessingRestrictionProjections
                        .IgnoreQueryFilters()
                        .Where(projection => projection.ScopeId == tenantId),
                    projection => projection.ApplicationId,
                    cancellationToken),
            WorkspaceTenantDestroyStage.OnboardingRestrictions =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.StaffOnboardingProcessingRestrictions
                        .IgnoreQueryFilters()
                        .Where(restriction => restriction.ScopeId == tenantId),
                    restriction => restriction.Id,
                    cancellationToken),
            WorkspaceTenantDestroyStage.AccessPlanProperties =>
                this.RemoveBatchAsync(
                    operation,
                    dbContext.StaffAccessPlanProperties
                        .IgnoreQueryFilters()
                        .Where(property => property.ScopeId == tenantId)
                        .OrderBy(property => property.PlanId)
                        .ThenBy(property => property.PropertyId),
                    property =>
                        $"{property.PlanId:N}|{property.PropertyId:N}",
                    cancellationToken),
            WorkspaceTenantDestroyStage.AccessPlans =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.StaffAccessPlans
                        .IgnoreQueryFilters()
                        .Where(plan => plan.ScopeId == tenantId),
                    plan => plan.Id,
                    cancellationToken),
            WorkspaceTenantDestroyStage.AccessProfileSnapshots =>
                this.RemoveAccessProfileSnapshotBatchAsync(
                    operation,
                    tenantId,
                    cancellationToken),
            WorkspaceTenantDestroyStage.AccessProcesses =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.StaffAccessProcesses
                        .IgnoreQueryFilters()
                        .Where(process => process.ScopeId == tenantId),
                    process => process.Id,
                    cancellationToken),
            WorkspaceTenantDestroyStage.OnboardingApplications =>
                this.RemoveOnboardingStageBatchAsync(
                    operation,
                    tenantId,
                    cancellationToken),
            WorkspaceTenantDestroyStage.RetentionCorrelationReceipts =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.StaffRetentionCorrelationReceipts
                        .IgnoreQueryFilters()
                        .Where(receipt => receipt.ScopeId == tenantId),
                    receipt => receipt.Id,
                    cancellationToken),
            WorkspaceTenantDestroyStage.AnonymisationRestoreReceipts =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.StaffCorrelationAnonymisationRestoreReceipts
                        .IgnoreQueryFilters()
                        .Where(receipt => receipt.ScopeId == tenantId),
                    receipt => receipt.Id,
                    cancellationToken),
            WorkspaceTenantDestroyStage.AnonymisationTombstones =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.StaffCorrelationAnonymisationTombstones
                        .IgnoreQueryFilters()
                        .Where(tombstone => tombstone.ScopeId == tenantId),
                    tombstone => tombstone.Id,
                    cancellationToken),
            WorkspaceTenantDestroyStage.AnonymisationReceipts =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.StaffCorrelationAnonymisationReceipts
                        .IgnoreQueryFilters()
                        .Where(receipt => receipt.ScopeId == tenantId),
                    receipt => receipt.Id,
                    cancellationToken),
            WorkspaceTenantDestroyStage.TerminationFenceReceipts =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.WorkspaceTerminationFenceReceipts
                        .IgnoreQueryFilters()
                        .Where(receipt => receipt.ScopeId == tenantId),
                    receipt => receipt.Id,
                    cancellationToken),
            WorkspaceTenantDestroyStage.HistoricalTerminationFences =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.WorkspaceTerminationFences
                        .IgnoreQueryFilters()
                        .Where(fence =>
                            fence.ScopeId == tenantId &&
                            fence.Id != operation.FenceId),
                    fence => fence.Id,
                    cancellationToken),
            WorkspaceTenantDestroyStage.StaffOnboardingRetentionExecutions =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.StaffOnboardingRetentionExecutions
                        .IgnoreQueryFilters()
                        .Where(execution => execution.ScopeId == tenantId),
                    execution => execution.Id,
                    cancellationToken),
            WorkspaceTenantDestroyStage.SweepCheckpoints =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.StaffIdentityAnchorSweepCheckpoints
                        .IgnoreQueryFilters()
                        .Where(checkpoint =>
                            checkpoint.ScopeId == tenantId),
                    checkpoint => checkpoint.Id,
                    cancellationToken),
            WorkspaceTenantDestroyStage.HistoricalNoProvisionReceipts =>
                this.RemoveGuidBatchAsync(
                    operation,
                    dbContext.StaffHistoricalNoProvisionReceipts
                        .IgnoreQueryFilters()
                        .Where(receipt => receipt.ScopeId == tenantId),
                    receipt => receipt.Id,
                    cancellationToken),
            _ => throw new InvalidDataException(
                "The Workspaces tenant destruction stage is invalid.")
        };

    private Task<bool> RemoveGuidBatchAsync<TEntity>(
        WorkspaceTenantDestroyOperation operation,
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

    private async Task<bool> RemoveOnboardingStageBatchAsync(
        WorkspaceTenantDestroyOperation operation,
        string tenantId,
        CancellationToken cancellationToken)
    {
        WorkspaceStaffDeferredClaimWithdrawal[] loaded = await dbContext
            .StaffDeferredClaimWithdrawals
            .IgnoreQueryFilters()
            .Where(withdrawal => withdrawal.ScopeId == tenantId)
            .OrderBy(withdrawal => withdrawal.Id)
            .Take(operation.BatchSize + 1)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (loaded.Length > 0)
        {
            WorkspaceStaffDeferredClaimWithdrawal[] selected = loaded
                .Take(operation.BatchSize)
                .ToArray();
            dbContext.RemoveRange(selected);
            EnsureBatchRecorded(
                operation,
                selected
                    .Select(withdrawal => $"deferred:{withdrawal.Id:N}")
                    .ToArray(),
                stageCompleted: false,
                clock.UtcNow);
            return true;
        }

        return await this.RemoveGuidBatchAsync(
            operation,
            dbContext.StaffOnboardingApplications
                .IgnoreQueryFilters()
                .Where(application => application.ScopeId == tenantId),
            application => application.Id,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> RemoveOnboardingReceiptBatchAsync(
        WorkspaceTenantDestroyOperation operation,
        string tenantId,
        CancellationToken cancellationToken)
    {
        WorkspaceStaffOnboardingRecoveryReceipt[] recoveryReceipts =
            await dbContext.StaffOnboardingRecoveryReceipts
                .IgnoreQueryFilters()
                .Where(receipt => receipt.ScopeId == tenantId)
                .OrderBy(receipt => receipt.Id)
                .Take(operation.BatchSize + 1)
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
        if (recoveryReceipts.Length > 0)
        {
            WorkspaceStaffOnboardingRecoveryReceipt[] selected =
                recoveryReceipts.Take(operation.BatchSize).ToArray();
            dbContext.RemoveRange(selected);
            EnsureBatchRecorded(
                operation,
                selected.Select(receipt =>
                    $"recovery:{receipt.Id:N}").ToArray(),
                stageCompleted: false,
                clock.UtcNow);
            return true;
        }

        WorkspaceStaffOnboardingCorrectionReceipt[] correctionReceipts =
            await dbContext.StaffOnboardingCorrectionReceipts
                .IgnoreQueryFilters()
                .Where(receipt => receipt.ScopeId == tenantId)
                .OrderBy(receipt => receipt.Id)
                .Take(operation.BatchSize + 1)
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
        if (correctionReceipts.Length > 0)
        {
            WorkspaceStaffOnboardingCorrectionReceipt[] selected =
                correctionReceipts.Take(operation.BatchSize).ToArray();
            dbContext.RemoveRange(selected);
            EnsureBatchRecorded(
                operation,
                selected.Select(receipt =>
                    $"correction:{receipt.Id:N}").ToArray(),
                correctionReceipts.Length <= operation.BatchSize,
                clock.UtcNow);
            return true;
        }

        EnsureStageAdvanced(operation, clock.UtcNow);
        return false;
    }

    private async Task<bool> RemoveBatchAsync<TEntity>(
        WorkspaceTenantDestroyOperation operation,
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

    private async Task<bool> RemoveAccessProfileSnapshotBatchAsync(
        WorkspaceTenantDestroyOperation operation,
        string tenantId,
        CancellationToken cancellationToken)
    {
        AccessProfileSnapshotRemovalRow[] loaded = await (
            from process in dbContext.StaffAccessProcesses
                .IgnoreQueryFilters()
                .AsNoTracking()
            where process.ScopeId == tenantId
            from snapshot in process.ProfileSnapshots
            orderby process.Id, snapshot.ProfileId, snapshot.AssignmentScope
            select new AccessProfileSnapshotRemovalRow(
                process.Id,
                snapshot.ProfileId,
                snapshot.AssignmentScope))
            .Take(operation.BatchSize + 1)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (loaded.Length == 0)
        {
            EnsureStageAdvanced(operation, clock.UtcNow);
            return false;
        }

        AccessProfileSnapshotRemovalRow[] selected = loaded
            .Take(operation.BatchSize)
            .ToArray();
        Guid[] processIds = selected
            .Select(row => row.ProcessId)
            .Distinct()
            .ToArray();
        WorkspaceStaffAccessProcess[] owners = await dbContext
            .StaffAccessProcesses
            .IgnoreQueryFilters()
            .Include(process => process.ProfileSnapshots)
            .Where(process =>
                process.ScopeId == tenantId &&
                processIds.Contains(process.Id))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        HashSet<AccessProfileSnapshotRemovalRow> selectedKeys =
            selected.ToHashSet();
        WorkspaceStaffAccessProfileSnapshot[] entities = owners
            .SelectMany(process => process.ProfileSnapshots.Select(snapshot =>
                new
                {
                    Entity = snapshot,
                    Key = new AccessProfileSnapshotRemovalRow(
                        process.Id,
                        snapshot.ProfileId,
                        snapshot.AssignmentScope)
                }))
            .Where(candidate => selectedKeys.Contains(candidate.Key))
            .Select(candidate => candidate.Entity)
            .ToArray();
        if (entities.Length != selected.Length)
        {
            throw new InvalidDataException(
                "Workspaces access profile snapshot batch changed during selection.");
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
        WorkspaceTenantDestroyOperation operation,
        string tenantId,
        CancellationToken cancellationToken)
    {
        bool hasOwnedRecords =
            await dbContext.OutboxMessages
                .IgnoreQueryFilters()
                .AnyAsync(message => message.ScopeId == tenantId, cancellationToken)
                .ConfigureAwait(false) ||
            await dbContext.InboxMessages
                .IgnoreQueryFilters()
                .AnyAsync(message => message.ScopeId == tenantId, cancellationToken)
                .ConfigureAwait(false) ||
            await dbContext.ProjectionRebuildCheckpoints
                .IgnoreQueryFilters()
                .AnyAsync(checkpoint => checkpoint.ScopeId == tenantId, cancellationToken)
                .ConfigureAwait(false) ||
            await dbContext.PropertyProjections
                .IgnoreQueryFilters()
                .AnyAsync(projection => projection.ScopeId == tenantId, cancellationToken)
                .ConfigureAwait(false) ||
            await dbContext.StaffOnboardingCorrectionReceipts
                .IgnoreQueryFilters()
                .AnyAsync(receipt => receipt.ScopeId == tenantId, cancellationToken)
                .ConfigureAwait(false) ||
            await dbContext.StaffOnboardingRecoveryReceipts
                .IgnoreQueryFilters()
                .AnyAsync(receipt => receipt.ScopeId == tenantId, cancellationToken)
                .ConfigureAwait(false) ||
            await dbContext.StaffOnboardingProcessingRestrictionReceipts
                .IgnoreQueryFilters()
                .AnyAsync(receipt => receipt.ScopeId == tenantId, cancellationToken)
                .ConfigureAwait(false) ||
            await dbContext.StaffOnboardingProcessingRestrictionProjections
                .IgnoreQueryFilters()
                .AnyAsync(projection => projection.ScopeId == tenantId, cancellationToken)
                .ConfigureAwait(false) ||
            await dbContext.StaffOnboardingProcessingRestrictions
                .IgnoreQueryFilters()
                .AnyAsync(restriction => restriction.ScopeId == tenantId, cancellationToken)
                .ConfigureAwait(false) ||
            await dbContext.StaffAccessPlanProperties
                .IgnoreQueryFilters()
                .AnyAsync(property => property.ScopeId == tenantId, cancellationToken)
                .ConfigureAwait(false) ||
            await dbContext.StaffAccessPlans
                .IgnoreQueryFilters()
                .AnyAsync(plan => plan.ScopeId == tenantId, cancellationToken)
                .ConfigureAwait(false) ||
            await dbContext.StaffAccessProcesses
                .IgnoreQueryFilters()
                .Where(process => process.ScopeId == tenantId)
                .SelectMany(process => process.ProfileSnapshots)
                .AnyAsync(cancellationToken)
                .ConfigureAwait(false) ||
            await dbContext.StaffAccessProcesses
                .IgnoreQueryFilters()
                .AnyAsync(process => process.ScopeId == tenantId, cancellationToken)
                .ConfigureAwait(false) ||
            await dbContext.StaffOnboardingApplications
                .IgnoreQueryFilters()
                .AnyAsync(application => application.ScopeId == tenantId, cancellationToken)
                .ConfigureAwait(false) ||
            await dbContext.StaffDeferredClaimWithdrawals
                .IgnoreQueryFilters()
                .AnyAsync(withdrawal => withdrawal.ScopeId == tenantId, cancellationToken)
                .ConfigureAwait(false) ||
            await dbContext.StaffRetentionCorrelationReceipts
                .IgnoreQueryFilters()
                .AnyAsync(receipt => receipt.ScopeId == tenantId, cancellationToken)
                .ConfigureAwait(false) ||
            await dbContext.StaffOnboardingRetentionExecutions
                .IgnoreQueryFilters()
                .AnyAsync(execution => execution.ScopeId == tenantId, cancellationToken)
                .ConfigureAwait(false) ||
            await dbContext.StaffCorrelationAnonymisationRestoreReceipts
                .IgnoreQueryFilters()
                .AnyAsync(receipt => receipt.ScopeId == tenantId, cancellationToken)
                .ConfigureAwait(false) ||
            await dbContext.StaffCorrelationAnonymisationTombstones
                .IgnoreQueryFilters()
                .AnyAsync(tombstone => tombstone.ScopeId == tenantId, cancellationToken)
                .ConfigureAwait(false) ||
            await dbContext.StaffCorrelationAnonymisationReceipts
                .IgnoreQueryFilters()
                .AnyAsync(receipt => receipt.ScopeId == tenantId, cancellationToken)
                .ConfigureAwait(false) ||
            await dbContext.WorkspaceTerminationFenceReceipts
                .IgnoreQueryFilters()
                .AnyAsync(receipt => receipt.ScopeId == tenantId, cancellationToken)
                .ConfigureAwait(false) ||
            await dbContext.StaffIdentityAnchorSweepCheckpoints
                .IgnoreQueryFilters()
                .AnyAsync(
                    checkpoint => checkpoint.ScopeId == tenantId,
                    cancellationToken)
                .ConfigureAwait(false) ||
            await dbContext.StaffHistoricalNoProvisionReceipts
                .IgnoreQueryFilters()
                .AnyAsync(
                    receipt => receipt.ScopeId == tenantId,
                    cancellationToken)
                .ConfigureAwait(false);
        if (hasOwnedRecords)
        {
            return true;
        }

        WorkspaceTerminationFence[] fences = await dbContext
            .WorkspaceTerminationFences
            .IgnoreQueryFilters()
            .Where(fence => fence.ScopeId == tenantId)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        return fences.Length != 1 ||
            fences[0].Id != operation.FenceId ||
            fences[0].State != WorkspaceTerminationFenceState.DestructionStarted ||
            fences[0].Version != operation.SelectedFenceVersion + 1;
    }

    private static void EnsureBatchRecorded(
        WorkspaceTenantDestroyOperation operation,
        string[] keys,
        bool stageCompleted,
        DateTimeOffset recordedAtUtc)
    {
        WorkspaceTenantDestroyStage stage = operation.Stage;
        string canonicalKeys = string.Concat(keys.Select(LengthPrefixed));
        string keysSha256 = WorkspaceTenantLifecycleHashes.Sha256(
            "bunkfy-workspaces-tenant-destroy-keys/v1|" +
            $"{(int)stage}|" +
            $"{keys.Length.ToString(CultureInfo.InvariantCulture)}|" +
            canonicalKeys);
        if (!operation.RecordBatch(
                stage,
                keys.Length,
                keysSha256,
                stageCompleted,
                recordedAtUtc))
        {
            throw new InvalidDataException(
                "Workspaces tenant destruction batch progress is invalid.");
        }
    }

    private static void EnsureStageAdvanced(
        WorkspaceTenantDestroyOperation operation,
        DateTimeOffset recordedAtUtc)
    {
        if (!operation.AdvanceEmptyStage(recordedAtUtc))
        {
            throw new InvalidDataException(
                "Workspaces tenant destruction stage progress is invalid.");
        }
    }

    private static string LengthPrefixed(string value) =>
        $"{value.Length.ToString(CultureInfo.InvariantCulture)}:{value}";

    private sealed record AccessProfileSnapshotRemovalRow(
        Guid ProcessId,
        Guid ProfileId,
        string AssignmentScope)
    {
        public string ProofKey =>
            $"{this.ProcessId:N}|{this.ProfileId:N}|" +
            LengthPrefixed(this.AssignmentScope);
    }
}
