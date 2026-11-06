namespace BunkFy.Modules.Ingestion.Tests.Application;

using BunkFy.Modules.Ingestion.Application.Commands;
using BunkFy.Modules.Ingestion.Application.Contributors;
using BunkFy.Modules.Ingestion.Application.Ports;
using BunkFy.Modules.Retention.Contracts;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Xunit;

[Trait("Category", "Unit")]
public sealed class IngestionRetentionExecutorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Raw_execution_propagates_attempt_to_every_owner_command()
    {
        RetentionContributionRequest request = CreateRequest(
            IngestionRetentionCoordinates.RawPayloadDataClass);
        RecordingDispatcher dispatcher = new(includeRawCandidate: true);
        RecordingRawPayloadStore payloads = new();
        IngestionRetentionExecutor executor = CreateExecutor(dispatcher, payloads);

        RetentionContributionResult result = await executor.ExecuteRawPayloadsAsync(
            request,
            CancellationToken.None);

        ClaimExpiredRawPayloadsCommand claim = Assert.Single(
            dispatcher.Commands.OfType<ClaimExpiredRawPayloadsCommand>());
        Assert.Equal(request.ExecutionId, claim.ClaimId);
        Assert.Equal(request.ExecutionId, claim.RetentionExecutionId);
        Assert.Equal(request.Attempt, claim.RetentionAttempt);
        CompleteRawPayloadPurgeCommand purge = Assert.Single(
            dispatcher.Commands.OfType<CompleteRawPayloadPurgeCommand>());
        Assert.Equal(request.ExecutionId, purge.ClaimId);
        Assert.Equal(request.ExecutionId, purge.RetentionExecutionId);
        Assert.Equal(request.Attempt, purge.RetentionAttempt);
        Assert.Single(payloads.DeletedPayloadIds);
        AssertCompletion(request, dispatcher);
        Assert.Equal(RetentionContributionStatus.Completed, result.Status);
    }

    [Fact]
    public async Task Sensitive_execution_propagates_attempt_to_redaction_and_completion()
    {
        RetentionContributionRequest request = CreateRequest(
            IngestionRetentionCoordinates.SensitiveHistoryDataClass);
        RecordingDispatcher dispatcher = new(includeRawCandidate: false);
        IngestionRetentionExecutor executor = CreateExecutor(
            dispatcher,
            new RecordingRawPayloadStore());

        RetentionContributionResult result =
            await executor.ExecuteSensitiveHistoryAsync(
                request,
                CancellationToken.None);

        RedactExpiredSensitiveHistoryCommand redaction = Assert.Single(
            dispatcher.Commands.OfType<RedactExpiredSensitiveHistoryCommand>());
        Assert.Equal(request.ExecutionId, redaction.RetentionExecutionId);
        Assert.Equal(request.Attempt, redaction.RetentionAttempt);
        AssertCompletion(request, dispatcher);
        Assert.Equal(RetentionContributionStatus.Completed, result.Status);
    }

    private static void AssertCompletion(
        RetentionContributionRequest request,
        RecordingDispatcher dispatcher)
    {
        CompleteIngestionRetentionExecutionCommand completion = Assert.Single(
            dispatcher.Commands
                .OfType<CompleteIngestionRetentionExecutionCommand>());
        Assert.Equal(request.ExecutionId, completion.ExecutionId);
        Assert.Equal(request.Attempt, completion.Attempt);
    }

    private static IngestionRetentionExecutor CreateExecutor(
        IRequestDispatcher dispatcher,
        IRawPayloadStore payloads) => new(
        dispatcher,
        payloads,
        new EmptyStatusReader(),
        new TestPolicy(),
        new TestClock());

    private static RetentionContributionRequest CreateRequest(
        string dataClassKey) => new(
        RetentionExecutionContract.CurrentVersion,
        Guid.NewGuid(),
        "tenant-a",
        PropertyId: null,
        IngestionRetentionCoordinates.OwnerKey,
        dataClassKey,
        IngestionRetentionCoordinates.ExecutionPolicyVersion,
        Attempt: 3,
        Now,
        Now.AddMinutes(15));

    private sealed class RecordingDispatcher(bool includeRawCandidate)
        : IRequestDispatcher
    {
        private readonly Guid receiptId = Guid.NewGuid();
        private readonly Guid payloadId = Guid.NewGuid();
        private readonly Guid connectionId = Guid.NewGuid();

        public List<object> Commands { get; } = [];

        public Task<Result<TResponse>> SendAsync<TResponse>(
            ICommand<TResponse> command,
            CancellationToken cancellationToken = default)
        {
            this.Commands.Add(command);
            object result = command switch
            {
                BeginIngestionRetentionExecutionCommand =>
                    Result.Success(new IngestionRetentionExecutionStart(
                        DispatchRequired: true,
                        CompletedResult: null)),
                ClaimExpiredRawPayloadsCommand =>
                    Result.Success<IReadOnlyList<RawPayloadPurgeCandidate>>(
                        includeRawCandidate
                            ? [new(this.receiptId, this.payloadId, this.connectionId)]
                            : []),
                CompleteRawPayloadPurgeCommand => Result.Success(Unit.Value),
                RedactExpiredSensitiveHistoryCommand => Result.Success(
                    new SensitiveHistoryRedactionBatchResult(0, 0)),
                CompleteIngestionRetentionExecutionCommand completion =>
                    Result.Success(new RetentionContributionResult(
                        RetentionExecutionContract.CurrentVersion,
                        RetentionContributionStatus.Completed,
                        ScannedCount: 0,
                        AffectedCount: includeRawCandidate ? 1 : 0,
                        RemainingCount: completion.RemainingCount,
                        completion.OutcomeCode,
                        completion.CompletedAtUtc)),
                _ => throw new InvalidOperationException(
                    $"Unexpected command {command.GetType().Name}.")
            };
            return Task.FromResult((Result<TResponse>)result);
        }

        public Task<Result<TResponse>> QueryAsync<TResponse>(
            IQuery<TResponse> query,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingRawPayloadStore : IRawPayloadStore
    {
        public List<Guid> DeletedPayloadIds { get; } = [];

        public Task StoreAsync(
            RawPayloadWrite write,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<RawPayloadRead?> ReadAsync(
            Guid payloadId,
            string scopeId,
            Guid connectionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(
            Guid payloadId,
            string scopeId,
            Guid connectionId,
            CancellationToken cancellationToken)
        {
            this.DeletedPayloadIds.Add(payloadId);
            return Task.FromResult(true);
        }
    }

    private sealed class EmptyStatusReader : IIngestionRetentionStatusReader
    {
        private static readonly IngestionRetentionBacklog Empty = new(0, 0, null);

        public Task<IngestionRetentionBacklog> ReadRawPayloadBacklogAsync(
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken) => Task.FromResult(Empty);

        public Task<IngestionRetentionBacklog> ReadSensitiveHistoryBacklogAsync(
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken) => Task.FromResult(Empty);
    }

    private sealed class TestPolicy : IIngestionRetentionPolicy
    {
        public DateTimeOffset GetRawPayloadRetainUntilUtc(
            Guid propertyId,
            Guid connectionId,
            DateTimeOffset receivedAtUtc) => throw new NotSupportedException();

        public DateTimeOffset GetSensitiveHistoryRetainUntilUtc(
            Guid propertyId,
            Guid connectionId,
            DateTimeOffset terminalAtUtc) => throw new NotSupportedException();

        public DateTimeOffset GetLegalHoldReviewDueAtUtc(
            DateTimeOffset placedAtUtc) => placedAtUtc.AddDays(30);
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}
