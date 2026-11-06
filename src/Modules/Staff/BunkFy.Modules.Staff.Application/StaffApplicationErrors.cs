namespace BunkFy.Modules.Staff.Application;

using BunkFy.DataGovernance;
using Gma.Framework.Results;
using BunkFy.Modules.Staff.Domain.Errors;

public static class StaffApplicationErrors
{
    public static readonly Error WorkspaceProcessingRestricted = new(
        "Staff.WorkspaceProcessingRestricted",
        "The workspace is not accepting Staff changes.");
    public static readonly Error WorkspaceProcessingAdmissionUnavailable = new(
        "Staff.WorkspaceProcessingAdmissionUnavailable",
        "Workspace processing admission is unavailable.");
    public static readonly Error ConfirmationRequired = new(
        "Staff.ConfirmationRequired",
        "Confirmation is required.");
    public static readonly Error StaffMemberNotFound = new("Staff.StaffMemberNotFound", "The staff member was not found.");
    public static readonly Error TenantRequired = new("Staff.TenantRequired", "A tenant context is required.");
    public static readonly Error CreationOperationInvalid = new(
        "Staff.CreationOperationInvalid",
        "A valid Staff creation operation id is required.");
    public static readonly Error CreationOperationConflict = new(
        "Staff.CreationOperationConflict",
        "The Staff creation operation was already used for different profile data.");
    public static readonly Error IdentityProvisioningSourceInvalid = new(
        "Staff.IdentityProvisioningSourceInvalid",
        "A valid stable Staff identity provisioning source id is required.");
    public static readonly Error IdentityProvisioningAnchorConflict = new(
        "Staff.IdentityProvisioningAnchorConflict",
        "The Staff identity provisioning source is anchored to a different identity.");
    public static readonly Error IdentityProvisioningAnchorCorrupt = new(
        "Staff.IdentityProvisioningAnchorCorrupt",
        "The Staff identity provisioning anchor target is unavailable.");
    public static readonly Error IdentityAnchorCutoverRequestInvalid = new(
        "Staff.IdentityAnchorCutoverRequestInvalid",
        "The Staff identity-anchor cutover request is invalid.");
    public static readonly Error IdentityAnchorCutoverBlocked = new(
        "Staff.IdentityAnchorCutoverBlocked",
        "The Staff identity-anchor cutover contains ambiguous or conflicting evidence.");
    public static readonly Error IdentityAnchorLifecycleRequestInvalid = new(
        "Staff.IdentityAnchorLifecycleRequestInvalid",
        "The Staff identity-anchor lifecycle request is invalid.");
    public static readonly Error IdentityAnchorResolutionRequired = new(
        "Staff.IdentityAnchorResolutionRequired",
        "Workspace onboarding must finish redaction before this Staff identity lifecycle change can continue.");
    public static readonly Error IdentityAnchorAccessClosureRequired = new(
        "Staff.IdentityAnchorAccessClosureRequired",
        "Workspace onboarding access must be closed before this Staff account link can change.");
    public static readonly Error OnboardingOperationInvalid = new(
        "Staff.OnboardingOperationInvalid",
        "A valid Staff onboarding operation id is required.");
    public static readonly Error OnboardingApplicantInvalid = new(
        "Staff.OnboardingApplicantInvalid",
        "A Staff onboarding Auth subject is required.");
    public static readonly Error OnboardingOperationConflict = new(
        "Staff.OnboardingOperationConflict",
        "The Staff onboarding operation was already used for different applicant data.");
    public static readonly Error OnboardingReplayUnavailable = new(
        "Staff.OnboardingReplayUnavailable",
        "The Staff onboarding operation can no longer be replayed safely.");
    public static readonly Error ProfileUpdateOperationInvalid = new(
        "Staff.ProfileUpdateOperationInvalid",
        "A valid Staff profile-update operation id is required.");
    public static readonly Error ProfileUpdateOperationConflict = new(
        "Staff.ProfileUpdateOperationConflict",
        "The Staff profile-update operation was already used for different data.");
    public static readonly Error AuthSubjectOperationInvalid = new(
        "Staff.AuthSubjectOperationInvalid",
        "A valid Staff account-link operation id is required.");
    public static readonly Error AuthSubjectOperationConflict = new(
        "Staff.AuthSubjectOperationConflict",
        "The Staff account-link operation was already used for different data.");
    public static readonly Error LifecycleOperationInvalid = new(
        "Staff.LifecycleOperationInvalid",
        "A valid Staff lifecycle operation id is required.");
    public static readonly Error LifecycleOperationConflict = new(
        "Staff.LifecycleOperationConflict",
        "The Staff lifecycle operation was already used for a different change.");
    public static readonly Error AssignmentOperationInvalid = new(
        "Staff.AssignmentOperationInvalid",
        "A valid Staff property-assignment operation id is required.");
    public static readonly Error AssignmentOperationConflict = new(
        "Staff.AssignmentOperationConflict",
        "The Staff property-assignment operation was already used for a different change.");
    public static readonly Error PropertyUnavailable = new("Staff.PropertyUnavailable", "The property is unavailable for staff assignment.");
    public static readonly Error EmployeeNumberConflict = new("Staff.EmployeeNumberConflict", "The employee number is already in use.");
    public static readonly Error AuthSubjectConflict = new("Staff.AuthSubjectConflict", "The Auth subject is already linked to another staff member.");
    public static readonly Error LifecycleTransitionDenied = new("Staff.LifecycleTransitionDenied", "The staff lifecycle change is not allowed by the workspace policy.");
    public static readonly Error LifecycleCoordinationPending = new("Staff.LifecycleCoordinationPending", "Workspace access could not be coordinated yet. Retry the staff lifecycle change.");
    public static readonly Error WorkspaceOwnerProtected = new("Staff.WorkspaceOwnerProtected", "Transfer workspace ownership before changing this staff member's lifecycle.");
    public static readonly Error DataRightsApprovalRequired = new(
        "Staff.DataRightsApprovalRequired",
        "An active approved data-rights correction execution is required.");
    public static readonly Error CorrectionIdempotencyConflict = new(
        "Staff.CorrectionIdempotencyConflict",
        "The correction execution was already used for different coordinates.");
    public static readonly Error CorrectionRequestInvalid = new(
        "Staff.CorrectionRequestInvalid",
        "The correction request is invalid.");
    public static readonly Error RestrictionRequestInvalid = new(
        "Staff.RestrictionRequestInvalid",
        "The processing-restriction request is invalid.");
    public static readonly Error RestrictionNotFound = new(
        "Staff.RestrictionNotFound",
        "The processing restriction was not found.");
    public static readonly Error RestrictionProjectionUnavailable = new(
        "Staff.RestrictionProjectionUnavailable",
        "The processing-restriction state is unavailable or unsupported.");
    public static readonly Error RestrictionActiveStateInvalid = new(
        "Staff.RestrictionActiveStateInvalid",
        "The active processing-restriction state is inconsistent.");
    public static readonly Error RestrictionOwnerProofInvalid = new(
        "Staff.RestrictionOwnerProofInvalid",
        "The committed processing-restriction proof is unavailable or inconsistent.");
    public static readonly Error RestrictionStaffVersionConflict = new(
        "Staff.RestrictionStaffVersionConflict",
        "The selected staff-member version is stale.");
    public static readonly Error RestrictionIdempotencyConflict = new(
        "Staff.RestrictionIdempotencyConflict",
        "The processing-restriction idempotency key was already used for a different request.");
    public static readonly Error RestrictionApprovalAlreadyUsed = new(
        "Staff.RestrictionApprovalAlreadyUsed",
        "The approved processing-restriction decision was already used.");
    public static readonly Error EmploymentGovernanceRequestInvalid = new(
        "Staff.EmploymentGovernanceRequestInvalid",
        "The employment-governance request is invalid.");
    public static readonly Error EmploymentGovernanceNotConfigured = new(
        "Staff.EmploymentGovernanceNotConfigured",
        "Employment governance is not configured for this staff member.");
    public static readonly Error EmploymentGovernanceStaffVersionConflict = new(
        "Staff.EmploymentGovernanceStaffVersionConflict",
        "The selected staff-member version is stale.");
    public static readonly Error EmploymentGovernanceIdempotencyConflict = new(
        "Staff.EmploymentGovernanceIdempotencyConflict",
        "The employment-governance idempotency key was already used for a different request.");
    public static Error EmploymentGovernancePolicyDenied(
        CountryPolicyDecisionReason reason) =>
        new(
            $"Staff.EmploymentGovernancePolicy.{reason}",
            "The selected country policy does not permit employment governance.");
    public static IReadOnlyList<Error> EmploymentGovernancePolicyDenials { get; } =
        Enum.GetValues<CountryPolicyDecisionReason>()
            .Where(reason =>
                reason is not CountryPolicyDecisionReason.Unknown and
                    not CountryPolicyDecisionReason.Allowed)
            .Select(EmploymentGovernancePolicyDenied)
            .ToArray();
    public static readonly Error DataHoldRequestInvalid = new(
        "Staff.DataHoldRequestInvalid",
        "The Staff data-hold request is invalid.");
    public static readonly Error DataHoldNotFound = new(
        "Staff.DataHoldNotFound",
        "The Staff data hold was not found.");
    public static readonly Error DataHoldStaffVersionConflict = new(
        "Staff.DataHoldStaffVersionConflict",
        "The selected staff-member version is stale.");
    public static readonly Error DataHoldStaffNotEligible = new(
        "Staff.DataHoldStaffNotEligible",
        "The staff member is not eligible for a data hold.");
    public static readonly Error DataHoldLimitReached = new(
        "Staff.DataHoldLimitReached",
        "The Staff data-hold limit has been reached.");
    public static readonly Error DataHoldIdempotencyConflict = new(
        "Staff.DataHoldIdempotencyConflict",
        "The Staff data-hold idempotency key was already used for a different request.");
    public static readonly Error AnonymisationRequestInvalid = new(
        "Staff.AnonymisationRequestInvalid",
        "The Staff anonymisation request is invalid.");
    public static readonly Error AnonymisationApprovalRequired = new(
        "Staff.AnonymisationApprovalRequired",
        "An active approved Staff anonymisation execution is required.");
    public static readonly Error AnonymisationIdempotencyConflict = new(
        "Staff.AnonymisationIdempotencyConflict",
        "The Staff anonymisation idempotency key was already used for different coordinates.");
    public static readonly Error AnonymisationProofUnavailable = new(
        "Staff.AnonymisationProofUnavailable",
        "The committed Staff anonymisation proof is unavailable or inconsistent.");
    public static readonly Error AnonymisationRestoreRequestInvalid = new(
        "Staff.AnonymisationRestoreRequestInvalid",
        "The Staff anonymisation restore request is invalid.");
    public static readonly Error AnonymisationRestoreProofConflict = new(
        "Staff.AnonymisationRestoreProofConflict",
        "The Staff anonymisation restore proof conflicts with current state.");
    public static readonly Error AnonymisationOperationLockUnavailable = new(
        "Staff.AnonymisationOperationLockUnavailable",
        "The Staff anonymisation operation lock is unavailable.");
    public static readonly Error AnonymisationStateChanged = new(
        "Staff.AnonymisationBlocked.FrozenStateChanged",
        "The Staff anonymisation state changed after approval.");
    public static readonly Error AnonymisationNotEligible = new(
        "Staff.AnonymisationBlocked.NotEligible",
        "The Staff member is not eligible for anonymisation.");
    public static readonly Error RetentionMutationInvalid = new(
        "Staff.RetentionMutationInvalid",
        "The Staff retention mutation request is invalid.");
    public static readonly Error RetentionExecutionNotFound = new(
        "Staff.RetentionExecutionNotFound",
        "The Staff retention execution was not found.");
    public static readonly Error RetentionProofConflict = new(
        "Staff.RetentionProofConflict",
        "The committed Staff retention proof is unavailable or inconsistent.");
    public static Error VersionConflict => StaffDomainErrors.VersionConflict;
    public static Error StaffSuspended => StaffDomainErrors.StaffSuspended;
    public static Error StaffDeparted => StaffDomainErrors.StaffDeparted;
    public static Error AuthSubjectUnlinkRequiresSuspension =>
        StaffDomainErrors.AuthSubjectUnlinkRequiresSuspension;
    public static Error AuthSubjectReplacementRequiresUnlink =>
        StaffDomainErrors.AuthSubjectReplacementRequiresUnlink;
    public static Error AuthSubjectLinkRequiresActive =>
        StaffDomainErrors.AuthSubjectLinkRequiresActive;
    public static Error AssignmentNotFound => StaffDomainErrors.AssignmentNotFound;
    public static Error AssignmentAlreadyExists => StaffDomainErrors.AssignmentAlreadyExists;
    public static Error PrimaryAssignmentExists => StaffDomainErrors.PrimaryAssignmentExists;
    public static Error CorrectionNoChanges => StaffDomainErrors.CorrectionNoChanges;
    public static Error RestrictionVersionConflict =>
        StaffDomainErrors.RestrictionVersionConflict;
    public static Error RestrictionAlreadyReleased =>
        StaffDomainErrors.RestrictionAlreadyReleased;
    public static Error RestrictionProjectionVersionConflict =>
        StaffDomainErrors.RestrictionProjectionVersionConflict;
    public static Error RestrictionProjectionStateInvalid =>
        StaffDomainErrors.RestrictionProjectionStateInvalid;
    public static Error RestrictionProjectionTransitionInvalid =>
        StaffDomainErrors.RestrictionProjectionTransitionInvalid;
    public static Error EmploymentGovernanceVersionConflict =>
        StaffDomainErrors.EmploymentGovernanceVersionConflict;
    public static Error DataHoldVersionConflict =>
        StaffDomainErrors.DataHoldVersionConflict;
    public static Error DataHoldAlreadyReleased =>
        StaffDomainErrors.DataHoldAlreadyReleased;
}
