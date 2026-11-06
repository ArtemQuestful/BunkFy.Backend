namespace BunkFy.Modules.DataRights.Contracts;

public sealed record DataRightsCorrectionExecutionDto(
    DataRightsCaseDto Case,
    DataRightsCorrectionExecutionDetailsDto Execution);

public sealed record DataRightsCorrectionExecutionDetailsDto(
    Guid ExecutionId,
    Guid CaseId,
    DataRightsCaseType CaseType,
    Guid? PropertyId,
    long SelectedCaseVersion,
    long ExecutionRevision,
    long ApprovalRevision,
    DataRightsSubjectCoordinate Subject,
    string FieldPolicyKey,
    string ClaimedBy,
    bool IsCurrentActor,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DataRightsCorrectionExecutionStatus Status,
    int? ReceiptContractVersion,
    Guid? ReceiptId,
    long? CurrentRecordVersion,
    int? ChangedFieldCount,
    string? ChangedFieldsSha256,
    string? ReceiptSha256,
    DateTimeOffset? CompletedAtUtc,
    long Version);

public enum DataRightsCorrectionExecutionStatus
{
    Unknown = 0,
    Claimed = 1,
    Completed = 2
}
