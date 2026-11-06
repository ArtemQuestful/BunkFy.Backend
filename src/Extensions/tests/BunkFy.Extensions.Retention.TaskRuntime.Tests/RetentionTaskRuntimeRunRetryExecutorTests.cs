namespace BunkFy.Extensions.Retention.TaskRuntime.Tests;

using System.Text.Json;
using BunkFy.Extensions.Retention.TaskRuntime;
using BunkFy.Modules.Retention.Contracts;
using Gma.Framework.Results;
using Gma.Framework.Tasks;
using Gma.Modules.TaskRuntime.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

[Trait("Category", "Unit")]
public sealed class RetentionTaskRuntimeRunRetryExecutorTests
{
    private const string TenantId = "tenant-a";
    private static readonly Guid RequestId = Guid.Parse(
        "93f5aa23-b24f-4c51-bd5d-6793fc255ef3");
    private static readonly Guid RunId = Guid.Parse(
        "06f7000b-347b-4ac3-b282-1d710003770a");
    private static readonly Guid PropertyId = Guid.Parse(
        "5cbce446-0c4d-4aba-968c-593e60cc831f");

    [Fact]
    public async Task Exact_failed_run_is_retried_with_an_opaque_request_token()
    {
        FakeController controller = new(Result.Success());
        IRetentionRunRetryExecutor executor = CreateExecutor(
            OwnedRun(),
            controller);

        RetentionRunRetryExecutionOutcome outcome = await executor.ExecuteAsync(
            WorkItem(),
            CancellationToken.None);

        Assert.Equal(RetentionRunRetryExecutionStatus.Applied, outcome.Status);
        Assert.Equal(RunId, controller.RunId);
        Assert.Equal(
            $"retention-recovery:{RequestId:N}",
            controller.RequestedBy);
        Assert.Equal(1, controller.RetryCalls);
    }

    [Fact]
    public async Task Replayed_event_reconciles_an_already_applied_retry()
    {
        string token = $"retention-recovery:{RequestId:N}";
        FakeController controller = new(Result.Success());
        IRetentionRunRetryExecutor executor = CreateExecutor(
            OwnedRun(
                status: TaskRunStatus.Queued,
                requestedBy: token),
            controller);

        RetentionRunRetryExecutionOutcome outcome = await executor.ExecuteAsync(
            WorkItem(),
            CancellationToken.None);

        Assert.Equal(RetentionRunRetryExecutionStatus.Applied, outcome.Status);
        Assert.Equal(0, controller.RetryCalls);
    }

    [Fact]
    public async Task Foreign_or_malformed_run_is_not_disclosed()
    {
        FakeController controller = new(Result.Success());
        IRetentionRunRetryExecutor executor = CreateExecutor(
            OwnedRun(scopeId: "tenant-b", payloadJson: "not-json"),
            controller);

        RetentionRunRetryExecutionOutcome outcome = await executor.ExecuteAsync(
            WorkItem(),
            CancellationToken.None);

        Assert.Equal(
            RetentionRunRetryExecutionStatus.StableFailure,
            outcome.Status);
        Assert.Equal("task-run-unavailable", outcome.FailureCode);
        Assert.Equal(0, controller.RetryCalls);
    }

    [Fact]
    public async Task Concurrent_task_mutation_is_transient_for_inbox_redelivery()
    {
        FakeController controller = new(
            Result.Failure(TaskRuntimeOperationErrors.ConcurrentMutation));
        IRetentionRunRetryExecutor executor = CreateExecutor(
            OwnedRun(),
            controller);

        RetentionRunRetryExecutionOutcome outcome = await executor.ExecuteAsync(
            WorkItem(),
            CancellationToken.None);

        Assert.Equal(
            RetentionRunRetryExecutionStatus.TransientFailure,
            outcome.Status);
        Assert.Equal("task-run-concurrent-mutation", outcome.FailureCode);
    }

    private static IRetentionRunRetryExecutor CreateExecutor(
        TaskRunDetails run,
        FakeController controller)
    {
        ServiceCollection services = new();
        services.AddScoped<ITaskRunReader>(_ => new FakeReader(run));
        services.AddScoped<ITaskRunController>(_ => controller);
        services.AddBunkFyRetentionTaskRuntimeRecovery();
        return services.BuildServiceProvider()
            .GetRequiredService<IRetentionRunRetryExecutor>();
    }

    private static RetentionRunRetryWorkItem WorkItem() => new(
        RequestId,
        RunId,
        TenantId,
        "guests",
        "guest-operational",
        RetentionTargetScopeKind.Property,
        PropertyId,
        ExecutionPolicyVersion: 2,
        EvidenceVersion: 7,
        Attempt: 1,
        ScheduledAtUtc: null);

    private static TaskRunDetails OwnedRun(
        string scopeId = TenantId,
        TaskRunStatus status = TaskRunStatus.Failed,
        string? requestedBy = "task-scheduler",
        string? payloadJson = null)
    {
        DateTimeOffset created =
            new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
        payloadJson ??= JsonSerializer.Serialize(
            new ExecuteRetentionSchedulePayload(
                "guests",
                "guest-operational",
                ExecutionPolicyVersion: 2,
                RetentionTargetScopeKind.Property,
                PropertyId));
        return new TaskRunDetails(
            new TaskRunSummary(
                RunId,
                RetentionModuleMetadata.Name,
                ExecuteRetentionSchedulePayload.TaskName,
                RetentionModuleMetadata.WorkerGroup,
                ExecuteRetentionSchedulePayload.PayloadVersion,
                status,
                scopeId,
                CorrelationId: null,
                created,
                created,
                created,
                created.AddMinutes(1),
                Attempts: 1,
                MaxAttempts: 3,
                LockedBy: null,
                LockedUntilUtc: null,
                LastHeartbeatAtUtc: null,
                ProgressPercent: null,
                ProgressMessage: null,
                LastError: "owner failure",
                RequestedBy: requestedBy,
                DeduplicationKey: "schedule-a"),
            payloadJson,
            NodeId: null,
            LeasedAtUtc: null,
            NextAttemptAtUtc: null,
            CancellationRequestedAtUtc: null,
            CancellationRequestedBy: null);
    }

    private sealed class FakeReader(TaskRunDetails run) : ITaskRunReader
    {
        public Task<Result<TaskRunDetails>> GetAsync(
            Guid runId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Success(run));

        public Task<Result<TaskRunPage>> ListAsync(
            TaskRunListRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<TaskRunStats>> GetStatsAsync(
            TaskRunStatsRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeController(Result retryResult)
        : ITaskRunController
    {
        public int RetryCalls { get; private set; }
        public Guid RunId { get; private set; }
        public string? RequestedBy { get; private set; }

        public Task<Result> RetryAsync(
            Guid runId,
            string? requestedBy,
            DateTimeOffset? scheduledAtUtc = null,
            CancellationToken cancellationToken = default)
        {
            this.RetryCalls++;
            this.RunId = runId;
            this.RequestedBy = requestedBy;
            return Task.FromResult(retryResult);
        }

        public Task<Result> CancelAsync(
            Guid runId,
            string? requestedBy,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<TaskControlMessage>> SendControlMessageAsync(
            TaskRunControlRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
