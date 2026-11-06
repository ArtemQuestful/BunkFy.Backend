namespace BunkFy.Modules.Staff.Api;

using Gma.Framework.AccessControl;
using Gma.Framework.AccessControl.AspNetCore;
using Gma.Framework.Api.Results;
using Microsoft.AspNetCore.Http;
using BunkFy.Modules.Staff.Application;

internal static class StaffApiEndpointSupport
{
    public static readonly ApiErrorStatusCodeMap ErrorStatusCodes =
        CreateErrorStatusCodes(
        new(StaffApplicationErrors.StaffMemberNotFound.Code, StatusCodes.Status404NotFound),
        new(StaffApplicationErrors.AssignmentNotFound.Code, StatusCodes.Status404NotFound),
        new(StaffApplicationErrors.EmploymentGovernanceNotConfigured.Code, StatusCodes.Status404NotFound),
        new(StaffApplicationErrors.DataHoldNotFound.Code, StatusCodes.Status404NotFound),
        new(StaffApplicationErrors.PropertyUnavailable.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.EmployeeNumberConflict.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.AuthSubjectConflict.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.AuthSubjectUnlinkRequiresSuspension.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.AuthSubjectReplacementRequiresUnlink.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.AuthSubjectLinkRequiresActive.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.CreationOperationConflict.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.ProfileUpdateOperationConflict.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.AuthSubjectOperationConflict.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.IdentityAnchorResolutionRequired.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.IdentityAnchorAccessClosureRequired.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.LifecycleOperationConflict.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.AssignmentOperationConflict.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.AssignmentAlreadyExists.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.PrimaryAssignmentExists.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.VersionConflict.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.StaffSuspended.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.StaffDeparted.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.DataRightsApprovalRequired.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.CorrectionIdempotencyConflict.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.CorrectionNoChanges.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.EmploymentGovernanceStaffVersionConflict.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.EmploymentGovernanceVersionConflict.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.EmploymentGovernanceIdempotencyConflict.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.DataHoldStaffVersionConflict.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.DataHoldStaffNotEligible.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.DataHoldLimitReached.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.DataHoldIdempotencyConflict.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.DataHoldVersionConflict.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.DataHoldAlreadyReleased.Code, StatusCodes.Status409Conflict),
        new(StaffApplicationErrors.CreationOperationInvalid.Code, StatusCodes.Status400BadRequest),
        new(StaffApplicationErrors.ProfileUpdateOperationInvalid.Code, StatusCodes.Status400BadRequest),
        new(StaffApplicationErrors.AuthSubjectOperationInvalid.Code, StatusCodes.Status400BadRequest),
        new(StaffApplicationErrors.LifecycleOperationInvalid.Code, StatusCodes.Status400BadRequest),
        new(StaffApplicationErrors.AssignmentOperationInvalid.Code, StatusCodes.Status400BadRequest),
        new(StaffApplicationErrors.CorrectionRequestInvalid.Code, StatusCodes.Status400BadRequest),
        new(StaffApplicationErrors.EmploymentGovernanceRequestInvalid.Code, StatusCodes.Status400BadRequest),
        new(StaffApplicationErrors.DataHoldRequestInvalid.Code, StatusCodes.Status400BadRequest),
        new(StaffApplicationErrors.ConfirmationRequired.Code, StatusCodes.Status400BadRequest));

    public static string ResolveActor(HttpContext context, IAccessHttpSubjectResolver resolver)
    {
        AccessSubject? subject = resolver.ResolveSubject(context);
        return subject is null
            ? "authenticated:unknown"
            : $"{AccessSubjectKindNames.GetName(subject.Kind)}:{subject.Id}";
    }

    public static void MarkSensitiveResponse(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.Expires = "0";
    }

    private static ApiErrorStatusCodeMap CreateErrorStatusCodes(
        params ApiErrorStatusCode[] entries) =>
        ApiErrorStatusCodeMap.Create(
            entries.Concat(
                StaffApplicationErrors
                    .EmploymentGovernancePolicyDenials
                    .Select(error => new ApiErrorStatusCode(
                        error.Code,
                        StatusCodes.Status409Conflict)))
                .ToArray());
}
