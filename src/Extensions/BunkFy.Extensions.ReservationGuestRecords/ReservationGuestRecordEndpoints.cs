namespace BunkFy.Extensions.ReservationGuestRecords;

using BunkFy.DataGovernance;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Reservations.Contracts;
using Gma.Framework.AccessControl;
using Gma.Framework.AccessControl.AspNetCore;
using Gma.Framework.Api.Results;
using Gma.Framework.Api.Tenancy;
using Gma.Framework.Permissions;
using Gma.Framework.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

public static class ReservationGuestRecordEndpoints
{
    private const string RouteBase =
        "/api/reservations/properties/{propertyId:guid}/" +
        "{reservationId:guid}/guest-record";

    public static IEndpointRouteBuilder MapBunkFyReservationGuestRecordEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        RouteGroupBuilder group = endpoints.MapGroup(RouteBase)
            .WithTags("Reservation Guest Records")
            .RequireAuthorization();

        group.MapPost("", ExecuteAsync)
            .RequireTenant()
            .RequireAllPermissions(PermissionRequirements)
            .Produces<ReservationGuestRecordLinkProcessDto>(
                StatusCodes.Status200OK)
            .Produces<ReservationGuestRecordLinkProcessDto>(
                StatusCodes.Status202Accepted);

        group.MapGet("/{operationId:guid}", GetAsync)
            .RequireTenant()
            .RequireAllPermissions(PermissionRequirements)
            .Produces<ReservationGuestRecordLinkProcessDto>(
                StatusCodes.Status200OK);

        return endpoints;
    }

    private static async Task<IResult> ExecuteAsync(
        Guid propertyId,
        Guid reservationId,
        ReservationGuestRecordWriteRequest request,
        HttpContext httpContext,
        IAccessHttpSubjectResolver subjectResolver,
        ReservationGuestRecordWorkflow workflow,
        CancellationToken cancellationToken)
    {
        string? actorId = ResolveActor(httpContext, subjectResolver);
        if (actorId is null)
        {
            return Results.Unauthorized();
        }

        Result<ReservationGuestRecordLinkProcessDto> result =
            await workflow.ExecuteAsync(
                new(
                    request.OperationId,
                    propertyId,
                    reservationId,
                    request.ExpectedReservationVersion,
                    request.DisplayName,
                    request.LegalName,
                    request.Email,
                    request.Phone,
                    request.DateOfBirth,
                    request.NationalityCountryCode,
                    request.PreferredLanguageTag,
                    request.Notes,
                    request.LanguageTags),
                actorId,
                cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return result.ToHttpResult(ErrorStatusCodes);
        }

        MarkSensitiveResponse(httpContext);
        return result.Value.IsTerminal
            ? Results.Ok(result.Value)
            : Results.Accepted(
                $"{httpContext.Request.Path}/{result.Value.OperationId:D}",
                result.Value);
    }

    private static async Task<IResult> GetAsync(
        Guid propertyId,
        Guid reservationId,
        Guid operationId,
        HttpContext httpContext,
        ReservationGuestRecordWorkflow workflow,
        CancellationToken cancellationToken)
    {
        Result<ReservationGuestRecordLinkProcessDto> result =
            await workflow.GetAsync(
                operationId,
                propertyId,
                reservationId,
                cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            MarkSensitiveResponse(httpContext);
        }

        return result.ToHttpResult(ErrorStatusCodes);
    }

    private static string? ResolveActor(
        HttpContext context,
        IAccessHttpSubjectResolver subjectResolver)
    {
        AccessSubject? subject = subjectResolver.ResolveSubject(context);
        return subject is null
            ? null
            : $"{AccessSubjectKindNames.GetName(subject.Kind)}:{subject.Id}";
    }

    private static void MarkSensitiveResponse(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.Expires = "0";
    }

    private static readonly AccessPermissionMetadata[] PermissionRequirements =
    [
        Permission(GuestsAdminPermissionCodes.Create),
        Permission(ReservationsAdminPermissionCodes.ManageGuests)
    ];

    private static AccessPermissionMetadata Permission(string code) => new(
        PermissionCode.Create(code),
        scopeResolverName:
            ReservationGuestRecordPropertyAccessScopeResolver.ResolverName,
        requireScope: true);

    internal static readonly ApiErrorStatusCodeMap ErrorStatusCodes =
        CreateErrorStatusCodes(
            new(
                ReservationGuestRecordWorkflowErrors.ProcessStateInvalid.Code,
                StatusCodes.Status500InternalServerError),
            new(
                ReservationGuestRecordWorkflowErrors.GuestIdentityMismatch.Code,
                StatusCodes.Status500InternalServerError),
            new(
                "Reservations.ReservationNotFound",
                StatusCodes.Status404NotFound),
            new(
                "Reservations.GuestRecordLinkProcessNotFound",
                StatusCodes.Status404NotFound),
            new(
                "Guests.CreationOperationConflict",
                StatusCodes.Status409Conflict),
            new(
                "Reservations.VersionConflict",
                StatusCodes.Status409Conflict),
            new(
                "Reservations.GuestRecordLinkProcessConflict",
                StatusCodes.Status409Conflict),
            new(
                "Reservations.GuestRecordLinkProcessReservationOccupied",
                StatusCodes.Status409Conflict),
            new(
                "Reservations.GuestRecordLinkProcessPending",
                StatusCodes.Status409Conflict),
            new(
                "Reservations.GuestRecordLinkProcessRetryInvalid",
                StatusCodes.Status409Conflict),
            new(
                "Guests.WorkspaceProcessingRestricted",
                StatusCodes.Status423Locked),
            new(
                "Reservations.WorkspaceProcessingRestricted",
                StatusCodes.Status423Locked),
            new(
                "Guests.WorkspaceProcessingAdmissionUnavailable",
                StatusCodes.Status503ServiceUnavailable),
            new(
                "Reservations.WorkspaceProcessingAdmissionUnavailable",
                StatusCodes.Status503ServiceUnavailable));

    private static ApiErrorStatusCodeMap CreateErrorStatusCodes(
        params ApiErrorStatusCode[] entries) =>
        ApiErrorStatusCodeMap.Create(entries.Concat(
            Enum.GetValues<CountryPolicyDecisionReason>()
                .Where(reason => reason is not
                    CountryPolicyDecisionReason.Unknown and not
                    CountryPolicyDecisionReason.Allowed)
                .SelectMany(reason => new[]
                {
                    new ApiErrorStatusCode(
                        $"Guests.CountryPolicyDenied.{reason}",
                        StatusCodes.Status409Conflict),
                    new ApiErrorStatusCode(
                        $"Reservations.CountryPolicyDenied.{reason}",
                        StatusCodes.Status409Conflict)
                })).ToArray());
}

public sealed record ReservationGuestRecordWriteRequest(
    Guid OperationId,
    long ExpectedReservationVersion,
    string DisplayName,
    string? LegalName,
    string? Email,
    string? Phone,
    DateOnly? DateOfBirth,
    string? NationalityCountryCode,
    string? PreferredLanguageTag,
    string? Notes,
    string[]? LanguageTags = null);
