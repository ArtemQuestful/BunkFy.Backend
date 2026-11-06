namespace BunkFy.Modules.DataRights.Application.Mapping;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;

internal static class DataRightsCorrectionExecutionMappings
{
    public static DataRightsCorrectionExecutionDetailsDto ToDto(
        this DataRightsCorrectionExecution execution,
        string actorId) => new(
        execution.Id,
        execution.CaseId,
        (DataRightsCaseType)execution.CaseKind,
        execution.PropertyId,
        execution.SelectedCaseVersion,
        execution.ExecutionRevision,
        execution.ApprovalRevision,
        new DataRightsSubjectCoordinate(
            execution.OwnerKey,
            execution.RecordType,
            execution.RecordId,
            execution.SelectedRecordVersion),
        execution.FieldPolicyKey,
        execution.ExecutedBy,
        execution.IsOwnedBy(actorId),
        execution.StartedAtUtc,
        execution.ExpiresAtUtc,
        (DataRightsCorrectionExecutionStatus)execution.State,
        execution.ReceiptContractVersion,
        execution.ReceiptId,
        execution.CurrentRecordVersion,
        execution.ChangedFieldCount,
        execution.ChangedFieldsSha256,
        execution.ReceiptSha256,
        execution.CompletedAtUtc,
        execution.Version);
}
