namespace BunkFy.Modules.DataRights.Application;

using BunkFy.Modules.DataRights.Domain.Errors;
using Gma.Framework.Results;

public static class DataRightsApplicationErrors
{
    public static readonly Error TenantRequired = new(
        "DataRights.TenantRequired",
        "A tenant scope is required.");
    public static readonly Error CreationOperationInvalid = new(
        "DataRights.CreationOperationInvalid",
        "A valid Data Rights case creation operation is required.");
    public static readonly Error CreationOperationConflict = new(
        "DataRights.CreationOperationConflict",
        "The Data Rights case creation operation is already bound to another request.");
    public static readonly Error CaseNotFound = new(
        "DataRights.CaseNotFound",
        "The data-rights case was not found.");
    public static readonly Error DiscoveryCriteriaInvalid = new(
        "DataRights.DiscoveryCriteriaInvalid",
        "Sensitive discovery requires exactly one valid record id, email, or phone.");
    public static readonly Error DiscoveryScopeUnavailable = new(
        "DataRights.DiscoveryScopeUnavailable",
        "The requested property is not available in the owner projection.");
    public static readonly Error SubjectOwnerUnavailable = new(
        "DataRights.SubjectOwnerUnavailable",
        "The selected subject owner is unavailable.");
    public static readonly Error SubjectOwnerCatalogInvalid = new(
        "DataRights.SubjectOwnerCatalogInvalid",
        "The subject-owner catalogue is invalid.");
    public static readonly Error SubjectOwnerRetryRequired = new(
        "DataRights.SubjectOwnerRetryRequired",
        "Subject-owner processing must be retried.");
    public static readonly Error SubjectOwnerResultInvalid = new(
        "DataRights.SubjectOwnerResultInvalid",
        "A subject owner returned an invalid result.");
    public static readonly Error SubjectNotFound = new(
        "DataRights.SubjectNotFound",
        "The selected subject is not available in the requested scope.");
    public static readonly Error SubjectStale = new(
        "DataRights.SubjectStale",
        "The selected subject changed and must be rediscovered.");
    public static readonly Error RequiredCompanionUnavailable = new(
        "DataRights.RequiredCompanionUnavailable",
        "A required companion owner is unavailable.");
    public static readonly Error RequiredCompanionBlocked = new(
        "DataRights.RequiredCompanionBlocked",
        "A required companion prevents this case from entering review.");
    public static readonly Error RequiredCompanionRetryRequired = new(
        "DataRights.RequiredCompanionRetryRequired",
        "Required companion discovery must be retried.");
    public static readonly Error RequiredCompanionResultInvalid = new(
        "DataRights.RequiredCompanionResultInvalid",
        "A required companion owner returned an invalid result.");
    public static readonly Error AnonymisationApprovalPolicyDenied = new(
        "DataRights.AnonymisationApprovalPolicyDenied",
        "The property policy does not authorize anonymisation.");
    public static readonly Error ResponseDeadlinePolicyUnavailable = new(
        "DataRights.ResponseDeadlinePolicyUnavailable",
        "The guest-rights response deadline policy is not available for this property.");
    public static readonly Error DeadlineAlertTaskOptionsInvalid = new(
        "DataRights.DeadlineAlertTaskOptionsInvalid",
        "The response-deadline alert task options are invalid.");
    public static readonly Error AnonymisationMustBeApprovedSeparately = new(
        "DataRights.AnonymisationMustBeApprovedSeparately",
        "Anonymisation must be reviewed and approved as the case's only operation.");
    public static readonly Error AnonymisationExecutionDenied = new(
        "DataRights.AnonymisationExecutionDenied",
        "The approved anonymisation is not executable with the current evidence.");
    public static readonly Error RestrictionExecutionDenied = new(
        "DataRights.RestrictionExecutionDenied",
        "The approved processing restriction is not executable with the current evidence.");
    public static readonly Error RestrictionOwnerUnavailable = new(
        "DataRights.RestrictionOwnerUnavailable",
        "The selected restriction owner is unavailable.");
    public static readonly Error RestrictionOwnerCatalogInvalid = new(
        "DataRights.RestrictionOwnerCatalogInvalid",
        "The processing-restriction owner catalogue is invalid.");
    public static readonly Error RestrictionOwnerRetryRequired = new(
        "DataRights.RestrictionOwnerRetryRequired",
        "Processing-restriction owner execution must be retried.");
    public static readonly Error RestrictionExecutionBlocked = new(
        "DataRights.RestrictionExecutionBlocked",
        "The selected owner cannot apply the approved processing restriction.");
    public static readonly Error RestrictionOwnerProofInvalid = new(
        "DataRights.RestrictionOwnerProofInvalid",
        "The selected owner returned an invalid processing-restriction result or proof.");
    public static readonly Error RestrictionReleaseTargetNotFound = new(
        "DataRights.RestrictionReleaseTargetNotFound",
        "The selected processing-restriction release target is not available.");
    public static readonly Error RestrictionReleaseTargetStale = new(
        "DataRights.RestrictionReleaseTargetStale",
        "The processing-restriction release target changed and must be selected again.");
    public static readonly Error RestrictionReleaseTargetResultInvalid = new(
        "DataRights.RestrictionReleaseTargetResultInvalid",
        "The selected owner returned an invalid processing-restriction target result.");
    public static Error RestrictionReleaseTargetRequired =>
        DataRightsDomainErrors.RestrictionReleaseTargetRequired;
    public static Error RestrictionReleaseTargetInvalid =>
        DataRightsDomainErrors.RestrictionReleaseTargetInvalid;
    public static Error RestrictionExecutionConflict =>
        DataRightsDomainErrors.RestrictionExecutionConflict;
    public static readonly Error CorrectionExecutionDenied = new(
        "DataRights.CorrectionExecutionDenied",
        "The approved correction is not executable with the current evidence.");
    public static readonly Error CorrectionOwnerUnavailable = new(
        "DataRights.CorrectionOwnerUnavailable",
        "The selected correction owner is unavailable.");
    public static readonly Error CorrectionOwnerCatalogInvalid = new(
        "DataRights.CorrectionOwnerCatalogInvalid",
        "The correction-owner policy catalogue is invalid.");
    public static readonly Error CorrectionExecutionNotFound = new(
        "DataRights.CorrectionExecutionNotFound",
        "The correction execution was not found.");
    public static Error CorrectionExecutionConflict =>
        DataRightsDomainErrors.CorrectionExecutionConflict;
    public static Error CorrectionExecutionExpired =>
        DataRightsDomainErrors.CorrectionExecutionExpired;
    public static readonly Error ExecutionNotFound = new(
        "DataRights.ExecutionNotFound",
        "The data-rights execution was not found.");
    public static readonly Error ExecutionAlreadyStarted = new(
        "DataRights.ExecutionAlreadyStarted",
        "The data-rights execution was already started with different coordinates.");
    public static readonly Error ExecutionStateInvalid = new(
        "DataRights.ExecutionStateInvalid",
        "The data-rights execution batch is incomplete or inconsistent.");
    public static readonly Error ExecutionOwnerResultInvalid = new(
        "DataRights.ExecutionOwnerResultInvalid",
        "The owner result is invalid or conflicts with the durable execution state.");
    public static readonly Error ProcessingLedgerConflict = new(
        "DataRights.ProcessingLedgerConflict",
        "The processing ledger conflicts with the durable owner proof.");
    public static readonly Error ProcessingLedgerDurabilityInvalid = new(
        "DataRights.ProcessingLedgerDurabilityInvalid",
        "The external processing-ledger durability proof is invalid.");
    public static readonly Error RestoreEvidenceInvalid = new(
        "DataRights.RestoreEvidenceInvalid",
        "The data-rights restore evidence is invalid.");
    public static readonly Error RestoreOwnerUnavailable = new(
        "DataRights.RestoreOwnerUnavailable",
        "A required data-rights restore owner is unavailable.");
    public static readonly Error RestorePrerequisiteUnavailable = new(
        "DataRights.RestorePrerequisiteUnavailable",
        "A required data-rights restore prerequisite is unavailable.");
    public static readonly Error RestorePrerequisiteBlocked = new(
        "DataRights.RestorePrerequisiteBlocked",
        "A required data-rights restore prerequisite blocked replay.");
    public static readonly Error RestorePrerequisiteRetryRequired = new(
        "DataRights.RestorePrerequisiteRetryRequired",
        "A required data-rights restore prerequisite must be retried.");
    public static readonly Error RestorePrerequisiteResultInvalid = new(
        "DataRights.RestorePrerequisiteResultInvalid",
        "A required data-rights restore prerequisite returned an invalid result.");
    public static readonly Error RestoreOwnerProofInvalid = new(
        "DataRights.RestoreOwnerProofInvalid",
        "A data-rights restore owner returned invalid proof.");
    public static readonly Error RestoreStorageConflict = new(
        "DataRights.RestoreStorageConflict",
        "The external and database data-rights restore state conflict.");
    public static readonly Error ExportNotEligible = new(
        "DataRights.ExportNotEligible",
        "The case is not eligible for protected access export generation.");
    public static readonly Error ExportArtifactNotFound = new(
        "DataRights.ExportArtifactNotFound",
        "The protected export artifact was not found.");
    public static readonly Error ExportArtifactAlreadyRequested = new(
        "DataRights.ExportArtifactAlreadyRequested",
        "The protected export was already requested with different coordinates.");
    public static readonly Error ExportArtifactVersionConflict = new(
        "DataRights.ExportArtifactVersionConflict",
        "The protected export artifact changed before the retry was requested.");
    public static readonly Error ExportOwnerUnavailable = new(
        "DataRights.ExportOwnerUnavailable",
        "A selected data owner is unavailable for protected export.");
    public static readonly Error ExportOwnerCatalogInvalid = new(
        "DataRights.ExportOwnerCatalogInvalid",
        "The protected-export owner catalogue is invalid.");
    public static readonly Error ExportGenerationConflict = new(
        "DataRights.ExportGenerationConflict",
        "The protected export generation no longer matches the approved case.");
    public static readonly Error ExportArtifactNotAvailable = new(
        "DataRights.ExportArtifactNotAvailable",
        "The protected export artifact is not available for download.");
    public static readonly Error ExportArtifactExpired = new(
        "DataRights.ExportArtifactExpired",
        "The protected export artifact has expired.");
    public static readonly Error ExportArtifactVerificationFailed = new(
        "DataRights.ExportArtifactVerificationFailed",
        "The protected export artifact could not be verified.");
    public static readonly Error TenantTerminationContributorCatalogInvalid = new(
        "DataRights.TenantTerminationContributorCatalogInvalid",
        "The tenant-termination owner catalogue is incomplete or invalid.");
    public static readonly Error TenantTerminationCaseNotFound = new(
        "DataRights.TenantTerminationCaseNotFound",
        "The tenant-termination case was not found.");
    public static readonly Error TenantTerminationActiveCaseExists = new(
        "DataRights.TenantTerminationActiveCaseExists",
        "An active tenant-termination case already exists.");
    public static readonly Error TenantTerminationRequestConflict = new(
        "DataRights.TenantTerminationRequestConflict",
        "The tenant-termination request id is already bound to another request.");
    public static readonly Error TenantTerminationDecisionConflict = new(
        "DataRights.TenantTerminationDecisionConflict",
        "The tenant-termination decision conflicts with the durable case state.");
    public static readonly Error TenantTerminationApprovalEvidenceInvalid = new(
        "DataRights.TenantTerminationApprovalEvidenceInvalid",
        "The tenant-termination approval evidence is invalid or no longer matches the production owner catalogue.");
    public static readonly Error TenantTerminationStartConflict = new(
        "DataRights.TenantTerminationStartConflict",
        "The tenant-termination process coordinates conflict with durable state.");
    public static readonly Error TenantTerminationReplayIntentInvalid = new(
        "DataRights.TenantTerminationReplayIntentInvalid",
        "The protected tenant-termination start intent is missing, unavailable, or inconsistent.");
    public static readonly Error TenantTerminationRecoveryRequiresRetry = new(
        "DataRights.TenantTerminationRecoveryRequiresRetry",
        "The tenant-termination process is blocked or failed and must be retried explicitly.");
    public static readonly Error TenantTerminationProcessNotFound = new(
        "DataRights.TenantTerminationProcessNotFound",
        "The tenant-termination process was not found.");
    public static readonly Error TenantTerminationExecutionStateInvalid = new(
        "DataRights.TenantTerminationExecutionStateInvalid",
        "The tenant-termination phase or durable owner work set is incomplete or inconsistent.");
    public static readonly Error TenantTerminationCancellationProofInvalid = new(
        "DataRights.TenantTerminationCancellationProofInvalid",
        "Tenant termination cannot be cancelled until every restore owner has durable completion proof.");
    public static readonly Error TenantTerminationExportProofInvalid = new(
        "DataRights.TenantTerminationExportProofInvalid",
        "The tenant-termination export does not contain the exact durable owner proof set.");
    public static readonly Error TenantTerminationExportArtifactNotFound = new(
        "DataRights.TenantTerminationExportArtifactNotFound",
        "The tenant-termination export artifact was not found.");
    public static readonly Error TenantTerminationExportArtifactNotAvailable = new(
        "DataRights.TenantTerminationExportArtifactNotAvailable",
        "The tenant-termination export artifact is not available.");
    public static readonly Error TenantTerminationExportArtifactExpired = new(
        "DataRights.TenantTerminationExportArtifactExpired",
        "The tenant-termination export artifact has expired.");
    public static readonly Error TenantTerminationExportVerificationFailed = new(
        "DataRights.TenantTerminationExportVerificationFailed",
        "The tenant-termination export artifact could not be verified.");
    public static readonly Error TenantTerminationExportConfirmationInvalid = new(
        "DataRights.TenantTerminationExportConfirmationInvalid",
        "The tenant-termination export confirmation is stale or invalid.");
    public static readonly Error TenantTerminationVerificationProofInvalid = new(
        "DataRights.TenantTerminationVerificationProofInvalid",
        "Tenant termination cannot complete until every destruction owner and protected replay proof is verified.");

    public static Error VersionConflict => DataRightsDomainErrors.VersionConflict;
    public static Error TransitionInvalid => DataRightsDomainErrors.TransitionInvalid;
    public static Error VerificationRequired => DataRightsDomainErrors.VerificationRequired;
    public static Error ControllerRoutingRequired => DataRightsDomainErrors.ControllerRoutingRequired;
    public static Error ResponseDeadlinePolicyRequired =>
        DataRightsDomainErrors.ResponseDeadlinePolicyRequired;
    public static Error SubjectCoordinateInvalid => DataRightsDomainErrors.SubjectCoordinateInvalid;
    public static Error SubjectAlreadySelected => DataRightsDomainErrors.SubjectAlreadySelected;
    public static Error SubjectNotSelected => DataRightsDomainErrors.SubjectNotSelected;
    public static Error SubjectSelectionLimitReached => DataRightsDomainErrors.SubjectSelectionLimitReached;
    public static Error SubjectSelectionRequired => DataRightsDomainErrors.SubjectSelectionRequired;
    public static Error DecisionInvalid => DataRightsDomainErrors.DecisionInvalid;
    public static Error AnonymisationApprovalInvalid =>
        DataRightsDomainErrors.AnonymisationApprovalInvalid;
    public static Error AnonymisationSubjectCountInvalid =>
        DataRightsDomainErrors.AnonymisationSubjectCountInvalid;
    public static Error DecisionActorCannotExecute =>
        DataRightsDomainErrors.DecisionActorCannotExecute;
    public static Error ExecutionCoordinateInvalid =>
        DataRightsDomainErrors.ExecutionCoordinateInvalid;
    public static Error RestoreCheckpointInvalid =>
        DataRightsDomainErrors.RestoreCheckpointInvalid;
    public static Error RestoreCheckpointConflict =>
        DataRightsDomainErrors.RestoreCheckpointConflict;
    public static Error ExportArtifactTransitionInvalid =>
        DataRightsDomainErrors.ExportArtifactTransitionInvalid;
}
