namespace BunkFy.Modules.DataRights.Contracts;

public sealed record TenantTerminationCaseDto(
    Guid Id,
    DataRightsRequesterRelationship RequesterRelationship,
    bool ExportRequested,
    DataRightsCaseStatus Status,
    DataRightsDecisionOutcome Decision,
    DataRightsDecisionReason DecisionReason,
    long? DecisionRevision,
    DateTimeOffset? DecidedAtUtc,
    long? ExecutionRevision,
    DateTimeOffset? ExecutionStartedAtUtc,
    long Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastChangedAtUtc);

public sealed record TenantTerminationProcessDto(
    Guid Id,
    Guid CaseId,
    long ApprovalRevision,
    bool ExportRequested,
    TenantTerminationPhase Phase,
    TenantTerminationStatus Status,
    long OperationRevision,
    string? OutcomeCode,
    DateTimeOffset? HoldReviewAtUtc,
    long Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastChangedAtUtc);

public sealed record TenantTerminationStartDto(
    TenantTerminationCaseDto Case,
    TenantTerminationProcessDto Process);

public sealed record TenantTerminationOperatorStatusDto(
    TenantTerminationCaseDto Case,
    TenantTerminationProcessDto? Process,
    IReadOnlyList<TenantTerminationOwnerWorkItemDto> OwnerWorkItems,
    TenantTerminationExportHandoffDto? ExportHandoff);

public sealed record TenantTerminationExportHandoffDto(
    Guid ArtifactId,
    long ExportOperationRevision,
    TenantTerminationExportArtifactStatus Status,
    int? FragmentCount,
    long? RecordCount,
    string FrozenRevisionSha256,
    string FragmentSetSha256,
    DateTimeOffset? AvailableAtUtc,
    DateTimeOffset ExpiresAtUtc,
    long ArtifactVersion,
    bool Confirmed,
    long? ConfirmedArtifactVersion,
    long ConfirmationRevision,
    string? ConfirmedBy,
    DateTimeOffset? ConfirmedAtUtc);

public sealed record TenantTerminationOwnerWorkItemDto(
    Guid Id,
    string OwnerKey,
    TenantTerminationOwnerWorkPhase Phase,
    TenantTerminationOwnerWorkStatus Status,
    long OperationRevision,
    int AttemptCount,
    Guid? TaskRunId,
    int LastTaskAttempt,
    DateTimeOffset? LastAttemptAtUtc,
    string? ResultCode,
    long? AffectedCount,
    long? RemainingActiveCount,
    DateTimeOffset? HoldReviewAtUtc,
    DateTimeOffset? ResultRecordedAtUtc,
    long Version);

public enum TenantTerminationPhase
{
    Unknown = 0,
    Freeze = 1,
    Export = 2,
    Destroy = 3,
    Verify = 4,
    Completed = 5,
    Restore = 6
}

public enum TenantTerminationStatus
{
    Unknown = 0,
    Pending = 1,
    Running = 2,
    Blocked = 3,
    Failed = 4,
    Completed = 5,
    Canceled = 6
}

public enum TenantTerminationOwnerWorkPhase
{
    Unknown = 0,
    Freeze = 1,
    Export = 2,
    Destroy = 3,
    Verify = 4,
    Restore = 5
}

public enum TenantTerminationOwnerWorkStatus
{
    Unknown = 0,
    Prepared = 1,
    Processing = 2,
    RetryRequired = 3,
    Blocked = 4,
    Failed = 5,
    Completed = 6
}

public enum TenantTerminationExportArtifactStatus
{
    Unknown = 0,
    Requested = 1,
    Generating = 2,
    Available = 3,
    Failed = 4,
    Expired = 5,
    Deleting = 6,
    Deleted = 7
}
