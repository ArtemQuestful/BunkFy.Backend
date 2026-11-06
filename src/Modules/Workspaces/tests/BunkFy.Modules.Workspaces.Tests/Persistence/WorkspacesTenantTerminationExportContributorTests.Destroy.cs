namespace BunkFy.Modules.Workspaces.Tests;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Persistence;
using BunkFy.Modules.Workspaces.Persistence.TenantTermination;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

public sealed partial class WorkspacesTenantTerminationExportContributorTests
{
    [Fact]
    public async Task Destroy_resumes_to_completion_and_exactly_replays()
    {
        await using WorkspacesDbContext context = CreateContext();
        SeedGraph(context);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        SeedFence(context);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        WorkspaceTenantDestructionOwner owner = new(
            context,
            new TestScopeContext(),
            new TestClock());
        TenantTerminationContributionRequest request = DestroyRequest();

        TenantTerminationContributionResult result =
            await CompleteDestroyAsync(owner, request);

        Assert.Equal(
            TenantTerminationContributionStatus.Completed,
            result.Status);
        Assert.Equal("workspace.termination.destroyed", result.ResultCode);
        Assert.Equal(13, result.AffectedCount);
        Assert.Equal(1, result.SelectedProofRevision);
        Assert.Equal(3, result.ResultingProofRevision);
        Assert.Empty(await context.TenantDestroyOperations.ToListAsync());
        WorkspaceTenantDestroyReceipt receipt =
            await context.TenantDestroyReceipts.SingleAsync();
        Assert.Equal(result.AffectedCount, receipt.RemovedRecordCount);
        Assert.False(await HasOwnerRecordsAsync(context));
        Assert.False(await context.TryAdmitMessageMutationAsync(
            TenantId,
            CancellationToken.None));

        TenantTerminationContributionResult replay =
            await owner.ExecuteAsync(request, CancellationToken.None);
        Assert.Equal(result, replay);

        TenantTerminationContributionResult conflict =
            await owner.ExecuteAsync(
                request with { ExecutingActorId = "other:executor" },
                CancellationToken.None);
        Assert.Equal(
            TenantTerminationContributionStatus.Failed,
            conflict.Status);
        Assert.Equal(
            "workspace.termination.destroy-conflict",
            conflict.ResultCode);

        context.TenantDestroyReceipts.Remove(receipt);
        InvalidOperationException receiptMutation =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => context.SaveChangesAsync());
        Assert.Contains("append-only", receiptMutation.Message);
    }

    [Fact]
    public void Destroy_progress_rejects_a_batch_above_the_persisted_bound()
    {
        WorkspaceTenantDestroyOperation operation = Assert.IsType<
            WorkspaceTenantDestroyOperation>(
            WorkspaceTenantDestroyOperation.TryCreate(
                Guid.NewGuid(),
                TenantId,
                Digest,
                Guid.NewGuid(),
                selectedFenceVersion: 4,
                WorkspaceTenantDestroyOperation.MaximumBatchSize,
                Now));

        Assert.False(operation.RecordBatch(
            WorkspaceTenantDestroyStage.OutboxMessages,
            WorkspaceTenantDestroyOperation.MaximumBatchSize + 1,
            Digest,
            stageCompleted: false,
            Now.AddMinutes(1)));
        Assert.Equal(0, operation.RemovedRecordCount);
        Assert.Equal(0, operation.CompletedBatchCount);

        Assert.True(operation.RecordBatch(
            WorkspaceTenantDestroyStage.OutboxMessages,
            WorkspaceTenantDestroyOperation.MaximumBatchSize,
            Digest,
            stageCompleted: false,
            Now.AddMinutes(1)));
        Assert.Equal(
            WorkspaceTenantDestroyOperation.MaximumBatchSize,
            operation.RemovedRecordCount);
        Assert.Equal(1, operation.CompletedBatchCount);
    }

    [Fact]
    public void Destroy_stage_ordinals_preserve_the_existing_protocol_and_append_receipts_after_checkpoints()
    {
        Assert.Equal(
            Enumerable.Range(1, 20),
            Enum.GetValues<WorkspaceTenantDestroyStage>()
                .Where(stage => stage is >=
                    WorkspaceTenantDestroyStage.OutboxMessages and <=
                    WorkspaceTenantDestroyStage.StaffOnboardingRetentionExecutions)
                .Select(stage => (int)stage));
        Assert.Equal(
            22,
            (int)WorkspaceTenantDestroyStage.SweepCheckpoints);
        Assert.Equal(
            23,
            (int)WorkspaceTenantDestroyStage
                .HistoricalNoProvisionReceipts);
        Assert.Equal(20, (int)WorkspaceTenantDestroyStage.StaffOnboardingRetentionExecutions);
        Assert.Equal(21, (int)WorkspaceTenantDestroyStage.Completed);

        WorkspaceTenantDestroyOperation operation = Assert.IsType<
            WorkspaceTenantDestroyOperation>(
            WorkspaceTenantDestroyOperation.TryCreate(
                Guid.NewGuid(),
                TenantId,
                Digest,
                Guid.NewGuid(),
                selectedFenceVersion: 4,
                WorkspaceTenantDestroyOperation.MaximumBatchSize,
                Now));
        for (int stage = 1; stage <= 20; stage++)
        {
            Assert.Equal(stage, (int)operation.Stage);
            Assert.True(operation.AdvanceEmptyStage(
                Now.AddMinutes(stage)));
        }

        Assert.Equal(
            WorkspaceTenantDestroyStage.SweepCheckpoints,
            operation.Stage);
        Assert.True(operation.AdvanceEmptyStage(Now.AddMinutes(21)));
        Assert.Equal(
            WorkspaceTenantDestroyStage.HistoricalNoProvisionReceipts,
            operation.Stage);
        Assert.True(operation.AdvanceEmptyStage(Now.AddMinutes(22)));
        Assert.Equal(WorkspaceTenantDestroyStage.Completed, operation.Stage);
        Assert.False(operation.AdvanceEmptyStage(Now.AddMinutes(23)));

        using WorkspacesDbContext context = CreateContext();
        IEntityType entity = context.GetService<IDesignTimeModel>().Model
            .FindEntityType(
                typeof(WorkspaceTenantDestroyOperation))!;
        ICheckConstraint constraint =
            Assert.Single(
                entity.GetCheckConstraints(),
                candidate => candidate.Name ==
                    "CK_workspaces_tenant_destroy_operation_progress");
        Assert.Contains(
            "\"Stage\" BETWEEN 1 AND 23",
            constraint.Sql,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(501, 3)]
    public async Task Destroy_removes_historical_receipts_in_bounded_batches_and_preserves_the_sentinel(
        int receiptCount,
        int expectedCompletedBatchCount)
    {
        const string sentinelTenantId =
            "10000000-0000-0000-0000-000000000099";
        string databaseName = Guid.NewGuid().ToString("N");
        InMemoryDatabaseRoot databaseRoot = new();
        await using WorkspacesDbContext tenant = CreateContext(
            databaseName,
            TenantId,
            databaseRoot);
        await using WorkspacesDbContext sentinel = CreateContext(
            databaseName,
            sentinelTenantId,
            databaseRoot);

        tenant.StaffHistoricalNoProvisionReceipts.AddRange(
            Enumerable.Range(1, receiptCount)
                .Select(index => CreateHistoricalReceipt(
                    index,
                    TenantId)));
        sentinel.StaffHistoricalNoProvisionReceipts.Add(
            CreateHistoricalReceipt(10_001, sentinelTenantId));
        await tenant.SaveChangesAsync();
        await sentinel.SaveChangesAsync();
        tenant.ChangeTracker.Clear();
        sentinel.ChangeTracker.Clear();

        SeedFence(tenant);
        await tenant.SaveChangesAsync();
        tenant.ChangeTracker.Clear();
        WorkspaceTenantDestructionOwner owner = new(
            tenant,
            new TestScopeContext(),
            new TestClock());

        TenantTerminationContributionResult result =
            await CompleteDestroyAsync(owner, DestroyRequest());

        Assert.Equal(
            TenantTerminationContributionStatus.Completed,
            result.Status);
        Assert.Equal(receiptCount + 1, result.AffectedCount);
        Assert.Empty(await tenant.StaffHistoricalNoProvisionReceipts
            .IgnoreQueryFilters()
            .Where(receipt => receipt.ScopeId == TenantId)
            .ToListAsync());
        sentinel.ChangeTracker.Clear();
        Assert.Equal(
            10_001,
            Assert.Single(await sentinel
                .StaffHistoricalNoProvisionReceipts
                .IgnoreQueryFilters()
                .ToListAsync()).OrganizationsSourceVersion);
        WorkspaceTenantDestroyReceipt receipt =
            await tenant.TenantDestroyReceipts.SingleAsync();
        Assert.Equal(receiptCount + 1, receipt.RemovedRecordCount);
        Assert.Equal(
            expectedCompletedBatchCount,
            receipt.CompletedBatchCount);
        Assert.True(receipt.BatchSize <=
            WorkspaceTenantDestroyOperation.MaximumBatchSize);
    }

    [Fact]
    public async Task Destroy_removes_an_active_sweep_checkpoint_and_preserves_the_sentinel_tenant()
    {
        const string sentinelTenantId =
            "10000000-0000-0000-0000-000000000099";
        string databaseName = Guid.NewGuid().ToString("N");
        InMemoryDatabaseRoot databaseRoot = new();
        await using WorkspacesDbContext tenant = CreateContext(
            databaseName,
            TenantId,
            databaseRoot);
        await using WorkspacesDbContext sentinel = CreateContext(
            databaseName,
            sentinelTenantId,
            databaseRoot);
        Guid checkpointId =
            Guid.Parse("17000000-0000-0000-0000-000000000001");
        Guid sentinelCheckpointId =
            Guid.Parse("17000000-0000-0000-0000-000000000099");
        WorkspaceStaffIdentityAnchorSweepCheckpoint active =
            WorkspaceStaffIdentityAnchorSweepCheckpoint.Create(
                checkpointId,
                TenantId,
                FrozenAtUtc.AddHours(-3)).Value;
        Assert.True(active.BeginCycle(
            Guid.Parse("18000000-0000-0000-0000-000000000001"),
            upperOrdinal: 7,
            Guid.Parse("19000000-0000-0000-0000-000000000001"),
            FrozenAtUtc.AddHours(-2)).IsSuccess);
        tenant.StaffIdentityAnchorSweepCheckpoints.Add(active);
        await tenant.SaveChangesAsync();
        tenant.ChangeTracker.Clear();

        WorkspaceStaffIdentityAnchorSweepCheckpoint preserved =
            WorkspaceStaffIdentityAnchorSweepCheckpoint.Create(
                sentinelCheckpointId,
                sentinelTenantId,
                FrozenAtUtc.AddHours(-3)).Value;
        sentinel.StaffIdentityAnchorSweepCheckpoints.Add(preserved);
        await sentinel.SaveChangesAsync();
        sentinel.ChangeTracker.Clear();

        SeedFence(tenant);
        await tenant.SaveChangesAsync();
        tenant.ChangeTracker.Clear();
        WorkspaceTenantDestructionOwner owner = new(
            tenant,
            new TestScopeContext(),
            new TestClock());

        TenantTerminationContributionResult result =
            await CompleteDestroyAsync(owner, DestroyRequest());

        Assert.Equal(
            TenantTerminationContributionStatus.Completed,
            result.Status);
        Assert.Equal(2, result.AffectedCount);
        Assert.Empty(await tenant.StaffIdentityAnchorSweepCheckpoints
            .ToListAsync());
        sentinel.ChangeTracker.Clear();
        Assert.Equal(
            sentinelCheckpointId,
            (await sentinel.StaffIdentityAnchorSweepCheckpoints
                .SingleAsync()).Id);
        WorkspaceTenantDestroyReceipt receipt =
            await tenant.TenantDestroyReceipts.SingleAsync();
        Assert.Equal(2, receipt.RemovedRecordCount);
    }

    private static async Task<TenantTerminationContributionResult>
        CompleteDestroyAsync(
            WorkspaceTenantDestructionOwner owner,
            TenantTerminationContributionRequest request)
    {
        TenantTerminationContributionResult result =
            await owner.ExecuteAsync(request, CancellationToken.None);
        int attempts = 1;
        while (result.Status ==
                TenantTerminationContributionStatus.RetryRequired &&
            attempts < 30)
        {
            result = await owner.ExecuteAsync(
                request,
                CancellationToken.None);
            attempts++;
        }

        return result;
    }

    private static TenantTerminationContributionRequest DestroyRequest() =>
        new(
            TenantTerminationContract.CurrentVersion,
            TenantId,
            ProcessId,
            CaseId,
            ApprovalRevision: 1,
            OperationRevision: 2,
            TerminationEpoch,
            TenantTerminationContributionPhase.Destroy,
            Guid.Parse("11000000-0000-0000-0000-000000000002"),
            Guid.Parse("12000000-0000-0000-0000-000000000002"),
            Digest,
            "termination-executor",
            Now.AddMinutes(5));

    private static async Task<bool> HasOwnerRecordsAsync(
        WorkspacesDbContext context) =>
        await context.OutboxMessages.IgnoreQueryFilters().AnyAsync() ||
        await context.InboxMessages.IgnoreQueryFilters().AnyAsync() ||
        await context.ProjectionRebuildCheckpoints
            .IgnoreQueryFilters().AnyAsync() ||
        await context.PropertyProjections.IgnoreQueryFilters().AnyAsync() ||
        await context.StaffOnboardingCorrectionReceipts
            .IgnoreQueryFilters().AnyAsync() ||
        await context.StaffOnboardingRecoveryReceipts
            .IgnoreQueryFilters().AnyAsync() ||
        await context.StaffOnboardingProcessingRestrictionReceipts
            .IgnoreQueryFilters().AnyAsync() ||
        await context.StaffOnboardingProcessingRestrictionProjections
            .IgnoreQueryFilters().AnyAsync() ||
        await context.StaffOnboardingProcessingRestrictions
            .IgnoreQueryFilters().AnyAsync() ||
        await context.StaffAccessPlanProperties
            .IgnoreQueryFilters().AnyAsync() ||
        await context.StaffAccessPlans.IgnoreQueryFilters().AnyAsync() ||
        await context.StaffAccessProcesses
            .IgnoreQueryFilters()
            .SelectMany(process => process.ProfileSnapshots)
            .AnyAsync() ||
        await context.StaffAccessProcesses.IgnoreQueryFilters().AnyAsync() ||
        await context.StaffOnboardingApplications
            .IgnoreQueryFilters().AnyAsync() ||
        await context.StaffDeferredClaimWithdrawals
            .IgnoreQueryFilters().AnyAsync() ||
        await context.StaffRetentionCorrelationReceipts
            .IgnoreQueryFilters().AnyAsync() ||
        await context.StaffOnboardingRetentionExecutions
            .IgnoreQueryFilters().AnyAsync() ||
        await context.StaffCorrelationAnonymisationRestoreReceipts
            .IgnoreQueryFilters().AnyAsync() ||
        await context.StaffCorrelationAnonymisationTombstones
            .IgnoreQueryFilters().AnyAsync() ||
        await context.StaffCorrelationAnonymisationReceipts
            .IgnoreQueryFilters().AnyAsync() ||
        await context.StaffIdentityAnchorSweepCheckpoints
            .IgnoreQueryFilters().AnyAsync() ||
        await context.StaffHistoricalNoProvisionReceipts
            .IgnoreQueryFilters().AnyAsync();
}
