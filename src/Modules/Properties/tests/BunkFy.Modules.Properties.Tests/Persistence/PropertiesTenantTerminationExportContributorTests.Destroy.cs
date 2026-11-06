namespace BunkFy.Modules.Properties.Tests;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Properties.Persistence;
using BunkFy.Modules.Properties.Persistence.Repositories;
using BunkFy.Modules.Properties.Persistence.TenantTermination;
using Gma.Framework.Messaging.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

public sealed partial class PropertiesTenantTerminationExportContributorTests
{
    [Fact]
    public async Task Destroy_resumes_to_completion_and_exactly_replays()
    {
        MutableFenceReader fences = new();
        await using PropertiesDbContext context = CreateContext(fences);
        SeedGraph(context);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        fences.Current = FrozenFence();
        PropertiesTenantTerminationContributor contributor = new(
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
        Assert.Equal("properties.termination.destroyed", result.ResultCode);
        Assert.Equal(14, result.AffectedCount);
        Assert.Equal(1, result.SelectedProofRevision);
        Assert.Equal(2, result.ResultingProofRevision);
        Assert.Empty(await context.TenantDestroyOperations.ToListAsync());
        PropertiesTenantDestroyReceipt receipt =
            await context.TenantDestroyReceipts.SingleAsync();
        Assert.Equal(result.AffectedCount, receipt.RemovedRecordCount);
        Assert.Equal(
            PropertiesTenantLifecycleStatus.Closed,
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
            "properties.termination.destroy-conflict",
            conflict.ResultCode);

        context.TenantDestroyReceipts.Remove(receipt);
        InvalidOperationException receiptMutation =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => context.SaveChangesAsync());
        Assert.Contains("append-only", receiptMutation.Message);

        context.ChangeTracker.Clear();
        fences.Current = null;
        context.Properties.Add(CreateProperty(Guid.NewGuid(), "CLOSED"));
        PropertiesOperationalAdmissionException closedFailure =
            await Assert.ThrowsAsync<PropertiesOperationalAdmissionException>(
                () => context.SaveChangesAsync());
        Assert.Equal(
            PropertiesOperationalAdmissionFailure.Restricted,
            closedFailure.Failure);
        Assert.False(await context.TryAdmitMessageMutationAsync(
            TenantId,
            CancellationToken.None));
    }

    [Fact]
    public async Task Destroy_waits_for_an_active_outbox_lease_then_resumes()
    {
        MutableFenceReader fences = new();
        await using PropertiesDbContext context = CreateContext(fences);
        OutboxMessage message = new(
            Guid.NewGuid(),
            "bunkfy.properties.lifecycle-test.v1",
            "lifecycle-test",
            version: 1,
            TenantId,
            Now,
            "{}",
            Now);
        message.MarkClaimed(
            "properties-test-worker",
            Now,
            TimeSpan.FromMinutes(5));
        context.OutboxMessages.Add(message);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        fences.Current = FrozenFence();
        PropertiesTenantTerminationContributor contributor = new(
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
            "properties.termination.destroy-outbox-busy",
            blocked.ResultCode);
        Assert.Single(await context.TenantDestroyOperations.ToListAsync());
        Assert.Equal(
            PropertiesTenantLifecycleStatus.Closing,
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
        PropertiesTenantDestroyOperation operation = Assert.IsType<
            PropertiesTenantDestroyOperation>(
            PropertiesTenantDestroyOperation.TryCreate(
                Guid.NewGuid(),
                TenantId,
                Digest,
                selectedRevision: 4,
                PropertiesTenantDestroyOperation.MaximumBatchSize,
                Now));

        Assert.False(operation.RecordBatch(
            PropertiesTenantDestroyStage.OutboxMessages,
            PropertiesTenantDestroyOperation.MaximumBatchSize + 1,
            Digest,
            stageCompleted: false,
            Now.AddMinutes(1)));
        Assert.Equal(0, operation.RemovedRecordCount);
        Assert.Equal(0, operation.CompletedBatchCount);

        Assert.True(operation.RecordBatch(
            PropertiesTenantDestroyStage.OutboxMessages,
            PropertiesTenantDestroyOperation.MaximumBatchSize,
            Digest,
            stageCompleted: false,
            Now.AddMinutes(1)));
        Assert.Equal(
            PropertiesTenantDestroyOperation.MaximumBatchSize,
            operation.RemovedRecordCount);
        Assert.Equal(1, operation.CompletedBatchCount);
    }

    private static async Task<TenantTerminationContributionResult>
        CompleteDestroyAsync(
            PropertiesTenantTerminationContributor contributor,
            TenantTerminationContributionRequest request)
    {
        TenantTerminationContributionResult result =
            await contributor.ExecuteAsync(request, CancellationToken.None);
        int attempts = 1;
        while (result.Status ==
                TenantTerminationContributionStatus.RetryRequired &&
            attempts < 20)
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
        PropertiesDbContext context) =>
        await context.OutboxMessages.AnyAsync() ||
        await context.InboxMessages.AnyAsync() ||
        await context.GovernanceRevisions.AnyAsync() ||
        await context.Properties
            .SelectMany(property => property.GovernanceAcknowledgements)
            .AnyAsync() ||
        await context.Rooms.SelectMany(room => room.Beds).AnyAsync() ||
        await context.Rooms.AnyAsync() ||
        await context.PropertyMutationOperations.AnyAsync() ||
        await context.PropertyTimeZoneOperations.AnyAsync() ||
        await context.Properties.AnyAsync() ||
        await context.PropertyOperationLocks.AnyAsync() ||
        await context.RoomOperationLocks.AnyAsync();
}
