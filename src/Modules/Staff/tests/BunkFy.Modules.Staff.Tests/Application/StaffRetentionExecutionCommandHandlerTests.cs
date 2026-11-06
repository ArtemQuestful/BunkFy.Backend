namespace BunkFy.Modules.Staff.Tests.Application;

using BunkFy.Modules.Retention.Contracts;
using BunkFy.Modules.Staff.Application.Commands;
using BunkFy.Modules.Staff.Application.Contributors;
using BunkFy.Modules.Staff.Application.Handlers;
using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Domain.DataRights;
using BunkFy.Modules.Staff.Domain.Retention;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Scoping;
using Xunit;

[Trait("Category", "Unit")]
public sealed class StaffRetentionExecutionCommandHandlerTests
{
    private const string TenantId = "tenant-a";
    private static readonly DateTimeOffset StartedAtUtc =
        StaffRetentionTestData.Now;

    [Fact]
    public async Task Failed_completion_retries_and_fences_stale_attempt()
    {
        StaffRetentionExecution execution = StartExecution();
        Assert.True(execution.RecordAffected().IsSuccess);
        StaffRetentionSweepCheckpoint checkpoint = CreateCheckpoint();
        FakeExecutionRepository repository = new(execution, checkpoint);
        CompleteStaffRetentionExecutionCommandHandler complete =
            new(repository);
        BeginStaffRetentionExecutionCommandHandler begin = new(
            repository,
            new TestScopeContext(),
            new ThrowingIdGenerator());
        DateTimeOffset failedAtUtc = StartedAtUtc.AddMinutes(1);

        Result<RetentionContributionResult> failed =
            await complete.HandleAsync(
                new(
                    execution.Id,
                    Attempt: 1,
                    StaffRetentionExecutionState.Failed,
                    ScannedCount: 1,
                    RemainingCount: 1,
                    StaffRetentionCoordinates.MutationFailedOutcome,
                    failedAtUtc,
                    HoldReviewDueAtUtc: null,
                    ExpectedAfterProjectionOrdinal: 0,
                    NextAfterProjectionOrdinal: 42),
                CancellationToken.None);

        Assert.True(failed.IsSuccess);
        Assert.Equal(1, failed.Value.AffectedCount);
        Assert.Equal(StaffRetentionExecutionState.Failed, execution.State);
        Assert.Equal(0, checkpoint.AfterProjectionOrdinal);
        Assert.Null(checkpoint.LastExecutionId);
        Assert.Equal(StartedAtUtc, checkpoint.UpdatedAtUtc);

        DateTimeOffset retryStartedAtUtc = StartedAtUtc.AddMinutes(2);
        Result<StaffRetentionExecutionStart> retried =
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
        Assert.Equal(StaffRetentionExecutionState.Running, execution.State);

        Result<RetentionContributionResult> stale =
            await complete.HandleAsync(
                Completion(
                    execution.Id,
                    attempt: 1,
                    StartedAtUtc.AddMinutes(3)),
                CancellationToken.None);

        Assert.Equal(
            "Staff.RetentionExecutionResultInvalid",
            stale.Error.Code);
        Assert.Equal(StaffRetentionExecutionState.Running, execution.State);
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
            StaffRetentionExecutionState.Completed,
            execution.State);
        Assert.Equal(42, checkpoint.AfterProjectionOrdinal);
        Assert.Equal(execution.Id, checkpoint.LastExecutionId);
    }

    [Fact]
    public async Task Begin_retry_rewinds_a_proven_legacy_checkpoint()
    {
        StaffRetentionExecution execution = StartExecution();
        DateTimeOffset failedAtUtc = StartedAtUtc.AddMinutes(1);
        Assert.True(execution.Complete(
            StaffRetentionExecutionState.Failed,
            attempt: 1,
            scannedCount: 0,
            remainingCount: 1,
            StaffRetentionCoordinates.MutationFailedOutcome,
            failedAtUtc,
            holdReviewDueAtUtc: null).IsSuccess);
        StaffRetentionSweepCheckpoint checkpoint = CreateCheckpoint();
        Assert.True(checkpoint.Advance(
            expectedAfterProjectionOrdinal: 0,
            nextAfterProjectionOrdinal: 42,
            execution.Id,
            failedAtUtc).IsSuccess);
        FakeExecutionRepository repository = new(execution, checkpoint);
        BeginStaffRetentionExecutionCommandHandler begin = new(
            repository,
            new TestScopeContext(),
            new ThrowingIdGenerator());
        DateTimeOffset retryStartedAtUtc = StartedAtUtc.AddMinutes(2);

        Result<StaffRetentionExecutionStart> retried =
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
        Assert.Equal(StaffRetentionExecutionState.Running, execution.State);
    }

    private static StaffRetentionExecution StartExecution() =>
        StaffRetentionExecution.Start(
            Guid.NewGuid(),
            TenantId,
            StaffRetentionCoordinates.DataClassKey,
            StaffRetentionCoordinates.ExecutionPolicyVersion,
            attempt: 1,
            startingProjectionOrdinal: 0,
            StartedAtUtc,
            StartedAtUtc.AddMinutes(15)).Value;

    private static StaffRetentionSweepCheckpoint CreateCheckpoint() =>
        StaffRetentionSweepCheckpoint.Create(
            Guid.NewGuid(),
            TenantId,
            StaffRetentionCoordinates.DataClassKey,
            StaffRetentionCoordinates.ExecutionPolicyVersion,
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
            StaffRetentionCoordinates.OwnerKey,
            StaffRetentionCoordinates.DataClassKey,
            StaffRetentionCoordinates.ExecutionPolicyVersion,
            attempt,
            startedAtUtc,
            deadlineUtc);

    private static CompleteStaffRetentionExecutionCommand Completion(
        Guid executionId,
        int attempt,
        DateTimeOffset completedAtUtc) =>
        new(
            executionId,
            attempt,
            StaffRetentionExecutionState.Completed,
            ScannedCount: 1,
            RemainingCount: 0,
            StaffRetentionCoordinates.CompletedOutcome,
            completedAtUtc,
            HoldReviewDueAtUtc: null,
            ExpectedAfterProjectionOrdinal: 0,
            NextAfterProjectionOrdinal: 42);

    private sealed class FakeExecutionRepository(
        StaffRetentionExecution execution,
        StaffRetentionSweepCheckpoint checkpoint)
        : IStaffRetentionExecutionRepository
    {
        public Task<StaffRetentionExecution?> GetExecutionAsync(
            Guid executionId,
            CancellationToken cancellationToken) =>
            Task.FromResult<StaffRetentionExecution?>(
                execution.Id == executionId ? execution : null);

        public Task AddExecutionAsync(
            StaffRetentionExecution added,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StaffRetentionSweepCheckpoint?> GetCheckpointAsync(
            string dataClassKey,
            int executionPolicyVersion,
            CancellationToken cancellationToken) =>
            Task.FromResult<StaffRetentionSweepCheckpoint?>(checkpoint);

        public Task AddCheckpointAsync(
            StaffRetentionSweepCheckpoint added,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StaffRetentionAnonymisationReceipt?> GetReceiptAsync(
            Guid staffMemberId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<StaffAnonymisationTombstone?> GetTombstoneAsync(
            Guid staffMemberId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task AddAnonymisationProofAsync(
            StaffRetentionAnonymisationReceipt receipt,
            StaffAnonymisationTombstone tombstone,
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
