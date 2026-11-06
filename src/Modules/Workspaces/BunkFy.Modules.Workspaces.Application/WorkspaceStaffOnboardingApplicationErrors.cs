namespace BunkFy.Modules.Workspaces.Application;

using Gma.Framework.Results;

public static class WorkspaceStaffOnboardingApplicationErrors
{
    public static readonly Error ScopeRequired = new("Workspaces.ScopeRequired", "A workspace scope is required.");
    public static readonly Error VerifiedIdentityRequired = new(
        "Workspaces.VerifiedIdentityRequired",
        "An active account with a verified email is required.");
    public static readonly Error JoinTokenInvalid = new("Workspaces.JoinTokenInvalid", "The workspace join token is invalid or unavailable.");
    public static readonly Error ApplicationNotFound = new("Workspaces.StaffOnboardingNotFound", "The Staff onboarding application was not found.");
    public static readonly Error ProvisioningFailed = new("Workspaces.StaffOnboardingProvisioningFailed", "Staff onboarding provisioning did not complete.");
    public static readonly Error RecoveryRequiresReview = new(
        "Workspaces.StaffOnboardingRecoveryRequiresReview",
        "Staff onboarding no longer matches its recorded access plan and requires review before retry.");
    public static readonly Error RecoveryAttemptRequired = new(
        "Workspaces.StaffOnboardingRecoveryAttemptRequired",
        "This partial Staff onboarding requires a recorded recovery attempt.");
    public static readonly Error RecoveryRequestInvalid = new(
        "Workspaces.StaffOnboardingRecoveryRequestInvalid",
        "The Staff onboarding recovery request is invalid.");
    public static readonly Error RecoveryApplicationVersionConflict = new(
        "Workspaces.StaffOnboardingRecoveryApplicationVersionConflict",
        "The Staff onboarding application changed before recovery began.");
    public static readonly Error RecoveryIdempotencyConflict = new(
        "Workspaces.StaffOnboardingRecoveryIdempotencyConflict",
        "The recovery operation identifier was already used for another request.");
    public static readonly Error RecoveryIneligible = new(
        "Workspaces.StaffOnboardingRecoveryIneligible",
        "Only a failed partial onboarding with a recorded Staff member can be recovered.");
    public static readonly Error RecoveryVerifierUnavailable = new(
        "Workspaces.StaffOnboardingRecoveryVerifierUnavailable",
        "The authoritative Staff recovery verifier is unavailable.");
    public static readonly Error RecoveryAttemptNotFound = new(
        "Workspaces.StaffOnboardingRecoveryAttemptNotFound",
        "No recovery attempt has been recorded for this application.");
    public static readonly Error MembershipStateConflict = new(
        "Workspaces.StaffOnboardingMembershipStateConflict",
        "The accepted organization membership no longer matches this Staff onboarding.");
    public static readonly Error MembershipStateUnavailable = new(
        "Workspaces.StaffOnboardingMembershipStateUnavailable",
        "The organization membership could not be verified consistently. Retry later.");
    public static readonly Error AccessPlanUnavailable = new(
        "Workspaces.StaffAccessPlanUnavailable",
        "The workspace Staff access plan is unavailable.");
    public static readonly Error ProfileMutationAuthorityUnavailable = new(
        "Workspaces.StaffOnboardingProfileMutationAuthorityUnavailable",
        "The Staff onboarding profile is no longer editable under the authoritative join state.");
    public static readonly Error ProfileReviewUnavailable = new(
        "Workspaces.StaffOnboardingProfileReviewUnavailable",
        "Applicant details are unavailable for review. Refresh the request status before continuing.");

    public static readonly Error RetentionCoordinateInvalid = new(
        "Workspaces.StaffOnboardingRetentionCoordinateInvalid",
        "The Staff onboarding retention coordinate is invalid.");
    public static readonly Error RetentionClaimInconsistent = new(
        "Workspaces.StaffOnboardingRetentionClaimInconsistent",
        "The authoritative enrollment claim is inconsistent with Staff onboarding.");
    public static readonly Error RetentionPlanInconsistent = new(
        "Workspaces.StaffOnboardingRetentionPlanInconsistent",
        "The Staff onboarding access plan is inconsistent with retention reconciliation.");
    public static readonly Error RetentionExecutionNotFound = new(
        "Workspaces.StaffOnboardingRetentionExecutionNotFound",
        "The Staff onboarding retention execution was not found.");
    public static readonly Error CorrectionRequestInvalid = new(
        "Workspaces.StaffOnboardingCorrectionRequestInvalid",
        "The Staff onboarding correction request is invalid.");
    public static readonly Error DataRightsApprovalRequired = new(
        "Workspaces.StaffOnboardingCorrectionApprovalRequired",
        "An active approved Data Rights correction execution is required.");
    public static readonly Error CorrectionTargetUnavailable = new(
        "Workspaces.StaffOnboardingCorrectionTargetUnavailable",
        "The selected Staff onboarding record is no longer editable.");
    public static readonly Error CorrectionIdempotencyConflict = new(
        "Workspaces.StaffOnboardingCorrectionIdempotencyConflict",
        "The Data Rights correction execution was already used for another request.");
    public static readonly Error RestrictionRequestInvalid = new(
        "Workspaces.StaffOnboardingRestrictionRequestInvalid",
        "The Staff onboarding restriction request is invalid.");
    public static readonly Error RestrictionApprovalRequired = new(
        "Workspaces.StaffOnboardingRestrictionApprovalRequired",
        "An active approved Data Rights restriction execution is required.");
    public static readonly Error RestrictionAuthorityUnavailable = new(
        "Workspaces.StaffOnboardingRestrictionAuthorityUnavailable",
        "Workspaces no longer owns the selected applicant record.");
    public static readonly Error RestrictionOnboardingVersionConflict = new(
        "Workspaces.StaffOnboardingRestrictionOnboardingVersionConflict",
        "The selected Staff onboarding version is no longer current.");
    public static readonly Error RestrictionApprovalAlreadyUsed = new(
        "Workspaces.StaffOnboardingRestrictionApprovalAlreadyUsed",
        "The approved restriction transition was already used.");
    public static readonly Error RestrictionProjectionUnavailable = new(
        "Workspaces.StaffOnboardingRestrictionProjectionUnavailable",
        "The Staff onboarding restriction projection is unavailable.");
    public static readonly Error RestrictionNotFound = new(
        "Workspaces.StaffOnboardingRestrictionNotFound",
        "The selected Staff onboarding restriction was not found.");
    public static readonly Error RestrictionIdempotencyConflict = new(
        "Workspaces.StaffOnboardingRestrictionIdempotencyConflict",
        "The restriction idempotency key was already used for another request.");
    public static readonly Error RestrictionActiveStateInvalid = new(
        "Workspaces.StaffOnboardingRestrictionActiveStateInvalid",
        "The active Staff onboarding restriction state requires explicit resolution.");
    public static readonly Error RestrictionOwnerProofInvalid = new(
        "Workspaces.StaffOnboardingRestrictionOwnerProofInvalid",
        "The Staff onboarding restriction proof is invalid.");
    public static readonly Error ProcessingRestricted = new(
        "Workspaces.StaffOnboardingProcessingRestricted",
        "The selected Staff onboarding record is restricted from ordinary processing.");
    public static readonly Error IdentityAnchorConflict = new(
        "Workspaces.StaffOnboardingIdentityAnchorConflict",
        "The Staff identity-anchor outcome conflicts with the onboarding record.");
    public static readonly Error IdentityAnchorSuspended = new(
        "Workspaces.StaffOnboardingIdentityAnchorSuspended",
        "The anchored Staff member is suspended and cannot receive workspace access.");
    public static readonly Error IdentityAnchorLifecycleTransitionPending = new(
        "Workspaces.StaffOnboardingIdentityAnchorLifecycleTransitionPending",
        "The anchored Staff member has an unfinished workspace access lifecycle transition.");
}
