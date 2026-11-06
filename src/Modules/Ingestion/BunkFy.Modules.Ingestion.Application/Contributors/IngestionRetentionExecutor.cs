namespace BunkFy.Modules.Ingestion.Application.Contributors;

using BunkFy.Modules.Ingestion.Application.Commands;
using BunkFy.Modules.Ingestion.Application.Ports;
using BunkFy.Modules.Ingestion.Contracts;
using BunkFy.Modules.Retention.Contracts;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;

internal sealed class IngestionRetentionExecutor(
    IRequestDispatcher dispatcher,
    IRawPayloadStore rawPayloads,
    IIngestionRetentionStatusReader statusReader,
    IIngestionRetentionPolicy policy,
    ISystemClock clock)
{
    public async Task<RetentionContributionResult> ExecuteRawPayloadsAsync(
        RetentionContributionRequest request,
        CancellationToken cancellationToken)
    {
        IngestionRetentionExecutionStart started = await this.BeginAsync(
            request,
            cancellationToken).ConfigureAwait(false);
        if (!started.DispatchRequired)
        {
            return started.CompletedResult!;
        }

        for (int batch = 0;
             batch < PurgeExpiredRawPayloadsPayload.DefaultMaxBatches;
             batch++)
        {
            Result<IReadOnlyList<RawPayloadPurgeCandidate>> claimed =
                await dispatcher.SendAsync(
                    new ClaimExpiredRawPayloadsCommand(
                        request.ExecutionId,
                        PurgeExpiredRawPayloadsPayload.DefaultBatchSize,
                        PurgeExpiredRawPayloadsPayload.DefaultStaleClaimMinutes,
                        request.ExecutionId,
                        request.Attempt),
                    cancellationToken).ConfigureAwait(false);
            IReadOnlyList<RawPayloadPurgeCandidate> candidates =
                Require(claimed);
            foreach (RawPayloadPurgeCandidate candidate in candidates)
            {
                _ = await rawPayloads.DeleteAsync(
                    candidate.RawPayloadFileId,
                    request.TenantId,
                    candidate.ConnectionId,
                    cancellationToken).ConfigureAwait(false);
                Result<Unit> completed = await dispatcher.SendAsync(
                    new CompleteRawPayloadPurgeCommand(
                        candidate.ReceiptId,
                        request.ExecutionId,
                        request.ExecutionId,
                        request.Attempt),
                    cancellationToken).ConfigureAwait(false);
                _ = Require(completed);
            }

            if (candidates.Count <
                PurgeExpiredRawPayloadsPayload.DefaultBatchSize)
            {
                break;
            }
        }

        return await this.FinalizeAsync(
            request,
            await statusReader.ReadRawPayloadBacklogAsync(
                clock.UtcNow,
                cancellationToken).ConfigureAwait(false),
            "ingestion.raw-payload",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<RetentionContributionResult> ExecuteSensitiveHistoryAsync(
        RetentionContributionRequest request,
        CancellationToken cancellationToken)
    {
        IngestionRetentionExecutionStart started = await this.BeginAsync(
            request,
            cancellationToken).ConfigureAwait(false);
        if (!started.DispatchRequired)
        {
            return started.CompletedResult!;
        }

        for (int batch = 0;
             batch < RedactExpiredReservationHistoryPayload.DefaultMaxBatches;
             batch++)
        {
            Result<SensitiveHistoryRedactionBatchResult> redacted =
                await dispatcher.SendAsync(
                    new RedactExpiredSensitiveHistoryCommand(
                        RedactExpiredReservationHistoryPayload.DefaultBatchSize,
                        request.ExecutionId,
                        request.Attempt),
                    cancellationToken).ConfigureAwait(false);
            SensitiveHistoryRedactionBatchResult result = Require(redacted);
            if (result.TotalCount <
                RedactExpiredReservationHistoryPayload.DefaultBatchSize)
            {
                break;
            }
        }

        return await this.FinalizeAsync(
            request,
            await statusReader.ReadSensitiveHistoryBacklogAsync(
                clock.UtcNow,
                cancellationToken).ConfigureAwait(false),
            "ingestion.sensitive-history",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<IngestionRetentionExecutionStart> BeginAsync(
        RetentionContributionRequest request,
        CancellationToken cancellationToken)
    {
        Result<IngestionRetentionExecutionStart> started =
            await dispatcher.SendAsync(
                new BeginIngestionRetentionExecutionCommand(request),
                cancellationToken).ConfigureAwait(false);
        return Require(started);
    }

    private async Task<RetentionContributionResult> FinalizeAsync(
        RetentionContributionRequest request,
        IngestionRetentionBacklog backlog,
        string outcomePrefix,
        CancellationToken cancellationToken)
    {
        DateTimeOffset completedAtUtc = clock.UtcNow;
        bool blocked = backlog.BlockedCount > 0;
        DateTimeOffset? reviewDueAtUtc = blocked
            ? policy.GetLegalHoldReviewDueAtUtc(
                backlog.EarliestBlockingHoldPlacedAtUtc ??
                throw new InvalidOperationException(
                    "Ingestion.RetentionBlockingHoldUnavailable"))
            : null;
        string outcomeCode = blocked
            ? $"{outcomePrefix}.legal-hold"
            : backlog.EligibleCount > 0
                ? $"{outcomePrefix}.backlog"
                : $"{outcomePrefix}.completed";
        Result<RetentionContributionResult> completed =
            await dispatcher.SendAsync(
                new CompleteIngestionRetentionExecutionCommand(
                    request.ExecutionId,
                    request.Attempt,
                    checked(backlog.EligibleCount + backlog.BlockedCount),
                    outcomeCode,
                    completedAtUtc,
                    reviewDueAtUtc),
                cancellationToken).ConfigureAwait(false);
        return Require(completed);
    }

    private static T Require<T>(Result<T> result) =>
        result.IsSuccess
            ? result.Value
            : throw new InvalidOperationException(
                $"{result.Error.Code}: {result.Error.Message}");
}
