namespace BunkFy.Modules.Retention.Tests.Application;

using BunkFy.Modules.Retention.Application.Commands;
using BunkFy.Modules.Retention.Application.Errors;
using BunkFy.Modules.Retention.Application.Handlers;
using BunkFy.Modules.Retention.Application.Ports;
using BunkFy.Modules.Retention.Contracts;
using BunkFy.Modules.Retention.Domain.Aggregates;
using BunkFy.Modules.Retention.Domain.Models;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Xunit;

[Trait("Category", "Unit")]
public sealed class RetryRetentionRunCommandHandlerTests
{
    private const string TenantId = "tenant-a";
    private static readonly Guid RunId = Guid.Parse(
        "54c5633d-5536-4921-9492-05bd14f529e4");
    private static readonly Guid PropertyId = Guid.Parse(
        "735739c6-8669-4078-9e5c-ef7d80fc9c88");
    private static readonly Guid RequestId = Guid.Parse(
        "e705e71c-7241-41ce-8f66-296dd5c6c1ac");
    private static readonly Guid EventId = Guid.Parse(
        "7c12bbdc-aa6b-4469-a87d-c160c1639a9d");
    private static readonly DateTimeOffset Now =
        new(2026, 8, 15, 12, 5, 0, TimeSpan.Zero);

    [Fact]
    public async Task Current_failed_evidence_creates_one_pending_request()
    {
        FakeRepository repository = new();
        RecordingMutationLock mutationLock = new();
        RequestRetentionRunRetryCommandHandler handler = CreateHandler(
            FailedSnapshot(),
            repository,
            mutationLock);

        Result<RetentionRunRetryReceiptDto> result = await handler.HandleAsync(
            Command(Expected()),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(RequestId, result.Value.RequestId);
        Assert.Equal(RunId, result.Value.RunId);
        Assert.Equal(7, result.Value.EvidenceVersion);
        Assert.Equal(1, result.Value.Attempt);
        Assert.Equal(RetentionRunRetryStatus.Pending, result.Value.Status);
        Assert.Equal(Now, result.Value.RequestedAtUtc);
        Assert.Same(repository.Added, repository.Stored);
        Assert.Equal(1, mutationLock.ScheduleCalls);
        Assert.Single(repository.Added!.DomainEvents);
    }

    [Fact]
    public async Task Exact_pending_replay_returns_the_existing_request()
    {
        RetentionRunRetryRequest existing = CreateRequest();
        FakeRepository repository = new(existing);
        RequestRetentionRunRetryCommandHandler handler = CreateHandler(
            FailedSnapshot(),
            repository);

        Result<RetentionRunRetryReceiptDto> result = await handler.HandleAsync(
            Command(Expected()),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(existing.Id, result.Value.RequestId);
        Assert.Equal(1, result.Value.Attempt);
        Assert.Null(repository.Added);
    }

    [Fact]
    public async Task Failed_delivery_can_be_requested_again_without_a_duplicate_row()
    {
        RetentionRunRetryRequest existing = CreateRequest();
        Assert.True(existing.MarkFailed(
            "task-run-unavailable",
            Now.AddMinutes(-1)).IsSuccess);
        FakeRepository repository = new(existing);
        RequestRetentionRunRetryCommandHandler handler = CreateHandler(
            FailedSnapshot(),
            repository,
            ids: new QueueIdGenerator(EventId));

        Result<RetentionRunRetryReceiptDto> result = await handler.HandleAsync(
            Command(Expected()),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Attempt);
        Assert.Equal(RetentionRunRetryStatus.Pending, result.Value.Status);
        Assert.Null(result.Value.FailureCode);
        Assert.Null(repository.Added);
    }

    [Fact]
    public async Task Stale_evidence_version_is_rejected_under_the_schedule_lock()
    {
        FakeRepository repository = new();
        RequestRetentionRunRetryCommandHandler handler = CreateHandler(
            FailedSnapshot(),
            repository);

        Result<RetentionRunRetryReceiptDto> result = await handler.HandleAsync(
            Command(Expected() with { EvidenceVersion = 6 }),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            RetentionApplicationErrors.ScheduleRetryEvidenceChanged,
            result.Error);
        Assert.Null(repository.Added);
    }

    [Fact]
    public async Task Non_failed_schedule_is_not_recoverable()
    {
        FakeRepository repository = new();
        RetentionScheduleStateSnapshot completed = FailedSnapshot() with
        {
            State = (int)RetentionExecutionState.Completed
        };
        RequestRetentionRunRetryCommandHandler handler = CreateHandler(
            completed,
            repository);

        Result<RetentionRunRetryReceiptDto> result = await handler.HandleAsync(
            Command(Expected()),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            RetentionApplicationErrors.ScheduleRetryEvidenceChanged,
            result.Error);
        Assert.Null(repository.Added);
    }

    [Fact]
    public async Task Missing_current_schedule_is_unavailable_not_stale()
    {
        FakeRepository repository = new();
        RequestRetentionRunRetryCommandHandler handler = CreateHandler(
            new FakeHealthReader(snapshot: null),
            repository);

        Result<RetentionRunRetryReceiptDto> result = await handler.HandleAsync(
            Command(Expected()),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(RetentionApplicationErrors.TaskRunUnavailable, result.Error);
        Assert.Null(repository.Added);
    }

    [Fact]
    public async Task Administration_path_resolves_the_current_schedule_by_run_id()
    {
        FakeHealthReader health = new(FailedSnapshot());
        FakeRepository repository = new();
        RequestRetentionRunRetryCommandHandler handler = CreateHandler(
            health,
            repository);

        Result<RetentionRunRetryReceiptDto> result = await handler.HandleAsync(
            Command(expected: null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, health.ByRunCalls);
        Assert.Equal(1, health.ByCoordinateCalls);
    }

    private static RequestRetentionRunRetryCommandHandler CreateHandler(
        RetentionScheduleStateSnapshot snapshot,
        FakeRepository repository,
        RecordingMutationLock? mutationLock = null,
        QueueIdGenerator? ids = null) =>
        CreateHandler(
            new FakeHealthReader(snapshot),
            repository,
            mutationLock,
            ids);

    private static RequestRetentionRunRetryCommandHandler CreateHandler(
        FakeHealthReader health,
        FakeRepository repository,
        RecordingMutationLock? mutationLock = null,
        QueueIdGenerator? ids = null) => new(
            mutationLock ?? new RecordingMutationLock(),
            health,
            repository,
            ids ?? new QueueIdGenerator(RequestId, EventId),
            new FixedClock(Now));

    private static RequestRetentionRunRetryCommand Command(
        RetentionRunRetryExpectation? expected) => new(
            RunId,
            TenantId,
            ScheduledAtUtc: null,
            expected);

    private static RetentionRunRetryExpectation Expected() => new(
        "guests",
        "guest-operational",
        RetentionTargetScopeKind.Property,
        PropertyId,
        ExecutionPolicyVersion: 2,
        EvidenceVersion: 7);

    private static RetentionScheduleStateSnapshot FailedSnapshot() => new(
        "guests",
        "guest-operational",
        PropertyId,
        ExecutionPolicyVersion: 2,
        Version: 7,
        State: (int)RetentionExecutionState.Failed,
        LastExecutionId: RunId,
        LastStartedAtUtc: Now.AddMinutes(-5),
        LastCompletedAtUtc: Now.AddMinutes(-4),
        NextDueAtUtc: Now.AddDays(1),
        ConsecutiveFailures: 1,
        LastScannedCount: 1,
        LastAffectedCount: 0,
        LastRemainingCount: 1,
        OutcomeCode: "guests.guest-operational.time-zone-unavailable",
        HoldReviewDueAtUtc: null,
        Retry: null);

    private static RetentionRunRetryRequest CreateRequest() =>
        RetentionRunRetryRequest.Create(
            RequestId,
            EventId,
            TenantId,
            RunId,
            "guests",
            "guest-operational",
            RetentionExecutionTargetKind.Property,
            PropertyId,
            executionPolicyVersion: 2,
            evidenceVersion: 7,
            Now.AddMinutes(-2),
            scheduledAtUtc: null).Value;

    private sealed class FakeHealthReader(
        RetentionScheduleStateSnapshot? snapshot)
        : IRetentionScheduleHealthReader
    {
        public int ByCoordinateCalls { get; private set; }
        public int ByRunCalls { get; private set; }

        public Task<RetentionScheduleStateSnapshot?> GetAsync(
            string tenantId,
            string ownerKey,
            string dataClassKey,
            Guid? propertyId,
            int executionPolicyVersion,
            CancellationToken cancellationToken)
        {
            this.ByCoordinateCalls++;
            return Task.FromResult(snapshot);
        }

        public Task<RetentionScheduleStateSnapshot?> GetByLastExecutionIdAsync(
            string tenantId,
            Guid lastExecutionId,
            CancellationToken cancellationToken)
        {
            this.ByRunCalls++;
            return Task.FromResult(snapshot);
        }

        public Task<IReadOnlyList<RetentionScheduleStateSnapshot>> ListAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeRepository(
        RetentionRunRetryRequest? stored = null)
        : IRetentionRunRetryRequestRepository
    {
        public RetentionRunRetryRequest? Stored { get; private set; } = stored;
        public RetentionRunRetryRequest? Added { get; private set; }

        public Task<RetentionRunRetryRequest?> GetAsync(
            Guid requestId,
            CancellationToken cancellationToken) =>
            Task.FromResult(this.Stored?.Id == requestId
                ? this.Stored
                : null);

        public Task<RetentionRunRetryRequest?> GetForEvidenceAsync(
            Guid runId,
            long evidenceVersion,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                this.Stored?.RunId == runId &&
                this.Stored.EvidenceVersion == evidenceVersion
                    ? this.Stored
                    : null);

        public Task AddAsync(
            RetentionRunRetryRequest request,
            CancellationToken cancellationToken)
        {
            this.Added = request;
            this.Stored = request;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingMutationLock : IRetentionMutationLock
    {
        public int ScheduleCalls { get; private set; }

        public Task AcquireScheduleAsync(
            string tenantId,
            string ownerKey,
            string dataClassKey,
            Guid? propertyId,
            int executionPolicyVersion,
            CancellationToken cancellationToken)
        {
            this.ScheduleCalls++;
            return Task.CompletedTask;
        }

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
    }

    private sealed class QueueIdGenerator(params Guid[] ids) : IIdGenerator
    {
        private readonly Queue<Guid> ids = new(ids);
        public Guid NewId() => this.ids.Dequeue();
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : ISystemClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
