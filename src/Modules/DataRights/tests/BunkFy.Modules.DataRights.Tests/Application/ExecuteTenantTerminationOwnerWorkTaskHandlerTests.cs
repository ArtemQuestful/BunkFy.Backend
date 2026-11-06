namespace BunkFy.Modules.DataRights.Tests.Application;

using BunkFy.Modules.DataRights.Application;
using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Application.Tasks;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Models;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Gma.Framework.Tasks;
using Gma.Framework.Tasks.Cqrs;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ExecuteTenantTerminationOwnerWorkTaskHandlerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 4, 19, 0, 0, TimeSpan.Zero);
    private static readonly string PolicyDigest = new('a', 64);
    private static readonly string CatalogDigest = new('b', 64);
    private static readonly Guid RunId =
        Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid ProcessId =
        Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public async Task Dispatch_and_result_are_durable_before_central_commit()
    {
        List<string> order = [];
        TenantTerminationOwnerWorkStart start = Start();
        FakeDispatcher dispatcher = new(start, order);
        FakeReplayStore replayStore = new(order);
        RecordingContributor contributor = new(Contribution(), order);
        ExecuteTenantTerminationOwnerWorkTaskHandler handler = new(
            dispatcher,
            replayStore,
            [contributor],
            new FixedClock(Now.AddSeconds(10)),
            new FixedScopeContext("tenant-a"),
            new RecordingScheduler());

        await handler.HandleAsync(
            Payload(),
            Context(),
            CancellationToken.None);

        Assert.Equal(
            ["begin", "append-dispatch", "owner", "append-result", "record"],
            order);
        Assert.NotNull(replayStore.Attempt?.Result);
        Assert.Same(start.Request, contributor.Request);
        Assert.NotNull(dispatcher.Recorded);
        Assert.Equal(RunId, dispatcher.Recorded.TaskRunId);
        Assert.Equal(1, dispatcher.Recorded.TaskAttempt);
        Assert.Equal(start.WorkItemVersion,
            dispatcher.Recorded.ExpectedWorkItemVersion);
    }

    [Fact]
    public async Task Late_cancellation_after_result_append_replays_without_reinvoking_owner()
    {
        List<string> order = [];
        using CancellationTokenSource source = new();
        TenantTerminationOwnerWorkStart start = Start();
        FakeDispatcher dispatcher = new(start, order);
        FakeReplayStore replayStore = new(order)
        {
            AfterAppend = entry =>
            {
                if (entry.Kind == TenantTerminationReplayEntryKind.Result)
                {
                    source.Cancel();
                }
            }
        };
        RecordingContributor contributor = new(Contribution(), order);
        ExecuteTenantTerminationOwnerWorkTaskHandler handler = new(
            dispatcher,
            replayStore,
            [contributor],
            new FixedClock(Now.AddSeconds(10)),
            new FixedScopeContext("tenant-a"),
            new RecordingScheduler());

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            handler.HandleAsync(Payload(), Context(), source.Token));

        Assert.Equal(
            ["begin", "append-dispatch", "owner", "append-result"],
            order);
        Assert.NotNull(replayStore.Attempt?.Result);
        Assert.Null(dispatcher.Recorded);

        await handler.HandleAsync(
            Payload(),
            Context(),
            CancellationToken.None);

        Assert.Equal(
            [
                "begin",
                "append-dispatch",
                "owner",
                "append-result",
                "begin",
                "record"
            ],
            order);
        Assert.NotNull(dispatcher.Recorded);
    }

    [Fact]
    public async Task Invalid_dispatch_durability_receipt_stops_before_owner()
    {
        List<string> order = [];
        TenantTerminationOwnerWorkStart start = Start();
        FakeDispatcher dispatcher = new(start, order);
        FakeReplayStore replayStore = new(order)
        {
            ReturnInvalidReceipt = true
        };
        RecordingContributor contributor = new(Contribution(), order);
        ExecuteTenantTerminationOwnerWorkTaskHandler handler = new(
            dispatcher,
            replayStore,
            [contributor],
            new FixedClock(Now.AddSeconds(10)),
            new FixedScopeContext("tenant-a"),
            new RecordingScheduler());

        InvalidOperationException failure =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                handler.HandleAsync(
                    Payload(),
                    Context(),
                    CancellationToken.None));

        Assert.Equal(
            "DataRights.TenantTerminationReplayProofInvalid",
            failure.Message);
        Assert.Equal(["begin", "append-dispatch"], order);
        Assert.Null(contributor.Request);
        Assert.Null(dispatcher.Recorded);
    }

    [Fact]
    public async Task Normal_return_at_deadline_is_not_journaled_or_recorded()
    {
        List<string> order = [];
        TenantTerminationOwnerWorkStart start = Start();
        MutableClock clock = new(Now.AddSeconds(10));
        FakeDispatcher dispatcher = new(start, order);
        FakeReplayStore replayStore = new(order);
        RecordingContributor contributor = new(
            Contribution(),
            order,
            onExecute: request => clock.UtcNow = request.DeadlineUtc);
        ExecuteTenantTerminationOwnerWorkTaskHandler handler = new(
            dispatcher,
            replayStore,
            [contributor],
            clock,
            new FixedScopeContext("tenant-a"),
            new RecordingScheduler());

        TimeoutException failure = await Assert.ThrowsAsync<TimeoutException>(
            () => handler.HandleAsync(
                Payload(),
                Context(),
                CancellationToken.None));

        Assert.Equal(
            "DataRights.TenantTerminationOwnerDeadlineExceeded",
            failure.Message);
        Assert.Equal(["begin", "append-dispatch", "owner"], order);
        Assert.Null(replayStore.Attempt?.Result);
        Assert.Null(dispatcher.Recorded);
    }

    [Fact]
    public async Task Authenticated_result_replay_skips_the_owner()
    {
        List<string> order = [];
        TenantTerminationOwnerWorkStart start = Start();
        TenantTerminationReplayDispatch dispatch = Dispatch(start);
        FakeReplayStore replayStore = new(order)
        {
            Attempt = new(
                dispatch,
                TenantTerminationReplayResult.Create(
                    dispatch,
                    Contribution(),
                    Now.AddSeconds(10)))
        };
        RecordingContributor contributor = new(Contribution(), order);
        FakeDispatcher dispatcher = new(start, order);
        ExecuteTenantTerminationOwnerWorkTaskHandler handler = new(
            dispatcher,
            replayStore,
            [contributor],
            new FixedClock(Now.AddSeconds(10)),
            new FixedScopeContext("tenant-a"),
            new RecordingScheduler());

        await handler.HandleAsync(
            Payload(),
            Context(),
            CancellationToken.None);

        Assert.Equal(["begin", "record"], order);
        Assert.Null(contributor.Request);
        Assert.NotNull(dispatcher.Recorded);
    }

    [Fact]
    public async Task Global_control_task_uses_the_established_tenant_context()
    {
        List<string> order = [];
        TenantTerminationOwnerWorkStart start = Start(
            boundary:
                TenantTerminationExecutionBoundary.GlobalControlTask);
        FakeDispatcher dispatcher = new(start, order);
        RecordingContributor contributor = new(
            Contribution(),
            order,
            TenantTerminationExecutionBoundary.GlobalControlTask);
        ExecuteTenantTerminationOwnerWorkTaskHandler executor = new(
            dispatcher,
            new FakeReplayStore(order),
            [contributor],
            new FixedClock(Now.AddSeconds(10)),
            new FixedScopeContext("tenant-a"),
            new RecordingScheduler());
        ExecuteGlobalTenantTerminationOwnerWorkTaskHandler handler = new(
            executor);

        await handler.HandleAsync(
            new(
                "tenant-a",
                ProcessId,
                Payload().WorkItemId,
                OperationRevision: 8,
                TenantTerminationContributionPhase.Destroy,
                "reservations"),
            GlobalContext(),
            CancellationToken.None);

        Assert.Equal(
            ["begin", "append-dispatch", "owner", "append-result", "record"],
            order);
        Assert.NotNull(dispatcher.Recorded);
    }

    [Fact]
    public async Task Terminal_task_replay_reschedules_ready_continuation()
    {
        List<string> order = [];
        TenantTerminationPlannedDispatch continuation = PlannedDispatch();
        TenantTerminationOwnerWorkStart start = new(
            DispatchRequired: false,
            TenantTerminationOwnerWorkState.RetryRequired,
            WorkItemVersion: 3,
            "reservations",
            CatalogVersion: 3,
            CatalogDigest,
            TenantTerminationExecutionBoundary.TenantScopedTask,
            Request: null,
            [continuation]);
        RecordingScheduler scheduler = new();
        ExecuteTenantTerminationOwnerWorkTaskHandler handler = new(
            new FakeDispatcher(start, order),
            new FakeReplayStore(order),
            [new RecordingContributor(Contribution(), order)],
            new FixedClock(Now.AddSeconds(10)),
            new FixedScopeContext("tenant-a"),
            scheduler);

        await handler.HandleAsync(
            Payload(),
            Context(),
            CancellationToken.None);

        Assert.Equal(["begin"], order);
        Assert.Equal("tenant-a", scheduler.TenantId);
        Assert.Equal(
            continuation.TaskRunId,
            Assert.Single(scheduler.Dispatches).TaskRunId);
    }

    [Fact]
    public async Task Foreign_replay_dispatch_fails_before_owner_execution()
    {
        List<string> order = [];
        TenantTerminationOwnerWorkStart start = Start();
        TenantTerminationContributionRequest foreign = start.Request! with
        {
            ExecutingActorId = "system:foreign-executor"
        };
        FakeReplayStore replayStore = new(order)
        {
            Attempt = new(
                TenantTerminationReplayDispatch.Create(
                    foreign,
                    start.OwnerKey,
                    start.CatalogVersion,
                    start.CatalogSha256,
                    start.ExecutionBoundary,
                    RunId,
                    taskAttempt: 1,
                    Now.AddSeconds(10)),
                Result: null)
        };
        RecordingContributor contributor = new(Contribution(), order);
        ExecuteTenantTerminationOwnerWorkTaskHandler handler = new(
            new FakeDispatcher(start, order),
            replayStore,
            [contributor],
            new FixedClock(Now.AddSeconds(10)),
            new FixedScopeContext("tenant-a"),
            new RecordingScheduler());

        InvalidOperationException failure = await Assert.ThrowsAsync<
            InvalidOperationException>(() => handler.HandleAsync(
                Payload(),
                Context(),
                CancellationToken.None));

        Assert.Equal(
            "DataRights.TenantTerminationReplayProofInvalid",
            failure.Message);
        Assert.Null(contributor.Request);
        Assert.Equal(["begin"], order);
    }

    [Theory]
    [InlineData(TenantTerminationContributionPhase.Export,
        TenantTerminationExecutionBoundary.TenantScopedTask,
        "DataRights.TenantTerminationExportExecutorRequired")]
    [InlineData(TenantTerminationContributionPhase.Destroy,
        TenantTerminationExecutionBoundary.GlobalControlTask,
        "DataRights.TenantTerminationExecutionBoundaryInvalid")]
    public async Task Dedicated_execution_boundaries_are_not_run_by_scoped_owner(
        TenantTerminationContributionPhase phase,
        TenantTerminationExecutionBoundary boundary,
        string expected)
    {
        TenantTerminationOwnerWorkStart start = Start(phase, boundary);
        ExecuteTenantTerminationOwnerWorkTaskHandler handler = new(
            new FakeDispatcher(start, []),
            new FakeReplayStore([]),
            [new RecordingContributor(Contribution(), [])],
            new FixedClock(Now.AddSeconds(10)),
            new FixedScopeContext("tenant-a"),
            new RecordingScheduler());

        InvalidOperationException failure = await Assert.ThrowsAsync<
            InvalidOperationException>(() => handler.HandleAsync(
                Payload(phase),
                Context(),
                CancellationToken.None));

        Assert.Equal(expected, failure.Message);
    }

    private static ExecuteTenantTerminationOwnerWorkPayload Payload(
        TenantTerminationContributionPhase phase =
            TenantTerminationContributionPhase.Destroy) =>
        new(
            ProcessId,
            Guid.Parse("44444444-5555-6666-7777-888888888888"),
            OperationRevision: 8,
            phase,
            "reservations");

    private static TaskExecutionContext Context() =>
        new(
            RunId,
            DataRightsModuleMetadata.Name,
            ExecuteTenantTerminationOwnerWorkPayload.TaskName,
            DataRightsModuleMetadata.TenantTerminationWorkerGroup,
            "worker-1",
            "node-1",
            attempt: 1,
            scopeId: "tenant-a",
            correlationId: ProcessId);

    private static TaskExecutionContext GlobalContext() =>
        new(
            RunId,
            DataRightsModuleMetadata.Name,
            ExecuteGlobalTenantTerminationOwnerWorkPayload.TaskName,
            DataRightsModuleMetadata.TenantTerminationWorkerGroup,
            "worker-1",
            "node-1",
            attempt: 1,
            scopeId: null,
            correlationId: ProcessId,
            payloadVersion:
                ExecuteGlobalTenantTerminationOwnerWorkPayload
                    .PayloadVersion);

    private static TenantTerminationOwnerWorkStart Start(
        TenantTerminationContributionPhase phase =
            TenantTerminationContributionPhase.Destroy,
        TenantTerminationExecutionBoundary boundary =
            TenantTerminationExecutionBoundary.TenantScopedTask) =>
        new(
            DispatchRequired: true,
            TenantTerminationOwnerWorkState.Processing,
            WorkItemVersion: 2,
            "reservations",
            CatalogVersion: 3,
            CatalogDigest,
            boundary,
            Request(phase),
            ReadyDispatches: []);

    private static TenantTerminationContributionRequest Request(
        TenantTerminationContributionPhase phase) =>
        new(
            TenantTerminationContract.CurrentVersion,
            "tenant-a",
            ProcessId,
            Guid.Parse("22222222-3333-4444-5555-666666666666"),
            ApprovalRevision: 7,
            OperationRevision: 8,
            Guid.Parse("33333333-4444-5555-6666-777777777777"),
            phase,
            Guid.Parse("44444444-5555-6666-7777-888888888888"),
            Guid.Parse("55555555-6666-7777-8888-999999999999"),
            PolicyDigest,
            TenantTerminationCoordination.ExecutorActorId,
            Now.AddMinutes(2));

    private static TenantTerminationReplayDispatch Dispatch(
        TenantTerminationOwnerWorkStart start) =>
        TenantTerminationReplayDispatch.Create(
            start.Request!,
            start.OwnerKey,
            start.CatalogVersion,
            start.CatalogSha256,
            start.ExecutionBoundary,
            RunId,
            taskAttempt: 1,
            Now.AddSeconds(10));

    private static TenantTerminationContributionResult Contribution() =>
        new(
            TenantTerminationContributionStatus.Completed,
            "reservations.termination.destroyed",
            AffectedCount: 12,
            RetainedMinimumCount: 2,
            RemainingActiveCount: 0,
            HoldReviewAtUtc: null,
            SelectedProofRevision: 4,
            ResultingProofRevision: 5,
            CatalogVersion: 3,
            CatalogDigest,
            Now.AddSeconds(10));

    private static TenantTerminationPlannedDispatch PlannedDispatch()
    {
        Guid taskRunId = Guid.NewGuid();
        return new(
            ProcessId,
            Guid.NewGuid(),
            OperationRevision: 8,
            TenantTerminationContributionPhase.Destroy,
            "reservations",
            TenantTerminationExecutionBoundary.TenantScopedTask,
            DispatchSequence: 2,
            taskRunId,
            TenantTerminationExecutionIdentity.CreateTaskDeduplicationKey(
                taskRunId));
    }

    private sealed class FakeDispatcher(
        TenantTerminationOwnerWorkStart start,
        List<string> order)
        : ITaskCommandDispatcher
    {
        public RecordTenantTerminationOwnerResultCommand? Recorded
        {
            get;
            private set;
        }

        public Task<Result<TResponse>> DispatchAsync<TCommand, TResponse>(
            TaskExecutionContext context,
            TCommand command,
            CancellationToken cancellationToken)
            where TCommand : ICommand<TResponse>
        {
            object result = command switch
            {
                BeginTenantTerminationOwnerWorkCommand => this.Begin(),
                RecordTenantTerminationOwnerResultCommand record =>
                    this.Record(record),
                _ => throw new InvalidOperationException(
                    $"Unexpected command {command.GetType().Name}.")
            };
            return Task.FromResult((Result<TResponse>)result);
        }

        private Result<TenantTerminationOwnerWorkStart> Begin()
        {
            order.Add("begin");
            return Result.Success(start);
        }

        private Result<TenantTerminationOwnerResultRecorded> Record(
            RecordTenantTerminationOwnerResultCommand command)
        {
            order.Add("record");
            this.Recorded = command;
            return Result.Success(new TenantTerminationOwnerResultRecorded(
                TenantTerminationOwnerWorkState.Completed,
                start.WorkItemVersion + 1,
                ReadyDispatches: []));
        }
    }

    private sealed class FakeReplayStore(List<string> order)
        : ITenantTerminationReplayStore
    {
        public TenantTerminationReplayAttempt? Attempt { get; set; }
        public Action<TenantTerminationReplayJournalEntry>? AfterAppend
        {
            get;
            init;
        }
        public bool ReturnInvalidReceipt { get; init; }

        public Task<TenantTerminationReplayStoreReadiness> CheckReadinessAsync(
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TenantTerminationReplayAppendReceipt> AppendAsync(
            TenantTerminationReplayJournalEntry entry,
            CancellationToken cancellationToken)
        {
            order.Add(entry.Kind == TenantTerminationReplayEntryKind.Dispatch
                ? "append-dispatch"
                : "append-result");
            this.Attempt = entry.Kind switch
            {
                TenantTerminationReplayEntryKind.Dispatch =>
                    new(entry.Dispatch!, Result: null),
                TenantTerminationReplayEntryKind.Result =>
                    new(entry.Dispatch!, entry.Result),
                _ => throw new InvalidOperationException()
            };
            this.AfterAppend?.Invoke(entry);
            return Task.FromResult(new TenantTerminationReplayAppendReceipt(
                TenantTerminationReplayAppendReceipt.CurrentContractVersion,
                this.ReturnInvalidReceipt
                    ? new string('f', 64)
                    : entry.LogicalEntryId,
                entry.Kind,
                new(1, new string('c', 64)),
                Now.AddSeconds(10),
                new string('d', 64)));
        }

        public Task<TenantTerminationReplayAttempt?> ReadAttemptAsync(
            TenantTerminationReplayAttemptCoordinate coordinate,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                this.Attempt?.Dispatch.Coordinate == coordinate
                    ? this.Attempt
                    : null);

        public Task<TenantTerminationReplayIntent?> ReadIntentAsync(
            string tenantId,
            Guid processId,
            CancellationToken cancellationToken) =>
            Task.FromResult<TenantTerminationReplayIntent?>(null);

        public Task<TenantTerminationReplayCheckpoint>
            ReadTrustedCheckpointAsync(
                string tenantId,
                Guid processId,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<TenantTerminationReplayPage> ReadAfterAsync(
            string tenantId,
            Guid processId,
            TenantTerminationReplayCursor cursor,
            int pageSize,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingContributor(
        TenantTerminationContributionResult result,
        List<string> order,
        TenantTerminationExecutionBoundary boundary =
            TenantTerminationExecutionBoundary.TenantScopedTask,
        Action<TenantTerminationContributionRequest>? onExecute = null)
        : ITenantTerminationContributor
    {
        public TenantTerminationContributionRequest? Request
        {
            get;
            private set;
        }

        public TenantTerminationContributorDescriptor Descriptor { get; } =
            new(
                "reservations",
                TenantTerminationContract.CurrentVersion,
                [
                    new(
                        TenantTerminationContributionPhase.Destroy,
                        [],
                        boundary),
                    new(TenantTerminationContributionPhase.Export, [])
                ],
                MandatoryForProduction: true,
                CatalogVersion: 3,
                CatalogDigest);

        public Task<TenantTerminationContributionResult> ExecuteAsync(
            TenantTerminationContributionRequest request,
            CancellationToken cancellationToken)
        {
            order.Add("owner");
            this.Request = request;
            onExecute?.Invoke(request);
            return Task.FromResult(result);
        }
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : ISystemClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    private sealed class FixedScopeContext(string scopeId) : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId { get; } = scopeId;
    }

    private sealed class RecordingScheduler
        : ITenantTerminationTaskScheduler
    {
        public List<TenantTerminationPlannedDispatch> Dispatches { get; } = [];
        public string? TenantId { get; private set; }

        public Task EnqueueAsync(
            string tenantId,
            IReadOnlyCollection<TenantTerminationPlannedDispatch> dispatches,
            CancellationToken cancellationToken)
        {
            this.TenantId = tenantId;
            this.Dispatches.AddRange(dispatches);
            return Task.CompletedTask;
        }

        public Task EnqueueExportArtifactAsync(
            string tenantId,
            Guid processId,
            long operationRevision,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task EnqueueVerificationAsync(
            string tenantId,
            Guid processId,
            long operationRevision,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
