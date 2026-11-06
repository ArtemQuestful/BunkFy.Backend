namespace BunkFy.Modules.Retention.Tests.Application;

using BunkFy.Modules.Retention.Application.Handlers;
using BunkFy.Modules.Retention.Application.Ports;
using BunkFy.Modules.Retention.Contracts;
using BunkFy.Modules.Retention.Domain.Aggregates;
using BunkFy.Modules.Retention.Domain.Models;
using Gma.Framework.Runtime.Time;
using Xunit;

[Trait("Category", "Unit")]
public sealed class RetentionRunRetryRequestedHandlerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Applied_execution_marks_the_exact_pending_request()
    {
        RetentionRunRetryRequest request = CreateRequest();
        RecordingExecutor executor = new(
            RetentionRunRetryExecutionOutcome.Applied);
        RetentionRunRetryRequestedHandler handler = CreateHandler(
            request,
            executor);

        await handler.HandleAsync(Event(request), CancellationToken.None);

        Assert.Equal(RetentionRunRetryRequestState.Applied, request.State);
        Assert.Equal(Now, request.CompletedAtUtc);
        Assert.Equal(request.Id, executor.WorkItem!.RequestId);
        Assert.Equal(request.EvidenceVersion, executor.WorkItem.EvidenceVersion);
    }

    [Fact]
    public async Task Stable_executor_failure_remains_visible_to_the_operator()
    {
        RetentionRunRetryRequest request = CreateRequest();
        RetentionRunRetryRequestedHandler handler = CreateHandler(
            request,
            new RecordingExecutor(
                RetentionRunRetryExecutionOutcome.StableFailure(
                    "task-run-state-changed")));

        await handler.HandleAsync(Event(request), CancellationToken.None);

        Assert.Equal(RetentionRunRetryRequestState.Failed, request.State);
        Assert.Equal("task-run-state-changed", request.FailureCode);
        Assert.Equal(Now, request.CompletedAtUtc);
    }

    [Fact]
    public async Task Transient_executor_failure_becomes_visible_for_operator_retry()
    {
        RetentionRunRetryRequest request = CreateRequest();
        RetentionRunRetryRequestedHandler handler = CreateHandler(
            request,
            new RecordingExecutor(
                RetentionRunRetryExecutionOutcome.TransientFailure(
                    "task-run-concurrent-mutation")));

        await handler.HandleAsync(Event(request), CancellationToken.None);

        Assert.Equal(RetentionRunRetryRequestState.Failed, request.State);
        Assert.Equal("task-run-concurrent-mutation", request.FailureCode);
    }

    [Fact]
    public async Task Changed_schedule_evidence_is_not_sent_to_task_runtime()
    {
        RetentionRunRetryRequest request = CreateRequest();
        RecordingExecutor executor = new(
            RetentionRunRetryExecutionOutcome.Applied);
        RetentionScheduleStateSnapshot changed = CurrentSchedule(request) with
        {
            Version = request.EvidenceVersion + 1
        };
        RetentionRunRetryRequestedHandler handler = CreateHandler(
            request,
            executor,
            changed);

        await handler.HandleAsync(Event(request), CancellationToken.None);

        Assert.Null(executor.WorkItem);
        Assert.Equal(RetentionRunRetryRequestState.Failed, request.State);
        Assert.Equal("schedule-evidence-changed", request.FailureCode);
    }

    [Fact]
    public async Task Executor_exception_becomes_a_nondisclosing_failure()
    {
        RetentionRunRetryRequest request = CreateRequest();
        RetentionRunRetryRequestedHandler handler = new(
            new FakeRepository(request),
            new NoopMutationLock(),
            new FakeHealthReader(CurrentSchedule(request)),
            new ThrowingExecutor(),
            new FixedClock(Now));

        await handler.HandleAsync(Event(request), CancellationToken.None);

        Assert.Equal(RetentionRunRetryRequestState.Failed, request.State);
        Assert.Equal("recovery-executor-unavailable", request.FailureCode);
    }

    [Fact]
    public async Task Stale_attempt_is_acknowledged_without_executor_work()
    {
        RetentionRunRetryRequest request = CreateRequest();
        Assert.True(request.MarkFailed(
            "task-run-unavailable",
            Now.AddMinutes(-2)).IsSuccess);
        Assert.True(request.RequestAgain(
            Guid.NewGuid(),
            Now.AddMinutes(-1),
            scheduledAtUtc: null).IsSuccess);
        RecordingExecutor executor = new(
            RetentionRunRetryExecutionOutcome.Applied);
        RetentionRunRetryRequestedHandler handler = CreateHandler(
            request,
            executor);

        await handler.HandleAsync(
            new RetentionRunRetryRequestedIntegrationEvent(
                Guid.NewGuid(),
                request.ScopeId,
                Now.AddMinutes(-4),
                request.Id,
                request.RunId,
                attempt: 1),
            CancellationToken.None);

        Assert.Null(executor.WorkItem);
        Assert.Equal(RetentionRunRetryRequestState.Pending, request.State);
    }

    private static RetentionRunRetryRequestedHandler CreateHandler(
        RetentionRunRetryRequest request,
        RecordingExecutor executor,
        RetentionScheduleStateSnapshot? schedule = null) => new(
            new FakeRepository(request),
            new NoopMutationLock(),
            new FakeHealthReader(schedule ?? CurrentSchedule(request)),
            executor,
            new FixedClock(Now));

    private static RetentionScheduleStateSnapshot CurrentSchedule(
        RetentionRunRetryRequest request) => new(
        request.OwnerKey,
        request.DataClassKey,
        request.PropertyId,
        request.ExecutionPolicyVersion,
        request.EvidenceVersion,
        State: (int)RetentionExecutionState.Failed,
        LastExecutionId: request.RunId,
        LastStartedAtUtc: Now.AddMinutes(-6),
        LastCompletedAtUtc: Now.AddMinutes(-5),
        NextDueAtUtc: Now.AddDays(1),
        ConsecutiveFailures: 1,
        LastScannedCount: 1,
        LastAffectedCount: 0,
        LastRemainingCount: 1,
        OutcomeCode: "retention.owner-timeout",
        HoldReviewDueAtUtc: null,
        Retry: null);

    private static RetentionRunRetryRequest CreateRequest() =>
        RetentionRunRetryRequest.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "tenant-a",
            Guid.NewGuid(),
            "guests",
            "guest-operational",
            RetentionExecutionTargetKind.Tenant,
            propertyId: null,
            executionPolicyVersion: 2,
            evidenceVersion: 5,
            Now.AddMinutes(-5),
            scheduledAtUtc: null).Value;

    private static RetentionRunRetryRequestedIntegrationEvent Event(
        RetentionRunRetryRequest request) => new(
            Guid.NewGuid(),
            request.ScopeId,
            Now.AddMinutes(-4),
            request.Id,
            request.RunId,
            request.Attempt);

    private sealed class FakeRepository(RetentionRunRetryRequest request)
        : IRetentionRunRetryRequestRepository
    {
        public Task<RetentionRunRetryRequest?> GetAsync(
            Guid requestId,
            CancellationToken cancellationToken) =>
            Task.FromResult(request.Id == requestId ? request : null);

        public Task<RetentionRunRetryRequest?> GetForEvidenceAsync(
            Guid runId,
            long evidenceVersion,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddAsync(
            RetentionRunRetryRequest request,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingExecutor(
        RetentionRunRetryExecutionOutcome outcome)
        : IRetentionRunRetryExecutor
    {
        public RetentionRunRetryWorkItem? WorkItem { get; private set; }

        public Task<RetentionRunRetryExecutionOutcome> ExecuteAsync(
            RetentionRunRetryWorkItem workItem,
            CancellationToken cancellationToken)
        {
            this.WorkItem = workItem;
            return Task.FromResult(outcome);
        }
    }

    private sealed class ThrowingExecutor : IRetentionRunRetryExecutor
    {
        public Task<RetentionRunRetryExecutionOutcome> ExecuteAsync(
            RetentionRunRetryWorkItem workItem,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("sensitive-runtime-detail");
    }

    private sealed class FakeHealthReader(
        RetentionScheduleStateSnapshot? schedule)
        : IRetentionScheduleHealthReader
    {
        public Task<RetentionScheduleStateSnapshot?> GetAsync(
            string tenantId,
            string ownerKey,
            string dataClassKey,
            Guid? propertyId,
            int executionPolicyVersion,
            CancellationToken cancellationToken) =>
            Task.FromResult(schedule);

        public Task<RetentionScheduleStateSnapshot?> GetByLastExecutionIdAsync(
            string tenantId,
            Guid lastExecutionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RetentionScheduleStateSnapshot>> ListAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class NoopMutationLock : IRetentionMutationLock
    {
        public Task AcquireTenantTargetReadAsync(
            string tenantId,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AcquireTenantTargetWriteAsync(
            string tenantId,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AcquirePropertyTargetReadAsync(
            string tenantId,
            Guid propertyId,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AcquirePropertyTargetWriteAsync(
            string tenantId,
            Guid propertyId,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AcquireExecutionAsync(
            string tenantId,
            Guid executionId,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AcquireScheduleAsync(
            string tenantId,
            string ownerKey,
            string dataClassKey,
            Guid? propertyId,
            int executionPolicyVersion,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : ISystemClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
