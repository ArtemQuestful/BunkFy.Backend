namespace BunkFy.Modules.Retention.Application.Handlers;

using BunkFy.Modules.Retention.Application.Ports;
using BunkFy.Modules.Retention.Application.Queries;
using BunkFy.Modules.Retention.Contracts;
using BunkFy.Modules.Retention.Domain.Models;
using Gma.Framework.Cqrs;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;

internal sealed class ListRetentionScheduleHealthQueryHandler(
    IRetentionScopeRepository scopes,
    IRetentionScheduleHealthReader states,
    IEnumerable<IRetentionExecutionContributor> contributors,
    ISystemClock clock)
    : IQueryHandler<
        ListRetentionScheduleHealthQuery,
        RetentionScheduleHealthListResponse>
{
    public async Task<Result<RetentionScheduleHealthListResponse>> HandleAsync(
        ListRetentionScheduleHealthQuery query,
        CancellationToken cancellationToken)
    {
        PageRequest page = PageRequest.Normalize(query.Page, query.PageSize);
        RetentionScheduleDescriptor[] descriptors =
            RetentionContributorCatalog.GetDescriptors(contributors);
        IReadOnlyList<RetentionScheduleStateSnapshot> snapshots =
            await states.ListAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<ScheduleKey, RetentionScheduleStateSnapshot> byCoordinate =
            snapshots.ToDictionary(
                snapshot => new ScheduleKey(
                    snapshot.OwnerKey,
                    snapshot.DataClassKey,
                    snapshot.PropertyId,
                    snapshot.ExecutionPolicyVersion));
        DateTimeOffset nowUtc = clock.UtcNow;
        List<RetentionScheduleHealthDto> health = [];
        foreach (IGrouping<RetentionTargetScopeKind, RetentionScheduleDescriptor> group in
                 descriptors.GroupBy(descriptor => descriptor.TargetScopeKind))
        {
            IReadOnlyList<RetentionScheduleTarget> targets =
                await scopes.ListCurrentActiveTargetsAsync(
                    group.Key,
                    cancellationToken).ConfigureAwait(false);
            foreach (RetentionScheduleDescriptor descriptor in group)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (RetentionScheduleTarget target in targets)
                {
                    byCoordinate.TryGetValue(
                        new(
                            descriptor.OwnerKey,
                            descriptor.DataClassKey,
                            target.PropertyId,
                            descriptor.ExecutionPolicyVersion),
                        out RetentionScheduleStateSnapshot? snapshot);
                    health.Add(ToDto(descriptor, target, snapshot, nowUtc));
                }
            }
        }

        RetentionScheduleHealthDto[] ordered = health
            .OrderByDescending(NeedsAttention)
            .ThenByDescending(item => item.Overdue)
            .ThenBy(item => item.NextDueAtUtc)
            .ThenBy(item => item.OwnerKey, StringComparer.Ordinal)
            .ThenBy(item => item.DataClassKey, StringComparer.Ordinal)
            .ThenBy(item => item.PropertyId)
            .ThenBy(item => item.ExecutionPolicyVersion)
            .ToArray();
        RetentionScheduleHealthSummaryDto summary = Summarize(ordered);
        RetentionScheduleHealthDto[] window = ordered
            .Skip(page.SkipCount)
            .Take(page.PageSize + 1)
            .ToArray();
        bool hasMore = window.Length > page.PageSize;
        if (hasMore)
        {
            window = window[..page.PageSize];
        }

        return Result.Success(new RetentionScheduleHealthListResponse(
            window,
            page.Page,
            page.PageSize,
            hasMore,
            summary));
    }

    private static RetentionScheduleHealthDto ToDto(
        RetentionScheduleDescriptor descriptor,
        RetentionScheduleTarget target,
        RetentionScheduleStateSnapshot? snapshot,
        DateTimeOffset now)
    {
        DateTimeOffset nextDueAtUtc = snapshot?.NextDueAtUtc ?? now;
        return new(
            descriptor.OwnerKey,
            descriptor.DataClassKey,
            descriptor.TargetScopeKind,
            target.PropertyId,
            descriptor.ExecutionPolicyVersion,
            snapshot?.Version ?? 0,
            snapshot is null
                ? RetentionExecutionStatus.NeverRun
                : Map(snapshot.State),
            snapshot?.LastExecutionId,
            snapshot?.LastStartedAtUtc,
            snapshot?.LastCompletedAtUtc,
            nextDueAtUtc,
            snapshot is not null && nextDueAtUtc < now,
            snapshot?.ConsecutiveFailures ?? 0,
            snapshot?.LastScannedCount,
            snapshot?.LastAffectedCount,
            snapshot?.LastRemainingCount,
            snapshot?.OutcomeCode,
            snapshot?.HoldReviewDueAtUtc,
            snapshot?.Retry is { } retry ? ToRetryDto(retry) : null);
    }

    private static RetentionRunRetryReceiptDto ToRetryDto(
        RetentionRunRetryRequestSnapshot retry) => new(
            retry.RequestId,
            retry.RunId,
            retry.EvidenceVersion,
            retry.Attempt,
            retry.State switch
            {
                (int)RetentionRunRetryRequestState.Pending =>
                    RetentionRunRetryStatus.Pending,
                (int)RetentionRunRetryRequestState.Applied =>
                    RetentionRunRetryStatus.Applied,
                _ => RetentionRunRetryStatus.Failed
            },
            retry.RequestedAtUtc,
            retry.ScheduledAtUtc,
            retry.CompletedAtUtc,
            retry.FailureCode);

    private static RetentionScheduleHealthSummaryDto Summarize(
        RetentionScheduleHealthDto[] items)
    {
        int healthy = 0;
        int running = 0;
        int needsAttention = 0;
        foreach (RetentionScheduleHealthDto item in items)
        {
            if (NeedsAttention(item))
            {
                needsAttention++;
            }
            else if (item.Status == RetentionExecutionStatus.Running)
            {
                running++;
            }
            else if (item.Status == RetentionExecutionStatus.Completed)
            {
                healthy++;
            }
        }

        return new(items.Length, healthy, running, needsAttention);
    }

    private static bool NeedsAttention(RetentionScheduleHealthDto item) =>
        item.Overdue || item.Status is
            RetentionExecutionStatus.Unknown or
            RetentionExecutionStatus.NeverRun or
            RetentionExecutionStatus.Blocked or
            RetentionExecutionStatus.Failed;

    private static RetentionExecutionStatus Map(int state) =>
        (RetentionExecutionState)state switch
        {
            RetentionExecutionState.Running => RetentionExecutionStatus.Running,
            RetentionExecutionState.Completed => RetentionExecutionStatus.Completed,
            RetentionExecutionState.Blocked => RetentionExecutionStatus.Blocked,
            RetentionExecutionState.Failed => RetentionExecutionStatus.Failed,
            _ => RetentionExecutionStatus.Unknown
        };

    private sealed record ScheduleKey(
        string OwnerKey,
        string DataClassKey,
        Guid? PropertyId,
        int ExecutionPolicyVersion);
}
