namespace BunkFy.Modules.Guests.Tests.Application;

using BunkFy.Modules.Guests.Application.Commands;
using BunkFy.Modules.Guests.Application.Contributors;
using BunkFy.Modules.Guests.Application.Handlers;
using BunkFy.Modules.Guests.Application.Ports;
using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.DataRights;
using BunkFy.Modules.Guests.Domain.Retention;
using BunkFy.Modules.Retention.Contracts;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Scoping;
using Xunit;

[Trait("Category", "Unit")]
public sealed class GuestRetentionExecutionCommandHandlerTests
{
    private const string TenantId = "tenant-a";
    private static readonly DateTimeOffset StartedAtUtc =
        GuestRetentionTestData.Now.AddMinutes(-1);

    [Fact]
    public async Task Failed_completion_retries_and_fences_stale_attempt()
    {
        GuestRetentionExecution execution = StartExecution();
        Assert.True(execution.RecordAffected().IsSuccess);
        GuestRetentionSweepCheckpoint checkpoint = CreateCheckpoint();
        FakeExecutionRepository repository = new(execution, checkpoint);
        CompleteGuestRetentionExecutionCommandHandler complete =
            new(repository);
        BeginGuestRetentionExecutionCommandHandler begin = new(
            repository,
            new TestScopeContext(),
            new ThrowingIdGenerator());
        DateTimeOffset failedAtUtc = StartedAtUtc.AddMinutes(1);

        Result<RetentionContributionResult> failed =
            await complete.HandleAsync(
                new(
                    execution.Id,
                    Attempt: 1,
                    GuestRetentionExecutionState.Failed,
                    ScannedCount: 1,
                    RemainingCount: 1,
                    GuestRetentionCoordinates.MutationFailedOutcome,
                    failedAtUtc,
                    HoldReviewDueAtUtc: null,
                    ExpectedAfterProjectionOrdinal: 0,
                    NextAfterProjectionOrdinal: 42),
                CancellationToken.None);

        Assert.True(failed.IsSuccess);
        Assert.Equal(1, failed.Value.AffectedCount);
        Assert.Equal(GuestRetentionExecutionState.Failed, execution.State);
        Assert.Equal(0, checkpoint.AfterProjectionOrdinal);
        Assert.Null(checkpoint.LastExecutionId);
        Assert.Equal(StartedAtUtc, checkpoint.UpdatedAtUtc);

        DateTimeOffset retryStartedAtUtc = StartedAtUtc.AddMinutes(2);
        Result<GuestRetentionExecutionStart> retried =
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
        Assert.Equal(GuestRetentionExecutionState.Running, execution.State);

        Result<RetentionContributionResult> stale =
            await complete.HandleAsync(
                Completion(
                    execution.Id,
                    attempt: 1,
                    StartedAtUtc.AddMinutes(3)),
                CancellationToken.None);

        Assert.Equal(
            "Guests.RetentionExecutionResultInvalid",
            stale.Error.Code);
        Assert.Equal(GuestRetentionExecutionState.Running, execution.State);
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
            GuestRetentionExecutionState.Completed,
            execution.State);
        Assert.Equal(42, checkpoint.AfterProjectionOrdinal);
        Assert.Equal(execution.Id, checkpoint.LastExecutionId);
    }

    [Fact]
    public async Task Begin_retry_rewinds_a_proven_legacy_checkpoint()
    {
        GuestRetentionExecution execution = StartExecution();
        DateTimeOffset failedAtUtc = StartedAtUtc.AddMinutes(1);
        Assert.True(execution.Complete(
            GuestRetentionExecutionState.Failed,
            attempt: 1,
            scannedCount: 0,
            remainingCount: 1,
            GuestRetentionCoordinates.MutationFailedOutcome,
            failedAtUtc,
            holdReviewDueAtUtc: null).IsSuccess);
        GuestRetentionSweepCheckpoint checkpoint = CreateCheckpoint();
        Assert.True(checkpoint.Advance(
            expectedAfterProjectionOrdinal: 0,
            nextAfterProjectionOrdinal: 42,
            execution.Id,
            failedAtUtc).IsSuccess);
        FakeExecutionRepository repository = new(execution, checkpoint);
        BeginGuestRetentionExecutionCommandHandler begin = new(
            repository,
            new TestScopeContext(),
            new ThrowingIdGenerator());
        DateTimeOffset retryStartedAtUtc = StartedAtUtc.AddMinutes(2);

        Result<GuestRetentionExecutionStart> retried =
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
        Assert.Equal(3, checkpoint.Version);
        Assert.Equal(2, execution.Attempt);
        Assert.Equal(GuestRetentionExecutionState.Running, execution.State);
    }

    private static GuestRetentionExecution StartExecution() =>
        GuestRetentionExecution.Start(
            Guid.NewGuid(),
            TenantId,
            GuestRetentionCoordinates.DataClassKey,
            GuestRetentionCoordinates.ExecutionPolicyVersion,
            attempt: 1,
            startingProjectionOrdinal: 0,
            StartedAtUtc,
            StartedAtUtc.AddMinutes(15)).Value;

    private static GuestRetentionSweepCheckpoint CreateCheckpoint() =>
        GuestRetentionSweepCheckpoint.Create(
            Guid.NewGuid(),
            TenantId,
            GuestRetentionCoordinates.DataClassKey,
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
            GuestRetentionCoordinates.OwnerKey,
            GuestRetentionCoordinates.DataClassKey,
            GuestRetentionCoordinates.ExecutionPolicyVersion,
            attempt,
            startedAtUtc,
            deadlineUtc);

    private static CompleteGuestRetentionExecutionCommand Completion(
        Guid executionId,
        int attempt,
        DateTimeOffset completedAtUtc) =>
        new(
            executionId,
            attempt,
            GuestRetentionExecutionState.Completed,
            ScannedCount: 1,
            RemainingCount: 0,
            GuestRetentionCoordinates.CompletedOutcome,
            completedAtUtc,
            HoldReviewDueAtUtc: null,
            ExpectedAfterProjectionOrdinal: 0,
            NextAfterProjectionOrdinal: 42);

    private sealed class FakeExecutionRepository(
        GuestRetentionExecution execution,
        GuestRetentionSweepCheckpoint checkpoint)
        : IGuestRetentionExecutionRepository
    {
        public Task<GuestRetentionExecution?> GetExecutionAsync(
            Guid executionId,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                execution.Id == executionId ? execution : null);

        public Task AddExecutionAsync(
            GuestRetentionExecution added,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GuestRetentionSweepCheckpoint?> GetCheckpointAsync(
            string dataClassKey,
            CancellationToken cancellationToken) =>
            Task.FromResult<GuestRetentionSweepCheckpoint?>(checkpoint);

        public Task AddCheckpointAsync(
            GuestRetentionSweepCheckpoint added,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GuestRetentionAnonymisationReceipt?> GetReceiptAsync(
            Guid guestId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GuestAnonymisationTombstone?> GetTombstoneAsync(
            Guid guestId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GuestProfile?> GetProfileAsync(
            Guid guestId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddAnonymisationProofAsync(
            GuestRetentionAnonymisationReceipt receipt,
            GuestAnonymisationTombstone tombstone,
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
