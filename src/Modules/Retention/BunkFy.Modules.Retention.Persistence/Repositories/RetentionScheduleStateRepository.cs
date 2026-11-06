namespace BunkFy.Modules.Retention.Persistence.Repositories;

using BunkFy.Modules.Retention.Application.Ports;
using BunkFy.Modules.Retention.Domain.Aggregates;
using Microsoft.EntityFrameworkCore;

internal sealed class RetentionScheduleStateRepository(
    RetentionDbContext dbContext)
    : IRetentionScheduleStateRepository, IRetentionScheduleHealthReader
{
    public async Task RecordStartedAsync(
        RetentionExecution execution,
        DateTimeOffset nextDueAtUtc,
        CancellationToken cancellationToken)
    {
        RetentionScheduleState? state = await this.GetAsync(
            execution,
            cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            dbContext.ScheduleStates.Add(new(execution, nextDueAtUtc));
            return;
        }

        state.RecordStarted(execution, nextDueAtUtc);
    }

    public async Task RecordCompletedAsync(
        RetentionExecution execution,
        CancellationToken cancellationToken)
    {
        RetentionScheduleState state = await this.GetAsync(
            execution,
            cancellationToken).ConfigureAwait(false) ??
            throw new InvalidOperationException(
                "Retention.ScheduleStateUnavailable");
        state.RecordCompleted(execution);
    }

    public async Task<IReadOnlyList<RetentionScheduleStateSnapshot>> ListAsync(
        CancellationToken cancellationToken)
    {
        ScheduleProjection[] states = await dbContext.ScheduleStates
            .AsNoTracking()
            .OrderBy(state => state.OwnerKey)
            .ThenBy(state => state.DataClassKey)
            .ThenBy(state => state.TargetKey)
            .Select(ToProjection())
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (states.Length == 0)
        {
            return [];
        }

        RetentionRunRetryRequestSnapshot[] retries = await (
            from request in dbContext.RunRetryRequests.AsNoTracking()
            join state in dbContext.ScheduleStates.AsNoTracking()
                on new
                {
                    request.ScopeId,
                    request.RunId,
                    request.EvidenceVersion
                }
                equals new
                {
                    state.ScopeId,
                    RunId = state.LastExecutionId,
                    EvidenceVersion = state.Version
                }
            select new RetentionRunRetryRequestSnapshot(
                request.Id,
                request.RunId,
                request.EvidenceVersion,
                request.Attempt,
                (int)request.State,
                request.RequestedAtUtc,
                request.ScheduledAtUtc,
                request.CompletedAtUtc,
                request.FailureCode))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        Dictionary<(Guid RunId, long EvidenceVersion),
            RetentionRunRetryRequestSnapshot> byEvidence = retries.ToDictionary(
            retry => (retry.RunId, retry.EvidenceVersion));
        return states
            .Select(state => ToSnapshot(
                state,
                byEvidence.GetValueOrDefault(
                    (state.LastExecutionId, state.Version))))
            .ToArray();
    }

    public async Task<RetentionScheduleStateSnapshot?> GetAsync(
        string tenantId,
        string ownerKey,
        string dataClassKey,
        Guid? propertyId,
        int executionPolicyVersion,
        CancellationToken cancellationToken)
    {
        string targetKey = RetentionScheduleState.CreateTargetKey(propertyId);
        ScheduleProjection? state = await dbContext.ScheduleStates
            .AsNoTracking()
            .Where(item =>
                item.ScopeId == tenantId &&
                item.OwnerKey == ownerKey &&
                item.DataClassKey == dataClassKey &&
                item.TargetKey == targetKey &&
                item.ExecutionPolicyVersion == executionPolicyVersion)
            .Select(ToProjection())
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return state is null ? null : ToSnapshot(state, retry: null);
    }

    public async Task<RetentionScheduleStateSnapshot?>
        GetByLastExecutionIdAsync(
            string tenantId,
            Guid lastExecutionId,
            CancellationToken cancellationToken)
    {
        ScheduleProjection? state = await dbContext.ScheduleStates
            .AsNoTracking()
            .Where(item =>
                item.ScopeId == tenantId &&
                item.LastExecutionId == lastExecutionId)
            .Select(ToProjection())
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return state is null ? null : ToSnapshot(state, retry: null);
    }

    private Task<RetentionScheduleState?> GetAsync(
        RetentionExecution execution,
        CancellationToken cancellationToken)
    {
        string targetKey = RetentionScheduleState.CreateTargetKey(
            execution.PropertyId);
        return dbContext.ScheduleStates.SingleOrDefaultAsync(
            state =>
                state.OwnerKey == execution.OwnerKey &&
                state.DataClassKey == execution.DataClassKey &&
                state.TargetKey == targetKey &&
                state.ExecutionPolicyVersion ==
                    execution.ExecutionPolicyVersion,
            cancellationToken);
    }

    private static System.Linq.Expressions.Expression<
        Func<RetentionScheduleState, ScheduleProjection>> ToProjection() =>
        state => new ScheduleProjection(
            state.OwnerKey,
            state.DataClassKey,
            state.PropertyId,
            state.ExecutionPolicyVersion,
            state.Version,
            (int)state.State,
            state.LastExecutionId,
            state.LastStartedAtUtc,
            state.LastCompletedAtUtc,
            state.NextDueAtUtc,
            state.ConsecutiveFailures,
            state.LastScannedCount,
            state.LastAffectedCount,
            state.LastRemainingCount,
            state.OutcomeCode,
            state.HoldReviewDueAtUtc);

    private static RetentionScheduleStateSnapshot ToSnapshot(
        ScheduleProjection state,
        RetentionRunRetryRequestSnapshot? retry) => new(
            state.OwnerKey,
            state.DataClassKey,
            state.PropertyId,
            state.ExecutionPolicyVersion,
            state.Version,
            state.State,
            state.LastExecutionId,
            state.LastStartedAtUtc,
            state.LastCompletedAtUtc,
            state.NextDueAtUtc,
            state.ConsecutiveFailures,
            state.LastScannedCount,
            state.LastAffectedCount,
            state.LastRemainingCount,
            state.OutcomeCode,
            state.HoldReviewDueAtUtc,
            retry);

    private sealed record ScheduleProjection(
        string OwnerKey,
        string DataClassKey,
        Guid? PropertyId,
        int ExecutionPolicyVersion,
        long Version,
        int State,
        Guid LastExecutionId,
        DateTimeOffset LastStartedAtUtc,
        DateTimeOffset? LastCompletedAtUtc,
        DateTimeOffset NextDueAtUtc,
        int ConsecutiveFailures,
        int? LastScannedCount,
        int? LastAffectedCount,
        int? LastRemainingCount,
        string? OutcomeCode,
        DateTimeOffset? HoldReviewDueAtUtc);
}
