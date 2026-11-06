namespace BunkFy.Modules.Workspaces.Tests;

using BunkFy.Modules.Retention.Contracts;
using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Application.Contributors;
using BunkFy.Modules.Workspaces.Application.Handlers;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Microsoft.Extensions.Options;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspaceStaffOnboardingRetentionExecutionTests
{
    private const string TenantId =
        "10000000-0000-0000-0000-000000000001";
    private static readonly Guid ExecutionId =
        Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset StartedAtUtc =
        new(2026, 8, 22, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Retry_preserves_cumulative_counts_and_fences_stale_attempts()
    {
        WorkspaceStaffOnboardingRetentionExecution execution = Start();
        Assert.True(execution.RecordCandidate(1, affected: true).IsSuccess);
        Assert.True(execution.RecordCandidate(1, affected: false).IsSuccess);

        Assert.Equal(
            WorkspaceStaffRetentionErrors.ExecutionTransitionInvalid,
            execution.BeginRetry(
                attempt: 2,
                StartedAtUtc.AddMinutes(1),
                StartedAtUtc.AddMinutes(10)).Error);

        Result failed = execution.Complete(
            WorkspaceStaffOnboardingRetentionExecutionState.Failed,
            attempt: 1,
            scannedCount: 3,
            remainingCount: 1,
            WorkspaceStaffOnboardingRetentionCoordinates
                .ReconciliationFailedOutcome,
            StartedAtUtc.AddMinutes(2));

        Assert.True(failed.IsSuccess);
        Assert.Equal(3, execution.ScannedCount);
        Assert.Equal(1, execution.AffectedCount);
        Assert.True(execution.BeginRetry(
            attempt: 2,
            StartedAtUtc.AddMinutes(3),
            StartedAtUtc.AddMinutes(13)).IsSuccess);
        Assert.Equal(3, execution.ScannedCount);
        Assert.Equal(1, execution.AffectedCount);
        Assert.Null(execution.CompletedAtUtc);
        Assert.Null(execution.RemainingCount);
        Assert.Null(execution.OutcomeCode);

        Assert.Equal(
            WorkspaceStaffRetentionErrors.ExecutionTransitionInvalid,
            execution.RecordCandidate(1, affected: true).Error);
        Assert.Equal(
            WorkspaceStaffRetentionErrors.ExecutionResultInvalid,
            execution.Complete(
                WorkspaceStaffOnboardingRetentionExecutionState.Completed,
                attempt: 1,
                scannedCount: 3,
                remainingCount: 0,
                WorkspaceStaffOnboardingRetentionCoordinates.CompletedOutcome,
                StartedAtUtc.AddMinutes(4)).Error);

        Assert.True(execution.RecordCandidate(2, affected: true).IsSuccess);
        DateTimeOffset completedAtUtc = StartedAtUtc.AddMinutes(5);
        Assert.True(execution.Complete(
            WorkspaceStaffOnboardingRetentionExecutionState.Completed,
            attempt: 2,
            scannedCount: 4,
            remainingCount: 0,
            WorkspaceStaffOnboardingRetentionCoordinates.CompletedOutcome,
            completedAtUtc).IsSuccess);
        long terminalVersion = execution.Version;

        Assert.True(execution.Complete(
            WorkspaceStaffOnboardingRetentionExecutionState.Completed,
            attempt: 2,
            scannedCount: 4,
            remainingCount: 0,
            WorkspaceStaffOnboardingRetentionCoordinates.CompletedOutcome,
            completedAtUtc).IsSuccess);
        Assert.Equal(terminalVersion, execution.Version);
        Assert.Equal(
            WorkspaceStaffRetentionErrors.ExecutionTransitionInvalid,
            execution.BeginRetry(
                attempt: 3,
                StartedAtUtc.AddMinutes(6),
                StartedAtUtc.AddMinutes(16)).Error);
    }

    [Fact]
    public void Completion_accepts_only_proven_scan_progress()
    {
        WorkspaceStaffOnboardingRetentionExecution execution = Start();
        Assert.True(execution.RecordCandidate(1, affected: false).IsSuccess);

        Assert.Equal(
            WorkspaceStaffRetentionErrors.ExecutionResultInvalid,
            execution.Complete(
                WorkspaceStaffOnboardingRetentionExecutionState.Completed,
                attempt: 1,
                scannedCount: 2,
                remainingCount: 0,
                WorkspaceStaffOnboardingRetentionCoordinates.CompletedOutcome,
                StartedAtUtc.AddMinutes(1)).Error);
        Assert.Equal(
            WorkspaceStaffRetentionErrors.ExecutionResultInvalid,
            execution.Complete(
                WorkspaceStaffOnboardingRetentionExecutionState.Failed,
                attempt: 1,
                scannedCount: 1,
                remainingCount: 0,
                WorkspaceStaffOnboardingRetentionCoordinates
                    .ReconciliationFailedOutcome,
                StartedAtUtc.AddMinutes(1)).Error);
        Assert.True(execution.Complete(
            WorkspaceStaffOnboardingRetentionExecutionState.Failed,
            attempt: 1,
            scannedCount: 2,
            remainingCount: 1,
            WorkspaceStaffOnboardingRetentionCoordinates
                .ReconciliationFailedOutcome,
            StartedAtUtc.AddMinutes(1)).IsSuccess);
    }

    [Fact]
    public async Task Begin_retries_failed_execution_and_exactly_replays_terminal_result()
    {
        FakeExecutionRepository repository = new();
        RecordingExecutionLock executionLock = new();
        WorkspaceStaffOnboardingRetentionExecutionCoordinator coordinator =
            new(executionLock, repository, new TestScopeContext());
        BeginWorkspaceStaffOnboardingRetentionExecutionCommandHandler begin =
            new(repository, coordinator, new TestScopeContext());
        CompleteWorkspaceStaffOnboardingRetentionExecutionCommandHandler
            complete = new(coordinator);

        Result<WorkspaceStaffOnboardingRetentionExecutionStart> started =
            await begin.HandleAsync(
                new(NewRequest(attempt: 1, StartedAtUtc)),
                CancellationToken.None);
        Assert.True(started.IsSuccess, started.Error.Code);
        Assert.True(started.Value.DispatchRequired);
        Assert.Equal(0, started.Value.ScannedCount);
        Assert.Equal(0, started.Value.AffectedCount);

        Result<RetentionContributionResult> failed = await complete.HandleAsync(
            new(
                ExecutionId,
                Attempt: 1,
                WorkspaceStaffOnboardingRetentionExecutionState.Failed,
                ScannedCount: 1,
                RemainingCount: 1,
                WorkspaceStaffOnboardingRetentionCoordinates
                    .ReconciliationFailedOutcome,
                StartedAtUtc.AddMinutes(1)),
            CancellationToken.None);
        Assert.True(failed.IsSuccess, failed.Error.Code);

        Result<WorkspaceStaffOnboardingRetentionExecutionStart> replay =
            await begin.HandleAsync(
                new(NewRequest(attempt: 1, StartedAtUtc)),
                CancellationToken.None);
        Assert.True(replay.IsSuccess, replay.Error.Code);
        Assert.False(replay.Value.DispatchRequired);
        Assert.Equal(failed.Value, replay.Value.CompletedResult);

        DateTimeOffset retryStartedAtUtc = StartedAtUtc.AddMinutes(2);
        Result<WorkspaceStaffOnboardingRetentionExecutionStart> retry =
            await begin.HandleAsync(
                new(NewRequest(attempt: 2, retryStartedAtUtc)),
                CancellationToken.None);
        Assert.True(retry.IsSuccess, retry.Error.Code);
        Assert.True(retry.Value.DispatchRequired);
        Assert.Equal(1, retry.Value.ScannedCount);
        Assert.Equal(0, retry.Value.AffectedCount);
        Assert.Equal(4, executionLock.AcquisitionCount);
    }

    [Fact]
    public async Task Begin_rejects_same_attempt_with_changed_time_window()
    {
        FakeExecutionRepository repository = new(Start());
        WorkspaceStaffOnboardingRetentionExecutionCoordinator coordinator =
            new(
                new RecordingExecutionLock(),
                repository,
                new TestScopeContext());
        BeginWorkspaceStaffOnboardingRetentionExecutionCommandHandler begin =
            new(repository, coordinator, new TestScopeContext());

        RetentionContributionRequest request = NewRequest(
            attempt: 1,
            StartedAtUtc.AddSeconds(1));
        Result<WorkspaceStaffOnboardingRetentionExecutionStart> result =
            await begin.HandleAsync(
                new(request),
                CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            WorkspaceStaffRetentionErrors.ExecutionCoordinateInvalid,
            result.Error);
    }

    [Fact]
    public async Task Stale_attempt_is_rejected_before_candidate_or_source_access()
    {
        WorkspaceStaffOnboardingRetentionExecution execution = Start();
        Assert.True(execution.BeginRetry(
            attempt: 2,
            StartedAtUtc.AddMinutes(1),
            StartedAtUtc.AddMinutes(11)).IsSuccess);
        FakeExecutionRepository repository = new(execution);
        WorkspaceStaffOnboardingRetentionExecutionCoordinator coordinator =
            new(
                new RecordingExecutionLock(),
                repository,
                new TestScopeContext());
        ThrowingCandidateRepository candidates = new();
        ListWorkspaceStaffOnboardingRetentionCandidatesCommandHandler list =
            new(candidates, coordinator);

        Result<IReadOnlyList<WorkspaceStaffOnboardingRetentionCandidate>>
            listed = await list.HandleAsync(
                new(
                    ExecutionId,
                    Attempt: 1,
                    StartedAtUtc,
                    MaximumCount: 2),
                CancellationToken.None);

        Assert.True(listed.IsFailure);
        Assert.Equal(0, candidates.CallCount);

        ReconcileWorkspaceStaffOnboardingRetentionCandidateCommandHandler
            reconcile = new(
                null!,
                null!,
                coordinator,
                null!,
                null!,
                null!,
                null!,
                Options.Create(
                    new WorkspaceStaffOnboardingRetentionOptions()),
                new TestClock());
        Result<WorkspaceStaffOnboardingRetentionReconciliation> reconciled =
            await reconcile.HandleAsync(
                new(
                    ExecutionId,
                    Attempt: 1,
                    Guid.NewGuid(),
                    ExpectedVersion: 1),
                CancellationToken.None);

        Assert.True(reconciled.IsFailure);
    }

    private static WorkspaceStaffOnboardingRetentionExecution Start() =>
        WorkspaceStaffOnboardingRetentionExecution.Start(
            ExecutionId,
            TenantId,
            WorkspaceStaffOnboardingRetentionCoordinates.DataClassKey,
            WorkspaceStaffOnboardingRetentionCoordinates.ExecutionPolicyVersion,
            attempt: 1,
            StartedAtUtc,
            StartedAtUtc.AddMinutes(10)).Value;

    private static RetentionContributionRequest NewRequest(
        int attempt,
        DateTimeOffset startedAtUtc) =>
        new(
            RetentionExecutionContract.CurrentVersion,
            ExecutionId,
            TenantId,
            PropertyId: null,
            WorkspaceStaffOnboardingRetentionCoordinates.OwnerKey,
            WorkspaceStaffOnboardingRetentionCoordinates.DataClassKey,
            WorkspaceStaffOnboardingRetentionCoordinates.ExecutionPolicyVersion,
            attempt,
            startedAtUtc,
            startedAtUtc.AddMinutes(10));

    private sealed class FakeExecutionRepository(
        WorkspaceStaffOnboardingRetentionExecution? execution = null)
        : IWorkspaceStaffOnboardingRetentionExecutionRepository
    {
        private WorkspaceStaffOnboardingRetentionExecution? execution =
            execution;

        public Task<WorkspaceStaffOnboardingRetentionExecution?> GetAsync(
            Guid executionId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                this.execution?.Id == executionId ? this.execution : null);

        public Task AddAsync(
            WorkspaceStaffOnboardingRetentionExecution added,
            CancellationToken cancellationToken)
        {
            Assert.Null(this.execution);
            this.execution = added;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingExecutionLock
        : IWorkspaceStaffOnboardingRetentionExecutionLock
    {
        public int AcquisitionCount { get; private set; }

        public Task AcquireAsync(
            string tenantId,
            Guid executionId,
            CancellationToken cancellationToken)
        {
            Assert.Equal(TenantId, tenantId);
            Assert.Equal(ExecutionId, executionId);
            this.AcquisitionCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingCandidateRepository
        : IWorkspaceStaffOnboardingRetentionRepository
    {
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<WorkspaceStaffOnboardingRetentionCandidate>>
            ListEligibleAsync(
                string tenantId,
                DateTimeOffset sourceExpiredBeforeUtc,
                int maximumCount,
                CancellationToken cancellationToken)
        {
            this.CallCount++;
            throw new InvalidOperationException(
                "Candidate access must be fenced before this point.");
        }
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => TenantId;
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => StartedAtUtc.AddMinutes(2);
    }
}
