namespace BunkFy.Modules.Ingestion.Application.Handlers;

using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using BunkFy.Modules.Ingestion.Application.Commands;
using BunkFy.Modules.Ingestion.Application.Contributors;
using BunkFy.Modules.Ingestion.Application.Ports;
using BunkFy.Modules.Ingestion.Contracts;
using BunkFy.Modules.Ingestion.Domain.Receipts;
using BunkFy.Modules.Ingestion.Domain.Retention;

internal sealed class ClaimExpiredRawPayloadsCommandHandler(
    IRawPayloadRetentionRepository retention,
    IngestionRetentionMutationCoordinator retentionMutations,
    IngestionSourceMutationCoordinator sourceMutations,
    IScopeContext scopeContext,
    ISystemClock clock)
    : ICommandHandler<ClaimExpiredRawPayloadsCommand, IReadOnlyList<RawPayloadPurgeCandidate>>
{
    public async Task<Result<IReadOnlyList<RawPayloadPurgeCandidate>>> HandleAsync(
        ClaimExpiredRawPayloadsCommand command,
        CancellationToken cancellationToken)
    {
        if (!scopeContext.IsEnabled || string.IsNullOrWhiteSpace(scopeContext.ScopeId))
        {
            return Result.Failure<IReadOnlyList<RawPayloadPurgeCandidate>>(IngestionApplicationErrors.ScopeRequired);
        }

        if (command.ClaimId == Guid.Empty ||
            command.BatchSize is <= 0 or > PurgeExpiredRawPayloadsPayload.MaximumBatchSize ||
            command.StaleClaimMinutes is < PurgeExpiredRawPayloadsPayload.MinimumStaleClaimMinutes or
                > PurgeExpiredRawPayloadsPayload.MaximumStaleClaimMinutes)
        {
            return Result.Failure<IReadOnlyList<RawPayloadPurgeCandidate>>(
                IngestionApplicationErrors.RetentionTaskOptionsInvalid);
        }

        if (command.RetentionExecutionId is { } executionId &&
            command.ClaimId != executionId)
        {
            return Result.Failure<IReadOnlyList<RawPayloadPurgeCandidate>>(
                IngestionRetentionExecutionErrors.CoordinateInvalid);
        }

        Result<IngestionRetentionExecutionLease> execution =
            await retentionMutations.AcquireRunningAsync(
                command.RetentionExecutionId,
                command.RetentionAttempt,
                IngestionRetentionCoordinates.RawPayloadDataClass,
                cancellationToken).ConfigureAwait(false);
        if (execution.IsFailure)
        {
            return Result.Failure<IReadOnlyList<RawPayloadPurgeCandidate>>(
                execution.Error);
        }

        DateTimeOffset nowUtc = clock.UtcNow;
        IReadOnlyList<RawPayloadPurgeClaimCandidate> candidates =
            await retention.FindClaimCandidatesAsync(
            command.ClaimId,
            nowUtc,
            nowUtc.AddMinutes(-command.StaleClaimMinutes),
            command.BatchSize,
            cancellationToken).ConfigureAwait(false);
        await sourceMutations.AcquireAllAsync(
                candidates
                    .Select(candidate => new IngestionSourceGraphCoordinate(
                        candidate.ReceiptId,
                        candidate.ConnectionId,
                        candidate.SourceLinkId))
                    .ToArray(),
                cancellationToken)
            .ConfigureAwait(false);

        return Result.Success(await retention.ClaimSelectedAsync(
            candidates.Select(candidate => candidate.ReceiptId).ToArray(),
            command.ClaimId,
            nowUtc,
            nowUtc.AddMinutes(-command.StaleClaimMinutes),
            cancellationToken).ConfigureAwait(false));
    }
}

internal sealed class CompleteRawPayloadPurgeCommandHandler(
    IObservationReceiptRepository receipts,
    IngestionRetentionMutationCoordinator retentionMutations,
    IngestionSourceMutationCoordinator sourceMutations,
    ISystemClock clock)
    : ICommandHandler<CompleteRawPayloadPurgeCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(
        CompleteRawPayloadPurgeCommand command,
        CancellationToken cancellationToken)
    {
        if (command.RetentionExecutionId is { } executionId &&
            command.ClaimId != executionId)
        {
            return Result.Failure<Unit>(
                IngestionRetentionExecutionErrors.CoordinateInvalid);
        }

        Result<IngestionRetentionExecutionLease> execution =
            await retentionMutations.AcquireRunningAsync(
                command.RetentionExecutionId,
                command.RetentionAttempt,
                IngestionRetentionCoordinates.RawPayloadDataClass,
                cancellationToken).ConfigureAwait(false);
        if (execution.IsFailure)
        {
            return Result.Failure<Unit>(execution.Error);
        }

        IngestionSourceMutationLease? source =
            await sourceMutations.AcquireReceiptAsync(
                    command.ReceiptId,
                    cancellationToken)
                .ConfigureAwait(false);
        if (source is null)
        {
            return Result.Failure<Unit>(IngestionApplicationErrors.ReceiptNotFound);
        }

        ObservationReceipt? receipt = await receipts.GetAsync(command.ReceiptId, cancellationToken)
            .ConfigureAwait(false);
        if (receipt is null)
        {
            return Result.Failure<Unit>(IngestionApplicationErrors.ReceiptNotFound);
        }

        Result completed = receipt.CompleteRawPayloadPurge(command.ClaimId, clock.UtcNow);
        if (completed.IsFailure)
        {
            return Result.Failure<Unit>(completed.Error);
        }

        if (execution.Value.Execution is { } currentExecution)
        {
            Result recorded = currentExecution.RecordAffected(
                command.RetentionAttempt!.Value,
                1);
            if (recorded.IsFailure)
            {
                return Result.Failure<Unit>(recorded.Error);
            }
        }

        return Result.Success(Unit.Value);
    }
}
