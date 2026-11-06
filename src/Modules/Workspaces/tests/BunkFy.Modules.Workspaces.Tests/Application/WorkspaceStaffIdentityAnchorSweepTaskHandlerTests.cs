namespace BunkFy.Modules.Workspaces.Tests.Application;

using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Application.Models;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Application.Tasks;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Scoping;
using Gma.Framework.Tasks;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspaceStaffIdentityAnchorSweepTaskHandlerTests
{
    private const string TenantId =
        "abcdef00-0000-0000-0000-000000000001";
    private static readonly DateTimeOffset Now =
        new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Passes_commit_before_record_and_observation_uses_a_fresh_dispatch()
    {
        Guid applicationId = Guid.NewGuid();
        Guid staffMemberId = Guid.NewGuid();
        Guid resolutionEventId = Guid.NewGuid();
        List<string> calls = [];
        WorkspaceStaffIdentityAnchorSweepPage page = Page(
            new WorkspaceStaffIdentityAnchorSweepCandidate(
                applicationId,
                IdentityAnchorSweepOrdinal: 1,
                "subject-1",
                HasLocalAnchorState: true));
        StaffWorkspaceOnboardingIdentityAnchorResolutionRequest request = new(
            resolutionEventId,
            applicationId,
            staffMemberId,
            WorkspaceApplicationVersion: 7,
            StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition
                .CompletedRedacted,
            Now);
        FakeTaskCommandDispatcher dispatcher = new(
            page,
            calls,
            [
                Candidate(
                    applicationId,
                    WorkspaceStaffIdentityAnchorSweepCandidateOutcome
                        .PassOneCommitted),
                Candidate(
                    applicationId,
                    WorkspaceStaffIdentityAnchorSweepCandidateOutcome
                        .ResolutionReadyToRecord,
                    request),
                Candidate(
                    applicationId,
                    WorkspaceStaffIdentityAnchorSweepCandidateOutcome
                        .ObservedNow)
            ]);
        StubOutcomeReader reader = new(
            calls,
            requests =>
            [
                new(
                    requests[0].ApplicationId,
                    StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus
                        .Unresolved,
                    staffMemberId,
                    StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle
                        .Active,
                    StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Exact,
                    WorkspaceApplicationVersion: null,
                    ResolutionDisposition: null,
                    resolutionEventId)
            ]);
        RecordingRecorder recorder = new(calls);
        WorkspaceStaffIdentityAnchorSweepTaskHandler handler = new(
            dispatcher,
            reader,
            recorder,
            new TestIdGenerator(),
            new TestScopeContext(TenantId));

        await handler.HandleAsync(
            new ReconcileWorkspaceStaffIdentityAnchorsPayload(),
            Context(),
            CancellationToken.None);

        Assert.Equal(
            [
                "prepare",
                "batch-read",
                "candidate:PassOneCommitted",
                "candidate:ResolutionReadyToRecord",
                "staff-record",
                "candidate:ObservedNow",
                "advance"
            ],
            calls);
        Assert.Equal(request, Assert.Single(recorder.Requests));
        Assert.NotNull(dispatcher.Advance);
        Assert.Equal(1, dispatcher.Advance.Counts.ScannedCount);
        Assert.Equal(1, dispatcher.Advance.Counts.ObservedCount);
        Assert.Equal(0, dispatcher.Advance.Counts.AlreadyObservedCount);
        Assert.Equal(1, dispatcher.Advance.Counts.PassOneCommittedCount);
        Assert.Equal(
            1,
            dispatcher.Advance.Counts.ResolutionRecordConfirmedCount);
    }

    [Fact]
    public async Task Already_observed_is_not_reported_as_newly_settled()
    {
        Guid applicationId = Guid.NewGuid();
        List<string> calls = [];
        FakeTaskCommandDispatcher dispatcher = new(
            Page(new WorkspaceStaffIdentityAnchorSweepCandidate(
                applicationId,
                IdentityAnchorSweepOrdinal: 1,
                "subject-1",
                HasLocalAnchorState: true)),
            calls,
            [Candidate(
                applicationId,
                WorkspaceStaffIdentityAnchorSweepCandidateOutcome
                    .AlreadyObserved)]);
        StubOutcomeReader reader = new(
            calls,
            requests =>
            [Resolved(requests[0].ApplicationId)]);
        RecordingRecorder recorder = new(calls);
        WorkspaceStaffIdentityAnchorSweepTaskHandler handler = new(
            dispatcher,
            reader,
            recorder,
            new TestIdGenerator(),
            new TestScopeContext(TenantId));

        await handler.HandleAsync(
            new ReconcileWorkspaceStaffIdentityAnchorsPayload(),
            Context(),
            CancellationToken.None);

        Assert.NotNull(dispatcher.Advance);
        Assert.Equal(0, dispatcher.Advance.Counts.ObservedCount);
        Assert.Equal(1, dispatcher.Advance.Counts.AlreadyObservedCount);
        Assert.Empty(recorder.Requests);
    }

    [Fact]
    public async Task Absent_rows_are_skipped_but_local_contradictions_are_conflicts()
    {
        Guid cleanApplicationId = Guid.NewGuid();
        Guid contradictoryApplicationId = Guid.NewGuid();
        List<string> calls = [];
        WorkspaceStaffIdentityAnchorSweepPage page = Page(
            new(cleanApplicationId, 1, "subject-clean", false),
            new(contradictoryApplicationId, 2, "subject-local", true));
        FakeTaskCommandDispatcher dispatcher = new(
            page,
            calls,
            [Candidate(
                contradictoryApplicationId,
                WorkspaceStaffIdentityAnchorSweepCandidateOutcome.NoAnchor)]);
        StubOutcomeReader reader = new(
            calls,
            requests => requests.Select(request => Absent(
                    request.ApplicationId))
                .ToArray());
        WorkspaceStaffIdentityAnchorSweepTaskHandler handler = new(
            dispatcher,
            reader,
            new RecordingRecorder(calls),
            new TestIdGenerator(),
            new TestScopeContext(TenantId));

        await handler.HandleAsync(
            new ReconcileWorkspaceStaffIdentityAnchorsPayload(),
            Context(),
            CancellationToken.None);

        Assert.Equal(1, dispatcher.CandidateDispatchCount);
        Assert.NotNull(dispatcher.Advance);
        Assert.Equal(1, dispatcher.Advance.Counts.NoAnchorCount);
        Assert.Equal(1, dispatcher.Advance.Counts.ConflictCount);
    }

    [Fact]
    public async Task Duplicate_staff_keys_abort_before_any_candidate_transaction()
    {
        Guid firstId = Guid.NewGuid();
        Guid secondId = Guid.NewGuid();
        List<string> calls = [];
        FakeTaskCommandDispatcher dispatcher = new(
            Page(
                new(firstId, 1, "subject-1", true),
                new(secondId, 2, "subject-2", true)),
            calls,
            []);
        StubOutcomeReader reader = new(
            calls,
            _ => [Absent(firstId), Absent(firstId)]);
        WorkspaceStaffIdentityAnchorSweepTaskHandler handler = new(
            dispatcher,
            reader,
            new RecordingRecorder(calls),
            new TestIdGenerator(),
            new TestScopeContext(TenantId));

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                handler.HandleAsync(
                    new ReconcileWorkspaceStaffIdentityAnchorsPayload(),
                    Context(),
                    CancellationToken.None));

        Assert.Contains(
            "Workspaces.IdentityAnchorSweepStaffBatchInvalid",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Equal(0, dispatcher.CandidateDispatchCount);
        Assert.Null(dispatcher.Advance);
    }

    [Fact]
    public async Task Malformed_absent_cannot_be_counted_as_no_anchor()
    {
        Guid applicationId = Guid.NewGuid();
        List<string> calls = [];
        FakeTaskCommandDispatcher dispatcher = new(
            Page(new WorkspaceStaffIdentityAnchorSweepCandidate(
                applicationId,
                IdentityAnchorSweepOrdinal: 1,
                "subject-1",
                HasLocalAnchorState: false)),
            calls,
            []);
        StubOutcomeReader reader = new(
            calls,
            _ =>
            [
                new(
                    applicationId,
                    StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus
                        .Absent,
                    Guid.NewGuid(),
                    StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle
                        .Active,
                    StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Exact,
                    WorkspaceApplicationVersion: null,
                    ResolutionDisposition: null,
                    Guid.NewGuid())
            ]);
        WorkspaceStaffIdentityAnchorSweepTaskHandler handler = new(
            dispatcher,
            reader,
            new RecordingRecorder(calls),
            new TestIdGenerator(),
            new TestScopeContext(TenantId));

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                handler.HandleAsync(
                    new ReconcileWorkspaceStaffIdentityAnchorsPayload(),
                    Context(),
                    CancellationToken.None));

        Assert.Contains(
            "Workspaces.IdentityAnchorSweepStaffBatchInvalid",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Equal(0, dispatcher.CandidateDispatchCount);
        Assert.Null(dispatcher.Advance);
    }

    [Fact]
    public async Task Malformed_resolved_cannot_settle_the_bounded_cycle()
    {
        Guid applicationId = Guid.NewGuid();
        List<string> calls = [];
        FakeTaskCommandDispatcher dispatcher = new(
            Page(new WorkspaceStaffIdentityAnchorSweepCandidate(
                applicationId,
                IdentityAnchorSweepOrdinal: 1,
                "subject-1",
                HasLocalAnchorState: true)),
            calls,
            []);
        StubOutcomeReader reader = new(
            calls,
            _ =>
            [
                new(
                    applicationId,
                    StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus
                        .Resolved,
                    Guid.NewGuid(),
                    StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle
                        .Active,
                    StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Exact,
                    WorkspaceApplicationVersion: null,
                    ResolutionDisposition: null,
                    Guid.NewGuid())
            ]);
        WorkspaceStaffIdentityAnchorSweepTaskHandler handler = new(
            dispatcher,
            reader,
            new RecordingRecorder(calls),
            new TestIdGenerator(),
            new TestScopeContext(TenantId));

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                handler.HandleAsync(
                    new ReconcileWorkspaceStaffIdentityAnchorsPayload(),
                    Context(),
                    CancellationToken.None));

        Assert.Contains(
            "Workspaces.IdentityAnchorSweepStaffBatchInvalid",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Equal(0, dispatcher.CandidateDispatchCount);
        Assert.Null(dispatcher.Advance);
    }

    [Fact]
    public async Task Noncanonical_task_scope_is_rejected_before_dispatch()
    {
        string noncanonical = TenantId.ToUpperInvariant();
        List<string> calls = [];
        FakeTaskCommandDispatcher dispatcher = new(
            Page(),
            calls,
            []);
        WorkspaceStaffIdentityAnchorSweepTaskHandler handler = new(
            dispatcher,
            new StubOutcomeReader(calls, _ => []),
            new RecordingRecorder(calls),
            new TestIdGenerator(),
            new TestScopeContext(noncanonical));

        InvalidOperationException exception =
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                handler.HandleAsync(
                    new ReconcileWorkspaceStaffIdentityAnchorsPayload(),
                    Context(noncanonical),
                    CancellationToken.None));

        Assert.Contains(
            "Workspaces.IdentityAnchorSweepTaskInvalid",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Empty(calls);
    }

    private static WorkspaceStaffIdentityAnchorSweepCandidateResult Candidate(
        Guid applicationId,
        WorkspaceStaffIdentityAnchorSweepCandidateOutcome outcome,
        StaffWorkspaceOnboardingIdentityAnchorResolutionRequest? request =
            null) =>
        new(applicationId, outcome, request);

    private static WorkspaceStaffIdentityAnchorSweepPage Page(
        params WorkspaceStaffIdentityAnchorSweepCandidate[] candidates) =>
        new(
            Guid.NewGuid(),
            CheckpointVersion: 4,
            Guid.NewGuid(),
            UpperOrdinal: Math.Max(1, candidates.Length),
            ExpectedAfterOrdinal: null,
            candidates.Length == 0
                ? 1
                : candidates[^1].IdentityAnchorSweepOrdinal,
            ReachedEnd: true,
            AdvanceRequired: true,
            Array.AsReadOnly(candidates));

    private static StaffWorkspaceOnboardingIdentityAnchorOutcome Absent(
        Guid applicationId) =>
        new(
            applicationId,
            StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Absent,
            StaffMemberId: null,
            StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Unknown,
            StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Unknown,
            WorkspaceApplicationVersion: null,
            ResolutionDisposition: null);

    private static StaffWorkspaceOnboardingIdentityAnchorOutcome Resolved(
        Guid applicationId) =>
        new(
            applicationId,
            StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Resolved,
            Guid.NewGuid(),
            StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Active,
            StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Exact,
            WorkspaceApplicationVersion: 7,
            StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition
                .CompletedRedacted,
            Guid.NewGuid());

    private static TaskExecutionContext Context(string scopeId = TenantId) =>
        new(
        Guid.NewGuid(),
        WorkspacesModuleMetadata.Name,
        ReconcileWorkspaceStaffIdentityAnchorsPayload.TaskName,
        ReconcileWorkspaceStaffIdentityAnchorsPayload.WorkerGroup,
        "worker-1",
        "node-1",
        attempt: 1,
        scopeId: scopeId);

    private sealed class FakeTaskCommandDispatcher(
        WorkspaceStaffIdentityAnchorSweepPage page,
        List<string> calls,
        IEnumerable<WorkspaceStaffIdentityAnchorSweepCandidateResult>
            candidates)
        : IWorkspaceStaffIdentityAnchorSweepTransactionDispatcher
    {
        private readonly Queue<
            WorkspaceStaffIdentityAnchorSweepCandidateResult> candidates =
            new(candidates);

        public int CandidateDispatchCount { get; private set; }
        public WorkspaceStaffIdentityAnchorSweepAdvance? Advance
        {
            get;
            private set;
        }

        public Task<Result<WorkspaceStaffIdentityAnchorSweepPage>>
            PreparePageAsync(
            TaskExecutionContext context,
            PrepareWorkspaceStaffIdentityAnchorSweepPageCommand command,
            CancellationToken cancellationToken)
        {
            calls.Add("prepare");
            return Task.FromResult(Result.Success(page));
        }

        public Task<Result<WorkspaceStaffIdentityAnchorSweepCandidateResult>>
            ReconcileCandidateAsync(
                TaskExecutionContext context,
                ReconcileWorkspaceStaffIdentityAnchorSweepCandidateCommand
                    command,
                CancellationToken cancellationToken)
        {
            this.CandidateDispatchCount++;
            WorkspaceStaffIdentityAnchorSweepCandidateResult result =
                this.candidates.Dequeue();
            calls.Add($"candidate:{result.Outcome}");
            return Task.FromResult(Result.Success(result));
        }

        public Task<Result<Unit>> AdvanceAsync(
            TaskExecutionContext context,
            AdvanceWorkspaceStaffIdentityAnchorSweepCommand command,
            CancellationToken cancellationToken)
        {
            this.Advance = command.Advance;
            calls.Add("advance");
            return Task.FromResult(Result.Success(Unit.Value));
        }
    }

    private sealed class StubOutcomeReader(
        List<string> calls,
        Func<IReadOnlyList<
            StaffWorkspaceOnboardingIdentityAnchorOutcomeRequest>,
            IReadOnlyList<StaffWorkspaceOnboardingIdentityAnchorOutcome>> read)
        : IStaffWorkspaceOnboardingIdentityAnchorOutcomeReader
    {
        public Task<IReadOnlyList<
            StaffWorkspaceOnboardingIdentityAnchorOutcome>> ReadAsync(
                IReadOnlyList<
                    StaffWorkspaceOnboardingIdentityAnchorOutcomeRequest>
                        requests,
                CancellationToken cancellationToken = default)
        {
            calls.Add("batch-read");
            Assert.InRange(requests.Count, 1, 500);
            return Task.FromResult(read(requests));
        }
    }

    private sealed class RecordingRecorder(List<string> calls)
        : IStaffWorkspaceOnboardingIdentityAnchorResolutionRecorder
    {
        public List<StaffWorkspaceOnboardingIdentityAnchorResolutionRequest>
            Requests
        { get; } = [];

        public Task<StaffWorkspaceOnboardingIdentityAnchorResolutionResult>
            RecordAsync(
                StaffWorkspaceOnboardingIdentityAnchorResolutionRequest request,
                CancellationToken cancellationToken = default)
        {
            calls.Add("staff-record");
            this.Requests.Add(request);
            return Task.FromResult(
                new StaffWorkspaceOnboardingIdentityAnchorResolutionResult(
                    StaffWorkspaceOnboardingIdentityAnchorResolutionStatus
                        .Recorded));
        }
    }

    private sealed class TestIdGenerator : IIdGenerator
    {
        public Guid NewId() => Guid.NewGuid();
    }

    private sealed class TestScopeContext(string scopeId) : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId { get; } = scopeId;
    }
}
