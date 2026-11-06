namespace BunkFy.Modules.Retention.Tests.Persistence;

using System.Text.Json;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Retention.Contracts;
using BunkFy.Modules.Retention.Domain.Aggregates;
using BunkFy.Modules.Retention.Domain.Models;
using BunkFy.Modules.Retention.Persistence;
using BunkFy.Modules.Retention.Persistence.Repositories;
using BunkFy.Modules.Retention.Persistence.TenantTermination;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Messaging.Infrastructure;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Xunit;

[Trait("Category", "Unit")]
public sealed class RetentionTenantTerminationContributorTests
{
    private const string TenantId =
        "10000000-0000-0000-0000-000000000001";
    private const string Digest =
        "0123456789abcdef0123456789abcdef" +
        "0123456789abcdef0123456789abcdef";
    private static readonly Guid ProcessId =
        Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid CaseId =
        Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid TerminationEpoch =
        Guid.Parse("40000000-0000-0000-0000-000000000001");
    private static readonly Guid PropertyId =
        Guid.Parse("50000000-0000-0000-0000-000000000001");
    private static readonly Guid ExecutionId =
        Guid.Parse("60000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset FrozenAtUtc =
        new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now =
        FrozenAtUtc.AddMinutes(1);

    [Fact]
    public async Task Export_streams_authoritative_state_in_stable_order()
    {
        MutableFenceReader fences = new();
        await using RetentionDbContext context = CreateContext(fences);
        SeedState(context);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        fences.Current = FrozenFence();
        RetentionTenantTerminationContributor contributor = new(
            context,
            new TestScopeContext(),
            new TestClock(),
            fences);
        CollectingSink first = new();

        TenantTerminationContributionResult result =
            await contributor.ExportAsync(
                Request(),
                first,
                CancellationToken.None);

        Assert.Equal(
            TenantTerminationContributionStatus.Completed,
            result.Status);
        Assert.Equal("retention.termination.exported", result.ResultCode);
        Assert.Equal(3, result.AffectedCount);
        Assert.Equal(1, result.SelectedProofRevision);
        Assert.Equal(1, result.ResultingProofRevision);
        Assert.Equal(
            [
                RetentionTenantTerminationMetadata.ExecutionRecordType,
                RetentionTenantTerminationMetadata.ScheduleStateRecordType,
                RetentionTenantTerminationMetadata.RunRetryRequestRecordType
            ],
            first.Records.Select(record => record.RecordType).ToArray());
        Assert.Equal(
            "ingestion",
            Field(first.Records[0], "retention.execution-record")
                .GetProperty("ownerKey")
                .GetString());
        Assert.Equal(
            PropertyId,
            Field(first.Records[1], "retention.property-reference")
                .GetGuid());
        Assert.Equal(
            ExecutionId,
            Field(first.Records[2], "retention.run-retry-request")
                .GetProperty("runId")
                .GetGuid());
        Assert.Equal(
            [
                TenantTerminationContributionPhase.Export,
                TenantTerminationContributionPhase.Destroy
            ],
            contributor.Descriptor.PhasePlans.Select(plan => plan.Phase));
        Assert.All(
            contributor.Descriptor.PhasePlans,
            plan => Assert.Equal(
                RetentionTenantTerminationMetadata.DependencyOwnerKey,
                Assert.Single(plan.DependsOnOwnerKeys)));
        Assert.Equal(
            RetentionTenantTerminationMetadata.ExportFieldIds
                .OrderBy(field => field, StringComparer.Ordinal),
            contributor.ExportDescriptor.FieldIds);

        CollectingSink replay = new();
        TenantTerminationContributionResult replayResult =
            await contributor.ExportAsync(
                Request(),
                replay,
                CancellationToken.None);

        Assert.Equal(result.AffectedCount, replayResult.AffectedCount);
        Assert.Equal(
            first.Records.Select(Identity).ToArray(),
            replay.Records.Select(Identity).ToArray());
    }

    [Fact]
    public async Task Export_retries_without_records_for_a_different_fence()
    {
        MutableFenceReader fences = new()
        {
            Current = FrozenFence() with { Version = 2 }
        };
        await using RetentionDbContext context = CreateContext(fences);
        RetentionTenantTerminationContributor contributor = new(
            context,
            new TestScopeContext(),
            new TestClock(),
            fences);
        CollectingSink sink = new();

        TenantTerminationContributionResult result =
            await contributor.ExportAsync(
                Request(),
                sink,
                CancellationToken.None);

        Assert.Equal(
            TenantTerminationContributionStatus.RetryRequired,
            result.Status);
        Assert.Equal(
            "retention.termination.export-fence-unavailable",
            result.ResultCode);
        Assert.Empty(sink.Records);
    }

    [Fact]
    public async Task Frozen_workspace_rejects_writes_without_advancing_revision()
    {
        MutableFenceReader fences = new();
        await using RetentionDbContext context = CreateContext(fences);
        RetentionTenantProjection projection = new(
            TenantId,
            Guid.NewGuid(),
            isActive: true,
            sourceVersion: 1);
        context.TenantProjections.Add(projection);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        fences.Current = FrozenFence();
        projection = await context.TenantProjections.SingleAsync();
        projection.Apply(
            projection.OrganizationId,
            isActive: false,
            sourceVersion: 2);

        RetentionOperationalAdmissionException failure =
            await Assert.ThrowsAsync<RetentionOperationalAdmissionException>(
                () => context.SaveChangesAsync());

        Assert.Equal(
            RetentionOperationalAdmissionFailure.Restricted,
            failure.Failure);
        context.ChangeTracker.Clear();
        Assert.Equal(
            1,
            await context.TenantRevisions
                .Select(revision => revision.Revision)
                .SingleAsync());
        Assert.True(
            (await context.TenantProjections.SingleAsync()).IsActive);
    }

    [Fact]
    public async Task Operational_saves_advance_the_tenant_revision_once_each()
    {
        MutableFenceReader fences = new();
        await using RetentionDbContext context = CreateContext(fences);
        RetentionTenantProjection projection = new(
            TenantId,
            Guid.NewGuid(),
            isActive: true,
            sourceVersion: 1);
        context.TenantProjections.Add(projection);
        await context.SaveChangesAsync();

        projection.Apply(
            projection.OrganizationId,
            isActive: false,
            sourceVersion: 2);
        await context.SaveChangesAsync();

        Assert.Equal(
            2,
            await context.TenantRevisions
                .Select(revision => revision.Revision)
                .SingleAsync());
    }

    [Fact]
    public async Task Operational_save_fails_closed_when_fence_read_fails()
    {
        await using RetentionDbContext context = CreateContext(
            new ThrowingFenceReader());
        context.TenantProjections.Add(new(
            TenantId,
            Guid.NewGuid(),
            isActive: true,
            sourceVersion: 1));

        RetentionOperationalAdmissionException failure =
            await Assert.ThrowsAsync<RetentionOperationalAdmissionException>(
                () => context.SaveChangesAsync());

        Assert.Equal(
            RetentionOperationalAdmissionFailure.Unavailable,
            failure.Failure);
        context.ChangeTracker.Clear();
        Assert.Empty(await context.TenantProjections.ToListAsync());
        Assert.Empty(await context.TenantRevisions.ToListAsync());
    }

    [Fact]
    public async Task Destroy_resumes_to_completion_and_exactly_replays()
    {
        MutableFenceReader fences = new();
        await using RetentionDbContext context = CreateContext(fences);
        SeedState(context);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        fences.Current = FrozenFence();
        RetentionTenantTerminationContributor contributor = new(
            context,
            new TestScopeContext(),
            new TestClock(),
            fences);
        TenantTerminationContributionRequest request = DestroyRequest();

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

        Assert.Equal(
            TenantTerminationContributionStatus.Completed,
            result.Status);
        Assert.Equal("retention.termination.destroyed", result.ResultCode);
        Assert.Equal(7, result.AffectedCount);
        Assert.Equal(1, result.SelectedProofRevision);
        Assert.Equal(2, result.ResultingProofRevision);
        Assert.Empty(await context.TenantDestroyOperations.ToListAsync());
        RetentionTenantDestroyReceipt receipt =
            await context.TenantDestroyReceipts.SingleAsync();
        Assert.Equal(result.AffectedCount, receipt.RemovedRecordCount);
        Assert.Equal(
            RetentionTenantLifecycleStatus.Closed,
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
            "retention.termination.destroy-conflict",
            conflict.ResultCode);

        context.TenantDestroyReceipts.Remove(receipt);
        InvalidOperationException receiptMutation =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => context.SaveChangesAsync());
        Assert.Contains("append-only", receiptMutation.Message);

        context.ChangeTracker.Clear();
        fences.Current = null;
        context.TenantProjections.Add(new(
            TenantId,
            Guid.NewGuid(),
            isActive: true,
            sourceVersion: 2));
        RetentionOperationalAdmissionException closedFailure =
            await Assert.ThrowsAsync<RetentionOperationalAdmissionException>(
                () => context.SaveChangesAsync());
        Assert.Equal(
            RetentionOperationalAdmissionFailure.Restricted,
            closedFailure.Failure);
        Assert.False(await context.TryAdmitMessageMutationAsync(
            TenantId,
            CancellationToken.None));
    }

    [Fact]
    public async Task Destroy_waits_for_an_active_outbox_lease()
    {
        MutableFenceReader fences = new();
        await using RetentionDbContext context = CreateContext(fences);
        OutboxMessage message = CreateOutboxMessage(Guid.NewGuid());
        message.MarkClaimed("retention-worker", Now, TimeSpan.FromMinutes(2));
        context.OutboxMessages.Add(message);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        fences.Current = FrozenFence();
        RetentionTenantTerminationContributor contributor = new(
            context,
            new TestScopeContext(),
            new TestClock(),
            fences);

        TenantTerminationContributionResult result =
            await contributor.ExecuteAsync(
                DestroyRequest(),
                CancellationToken.None);

        Assert.Equal(
            TenantTerminationContributionStatus.RetryRequired,
            result.Status);
        Assert.Equal(
            "retention.termination.destroy-outbox-busy",
            result.ResultCode);
        Assert.Single(await context.OutboxMessages.ToListAsync());
    }

    [Fact]
    public void Destroy_progress_rejects_a_batch_above_the_persisted_bound()
    {
        RetentionTenantDestroyOperation operation = Assert.IsType<
            RetentionTenantDestroyOperation>(
            RetentionTenantDestroyOperation.TryCreate(
                Guid.NewGuid(),
                TenantId,
                Digest,
                selectedRevision: 4,
                RetentionTenantDestroyOperation.MaximumBatchSize,
                Now));

        Assert.False(operation.RecordBatch(
            RetentionTenantDestroyStage.OutboxMessages,
            RetentionTenantDestroyOperation.MaximumBatchSize + 1,
            Digest,
            stageCompleted: false,
            Now.AddMinutes(1)));
        Assert.Equal(0, operation.RemovedRecordCount);
        Assert.Equal(0, operation.CompletedBatchCount);

        Assert.True(operation.RecordBatch(
            RetentionTenantDestroyStage.OutboxMessages,
            RetentionTenantDestroyOperation.MaximumBatchSize,
            Digest,
            stageCompleted: false,
            Now.AddMinutes(1)));
        Assert.Equal(
            RetentionTenantDestroyOperation.MaximumBatchSize,
            operation.RemovedRecordCount);
        Assert.Equal(1, operation.CompletedBatchCount);
    }

    private static void SeedState(RetentionDbContext context)
    {
        RetentionExecution execution = RetentionExecution.Start(
            ExecutionId,
            TenantId,
            "ingestion",
            "raw-source-evidence",
            RetentionExecutionTargetKind.Property,
            PropertyId,
            executionPolicyVersion: 3,
            attempt: 1,
            FrozenAtUtc.AddDays(-1),
            FrozenAtUtc.AddDays(-1).AddMinutes(10)).Value;
        RetentionScheduleState schedule = new(
            execution,
            FrozenAtUtc.AddHours(1));
        Assert.True(execution.Complete(
            RetentionExecutionState.Completed,
            attempt: 1,
            scannedCount: 3,
            affectedCount: 2,
            remainingCount: 0,
            "ingestion.raw-payload.completed",
            FrozenAtUtc.AddDays(-1).AddMinutes(5),
            holdReviewDueAtUtc: null).IsSuccess);
        schedule.RecordCompleted(execution);

        RetentionRunRetryRequest retry =
            RetentionRunRetryRequest.Create(
                Guid.Parse("61000000-0000-0000-0000-000000000001"),
                Guid.Parse("62000000-0000-0000-0000-000000000001"),
                TenantId,
                ExecutionId,
                "ingestion",
                "raw-source-evidence",
                RetentionExecutionTargetKind.Property,
                PropertyId,
                executionPolicyVersion: 3,
                evidenceVersion: schedule.Version,
                FrozenAtUtc.AddMinutes(-10),
                scheduledAtUtc: null).Value;

        context.Executions.Add(execution);
        context.ScheduleStates.Add(schedule);
        context.RunRetryRequests.Add(retry);
        context.OutboxMessages.Add(CreateOutboxMessage(
            Guid.Parse("63000000-0000-0000-0000-000000000001")));
        context.InboxMessages.Add(InboxMessage.Create(
            Guid.Parse("64000000-0000-0000-0000-000000000001"),
            RetentionModuleMetadata.RunRetryRequestedHandlerName,
            "bunkfy.retention.retention-run-retry-requested.v1",
            RetentionRunRetryRequestedIntegrationEvent.EventType,
            RetentionRunRetryRequestedIntegrationEvent.EventVersion,
            TenantId,
            FrozenAtUtc.AddMinutes(-10),
            FrozenAtUtc.AddMinutes(-10)));
        context.TenantProjections.Add(new(
            TenantId,
            Guid.NewGuid(),
            isActive: true,
            sourceVersion: 1));
        context.PropertyProjections.Add(new(
            TenantId,
            PropertyId,
            isActive: true,
            topologySourceVersion: 1));
    }

    private static WorkspaceTerminationFenceSnapshot FrozenFence() =>
        new(
            ProcessId,
            TerminationEpoch,
            WorkspaceTerminationFenceState.Frozen,
            Version: 3);

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
            Guid.Parse("70000000-0000-0000-0000-000000000002"),
            Guid.Parse("80000000-0000-0000-0000-000000000002"),
            Digest,
            "termination-executor",
            Now.AddMinutes(5));

    private static TenantTerminationExportRequest Request() =>
        new(
            new TenantTerminationContributionRequest(
                TenantTerminationContract.CurrentVersion,
                TenantId,
                ProcessId,
                CaseId,
                ApprovalRevision: 1,
                OperationRevision: 2,
                TerminationEpoch,
                TenantTerminationContributionPhase.Export,
                Guid.Parse("70000000-0000-0000-0000-000000000001"),
                Guid.Parse("80000000-0000-0000-0000-000000000001"),
                Digest,
                "termination-exporter",
                Now.AddMinutes(5)),
            FreezeOperationRevision: 1,
            WorkspaceFenceRevision: 3,
            Digest,
            FrozenAtUtc);

    private static JsonElement Field(
        DataRightsExportRecord record,
        string fieldId) =>
        Assert.Single(
            record.Fields,
            field => field.FieldId == fieldId).Value;

    private static string Identity(DataRightsExportRecord record) =>
        $"{record.RecordType}|{record.RecordId:N}|{record.RecordVersion}";

    private static async Task<bool> HasOwnerRecordsAsync(
        RetentionDbContext context) =>
        await context.OutboxMessages.AnyAsync() ||
        await context.InboxMessages.AnyAsync() ||
        await context.RunRetryRequests.AnyAsync() ||
        await context.ScheduleStates.AnyAsync() ||
        await context.Executions.AnyAsync() ||
        await context.PropertyProjections.AnyAsync() ||
        await context.TenantProjections.AnyAsync();

    private static OutboxMessage CreateOutboxMessage(Guid id) => new(
        id,
        "bunkfy.retention.retention-run-retry-requested.v1",
        RetentionRunRetryRequestedIntegrationEvent.EventType,
        RetentionRunRetryRequestedIntegrationEvent.EventVersion,
        TenantId,
        FrozenAtUtc.AddMinutes(-10),
        "{}",
        FrozenAtUtc.AddMinutes(-10));

    private static RetentionDbContext CreateContext(
        IWorkspaceTerminationFenceReader fences)
    {
        DbContextOptions<RetentionDbContext> options =
            new DbContextOptionsBuilder<RetentionDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;
        return new(options, new TestScopeContext(), fences);
    }

    private sealed class CollectingSink : IDataRightsExportSink
    {
        public List<DataRightsExportRecord> Records { get; } = [];

        public ValueTask WriteAsync(
            DataRightsExportRecord record,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.Records.Add(record);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MutableFenceReader
        : IWorkspaceTerminationFenceReader
    {
        public WorkspaceTerminationFenceSnapshot? Current { get; set; }

        public Task<WorkspaceTerminationFenceSnapshot?> GetCurrentAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(this.Current);
        }
    }

    private sealed class ThrowingFenceReader
        : IWorkspaceTerminationFenceReader
    {
        public Task<WorkspaceTerminationFenceSnapshot?> GetCurrentAsync(
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Fence store unavailable.");
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => TenantId;
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}
