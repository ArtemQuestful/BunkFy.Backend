namespace BunkFy.Extensions.Operations.Notifications;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Naming;
using Gma.Modules.Notifications.Contracts;

internal sealed partial class OperationsNotificationsTenantTerminationContributor
{
    private const int DestroyBatchSize =
        NotificationScopeLifecycleLimits.MaximumDestroyBatchSize;

    public async Task<TenantTerminationContributionResult> ExecuteAsync(
        TenantTerminationContributionRequest request,
        CancellationToken cancellationToken)
    {
        DateTimeOffset startedAtUtc = clock.UtcNow;
        if (request?.Phase == TenantTerminationContributionPhase.Export)
        {
            return Failed(
                "operations-notifications.termination.export-requires-fragment-assembler",
                startedAtUtc);
        }

        if (request is null ||
            !IsValidDestroy(request, scopeContext, startedAtUtc))
        {
            return Failed(
                "operations-notifications.termination.destroy-request-invalid",
                startedAtUtc);
        }

        _ = TenantIds.TryNormalize(request.TenantId, out string? tenantId);
        WorkspaceTerminationFenceSnapshot? fence =
            await this.ReadFenceAsync(cancellationToken)
                .ConfigureAwait(false);
        DateTimeOffset observedAtUtc = this.GetUtcNowBeforeDeadline(
            request.DeadlineUtc);
        if (!Matches(request, fence))
        {
            return Retry(
                "operations-notifications.termination.destroy-fence-unavailable",
                observedAtUtc);
        }

        NotificationScopeSnapshot selected = await lifecycle.GetSnapshotAsync(
                tenantId!,
                cancellationToken)
            .ConfigureAwait(false);
        observedAtUtc = this.GetUtcNowBeforeDeadline(request.DeadlineUtc);
        if (!TrySelectDestroyRevision(selected, out long expectedRevision))
        {
            return selected?.Status == NotificationScopeStatus.ScopeUnavailable
                ? Retry(
                    "operations-notifications.termination.destroy-scope-unavailable",
                    observedAtUtc)
                : Failed(
                    "operations-notifications.termination.destroy-scope-invalid",
                    observedAtUtc);
        }

        NotificationScopeDestroyResult result = await lifecycle.DestroyBatchAsync(
                new NotificationScopeDestroyRequest(
                    request.IdempotencyKey,
                    tenantId!,
                    expectedRevision,
                    DestroyBatchSize),
                cancellationToken)
            .ConfigureAwait(false);
        _ = this.GetUtcNowBeforeDeadline(request.DeadlineUtc);
        WorkspaceTerminationFenceSnapshot? resultingFence =
            await this.ReadFenceAsync(cancellationToken)
                .ConfigureAwait(false);
        observedAtUtc = this.GetUtcNowBeforeDeadline(request.DeadlineUtc);
        if (!Matches(request, resultingFence) ||
            resultingFence!.Version != fence!.Version)
        {
            return Retry(
                "operations-notifications.termination.destroy-fence-changed",
                observedAtUtc);
        }

        return MapDestroyResult(
            request,
            expectedRevision,
            result,
            observedAtUtc);
    }

    private static TenantTerminationContributionResult MapDestroyResult(
        TenantTerminationContributionRequest request,
        long expectedRevision,
        NotificationScopeDestroyResult? result,
        DateTimeOffset recordedAtUtc)
    {
        if (result is null)
        {
            return Failed(
                "operations-notifications.termination.destroy-response-invalid",
                recordedAtUtc);
        }

        if (result.Status is NotificationScopeDestroyStatus.Completed or
            NotificationScopeDestroyStatus.Replayed)
        {
            return IsValid(
                result.Receipt,
                request,
                expectedRevision,
                recordedAtUtc)
                ? Completed(
                    "operations-notifications.termination.destroyed",
                    result.Receipt!.RemovedRecordCount,
                    expectedRevision,
                    result.Receipt.ResultingRevision,
                    result.Receipt.CompletedAtUtc)
                : Failed(
                    "operations-notifications.termination.destroy-response-invalid",
                    recordedAtUtc);
        }

        if (result.Status == NotificationScopeDestroyStatus.InProgress)
        {
            return IsValid(
                result.Progress,
                request,
                expectedRevision,
                recordedAtUtc)
                ? Retry(
                    "operations-notifications.termination.destroy-in-progress",
                    recordedAtUtc,
                    result.Progress!.RemovedRecordCount)
                : Failed(
                    "operations-notifications.termination.destroy-response-invalid",
                    recordedAtUtc);
        }

        if (result.Status == NotificationScopeDestroyStatus.Busy)
        {
            if (result.Progress is not null &&
                !IsValid(
                    result.Progress,
                    request,
                    expectedRevision,
                    recordedAtUtc))
            {
                return Failed(
                    "operations-notifications.termination.destroy-response-invalid",
                    recordedAtUtc);
            }

            return Retry(
                "operations-notifications.termination.destroy-scope-busy",
                recordedAtUtc,
                result.Progress?.RemovedRecordCount ?? 0);
        }

        return result.Status switch
        {
            NotificationScopeDestroyStatus.Stale => Retry(
                "operations-notifications.termination.destroy-revision-changed",
                recordedAtUtc),
            NotificationScopeDestroyStatus.ScopeUnavailable => Retry(
                "operations-notifications.termination.destroy-scope-unavailable",
                recordedAtUtc),
            NotificationScopeDestroyStatus.Conflict => Failed(
                "operations-notifications.termination.destroy-conflict",
                recordedAtUtc),
            _ => Failed(
                "operations-notifications.termination.destroy-response-invalid",
                recordedAtUtc)
        };
    }

    private static bool TrySelectDestroyRevision(
        NotificationScopeSnapshot? snapshot,
        out long expectedRevision)
    {
        expectedRevision = 0;
        if (snapshot is null)
        {
            return false;
        }

        if (snapshot.Status == NotificationScopeStatus.Missing &&
            snapshot.Revision == 0)
        {
            return true;
        }

        if (snapshot.Status == NotificationScopeStatus.Open &&
            snapshot.Revision is > 0 and < long.MaxValue)
        {
            expectedRevision = snapshot.Revision;
            return true;
        }

        if (snapshot.Status == NotificationScopeStatus.Closed &&
            snapshot.Revision > 0)
        {
            expectedRevision = snapshot.Revision - 1;
            return true;
        }

        return false;
    }

    private static bool IsValid(
        NotificationScopeDestroyProgress? progress,
        TenantTerminationContributionRequest request,
        long expectedRevision,
        DateTimeOffset recordedAtUtc) =>
        progress is not null &&
        progress.OperationId == request.IdempotencyKey &&
        progress.ResultingRevision == expectedRevision + 1 &&
        progress.BatchSize == DestroyBatchSize &&
        progress.Stage is > NotificationScopeDestructionStage.Unknown and <
            NotificationScopeDestructionStage.Completed &&
        progress.RemovedRecordCount >= 0 &&
        progress.CompletedBatchCount > 0 &&
        progress.RemovalProofVersion > 0 &&
        OperationsNotificationsDataRightsValidation.IsSha256(
            progress.RemovalProofSha256) &&
        progress.StartedAtUtc != default &&
        progress.UpdatedAtUtc >= progress.StartedAtUtc &&
        progress.UpdatedAtUtc < request.DeadlineUtc &&
        progress.UpdatedAtUtc <= recordedAtUtc;

    private static bool IsValid(
        NotificationScopeDestroyReceipt? receipt,
        TenantTerminationContributionRequest request,
        long expectedRevision,
        DateTimeOffset recordedAtUtc) =>
        receipt is not null &&
        receipt.OperationId == request.IdempotencyKey &&
        receipt.ResultingRevision == expectedRevision + 1 &&
        receipt.BatchSize == DestroyBatchSize &&
        receipt.RemovedRecordCount >= 0 &&
        receipt.CompletedBatchCount >= 0 &&
        receipt.RemovalProofVersion > 0 &&
        OperationsNotificationsDataRightsValidation.IsSha256(
            receipt.RemovalProofSha256) &&
        receipt.StartedAtUtc != default &&
        receipt.CompletedAtUtc >= receipt.StartedAtUtc &&
        receipt.CompletedAtUtc < request.DeadlineUtc &&
        receipt.CompletedAtUtc <= recordedAtUtc;
}
