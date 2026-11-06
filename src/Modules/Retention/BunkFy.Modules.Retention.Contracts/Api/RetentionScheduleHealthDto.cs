namespace BunkFy.Modules.Retention.Contracts;

public sealed record RetentionScheduleHealthDto(
    string OwnerKey,
    string DataClassKey,
    RetentionTargetScopeKind TargetScopeKind,
    Guid? PropertyId,
    int ExecutionPolicyVersion,
    long EvidenceVersion,
    RetentionExecutionStatus Status,
    Guid? LastRunId,
    DateTimeOffset? LastStartedAtUtc,
    DateTimeOffset? LastCompletedAtUtc,
    DateTimeOffset NextDueAtUtc,
    bool Overdue,
    int ConsecutiveFailures,
    int? LastScannedCount,
    int? LastAffectedCount,
    int? LastRemainingCount,
    string? OutcomeCode,
    DateTimeOffset? HoldReviewDueAtUtc,
    RetentionRunRetryReceiptDto? Retry);

public sealed record RetentionScheduleHealthSummaryDto(
    int Total,
    int Healthy,
    int Running,
    int NeedsAttention);

public sealed record RetentionScheduleHealthListResponse(
    IReadOnlyList<RetentionScheduleHealthDto> Items,
    int Page,
    int PageSize,
    bool HasMore,
    RetentionScheduleHealthSummaryDto Summary);

public enum RetentionExecutionStatus
{
    Unknown = 0,
    NeverRun = 1,
    Running = 2,
    Completed = 3,
    Blocked = 4,
    Failed = 5
}
