namespace BunkFy.Modules.DataRights.Domain.Errors;

using Gma.Framework.Results;

public static class DataRightsDomainErrors
{
    public static readonly Error CaseIdRequired = new(
        "DataRights.CaseIdRequired",
        "A data-rights case id is required.");
    public static readonly Error TenantInvalid = new(
        "DataRights.TenantInvalid",
        "The tenant id is invalid.");
    public static readonly Error PropertyRequired = new(
        "DataRights.PropertyRequired",
        "A guest data-rights case requires a property.");
    public static readonly Error PropertyNotAllowed = new(
        "DataRights.PropertyNotAllowed",
        "A tenant-scoped data-rights case cannot be limited to one property.");
    public static readonly Error CaseTypeInvalid = new(
        "DataRights.CaseTypeInvalid",
        "The data-rights case type is invalid.");
    public static readonly Error OperationsInvalid = new(
        "DataRights.OperationsInvalid",
        "The requested data-rights operations are invalid.");
    public static readonly Error RestrictionDirectiveInvalid = new(
        "DataRights.RestrictionDirectiveInvalid",
        "A restriction request must declare exactly one supported directive.");
    public static readonly Error RequesterRelationshipInvalid = new(
        "DataRights.RequesterRelationshipInvalid",
        "The requester relationship is invalid.");
    public static readonly Error GuestRightsRequesterInvalid = new(
        "DataRights.GuestRightsRequesterInvalid",
        "A guest data-rights case must be initiated by the data subject, an authorized representative, or the controller.");
    public static readonly Error StaffRightsRequesterInvalid = new(
        "DataRights.StaffRightsRequesterInvalid",
        "A staff data-rights case must be initiated by the data subject, an authorized representative, or the controller.");
    public static readonly Error StaffRightsOperationsInvalid = new(
        "DataRights.StaffRightsOperationsInvalid",
        "A staff data-rights case requires one supported operation.");
    public static readonly Error TenantTerminationRequesterInvalid = new(
        "DataRights.TenantTerminationRequesterInvalid",
        "Tenant termination must be initiated by the controller or tenant owner.");
    public static readonly Error ActorInvalid = new(
        "DataRights.ActorInvalid",
        "The actor id is invalid.");
    public static readonly Error TimestampInvalid = new(
        "DataRights.TimestampInvalid",
        "The case timestamp is invalid.");
    public static readonly Error VersionConflict = new(
        "DataRights.VersionConflict",
        "The data-rights case version has changed.");
    public static readonly Error TransitionInvalid = new(
        "DataRights.TransitionInvalid",
        "The data-rights case cannot make that transition.");
    public static readonly Error VerificationRequired = new(
        "DataRights.VerificationRequired",
        "Requester verification must succeed before sensitive discovery.");
    public static readonly Error ControllerRoutingRequired = new(
        "DataRights.ControllerRoutingRequired",
        "Controller routing must complete before sensitive discovery.");
    public static readonly Error ResponseDeadlinePolicyEvidenceInvalid = new(
        "DataRights.ResponseDeadlinePolicyEvidenceInvalid",
        "The response deadline policy evidence is invalid or conflicts with the case.");
    public static readonly Error ResponseDeadlinePolicyRequired = new(
        "DataRights.ResponseDeadlinePolicyRequired",
        "A response deadline policy must be assigned before controller routing completes.");
    public static readonly Error SubjectCoordinateInvalid = new(
        "DataRights.SubjectCoordinateInvalid",
        "The selected subject coordinate is invalid.");
    public static readonly Error SubjectAlreadySelected = new(
        "DataRights.SubjectAlreadySelected",
        "The subject coordinate is already selected.");
    public static readonly Error SubjectNotSelected = new(
        "DataRights.SubjectNotSelected",
        "The subject coordinate is not selected.");
    public static readonly Error SubjectSelectionLimitReached = new(
        "DataRights.SubjectSelectionLimitReached",
        "The case has reached its selected-subject limit.");
    public static readonly Error SubjectSelectionRequired = new(
        "DataRights.SubjectSelectionRequired",
        "Select at least one subject coordinate before review.");
    public static readonly Error DecisionInvalid = new(
        "DataRights.DecisionInvalid",
        "The data-rights decision and reason combination is invalid.");
    public static readonly Error ApprovalPolicyEvidenceInvalid = new(
        "DataRights.ApprovalPolicyEvidenceInvalid",
        "An anonymisation approval requires valid immutable policy evidence.");
    public static readonly Error AnonymisationApprovalInvalid = new(
        "DataRights.AnonymisationApprovalInvalid",
        "The case does not contain an executable anonymisation approval.");
    public static readonly Error AnonymisationSubjectCountInvalid = new(
        "DataRights.AnonymisationSubjectCountInvalid",
        "An anonymisation execution requires at least one selected subject.");
    public static readonly Error RestrictionExecutionInvalid = new(
        "DataRights.RestrictionExecutionInvalid",
        "The case does not contain one executable processing restriction.");
    public static readonly Error RestrictionReleaseTargetInvalid = new(
        "DataRights.RestrictionReleaseTargetInvalid",
        "The processing-restriction release target is invalid.");
    public static readonly Error RestrictionReleaseTargetRequired = new(
        "DataRights.RestrictionReleaseTargetRequired",
        "Select one processing-restriction release target before review.");
    public static readonly Error RestrictionExecutionProofInvalid = new(
        "DataRights.RestrictionExecutionProofInvalid",
        "The processing-restriction owner proof is invalid.");
    public static readonly Error RestrictionExecutionConflict = new(
        "DataRights.RestrictionExecutionConflict",
        "The processing restriction was already executed with different coordinates.");
    public static readonly Error CorrectionExecutionInvalid = new(
        "DataRights.CorrectionExecutionInvalid",
        "The case does not contain one executable correction.");
    public static readonly Error CorrectionExecutionCoordinateInvalid = new(
        "DataRights.CorrectionExecutionCoordinateInvalid",
        "The correction execution coordinate is invalid.");
    public static readonly Error CorrectionExecutionConflict = new(
        "DataRights.CorrectionExecutionConflict",
        "The correction execution is already bound to different coordinates.");
    public static readonly Error CorrectionExecutionProofInvalid = new(
        "DataRights.CorrectionExecutionProofInvalid",
        "The correction owner proof is invalid.");
    public static readonly Error CorrectionExecutionExpired = new(
        "DataRights.CorrectionExecutionExpired",
        "The correction execution claim has expired.");
    public static readonly Error DecisionActorCannotExecute = new(
        "DataRights.DecisionActorCannotExecute",
        "The decision actor cannot execute this anonymisation.");
    public static readonly Error ExecutionCoordinateInvalid = new(
        "DataRights.ExecutionCoordinateInvalid",
        "The data-rights execution coordinate is invalid.");
    public static readonly Error ExecutionTaskConflict = new(
        "DataRights.ExecutionTaskConflict",
        "The data-rights execution is already bound to another task or attempt.");
    public static readonly Error ExecutionOwnerResultInvalid = new(
        "DataRights.ExecutionOwnerResultInvalid",
        "The data-rights owner result is invalid or conflicts with durable proof.");
    public static readonly Error ExecutionOutcomeInvalid = new(
        "DataRights.ExecutionOutcomeInvalid",
        "The data-rights execution outcome does not match its selected subjects.");
    public static readonly Error RecordPseudonymInvalid = new(
        "DataRights.RecordPseudonymInvalid",
        "The data-rights record pseudonym is invalid.");
    public static readonly Error RecordPseudonymKeyUnavailable = new(
        "DataRights.RecordPseudonymKeyUnavailable",
        "The requested data-rights record pseudonym key is unavailable.");
    public static readonly Error ProcessingLedgerEntryInvalid = new(
        "DataRights.ProcessingLedgerEntryInvalid",
        "The data-rights processing ledger entry is invalid.");
    public static readonly Error ReplayEnvelopeInvalid = new(
        "DataRights.ReplayEnvelopeInvalid",
        "The data-rights replay envelope is invalid.");
    public static readonly Error ReplayEnvelopeKeyUnavailable = new(
        "DataRights.ReplayEnvelopeKeyUnavailable",
        "The requested data-rights replay-envelope key is unavailable.");
    public static readonly Error RestoreCheckpointInvalid = new(
        "DataRights.RestoreCheckpointInvalid",
        "The data-rights restore checkpoint is invalid.");
    public static readonly Error RestoreCheckpointConflict = new(
        "DataRights.RestoreCheckpointConflict",
        "The data-rights restore checkpoint changed during reconciliation.");
    public static readonly Error ExportArtifactCoordinateInvalid = new(
        "DataRights.ExportArtifactCoordinateInvalid",
        "The protected export artifact coordinate is invalid.");
    public static readonly Error ExportArtifactGenerationInvalid = new(
        "DataRights.ExportArtifactGenerationInvalid",
        "The protected export generation attempt is invalid.");
    public static readonly Error ExportArtifactTransitionInvalid = new(
        "DataRights.ExportArtifactTransitionInvalid",
        "The protected export artifact cannot make that transition.");
    public static readonly Error ExportArtifactCompletionInvalid = new(
        "DataRights.ExportArtifactCompletionInvalid",
        "The protected export artifact completion proof is invalid.");
    public static readonly Error ExportArtifactFailureInvalid = new(
        "DataRights.ExportArtifactFailureInvalid",
        "The protected export artifact failure proof is invalid.");
    public static readonly Error ExportArtifactDeletionInvalid = new(
        "DataRights.ExportArtifactDeletionInvalid",
        "The protected export artifact deletion proof is invalid.");
    public static readonly Error ExportAuditEntryInvalid = new(
        "DataRights.ExportAuditEntryInvalid",
        "The protected export audit fact is invalid.");
    public static readonly Error AccessExportCompletionInvalid = new(
        "DataRights.AccessExportCompletionInvalid",
        "The case does not contain the matching approved access export.");
    public static readonly Error TenantTerminationCoordinateInvalid = new(
        "DataRights.TenantTerminationCoordinateInvalid",
        "The tenant-termination coordinate is invalid.");
    public static readonly Error TenantTerminationTransitionInvalid = new(
        "DataRights.TenantTerminationTransitionInvalid",
        "The tenant-termination process cannot make that transition.");
    public static readonly Error TenantTerminationFreezeCheckpointInvalid = new(
        "DataRights.TenantTerminationFreezeCheckpointInvalid",
        "The tenant-termination freeze checkpoint is invalid or conflicts with durable proof.");
    public static readonly Error TenantTerminationExecutorInvalid = new(
        "DataRights.TenantTerminationExecutorInvalid",
        "The tenant-termination decision actor cannot begin destructive execution.");
    public static readonly Error TenantTerminationOwnerWorkInvalid = new(
        "DataRights.TenantTerminationOwnerWorkInvalid",
        "The tenant-termination owner work is invalid or conflicts with durable proof.");
    public static readonly Error TenantTerminationExportFragmentInvalid = new(
        "DataRights.TenantTerminationExportFragmentInvalid",
        "The tenant-termination export fragment coordinate is invalid.");
    public static readonly Error TenantTerminationExportFragmentTransitionInvalid = new(
        "DataRights.TenantTerminationExportFragmentTransitionInvalid",
        "The tenant-termination export fragment cannot make that transition.");
    public static readonly Error TenantTerminationExportFragmentGenerationInvalid = new(
        "DataRights.TenantTerminationExportFragmentGenerationInvalid",
        "The tenant-termination export fragment generation attempt is invalid.");
    public static readonly Error TenantTerminationExportFragmentCompletionInvalid = new(
        "DataRights.TenantTerminationExportFragmentCompletionInvalid",
        "The tenant-termination export fragment completion proof is invalid.");
    public static readonly Error TenantTerminationExportFragmentFailureInvalid = new(
        "DataRights.TenantTerminationExportFragmentFailureInvalid",
        "The tenant-termination export fragment failure proof is invalid.");
    public static readonly Error TenantTerminationExportFragmentDeletionInvalid = new(
        "DataRights.TenantTerminationExportFragmentDeletionInvalid",
        "The tenant-termination export fragment deletion proof is invalid.");
    public static readonly Error TenantTerminationExportArtifactInvalid = new(
        "DataRights.TenantTerminationExportArtifactInvalid",
        "The tenant-termination export artifact coordinate is invalid.");
    public static readonly Error TenantTerminationExportArtifactTransitionInvalid = new(
        "DataRights.TenantTerminationExportArtifactTransitionInvalid",
        "The tenant-termination export artifact cannot make that transition.");
    public static readonly Error TenantTerminationExportArtifactGenerationInvalid = new(
        "DataRights.TenantTerminationExportArtifactGenerationInvalid",
        "The tenant-termination export artifact generation attempt is invalid.");
    public static readonly Error TenantTerminationExportArtifactCompletionInvalid = new(
        "DataRights.TenantTerminationExportArtifactCompletionInvalid",
        "The tenant-termination export artifact completion proof is invalid.");
    public static readonly Error TenantTerminationExportArtifactFailureInvalid = new(
        "DataRights.TenantTerminationExportArtifactFailureInvalid",
        "The tenant-termination export artifact failure proof is invalid.");
    public static readonly Error TenantTerminationExportArtifactDeletionInvalid = new(
        "DataRights.TenantTerminationExportArtifactDeletionInvalid",
        "The tenant-termination export artifact deletion proof is invalid.");
    public static readonly Error TenantTerminationExportConfirmationInvalid = new(
        "DataRights.TenantTerminationExportConfirmationInvalid",
        "The tenant-termination export confirmation proof is invalid.");
    public static readonly Error TenantTerminationVerificationConfirmationInvalid = new(
        "DataRights.TenantTerminationVerificationConfirmationInvalid",
        "The tenant-termination verification confirmation proof is invalid.");
    public static readonly Error TenantTerminationTerminalReceiptInvalid = new(
        "DataRights.TenantTerminationTerminalReceiptInvalid",
        "The tenant-termination terminal receipt is invalid or conflicts with durable proof.");
}
