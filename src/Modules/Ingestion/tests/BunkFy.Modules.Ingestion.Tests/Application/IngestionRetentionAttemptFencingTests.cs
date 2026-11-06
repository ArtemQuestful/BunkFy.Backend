namespace BunkFy.Modules.Ingestion.Tests.Application;

using BunkFy.Modules.Ingestion.Application.Commands;
using BunkFy.Modules.Ingestion.Application.Contributors;
using BunkFy.Modules.Ingestion.Application.Handlers;
using BunkFy.Modules.Ingestion.Application.Ports;
using BunkFy.Modules.Ingestion.Contracts;
using BunkFy.Modules.Ingestion.Domain.Retention;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Xunit;

[Trait("Category", "Unit")]
public sealed class IngestionRetentionAttemptFencingTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 22, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Stable_execution_lock_precedes_authoritative_reload()
    {
        IngestionRetentionExecution execution = CreateExecution();
        List<string> calls = [];
        IngestionRetentionMutationCoordinator coordinator = new(
            new RecordingExecutionLock(calls),
            new RecordingExecutionRepository(execution, calls),
            new TestScope());

        var acquired = await coordinator.AcquireRunningAsync(
            execution.Id,
            attempt: 1,
            IngestionRetentionCoordinates.RawPayloadDataClass,
            CancellationToken.None);

        Assert.True(acquired.IsSuccess, acquired.Error.Code);
        Assert.Same(execution, acquired.Value.Execution);
        Assert.Equal(["retention-lock", "execution-reload"], calls);
    }

    [Fact]
    public async Task Missing_execution_is_represented_by_a_non_null_lease()
    {
        List<string> calls = [];
        IngestionRetentionMutationCoordinator coordinator = new(
            new RecordingExecutionLock(calls),
            new RecordingExecutionRepository(execution: null, calls),
            new TestScope());

        var acquired = await coordinator.AcquireAsync(
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(acquired.IsSuccess, acquired.Error.Code);
        Assert.Null(acquired.Value.Execution);
        Assert.Equal(["retention-lock", "execution-reload"], calls);
    }

    [Fact]
    public async Task Partial_coordinate_fails_before_lock_or_reload()
    {
        List<string> calls = [];
        IngestionRetentionMutationCoordinator coordinator = new(
            new RecordingExecutionLock(calls),
            new RecordingExecutionRepository(CreateExecution(), calls),
            new TestScope());

        var acquired = await coordinator.AcquireRunningAsync(
            Guid.NewGuid(),
            attempt: null,
            IngestionRetentionCoordinates.RawPayloadDataClass,
            CancellationToken.None);

        Assert.Equal(
            IngestionRetentionExecutionErrors.CoordinateInvalid,
            acquired.Error);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Stale_raw_claim_fails_before_candidate_discovery()
    {
        IngestionRetentionExecution execution = CreateRetriedExecution();
        ClaimExpiredRawPayloadsCommandHandler handler = new(
            new ThrowingRawPayloadRetentionRepository(),
            CreateCoordinator(execution),
            sourceMutations: null!,
            new TestScope(),
            new TestClock());

        var result = await handler.HandleAsync(
            new ClaimExpiredRawPayloadsCommand(
                execution.Id,
                BatchSize: 10,
                PurgeExpiredRawPayloadsPayload.DefaultStaleClaimMinutes,
                execution.Id,
                RetentionAttempt: 1),
            CancellationToken.None);

        Assert.Equal(
            IngestionRetentionExecutionErrors.CoordinateInvalid,
            result.Error);
    }

    [Fact]
    public async Task Stale_raw_completion_fails_before_receipt_or_source_lock()
    {
        IngestionRetentionExecution execution = CreateRetriedExecution();
        CompleteRawPayloadPurgeCommandHandler handler = new(
            receipts: null!,
            CreateCoordinator(execution),
            sourceMutations: null!,
            new TestClock());

        var result = await handler.HandleAsync(
            new CompleteRawPayloadPurgeCommand(
                Guid.NewGuid(),
                execution.Id,
                execution.Id,
                RetentionAttempt: 1),
            CancellationToken.None);

        Assert.Equal(
            IngestionRetentionExecutionErrors.CoordinateInvalid,
            result.Error);
    }

    [Fact]
    public async Task Stale_sensitive_redaction_fails_before_candidate_discovery()
    {
        IngestionRetentionExecution execution = CreateRetriedExecution(
            IngestionRetentionCoordinates.SensitiveHistoryDataClass);
        RedactExpiredSensitiveHistoryCommandHandler handler = new(
            new ThrowingSensitiveHistoryRetentionRepository(),
            CreateCoordinator(execution),
            sourceMutations: null!,
            new TestScope(),
            new TestClock());

        var result = await handler.HandleAsync(
            new RedactExpiredSensitiveHistoryCommand(
                BatchSize: 10,
                execution.Id,
                RetentionAttempt: 1),
            CancellationToken.None);

        Assert.Equal(
            IngestionRetentionExecutionErrors.CoordinateInvalid,
            result.Error);
    }

    private static IngestionRetentionMutationCoordinator CreateCoordinator(
        IngestionRetentionExecution execution) => new(
        new RecordingExecutionLock([]),
        new RecordingExecutionRepository(execution, []),
        new TestScope());

    private static IngestionRetentionExecution CreateRetriedExecution(
        string dataClassKey = IngestionRetentionCoordinates.RawPayloadDataClass)
    {
        IngestionRetentionExecution execution = CreateExecution(dataClassKey);
        Assert.True(execution.BeginRetry(
            attempt: 2,
            Now.AddMinutes(1),
            Now.AddMinutes(20)).IsSuccess);
        return execution;
    }

    private static IngestionRetentionExecution CreateExecution(
        string dataClassKey = IngestionRetentionCoordinates.RawPayloadDataClass) =>
        IngestionRetentionExecution.Start(
            Guid.NewGuid(),
            "tenant-a",
            dataClassKey,
            IngestionRetentionCoordinates.ExecutionPolicyVersion,
            attempt: 1,
            Now,
            Now.AddMinutes(15)).Value;

    private sealed class RecordingExecutionLock(List<string> calls)
        : IIngestionExecutionLock
    {
        public Task AcquireRetentionExecutionAsync(
            string tenantId,
            Guid executionId,
            CancellationToken cancellationToken)
        {
            Assert.Equal("tenant-a", tenantId);
            Assert.NotEqual(Guid.Empty, executionId);
            calls.Add("retention-lock");
            return Task.CompletedTask;
        }

        public Task AcquireTaskExecutionAsync(
            string tenantId,
            Guid taskRunId,
            int taskAttempt,
            CancellationToken cancellationToken) => Unexpected();

        public Task AcquireConnectionReadAsync(
            string tenantId,
            Guid connectionId,
            CancellationToken cancellationToken) => Unexpected();

        public Task AcquireConnectionWriteAsync(
            string tenantId,
            Guid connectionId,
            CancellationToken cancellationToken) => Unexpected();

        public Task AcquireRunReadAsync(
            string tenantId,
            Guid runId,
            CancellationToken cancellationToken) => Unexpected();

        public Task AcquireRunWriteAsync(
            string tenantId,
            Guid runId,
            CancellationToken cancellationToken) => Unexpected();

        private static Task Unexpected() =>
            throw new InvalidOperationException("Unexpected execution lock.");
    }

    private sealed class RecordingExecutionRepository(
        IngestionRetentionExecution? execution,
        List<string> calls)
        : IIngestionRetentionExecutionRepository
    {
        public Task AddAsync(
            IngestionRetentionExecution added,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IngestionRetentionExecution?> GetAsync(
            Guid executionId,
            CancellationToken cancellationToken)
        {
            calls.Add("execution-reload");
            return Task.FromResult<IngestionRetentionExecution?>(
                execution?.Id == executionId ? execution : null);
        }
    }

    private sealed class ThrowingRawPayloadRetentionRepository
        : IRawPayloadRetentionRepository
    {
        public Task<IReadOnlyList<RawPayloadPurgeClaimCandidate>>
            FindClaimCandidatesAsync(
                Guid claimId,
                DateTimeOffset nowUtc,
                DateTimeOffset staleClaimBeforeUtc,
                int batchSize,
                CancellationToken cancellationToken) =>
            Unexpected<IReadOnlyList<RawPayloadPurgeClaimCandidate>>();

        public Task<IReadOnlyList<RawPayloadPurgeCandidate>> ClaimSelectedAsync(
            IReadOnlyCollection<Guid> receiptIds,
            Guid claimId,
            DateTimeOffset nowUtc,
            DateTimeOffset staleClaimBeforeUtc,
            CancellationToken cancellationToken) =>
            Unexpected<IReadOnlyList<RawPayloadPurgeCandidate>>();

        private static Task<T> Unexpected<T>() =>
            throw new InvalidOperationException(
                "Raw retention repository must not be reached.");
    }

    private sealed class ThrowingSensitiveHistoryRetentionRepository
        : ISensitiveHistoryRetentionRepository
    {
        public Task<IReadOnlyList<SensitiveHistoryRedactionCandidate>>
            FindRedactionCandidatesAsync(
                DateTimeOffset nowUtc,
                int batchSize,
                CancellationToken cancellationToken) =>
            Unexpected<IReadOnlyList<SensitiveHistoryRedactionCandidate>>();

        public Task<SensitiveHistoryRedactionBatchResult> RedactSelectedAsync(
            IReadOnlyCollection<Guid> proposalIds,
            IReadOnlyCollection<Guid> dispatchIds,
            DateTimeOffset nowUtc,
            CancellationToken cancellationToken) =>
            Unexpected<SensitiveHistoryRedactionBatchResult>();

        private static Task<T> Unexpected<T>() =>
            throw new InvalidOperationException(
                "Sensitive retention repository must not be reached.");
    }

    private sealed class TestScope : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}
