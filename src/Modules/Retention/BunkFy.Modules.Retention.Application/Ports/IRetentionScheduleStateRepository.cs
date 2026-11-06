namespace BunkFy.Modules.Retention.Application.Ports;

using BunkFy.Modules.Retention.Domain.Aggregates;

public interface IRetentionScheduleStateRepository
{
    Task RecordStartedAsync(
        RetentionExecution execution,
        DateTimeOffset nextDueAtUtc,
        CancellationToken cancellationToken);

    Task RecordCompletedAsync(
        RetentionExecution execution,
        CancellationToken cancellationToken);
}

public interface IRetentionScheduleHealthReader
{
    Task<RetentionScheduleStateSnapshot?> GetAsync(
        string tenantId,
        string ownerKey,
        string dataClassKey,
        Guid? propertyId,
        int executionPolicyVersion,
        CancellationToken cancellationToken);

    Task<RetentionScheduleStateSnapshot?> GetByLastExecutionIdAsync(
        string tenantId,
        Guid lastExecutionId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<RetentionScheduleStateSnapshot>> ListAsync(
        CancellationToken cancellationToken);
}

public sealed record RetentionScheduleStateSnapshot(
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
    DateTimeOffset? HoldReviewDueAtUtc,
    RetentionRunRetryRequestSnapshot? Retry);

public sealed record RetentionRunRetryRequestSnapshot(
    Guid RequestId,
    Guid RunId,
    long EvidenceVersion,
    int Attempt,
    int State,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? ScheduledAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? FailureCode);
