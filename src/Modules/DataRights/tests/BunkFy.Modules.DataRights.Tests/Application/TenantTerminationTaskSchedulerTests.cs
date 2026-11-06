namespace BunkFy.Modules.DataRights.Tests.Application;

using System.Text.Json;
using BunkFy.Modules.DataRights.Application;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Tasks;
using BunkFy.Modules.DataRights.Contracts;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Tasks;
using Xunit;

[Trait("Category", "Unit")]
public sealed class TenantTerminationTaskSchedulerTests
{
    private const string TenantId =
        "11111111-1111-1111-1111-111111111111";
    private static readonly DateTimeOffset Now =
        new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Enqueues_scoped_and_global_work_with_exact_boundaries()
    {
        RecordingTaskRunStore store = new();
        TenantTerminationTaskScheduler scheduler = new(
            store,
            new FixedClock(Now));
        Guid processId = Guid.NewGuid();
        TenantTerminationPlannedDispatch scoped = Dispatch(
            processId,
            "inventory",
            TenantTerminationExecutionBoundary.TenantScopedTask);
        TenantTerminationPlannedDispatch global = Dispatch(
            processId,
            "task-runtime",
            TenantTerminationExecutionBoundary.GlobalControlTask);

        await scheduler.EnqueueAsync(
            TenantId,
            [global, scoped],
            CancellationToken.None);

        Assert.Equal(2, store.Requests.Count);
        TaskRunRequest scopedRequest = store.Requests[0];
        Assert.Equal(scoped.TaskRunId, scopedRequest.RunId);
        Assert.Equal(
            ExecuteTenantTerminationOwnerWorkPayload.TaskName,
            scopedRequest.TaskName);
        Assert.Equal(TenantId, scopedRequest.ScopeId);
        Assert.Equal(processId, scopedRequest.CorrelationId);
        Assert.Equal(
            TenantTerminationCoordination.ExecutorActorId,
            scopedRequest.RequestedBy);
        Assert.Equal(
            TenantTerminationCoordination.MaximumTaskAttempts,
            scopedRequest.MaxAttempts);
        ExecuteTenantTerminationOwnerWorkPayload scopedPayload =
            JsonSerializer.Deserialize<
                ExecuteTenantTerminationOwnerWorkPayload>(
                    scopedRequest.PayloadJson,
                    SerializerOptions)!;
        Assert.Equal(scoped.WorkItemId, scopedPayload.WorkItemId);

        TaskRunRequest globalRequest = store.Requests[1];
        Assert.Equal(global.TaskRunId, globalRequest.RunId);
        Assert.Equal(
            ExecuteGlobalTenantTerminationOwnerWorkPayload.TaskName,
            globalRequest.TaskName);
        Assert.Null(globalRequest.ScopeId);
        ExecuteGlobalTenantTerminationOwnerWorkPayload globalPayload =
            JsonSerializer.Deserialize<
                ExecuteGlobalTenantTerminationOwnerWorkPayload>(
                    globalRequest.PayloadJson,
                    SerializerOptions)!;
        Assert.Equal(TenantId, globalPayload.TenantId);
        Assert.Equal(global.WorkItemId, globalPayload.WorkItemId);
    }

    [Fact]
    public async Task Logical_continuation_uses_bounded_delay()
    {
        RecordingTaskRunStore store = new();
        TenantTerminationTaskScheduler scheduler = new(
            store,
            new FixedClock(Now));
        TenantTerminationPlannedDispatch continuation = Dispatch(
            Guid.NewGuid(),
            "inventory",
            TenantTerminationExecutionBoundary.TenantScopedTask,
            dispatchSequence: 4);

        await scheduler.EnqueueAsync(
            TenantId,
            [continuation],
            CancellationToken.None);

        TaskRunRequest request = Assert.Single(store.Requests);
        Assert.Equal(Now, request.CreatedAtUtc);
        Assert.Equal(
            Now.Add(TimeSpan.FromSeconds(20)),
            request.ScheduledAtUtc);
    }

    [Fact]
    public async Task Exact_existing_task_run_is_an_idempotent_replay()
    {
        RecordingTaskRunStore store = new()
        {
            Created = false
        };
        TenantTerminationTaskScheduler scheduler = new(
            store,
            new FixedClock(Now));

        await scheduler.EnqueueAsync(
            TenantId,
            [Dispatch(
                Guid.NewGuid(),
                "inventory",
                TenantTerminationExecutionBoundary.TenantScopedTask)],
            CancellationToken.None);

        Assert.Single(store.Requests);
    }

    [Fact]
    public async Task Conflicting_existing_task_run_is_rejected()
    {
        RecordingTaskRunStore store = new(request =>
            Details(request, payloadJson: "{}"));
        TenantTerminationTaskScheduler scheduler = new(
            store,
            new FixedClock(Now));

        InvalidOperationException failure =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                scheduler.EnqueueAsync(
                    TenantId,
                    [Dispatch(
                        Guid.NewGuid(),
                        "inventory",
                        TenantTerminationExecutionBoundary.TenantScopedTask)],
                    CancellationToken.None));

        Assert.Equal(
            "DataRights.TenantTerminationTaskRunConflict",
            failure.Message);
    }

    [Fact]
    public async Task Export_uses_dedicated_scoped_task_and_rejects_invalid_scope()
    {
        RecordingTaskRunStore store = new();
        TenantTerminationTaskScheduler scheduler = new(
            store,
            new FixedClock(Now));
        TenantTerminationPlannedDispatch export = Dispatch(
            Guid.NewGuid(),
            "inventory",
            TenantTerminationExecutionBoundary.TenantScopedTask) with
        {
            Phase = TenantTerminationContributionPhase.Export
        };

        await scheduler.EnqueueAsync(
            TenantId,
            [export],
            CancellationToken.None);
        TaskRunRequest exportRequest = Assert.Single(store.Requests);
        Assert.Equal(
            ExecuteTenantTerminationExportOwnerWorkPayload.TaskName,
            exportRequest.TaskName);
        ExecuteTenantTerminationExportOwnerWorkPayload exportPayload =
            JsonSerializer.Deserialize<
                ExecuteTenantTerminationExportOwnerWorkPayload>(
                    exportRequest.PayloadJson,
                    SerializerOptions)!;
        Assert.Equal(export.WorkItemId, exportPayload.WorkItemId);

        TenantTerminationPlannedDispatch globalExport = export with
        {
            ExecutionBoundary =
                TenantTerminationExecutionBoundary.GlobalControlTask
        };
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scheduler.EnqueueAsync(
                TenantId,
                [globalExport],
                CancellationToken.None));
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scheduler.EnqueueAsync(
                $" {TenantId}",
                [],
                CancellationToken.None));

        Assert.Single(store.Requests);
    }

    [Fact]
    public async Task Export_artifact_task_has_deterministic_identity()
    {
        RecordingTaskRunStore store = new();
        TenantTerminationTaskScheduler scheduler = new(
            store,
            new FixedClock(Now));
        Guid processId = Guid.NewGuid();

        await scheduler.EnqueueExportArtifactAsync(
            TenantId,
            processId,
            operationRevision: 9,
            CancellationToken.None);

        TaskRunRequest request = Assert.Single(store.Requests);
        Assert.Equal(
            TenantTerminationExecutionIdentity
                .CreateExportArtifactTaskRunId(processId, 9),
            request.RunId);
        Assert.Equal(
            GenerateTenantTerminationExportArtifactPayload.TaskName,
            request.TaskName);
        Assert.Equal(TenantId, request.ScopeId);
        GenerateTenantTerminationExportArtifactPayload payload =
            JsonSerializer.Deserialize<
                GenerateTenantTerminationExportArtifactPayload>(
                    request.PayloadJson,
                    SerializerOptions)!;
        Assert.Equal(processId, payload.ProcessId);
        Assert.Equal(9, payload.OperationRevision);
    }

    [Fact]
    public async Task Verification_task_has_deterministic_scoped_identity()
    {
        RecordingTaskRunStore store = new();
        TenantTerminationTaskScheduler scheduler = new(
            store,
            new FixedClock(Now));
        Guid processId = Guid.NewGuid();

        await scheduler.EnqueueVerificationAsync(
            TenantId,
            processId,
            operationRevision: 11,
            CancellationToken.None);

        TaskRunRequest request = Assert.Single(store.Requests);
        Assert.Equal(
            TenantTerminationExecutionIdentity.CreateVerificationTaskRunId(
                processId,
                11),
            request.RunId);
        Assert.Equal(VerifyTenantTerminationPayload.TaskName, request.TaskName);
        Assert.Equal(TenantId, request.ScopeId);
        VerifyTenantTerminationPayload payload = JsonSerializer.Deserialize<
            VerifyTenantTerminationPayload>(
                request.PayloadJson,
                SerializerOptions)!;
        Assert.Equal(processId, payload.ProcessId);
        Assert.Equal(11, payload.OperationRevision);
    }

    [Fact]
    public async Task Export_retention_tasks_are_global_and_pin_tenant_identity_and_expiry()
    {
        RecordingTaskRunStore store = new();
        TenantTerminationExportRetentionScheduler scheduler = new(
            store,
            new FixedClock(Now));
        Guid processId = Guid.NewGuid();
        Guid artifactId = Guid.NewGuid();
        Guid fragmentId = Guid.NewGuid();
        DateTimeOffset artifactExpiry = Now.AddHours(12);
        DateTimeOffset fragmentExpiry = Now.AddHours(24);

        await scheduler.EnqueueArtifactCleanupAsync(
            TenantId,
            processId,
            artifactId,
            operationRevision: 9,
            artifactExpiry,
            CancellationToken.None);
        await scheduler.EnqueueFragmentCleanupAsync(
            TenantId,
            processId,
            fragmentId,
            operationRevision: 9,
            fragmentExpiry,
            CancellationToken.None);

        Assert.Equal(2, store.Requests.Count);
        TaskRunRequest artifactRequest = store.Requests[0];
        Assert.Equal(
            TenantTerminationExecutionIdentity
                .CreateExportArtifactCleanupTaskRunId(artifactId),
            artifactRequest.RunId);
        Assert.Equal(
            DeleteExpiredTenantTerminationExportArtifactPayload.TaskName,
            artifactRequest.TaskName);
        Assert.Equal(artifactExpiry, artifactRequest.ScheduledAtUtc);
        Assert.Null(artifactRequest.ScopeId);
        Assert.Equal(processId, artifactRequest.CorrelationId);
        DeleteExpiredTenantTerminationExportArtifactPayload artifactPayload =
            JsonSerializer.Deserialize<
                DeleteExpiredTenantTerminationExportArtifactPayload>(
                    artifactRequest.PayloadJson,
                    SerializerOptions)!;
        Assert.Equal(artifactId, artifactPayload.ArtifactId);
        Assert.Equal(TenantId, artifactPayload.TenantId);
        Assert.Equal(artifactExpiry, artifactPayload.ExpiresAtUtc);

        TaskRunRequest fragmentRequest = store.Requests[1];
        Assert.Equal(
            TenantTerminationExecutionIdentity
                .CreateExportFragmentCleanupTaskRunId(fragmentId),
            fragmentRequest.RunId);
        Assert.Equal(
            DeleteExpiredTenantTerminationExportFragmentPayload.TaskName,
            fragmentRequest.TaskName);
        Assert.Equal(fragmentExpiry, fragmentRequest.ScheduledAtUtc);
        Assert.Null(fragmentRequest.ScopeId);
        Assert.Equal(processId, fragmentRequest.CorrelationId);
        DeleteExpiredTenantTerminationExportFragmentPayload fragmentPayload =
            JsonSerializer.Deserialize<
                DeleteExpiredTenantTerminationExportFragmentPayload>(
                    fragmentRequest.PayloadJson,
                    SerializerOptions)!;
        Assert.Equal(fragmentId, fragmentPayload.FragmentId);
        Assert.Equal(TenantId, fragmentPayload.TenantId);
        Assert.Equal(fragmentExpiry, fragmentPayload.ExpiresAtUtc);
    }

    [Fact]
    public async Task Export_retention_rejects_a_noncanonical_tenant_before_enqueue()
    {
        RecordingTaskRunStore store = new();
        TenantTerminationExportRetentionScheduler scheduler = new(
            store,
            new FixedClock(Now));

        InvalidOperationException failure =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                scheduler.EnqueueArtifactCleanupAsync(
                    $" {TenantId}",
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    operationRevision: 9,
                    Now.AddHours(12),
                    CancellationToken.None));

        Assert.Equal(
            "DataRights.TenantTerminationTaskScheduleInvalid",
            failure.Message);
        Assert.Empty(store.Requests);
    }

    [Fact]
    public async Task Export_retention_replay_rejects_a_conflicting_schedule()
    {
        RecordingTaskRunStore store = new(request => Details(
            request,
            scheduledAtUtc: request.ScheduledAtUtc.AddMinutes(-1)));
        TenantTerminationExportRetentionScheduler scheduler = new(
            store,
            new FixedClock(Now));

        InvalidOperationException failure =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                scheduler.EnqueueArtifactCleanupAsync(
                    TenantId,
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    operationRevision: 9,
                    Now.AddHours(12),
                    CancellationToken.None));

        Assert.Equal(
            "DataRights.TenantTerminationTaskRunConflict",
            failure.Message);
    }

    private static TenantTerminationPlannedDispatch Dispatch(
        Guid processId,
        string ownerKey,
        TenantTerminationExecutionBoundary boundary,
        int dispatchSequence = 1)
    {
        Guid taskRunId = Guid.NewGuid();
        return new(
            processId,
            Guid.NewGuid(),
            OperationRevision: 8,
            TenantTerminationContributionPhase.Destroy,
            ownerKey,
            boundary,
            dispatchSequence,
            taskRunId,
            TenantTerminationExecutionIdentity.CreateTaskDeduplicationKey(
                taskRunId));
    }

    private static TaskRunDetails Details(
        TaskRunRequest request,
        string? payloadJson = null,
        DateTimeOffset? scheduledAtUtc = null)
    {
        TaskRunSummary summary = new(
            request.RunId,
            request.ModuleName,
            request.TaskName,
            request.WorkerGroup,
            request.PayloadVersion,
            TaskRunStatus.Queued,
            request.ScopeId,
            request.CorrelationId,
            request.CreatedAtUtc,
            scheduledAtUtc ?? request.ScheduledAtUtc,
            StartedAtUtc: null,
            CompletedAtUtc: null,
            Attempts: 0,
            request.MaxAttempts,
            LockedBy: null,
            LockedUntilUtc: null,
            LastHeartbeatAtUtc: null,
            ProgressPercent: null,
            ProgressMessage: null,
            LastError: null,
            request.RequestedBy,
            request.DeduplicationKey);
        return new(
            summary,
            payloadJson ?? request.PayloadJson,
            NodeId: null,
            LeasedAtUtc: null,
            NextAttemptAtUtc: null,
            CancellationRequestedAtUtc: null,
            CancellationRequestedBy: null);
    }

    private sealed class RecordingTaskRunStore(
        Func<TaskRunRequest, TaskRunDetails>? result = null)
        : ITaskRunStore
    {
        public List<TaskRunRequest> Requests { get; } = [];
        public bool Created { get; set; } = true;

        public Task<TaskRunEnqueueResult> EnqueueAsync(
            TaskRunRequest request,
            CancellationToken cancellationToken)
        {
            this.Requests.Add(request);
            return Task.FromResult(new TaskRunEnqueueResult(
                result?.Invoke(request) ?? Details(request),
                this.Created));
        }

        public Task<TaskRunPage> ListAsync(
            TaskRunFilter filter,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TaskRunDetails?> GetAsync(
            Guid runId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TaskRunStats> GetStatsAsync(
            TaskRunStatsFilter filter,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TaskRunLease>> ClaimReadyAsync(
            TaskWorkerClaim claim,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TaskRunMutationOutcome> MarkStartedAsync(
            TaskExecutionContext context,
            DateTimeOffset startedAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TaskRunMutationOutcome> MarkSucceededAsync(
            TaskExecutionContext context,
            DateTimeOffset completedAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TaskRunMutationOutcome> MarkCanceledAsync(
            TaskExecutionContext context,
            DateTimeOffset canceledAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TaskRunMutationOutcome> MarkFailedAsync(
            TaskExecutionContext context,
            string error,
            DateTimeOffset failedAtUtc,
            DateTimeOffset? retryAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TaskRunMutationOutcome> RequestCancellationAsync(
            Guid runId,
            string? requestedBy,
            DateTimeOffset requestedAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TaskRunMutationOutcome> RetryAsync(
            Guid runId,
            string? requestedBy,
            DateTimeOffset scheduledAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TaskRunSummary>> MarkStaleTimedOutAsync(
            DateTimeOffset nowUtc,
            TimeSpan staleAfter,
            int maxRuns,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TaskControlMessageEnqueueOutcome> EnqueueControlMessageAsync(
            TaskControlMessage message,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TaskRunMutationOutcome> ReportHeartbeatAsync(
            TaskExecutionContext context,
            DateTimeOffset observedAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TaskRunMutationOutcome> ReportProgressAsync(
            TaskExecutionContext context,
            TaskProgress progress,
            DateTimeOffset observedAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TaskControlMessage>> ReadPendingAsync(
            TaskExecutionContext context,
            int maxMessages,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TaskRunMutationOutcome> MarkHandledAsync(
            TaskExecutionContext context,
            Guid messageId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TaskRunMutationOutcome> MarkFailedAsync(
            TaskExecutionContext context,
            Guid messageId,
            string error,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : ISystemClock
    {
        public DateTimeOffset UtcNow => utcNow;
    }
}
