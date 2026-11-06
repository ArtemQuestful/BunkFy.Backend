namespace BunkFy.Modules.Retention.Contracts;

public interface IRetentionRunRetryExecutor
{
    Task<RetentionRunRetryExecutionOutcome> ExecuteAsync(
        RetentionRunRetryWorkItem workItem,
        CancellationToken cancellationToken);
}

public sealed record RetentionRunRetryWorkItem(
    Guid RequestId,
    Guid RunId,
    string TenantId,
    string OwnerKey,
    string DataClassKey,
    RetentionTargetScopeKind TargetScopeKind,
    Guid? PropertyId,
    int ExecutionPolicyVersion,
    long EvidenceVersion,
    int Attempt,
    DateTimeOffset? ScheduledAtUtc);

public sealed record RetentionRunRetryExecutionOutcome(
    RetentionRunRetryExecutionStatus Status,
    string? FailureCode = null)
{
    public static RetentionRunRetryExecutionOutcome Applied { get; } =
        new(RetentionRunRetryExecutionStatus.Applied);

    public static RetentionRunRetryExecutionOutcome StableFailure(
        string failureCode) =>
        new(RetentionRunRetryExecutionStatus.StableFailure, failureCode);

    public static RetentionRunRetryExecutionOutcome TransientFailure(
        string failureCode) =>
        new(RetentionRunRetryExecutionStatus.TransientFailure, failureCode);
}

public enum RetentionRunRetryExecutionStatus
{
    Applied = 1,
    StableFailure = 2,
    TransientFailure = 3
}
