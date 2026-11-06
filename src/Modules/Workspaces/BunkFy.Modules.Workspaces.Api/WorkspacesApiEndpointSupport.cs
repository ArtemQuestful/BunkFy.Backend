namespace BunkFy.Modules.Workspaces.Api;

using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.AccessControl;
using Gma.Framework.AccessControl.AspNetCore;
using Gma.Framework.Api.Results;
using Gma.Modules.AccessControl.Contracts;
using Microsoft.AspNetCore.Http;

internal static class WorkspacesApiEndpointSupport
{
    public static readonly ApiErrorStatusCodeMap ErrorStatusCodes = ApiErrorStatusCodeMap.Create(
        new(AccessProfileManagementErrors.AccessDenied.Code, StatusCodes.Status403Forbidden),
        new(ScopedAccessProfileManagementErrors.AccessDenied.Code, StatusCodes.Status403Forbidden),
        new(ScopedAccessProfileManagementErrors.AssignmentRejected.Code, StatusCodes.Status403Forbidden),
        new(ScopedAccessProfileManagementErrors.PermissionEscalation.Code, StatusCodes.Status403Forbidden),
        new(ScopedAccessProfileManagementErrors.ScopeInvalid.Code, StatusCodes.Status400BadRequest),
        new(ScopedAccessProfileManagementErrors.ProfileUnavailable.Code, StatusCodes.Status409Conflict),
        new(WorkspaceAccessManagementErrors.ScopeRequired.Code, StatusCodes.Status400BadRequest),
        new(WorkspaceAccessManagementErrors.PermissionsInvalid.Code, StatusCodes.Status422UnprocessableEntity),
        new(WorkspaceAccessManagementErrors.PermissionDependencyMissing.Code, StatusCodes.Status422UnprocessableEntity),
        new(WorkspaceAccessManagementErrors.SeedProfileProtected.Code, StatusCodes.Status409Conflict),
        new(WorkspaceAccessManagementErrors.ProfileAssigned.Code, StatusCodes.Status409Conflict),
        new(WorkspaceAccessManagementErrors.RequestInvalid.Code, StatusCodes.Status400BadRequest),
        new(WorkspaceAccessManagementErrors.RequestConflict.Code, StatusCodes.Status409Conflict),
        new(WorkspaceAccessManagementErrors.MemberInvalid.Code, StatusCodes.Status400BadRequest),
        new(WorkspaceAccessManagementErrors.MemberUnavailable.Code, StatusCodes.Status404NotFound),
        new(WorkspaceAccessManagementErrors.OwnerProtected.Code, StatusCodes.Status409Conflict),
        new(WorkspaceAccessManagementErrors.ProfileUnavailable.Code, StatusCodes.Status409Conflict),
        new(WorkspaceAccessManagementErrors.PropertiesInvalid.Code, StatusCodes.Status400BadRequest),
        new(WorkspaceAccessManagementErrors.PropertyUnavailable.Code, StatusCodes.Status409Conflict),
        new(WorkspaceAccessManagementErrors.JoinSourceRequestInvalid.Code, StatusCodes.Status400BadRequest),
        new(WorkspaceAccessManagementErrors.JoinSourceManagementFailed.Code, StatusCodes.Status409Conflict),
        new(WorkspaceAccessManagementErrors.JoinSourcePlanUnavailable.Code, StatusCodes.Status404NotFound),
        new(WorkspaceAccessManagementErrors.JoinSourceReplacementUnavailable.Code, StatusCodes.Status409Conflict),
        new(WorkspaceOperationalAdmissionErrors.ProcessingRestricted.Code, StatusCodes.Status423Locked),
        new(WorkspaceOperationalAdmissionErrors.AdmissionUnavailable.Code, StatusCodes.Status503ServiceUnavailable),
        new(WorkspaceStaffOnboardingApplicationErrors.ScopeRequired.Code, StatusCodes.Status400BadRequest),
        new(WorkspaceStaffOnboardingApplicationErrors.VerifiedIdentityRequired.Code, StatusCodes.Status403Forbidden),
        new(WorkspaceStaffOnboardingApplicationErrors.JoinTokenInvalid.Code, StatusCodes.Status404NotFound),
        new(WorkspaceStaffOnboardingApplicationErrors.ApplicationNotFound.Code, StatusCodes.Status404NotFound),
        new(WorkspaceStaffOnboardingApplicationErrors.ProvisioningFailed.Code, StatusCodes.Status409Conflict),
        new(WorkspaceStaffOnboardingApplicationErrors.RecoveryRequiresReview.Code, StatusCodes.Status409Conflict),
        new(WorkspaceStaffOnboardingApplicationErrors.RecoveryAttemptRequired.Code, StatusCodes.Status409Conflict),
        new(WorkspaceStaffOnboardingApplicationErrors.RecoveryRequestInvalid.Code, StatusCodes.Status400BadRequest),
        new(WorkspaceStaffOnboardingApplicationErrors.RecoveryApplicationVersionConflict.Code, StatusCodes.Status409Conflict),
        new(WorkspaceStaffOnboardingApplicationErrors.RecoveryIdempotencyConflict.Code, StatusCodes.Status409Conflict),
        new(WorkspaceStaffOnboardingApplicationErrors.RecoveryIneligible.Code, StatusCodes.Status409Conflict),
        new(WorkspaceStaffOnboardingApplicationErrors.RecoveryVerifierUnavailable.Code, StatusCodes.Status503ServiceUnavailable),
        new(WorkspaceStaffOnboardingApplicationErrors.RecoveryAttemptNotFound.Code, StatusCodes.Status404NotFound),
        new(WorkspaceStaffOnboardingApplicationErrors.MembershipStateConflict.Code, StatusCodes.Status409Conflict),
        new(WorkspaceStaffOnboardingApplicationErrors.MembershipStateUnavailable.Code, StatusCodes.Status503ServiceUnavailable),
        new(WorkspaceStaffOnboardingApplicationErrors.AccessPlanUnavailable.Code, StatusCodes.Status409Conflict),
        new(WorkspaceStaffOnboardingApplicationErrors.ProfileMutationAuthorityUnavailable.Code, StatusCodes.Status409Conflict),
        new(WorkspaceStaffOnboardingApplicationErrors.ProfileReviewUnavailable.Code, StatusCodes.Status409Conflict),
        new(WorkspaceStaffOnboardingApplicationErrors.CorrectionRequestInvalid.Code, StatusCodes.Status400BadRequest),
        new(WorkspaceStaffOnboardingApplicationErrors.DataRightsApprovalRequired.Code, StatusCodes.Status403Forbidden),
        new(WorkspaceStaffOnboardingApplicationErrors.CorrectionTargetUnavailable.Code, StatusCodes.Status409Conflict),
        new(WorkspaceStaffOnboardingApplicationErrors.CorrectionIdempotencyConflict.Code, StatusCodes.Status409Conflict),
        new(WorkspaceStaffAccessPlanApplicationErrors.PlanNotFound.Code, StatusCodes.Status404NotFound),
        new(WorkspaceStaffAccessPlanApplicationErrors.ProfileUnavailable.Code, StatusCodes.Status409Conflict),
        new(WorkspaceStaffAccessPlanApplicationErrors.ProfileNotDelegable.Code, StatusCodes.Status403Forbidden),
        new(WorkspaceStaffAccessPlanApplicationErrors.PropertyUnavailable.Code, StatusCodes.Status409Conflict),
        new(WorkspaceStaffAccessPlanApplicationErrors.DelegationDenied.Code, StatusCodes.Status403Forbidden),
        new(WorkspaceStaffAccessPlanApplicationErrors.JoinSourceIssuanceFailed.Code, StatusCodes.Status409Conflict),
        new(WorkspaceStaffAccessPlanErrors.Invalid.Code, StatusCodes.Status400BadRequest),
        new(WorkspaceStaffAccessPlanErrors.Conflict.Code, StatusCodes.Status409Conflict),
        new(WorkspaceStaffAccessPlanErrors.StateConflict.Code, StatusCodes.Status409Conflict),
        new(WorkspaceStaffOnboardingErrors.Invalid.Code, StatusCodes.Status400BadRequest),
        new(WorkspaceStaffOnboardingErrors.Unavailable.Code, StatusCodes.Status409Conflict),
        new(WorkspaceStaffOnboardingErrors.ClaimConflict.Code, StatusCodes.Status409Conflict),
        new(WorkspaceStaffOnboardingErrors.StateConflict.Code, StatusCodes.Status409Conflict),
        new(WorkspaceStaffOnboardingErrors.CorrectionVersionConflict.Code, StatusCodes.Status409Conflict),
        new(WorkspaceStaffOnboardingErrors.CorrectionUnavailable.Code, StatusCodes.Status409Conflict),
        new(WorkspaceStaffOnboardingErrors.CorrectionNoChanges.Code, StatusCodes.Status409Conflict),
        new(WorkspaceStaffOnboardingErrors.CorrectionReceiptIdentityInvalid.Code, StatusCodes.Status400BadRequest),
        new(WorkspaceStaffOnboardingErrors.CorrectionReceiptVersionInvalid.Code, StatusCodes.Status400BadRequest),
        new(WorkspaceStaffOnboardingErrors.CorrectionReceiptFieldsInvalid.Code, StatusCodes.Status400BadRequest));

    public static AccessSubject? ResolveUser(
        HttpContext context,
        IAccessHttpSubjectResolver resolver)
    {
        AccessSubject? subject = resolver.ResolveSubject(context);
        return subject?.Kind == AccessSubjectKind.User ? subject : null;
    }

    public static void SetNoStore(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.Expires = "0";
    }
}
