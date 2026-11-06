namespace BunkFy.Modules.Reservations.Tests;

using BunkFy.Modules.Reservations.Application.Commands;
using BunkFy.Modules.Reservations.Application.Contributors;
using BunkFy.Modules.Reservations.Application.Handlers;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Domain.DataRights;
using BunkFy.Modules.Reservations.Domain.Retention;
using BunkFy.Modules.Retention.Contracts;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Scoping;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ReservationRetentionExecutionCommandHandlerTests
{
    private const string TenantId = "tenant-a";
    private static readonly DateTimeOffset StartedAtUtc =
        ReservationRetentionTestData.Now;

    [Fact]
    public async Task Failed_completion_retries_and_fences_stale_attempt()
    {
        ReservationRetentionExecution execution = StartExecution();
        Assert.True(execution.RecordAffected().IsSuccess);
        ReservationRetentionSweepCheckpoint checkpoint =
            CreateCheckpoint();
        FakeExecutionRepository repository = new(
            execution,
            checkpoint);
        CompleteReservationRetentionExecutionCommandHandler complete =
            new(repository);
        BeginReservationRetentionExecutionCommandHandler begin = new(
            repository,
            new TestScopeContext(),
            new ThrowingIdGenerator());
        DateTimeOffset failedAtUtc = StartedAtUtc.AddMinutes(1);

        Result<RetentionContributionResult> failed =
            await complete.HandleAsync(
                new(
                    execution.Id,
                    Attempt: 1,
                    ReservationRetentionExecutionState.Failed,
                    ScannedCount: 1,
                    RemainingCount: 1,
                    ReservationRetentionCoordinates
                        .MutationFailedOutcome,
                    failedAtUtc,
                    HoldReviewDueAtUtc: null,
                    ExpectedAfterProjectionOrdinal: 0,
                    NextAfterProjectionOrdinal: 42),
                CancellationToken.None);

        Assert.True(failed.IsSuccess);
        Assert.Equal(1, failed.Value.AffectedCount);
        Assert.Equal(
            ReservationRetentionExecutionState.Failed,
            execution.State);
        Assert.Equal(0, checkpoint.AfterProjectionOrdinal);
        Assert.Null(checkpoint.LastExecutionId);
        Assert.Equal(StartedAtUtc, checkpoint.UpdatedAtUtc);

        DateTimeOffset retryStartedAtUtc =
            StartedAtUtc.AddMinutes(2);
        Result<ReservationRetentionExecutionStart> retried =
            await begin.HandleAsync(
                new(Request(
                    execution.Id,
                    attempt: 2,
                    retryStartedAtUtc,
                    retryStartedAtUtc.AddMinutes(10))),
                CancellationToken.None);

        Assert.True(retried.IsSuccess);
        Assert.True(retried.Value.DispatchRequired);
        Assert.Equal(0, retried.Value.StartingProjectionOrdinal);
        Assert.Equal(1, retried.Value.AffectedCount);
        Assert.Equal(2, execution.Attempt);
        Assert.Equal(
            ReservationRetentionExecutionState.Running,
            execution.State);

        Result<RetentionContributionResult> stale =
            await complete.HandleAsync(
                Completion(
                    execution.Id,
                    attempt: 1,
                    StartedAtUtc.AddMinutes(3)),
                CancellationToken.None);

        Assert.Equal(
            "Reservations.RetentionExecutionResultInvalid",
            stale.Error.Code);
        Assert.Equal(
            ReservationRetentionExecutionState.Running,
            execution.State);
        Assert.Equal(0, checkpoint.AfterProjectionOrdinal);

        Result<RetentionContributionResult> completed =
            await complete.HandleAsync(
                Completion(
                    execution.Id,
                    attempt: 2,
                    StartedAtUtc.AddMinutes(3)),
                CancellationToken.None);

        Assert.True(completed.IsSuccess);
        Assert.Equal(
            ReservationRetentionExecutionState.Completed,
            execution.State);
        Assert.Equal(42, checkpoint.AfterProjectionOrdinal);
        Assert.Equal(execution.Id, checkpoint.LastExecutionId);
    }

    [Fact]
    public async Task Begin_retry_rewinds_a_proven_legacy_checkpoint()
    {
        ReservationRetentionExecution execution = StartExecution();
        DateTimeOffset failedAtUtc = StartedAtUtc.AddMinutes(1);
        Assert.True(execution.Complete(
            ReservationRetentionExecutionState.Failed,
            attempt: 1,
            scannedCount: 0,
            remainingCount: 1,
            ReservationRetentionCoordinates.MutationFailedOutcome,
            failedAtUtc,
            holdReviewDueAtUtc: null).IsSuccess);
        ReservationRetentionSweepCheckpoint checkpoint =
            CreateCheckpoint();
        Assert.True(checkpoint.Advance(
            expectedAfterProjectionOrdinal: 0,
            nextAfterProjectionOrdinal: 42,
            execution.Id,
            failedAtUtc).IsSuccess);
        FakeExecutionRepository repository = new(
            execution,
            checkpoint);
        BeginReservationRetentionExecutionCommandHandler begin = new(
            repository,
            new TestScopeContext(),
            new ThrowingIdGenerator());
        DateTimeOffset retryStartedAtUtc =
            StartedAtUtc.AddMinutes(2);

        Result<ReservationRetentionExecutionStart> retried =
            await begin.HandleAsync(
                new(Request(
                    execution.Id,
                    attempt: 2,
                    retryStartedAtUtc,
                    retryStartedAtUtc.AddMinutes(10))),
                CancellationToken.None);

        Assert.True(retried.IsSuccess);
        Assert.True(retried.Value.DispatchRequired);
        Assert.Equal(0, checkpoint.AfterProjectionOrdinal);
        Assert.Null(checkpoint.LastExecutionId);
        Assert.Equal(retryStartedAtUtc, checkpoint.UpdatedAtUtc);
        Assert.Equal(2, execution.Attempt);
        Assert.Equal(
            ReservationRetentionExecutionState.Running,
            execution.State);
    }

    private static ReservationRetentionExecution StartExecution() =>
        ReservationRetentionExecution.Start(
            Guid.NewGuid(),
            TenantId,
            ReservationRetentionCoordinates.DataClassKey,
            ReservationRetentionCoordinates.ExecutionPolicyVersion,
            attempt: 1,
            startingProjectionOrdinal: 0,
            StartedAtUtc,
            StartedAtUtc.AddMinutes(15)).Value;

    private static ReservationRetentionSweepCheckpoint
        CreateCheckpoint() =>
        ReservationRetentionSweepCheckpoint.Create(
            Guid.NewGuid(),
            TenantId,
            ReservationRetentionCoordinates.DataClassKey,
            ReservationRetentionCoordinates.ExecutionPolicyVersion,
            StartedAtUtc).Value;

    private static RetentionContributionRequest Request(
        Guid executionId,
        int attempt,
        DateTimeOffset startedAtUtc,
        DateTimeOffset deadlineUtc) =>
        new(
            RetentionExecutionContract.CurrentVersion,
            executionId,
            TenantId,
            PropertyId: null,
            ReservationRetentionCoordinates.OwnerKey,
            ReservationRetentionCoordinates.DataClassKey,
            ReservationRetentionCoordinates.ExecutionPolicyVersion,
            attempt,
            startedAtUtc,
            deadlineUtc);

    private static CompleteReservationRetentionExecutionCommand
        Completion(
            Guid executionId,
            int attempt,
            DateTimeOffset completedAtUtc) =>
        new(
            executionId,
            attempt,
            ReservationRetentionExecutionState.Completed,
            ScannedCount: 1,
            RemainingCount: 0,
            ReservationRetentionCoordinates.CompletedOutcome,
            completedAtUtc,
            HoldReviewDueAtUtc: null,
            ExpectedAfterProjectionOrdinal: 0,
            NextAfterProjectionOrdinal: 42);

    private sealed class FakeExecutionRepository(
        ReservationRetentionExecution execution,
        ReservationRetentionSweepCheckpoint checkpoint)
        : IReservationRetentionExecutionRepository
    {
        public Task<ReservationRetentionExecution?> GetExecutionAsync(
            Guid executionId,
            CancellationToken cancellationToken) =>
            Task.FromResult<ReservationRetentionExecution?>(
                execution.Id == executionId ? execution : null);

        public Task AddExecutionAsync(
            ReservationRetentionExecution added,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ReservationRetentionSweepCheckpoint?>
            GetCheckpointAsync(
                string dataClassKey,
                int executionPolicyVersion,
                CancellationToken cancellationToken) =>
            Task.FromResult<
                ReservationRetentionSweepCheckpoint?>(checkpoint);

        public Task AddCheckpointAsync(
            ReservationRetentionSweepCheckpoint added,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ReservationRetentionAnonymisationReceipt?>
            GetReceiptAsync(
                Guid reservationId,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ReservationAnonymisationTombstone?>
            GetTombstoneAsync(
                Guid reservationId,
                CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddAnonymisationProofAsync(
            ReservationRetentionAnonymisationReceipt receipt,
            ReservationAnonymisationTombstone tombstone,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => TenantId;
    }

    private sealed class ThrowingIdGenerator : IIdGenerator
    {
        public Guid NewId() => throw new NotSupportedException();
    }
}
