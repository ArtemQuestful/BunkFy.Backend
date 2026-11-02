namespace BunkFy.Modules.Workspaces.Tests;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Workspaces.Persistence;
using BunkFy.Modules.Workspaces.Persistence.TenantTermination;
using Microsoft.EntityFrameworkCore;
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
            .IgnoreQueryFilters().AnyAsync();
}
