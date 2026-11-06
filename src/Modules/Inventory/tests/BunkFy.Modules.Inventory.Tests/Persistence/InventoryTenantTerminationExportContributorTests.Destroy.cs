namespace BunkFy.Modules.Inventory.Tests;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using BunkFy.Modules.Inventory.Persistence;
using BunkFy.Modules.Inventory.Persistence.Repositories;
using BunkFy.Modules.Inventory.Persistence.TenantTermination;
using Gma.Framework.Messaging.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

public sealed partial class InventoryTenantTerminationExportContributorTests
{
    [Fact]
    public async Task Destroy_resumes_to_completion_and_exactly_replays()
    {
        MutableFenceReader fences = new();
        await using InventoryDbContext context = CreateContext(fences);
        SeedGraph(context);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        fences.Current = FrozenFence();
        InventoryTenantTerminationContributor contributor = new(
            context,
            new TestScopeContext(),
            new TestClock(),
            fences);
        TenantTerminationContributionRequest request = DestroyRequest();

        TenantTerminationContributionResult result =
            await CompleteDestroyAsync(contributor, request);

        Assert.Equal(
            TenantTerminationContributionStatus.Completed,
            result.Status);
        Assert.Equal("inventory.termination.destroyed", result.ResultCode);
        Assert.Equal(13, result.AffectedCount);
        Assert.Equal(1, result.SelectedProofRevision);
        Assert.Equal(2, result.ResultingProofRevision);
        Assert.Empty(await context.TenantDestroyOperations.ToListAsync());
        InventoryTenantDestroyReceipt receipt =
            await context.TenantDestroyReceipts.SingleAsync();
        Assert.Equal(result.AffectedCount, receipt.RemovedRecordCount);
        Assert.Equal(
            InventoryTenantLifecycleStatus.Closed,
            (await context.TenantRevisions.SingleAsync()).LifecycleStatus);
        Assert.False(await HasOwnerRecordsAsync(context));

        TenantTerminationContributionResult replay =
            await contributor.ExecuteAsync(request, CancellationToken.None);
        Assert.Equal(result, replay);

        TenantTerminationContributionResult conflict =
            await contributor.ExecuteAsync(
                request with { ExecutingActorId = "other:executor" },
                CancellationToken.None);
        Assert.Equal(
            TenantTerminationContributionStatus.Failed,
            conflict.Status);
        Assert.Equal(
            "inventory.termination.destroy-conflict",
            conflict.ResultCode);

        context.TenantDestroyReceipts.Remove(receipt);
        InvalidOperationException receiptMutation =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => context.SaveChangesAsync());
        Assert.Contains("append-only", receiptMutation.Message);

        context.ChangeTracker.Clear();
        fences.Current = null;
        context.InventoryUnits.Add(CreateUnit(Guid.NewGuid()));
        InventoryOperationalAdmissionException closedFailure =
            await Assert.ThrowsAsync<InventoryOperationalAdmissionException>(
                () => context.SaveChangesAsync());
        Assert.Equal(
            InventoryOperationalAdmissionFailure.Restricted,
            closedFailure.Failure);
        Assert.False(await context.TryAdmitMessageMutationAsync(
            TenantId,
            CancellationToken.None));
    }

    [Fact]
    public async Task Destroy_preserves_foreign_same_id_amendment_decision()
    {
        const string foreignTenantId =
            "10000000-0000-0000-0000-000000000002";
        Guid sharedDecisionId =
            Guid.Parse("8a000000-0000-0000-0000-000000000001");
        InMemoryDatabaseRoot root = new();
        string databaseName = Guid.NewGuid().ToString("N");
        MutableFenceReader fences = new();
        await using InventoryDbContext context = CreateContext(
            fences,
            new TestScopeContext(),
            databaseName,
            root);
        SeedGraph(context);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        await using InventoryDbContext foreign = CreateContext(
            fences,
            new TestScopeContext(foreignTenantId),
            databaseName,
            root);
        foreign.AllocationAmendmentDecisions.Add(new(new(
            sharedDecisionId,
            foreignTenantId,
            AllocationId,
            Guid.Parse("83000000-0000-0000-0000-000000000001"),
            PropertyId,
            new string('b', 64),
            Confirmed: false,
            InventoryAllocationRejectionReason.AllocationConflict,
            AllocationVersion: null,
            FrozenAtUtc.AddDays(-1))));
        await foreign.SaveChangesAsync();
        foreign.ChangeTracker.Clear();

        fences.Current = FrozenFence();
        InventoryTenantTerminationContributor contributor = new(
            context,
            new TestScopeContext(),
            new TestClock(),
            fences);

        TenantTerminationContributionResult result =
            await CompleteDestroyAsync(contributor, DestroyRequest());

        Assert.Equal(TenantTerminationContributionStatus.Completed, result.Status);
        Assert.Equal(13, result.AffectedCount);
        Assert.Empty(await context.ManualBlockGroups.ToArrayAsync());
        Assert.Empty(await context.AllocationAmendmentDecisions.ToArrayAsync());
        InventoryAllocationAmendmentDecision preserved = await foreign
            .AllocationAmendmentDecisions
            .SingleAsync();
        Assert.Equal(foreignTenantId, preserved.ScopeId);
        Assert.Equal(sharedDecisionId, preserved.Id);
    }

    [Fact]
    public async Task Destroy_waits_for_an_active_outbox_lease_then_resumes()
    {
        MutableFenceReader fences = new();
        await using InventoryDbContext context = CreateContext(fences);
        OutboxMessage message = new(
            Guid.NewGuid(),
            "bunkfy.inventory.lifecycle-test.v1",
            "lifecycle-test",
            version: 1,
            TenantId,
            Now,
            "{}",
            Now);
        message.MarkClaimed("inventory-test-worker", Now, TimeSpan.FromMinutes(5));
        context.OutboxMessages.Add(message);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        fences.Current = FrozenFence();
        InventoryTenantTerminationContributor contributor = new(
            context,
            new TestScopeContext(),
            new TestClock(),
            fences);
        TenantTerminationContributionRequest request = DestroyRequest();

        TenantTerminationContributionResult blocked =
            await contributor.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(
            TenantTerminationContributionStatus.RetryRequired,
            blocked.Status);
        Assert.Equal(
            "inventory.termination.destroy-outbox-busy",
            blocked.ResultCode);
        Assert.Single(await context.TenantDestroyOperations.ToListAsync());
        Assert.Equal(
            InventoryTenantLifecycleStatus.Closing,
            (await context.TenantRevisions.SingleAsync()).LifecycleStatus);

        message = await context.OutboxMessages.SingleAsync();
        message.MarkProcessed(Now.AddMinutes(1));
        await context.SaveChangesAsync();

        TenantTerminationContributionResult result =
            await CompleteDestroyAsync(contributor, request);

        Assert.Equal(
            TenantTerminationContributionStatus.Completed,
            result.Status);
        Assert.Equal(1, result.AffectedCount);
    }

    [Fact]
    public void Destroy_progress_rejects_a_batch_above_the_persisted_bound()
    {
        InventoryTenantDestroyOperation operation = Assert.IsType<
            InventoryTenantDestroyOperation>(
            InventoryTenantDestroyOperation.TryCreate(
                Guid.NewGuid(),
                TenantId,
                Digest,
                selectedRevision: 4,
                InventoryTenantDestroyOperation.MaximumBatchSize,
                Now));

        Assert.False(operation.RecordBatch(
            InventoryTenantDestroyStage.OutboxMessages,
            InventoryTenantDestroyOperation.MaximumBatchSize + 1,
            Digest,
            stageCompleted: false,
            Now.AddMinutes(1)));
        Assert.Equal(0, operation.RemovedRecordCount);
        Assert.Equal(0, operation.CompletedBatchCount);

        Assert.True(operation.RecordBatch(
            InventoryTenantDestroyStage.OutboxMessages,
            InventoryTenantDestroyOperation.MaximumBatchSize,
            Digest,
            stageCompleted: false,
            Now.AddMinutes(1)));
        Assert.Equal(
            InventoryTenantDestroyOperation.MaximumBatchSize,
            operation.RemovedRecordCount);
        Assert.Equal(1, operation.CompletedBatchCount);
    }

    [Fact]
    public async Task Destroy_drains_block_group_replacement_chains_leaf_first()
    {
        MutableFenceReader fences = new();
        await using InventoryDbContext context = CreateContext(fences);
        ManualInventoryBlockGroup predecessor = CreateGroup(
            Guid.Parse("91000000-0000-0000-0000-000000000001"),
            replacesGroupId: null,
            Now);
        context.ManualBlockGroups.Add(predecessor);
        await context.SaveChangesAsync();

        ManualInventoryBlockGroup successor = CreateGroup(
            Guid.Parse("92000000-0000-0000-0000-000000000001"),
            predecessor.Id,
            Now.AddMinutes(1));
        Assert.True(predecessor.ReplaceWith(
            predecessor.Version,
            successor.Id,
            releasedBlockCount: 1,
            Now.AddMinutes(1),
            "user:owner").IsSuccess);
        context.ManualBlockGroups.Add(successor);
        await context.SaveChangesAsync();

        ManualInventoryBlockGroup leaf = CreateGroup(
            Guid.Parse("93000000-0000-0000-0000-000000000001"),
            successor.Id,
            Now.AddMinutes(2));
        Assert.True(successor.ReplaceWith(
            successor.Version,
            leaf.Id,
            releasedBlockCount: 1,
            Now.AddMinutes(2),
            "user:owner").IsSuccess);
        context.ManualBlockGroups.Add(leaf);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        fences.Current = FrozenFence();
        InventoryTenantTerminationContributor contributor = new(
            context,
            new TestScopeContext(),
            new TestClock(),
            fences);

        TenantTerminationContributionResult result =
            await CompleteDestroyAsync(contributor, DestroyRequest());

        Assert.Equal(
            TenantTerminationContributionStatus.Completed,
            result.Status);
        Assert.Equal(3, result.AffectedCount);
        Assert.Empty(await context.ManualBlockGroups.ToArrayAsync());
    }

    private static ManualInventoryBlockGroup CreateGroup(
        Guid id,
        Guid? replacesGroupId,
        DateTimeOffset createdAtUtc) => ManualInventoryBlockGroup.Create(
            id,
            TenantId,
            PropertyId,
            ManualInventoryBlockGroupTargetKind.Property,
            buildingLabel: null,
            floorLabel: null,
            roomId: null,
            inventoryUnitId: null,
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 2),
            "maintenance",
            Digest,
            Digest,
            ManualInventoryBlockGroup.CurrentMembershipDigestVersion,
            initialBlockCount: 1,
            replacesGroupId,
            createdAtUtc,
            "user:owner").Value;

    private static async Task<TenantTerminationContributionResult>
        CompleteDestroyAsync(
            InventoryTenantTerminationContributor contributor,
            TenantTerminationContributionRequest request)
    {
        TenantTerminationContributionResult result =
            await contributor.ExecuteAsync(request, CancellationToken.None);
        int attempts = 1;
        while (result.Status ==
                TenantTerminationContributionStatus.RetryRequired &&
            attempts < 40)
        {
            result = await contributor.ExecuteAsync(
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
            Guid.Parse("90000000-0000-0000-0000-000000000002"),
            Guid.Parse("a0000000-0000-0000-0000-000000000002"),
            Digest,
            "termination-executor",
            Now.AddMinutes(5));

    private static async Task<bool> HasOwnerRecordsAsync(
        InventoryDbContext context) =>
        await context.OutboxMessages.AnyAsync() ||
        await context.InboxMessages.AnyAsync() ||
        await context.AllocationAnonymisationRestoreReceipts.AnyAsync() ||
        await context.AllocationAnonymisationReceipts.AnyAsync() ||
        await context.AllocationAnonymisationTombstones.AnyAsync() ||
        await context.AllocationAmendmentDecisions.AnyAsync() ||
        await context.AllocationUnits.AnyAsync() ||
        await context.Allocations.AnyAsync() ||
        await context.ManualBlocks.AnyAsync() ||
        await context.ManualBlockGroups.AnyAsync() ||
        await context.AllocationOperationLocks.AnyAsync() ||
        await context.BedRetirements.AnyAsync() ||
        await context.RoomRetirements.AnyAsync() ||
        await context.ManagementOperations.AnyAsync() ||
        await context.RoomConfigurations.AnyAsync() ||
        await context.InventoryUnits.AnyAsync() ||
        await context.BedTopology.AnyAsync() ||
        await context.RoomTopology.AnyAsync() ||
        await context.PropertyTopology.AnyAsync() ||
        await context.ProjectionRebuildCheckpoints.AnyAsync();
}
