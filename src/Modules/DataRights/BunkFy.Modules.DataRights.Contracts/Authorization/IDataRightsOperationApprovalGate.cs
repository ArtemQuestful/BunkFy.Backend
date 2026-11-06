namespace BunkFy.Modules.DataRights.Contracts.Authorization;

public interface IDataRightsOperationApprovalGate
{
    Task<DataRightsOperationApprovalResult> EvaluateAsync(
        DataRightsOperationApprovalRequest request,
        CancellationToken cancellationToken);
}

public sealed record DataRightsOperationApprovalRequest(
    string TenantId,
    Guid? PropertyId,
    Guid CaseId,
    long ApprovalRevision,
    DataRightsOperation Operation,
    string OwnerKey,
    string RecordType,
    Guid RecordId,
    long RecordVersion,
    DataRightsRestrictionDirective RestrictionDirective = DataRightsRestrictionDirective.Unknown,
    string? ExecutingActorId = null,
    DataRightsCaseType CaseType = DataRightsCaseType.GuestRights,
    Guid? RestrictionTargetOwnerOperationId = null,
    long? RestrictionTargetOwnerOperationVersion = null);

public sealed record DataRightsOperationApprovalResult(
    bool IsApproved,
    DataRightsOperationApprovalDenial Denial,
    DataRightsApprovalEvidence? ApprovalEvidence = null)
{
    public static DataRightsOperationApprovalResult Approved { get; } =
        new(true, DataRightsOperationApprovalDenial.None);

    public static DataRightsOperationApprovalResult ApprovedWithEvidence(
        DataRightsApprovalEvidence evidence) =>
        new(true, DataRightsOperationApprovalDenial.None, evidence);

    public static DataRightsOperationApprovalResult Denied(
        DataRightsOperationApprovalDenial denial) => new(false, denial);
}

public enum DataRightsOperationApprovalDenial
{
    None = 0,
    InvalidRequest = 1,
    CaseNotFound = 2,
    CaseNotApproved = 3,
    ApprovalRevisionMismatch = 4,
    OperationNotApproved = 5,
    SubjectNotApproved = 6,
    RestrictionDirectiveMismatch = 7,
    ApprovalEvidenceMissing = 8,
    ExecutionActorRequired = 9,
    DecisionActorCannotExecute = 10,
    RestrictionTargetMismatch = 11
}
