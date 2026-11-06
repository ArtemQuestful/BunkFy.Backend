namespace BunkFy.Modules.Retention.Contracts;

public sealed record RetryRetentionScheduleRequest(
    bool Confirmed,
    string OwnerKey,
    string DataClassKey,
    RetentionTargetScopeKind TargetScopeKind,
    Guid? PropertyId,
    int ExecutionPolicyVersion,
    long EvidenceVersion);

public sealed record RetentionRunRetryReceiptDto(
    Guid RequestId,
    Guid RunId,
    long EvidenceVersion,
    int Attempt,
    RetentionRunRetryStatus Status,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? ScheduledAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? FailureCode);

public enum RetentionRunRetryStatus
{
    Pending = 1,
    Applied = 2,
    Failed = 3
}
