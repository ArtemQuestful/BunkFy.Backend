namespace BunkFy.Modules.Retention.Persistence.Repositories;

using BunkFy.Modules.Retention.Domain.Models;

[AttributeUsage(AttributeTargets.Property)]
internal sealed class RetentionTenantExportFieldAttribute(string fieldId)
    : Attribute
{
    public string FieldId { get; } = fieldId;
}

internal sealed record RetentionExecutionTenantExport(
    [property: RetentionTenantExportField(
        "retention.tenant-scope-reference")]
    string ScopeId,
    [property: RetentionTenantExportField("retention.property-reference")]
    Guid? PropertyId,
    [property: RetentionTenantExportField("retention.execution-record")]
    RetentionExecutionTenantExportPayload Execution);

internal sealed record RetentionExecutionTenantExportPayload(
    Guid ExecutionId,
    string OwnerKey,
    string DataClassKey,
    RetentionExecutionTargetKind TargetKind,
    int ExecutionPolicyVersion,
    int Attempt,
    RetentionExecutionState State,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset DeadlineUtc,
    DateTimeOffset? CompletedAtUtc,
    int? ScannedCount,
    int? AffectedCount,
    int? RemainingCount,
    string? OutcomeCode,
    DateTimeOffset? HoldReviewDueAtUtc,
    long Version);

internal sealed record RetentionScheduleStateTenantExport(
    [property: RetentionTenantExportField(
        "retention.tenant-scope-reference")]
    string ScopeId,
    [property: RetentionTenantExportField("retention.property-reference")]
    Guid? PropertyId,
    [property: RetentionTenantExportField("retention.schedule-state")]
    RetentionScheduleStateTenantExportPayload Schedule);

internal sealed record RetentionScheduleStateTenantExportPayload(
    string OwnerKey,
    string DataClassKey,
    string TargetKey,
    int ExecutionPolicyVersion,
    Guid LastExecutionId,
    RetentionExecutionState State,
    DateTimeOffset LastStartedAtUtc,
    DateTimeOffset? LastCompletedAtUtc,
    DateTimeOffset NextDueAtUtc,
    int ConsecutiveFailures,
    int? LastScannedCount,
    int? LastAffectedCount,
    int? LastRemainingCount,
    string? OutcomeCode,
    DateTimeOffset? HoldReviewDueAtUtc,
    long Version);

internal sealed record RetentionRunRetryRequestTenantExport(
    [property: RetentionTenantExportField(
        "retention.tenant-scope-reference")]
    string ScopeId,
    [property: RetentionTenantExportField("retention.property-reference")]
    Guid? PropertyId,
    [property: RetentionTenantExportField("retention.run-retry-request")]
    RetentionRunRetryRequestTenantExportPayload RetryRequest);

internal sealed record RetentionRunRetryRequestTenantExportPayload(
    Guid RequestId,
    Guid RunId,
    string OwnerKey,
    string DataClassKey,
    RetentionExecutionTargetKind TargetKind,
    int ExecutionPolicyVersion,
    long EvidenceVersion,
    int Attempt,
    RetentionRunRetryRequestState State,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? ScheduledAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? FailureCode,
    long Version);
