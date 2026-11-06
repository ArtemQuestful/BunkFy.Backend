namespace BunkFy.Modules.Guests.Api;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Guests.Application;
using BunkFy.Modules.Guests.Application.Commands;
using BunkFy.Modules.Guests.Application.Queries;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Guests.Persistence;
using Gma.Framework.AccessControl;
using Gma.Framework.AccessControl.AspNetCore;
using Gma.Framework.Api.Modules;
using Gma.Framework.Api.Observability;
using Gma.Framework.Api.Results;
using Gma.Framework.Api.Tenancy;
using Gma.Framework.Cqrs;
using Gma.Framework.ModuleComposition;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Gma.Framework.Tenancy.AccessControl.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

public sealed class GuestsModule : IModule
{
    public string Name => GuestsModuleMetadata.Name;

    public void AddServices(IHostApplicationBuilder builder)
    {
        builder.SelectModuleProfile(GuestsProfiles.Default, "BunkFy.Modules.Guests.Api");
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IAccessHttpScopeResolver, GuestsPropertyAccessScopeResolver>());
        builder.Services.AddOptions<GuestsApiSecurityOptions>();
        builder.Services.AddGuestsApplication();
        builder.AddGuestsPersistence();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        GuestsApiSecurityOptions security = endpoints.ServiceProvider
            .GetRequiredService<IOptions<GuestsApiSecurityOptions>>()
            .Value;
        RouteGroupBuilder group = endpoints.MapGroup("/api/guests/properties/{propertyId:guid}")
            .WithModuleName(this.Name)
            .WithTags("Guests")
            .RequireAuthorization();
        group.AddEndpointFilter(SensitiveResponseFilter);

        group.MapGet("", async (
            Guid propertyId,
            string? search,
            GuestStatus? status,
            int? page,
            int? pageSize,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            (await dispatcher.QueryAsync(
                new ListGuestProfilesQuery(
                    propertyId,
                    search,
                    status,
                    page ?? PageRequest.DefaultPage,
                    pageSize ?? PageRequest.DefaultPageSize),
                cancellationToken).ConfigureAwait(false)).ToHttpResult(ErrorStatusCodes))
            .RequireTenant()
            .RequireResolvedScopePermission(
                GuestsAdminPermissionCodes.Read,
                GuestsPropertyAccessScopeResolver.ResolverName)
            .Produces<GuestListResponse>(StatusCodes.Status200OK);

        group.MapGet("/{guestId:guid}", async (
            Guid propertyId,
            Guid guestId,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            (await dispatcher.QueryAsync(
                new GetGuestProfileQuery(propertyId, guestId),
                cancellationToken).ConfigureAwait(false)).ToHttpResult(ErrorStatusCodes))
            .RequireTenant()
            .RequireResolvedScopePermission(
                GuestsAdminPermissionCodes.Read,
                GuestsPropertyAccessScopeResolver.ResolverName)
            .Produces<GuestProfileDto>(StatusCodes.Status200OK);

        group.MapGet("/{guestId:guid}/stays", async (
            Guid propertyId,
            Guid guestId,
            int? page,
            int? pageSize,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            (await dispatcher.QueryAsync(
                new GetGuestStayHistoryQuery(
                    propertyId,
                    guestId,
                    page ?? PageRequest.DefaultPage,
                    pageSize ?? PageRequest.DefaultPageSize),
                cancellationToken).ConfigureAwait(false)).ToHttpResult(ErrorStatusCodes))
            .RequireTenant()
            .RequireResolvedScopePermission(
                GuestsAdminPermissionCodes.Read,
                GuestsPropertyAccessScopeResolver.ResolverName)
            .Produces<GuestStayHistoryListResponse>(StatusCodes.Status200OK);

        group.MapPost("", async (
            Guid propertyId,
            GuestProfileWriteRequest request,
            HttpContext context,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            string? actor = ResolveActor(context, subjectResolver);
            return actor is null
                ? Results.Unauthorized()
                : (await dispatcher.SendAsync(
                    new CreateGuestProfileCommand(
                        request.OperationId,
                        propertyId,
                        request.DisplayName,
                        request.LegalName,
                        request.Email,
                        request.Phone,
                        request.DateOfBirth,
                        request.NationalityCountryCode,
                        request.PreferredLanguageTag,
                        request.Notes,
                        actor,
                        LanguageTags: request.LanguageTags),
                    cancellationToken).ConfigureAwait(false)).ToHttpResult(ErrorStatusCodes);
        })
            .Produces<GuestMutationReceiptDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                GuestsAdminPermissionCodes.Create,
                GuestsPropertyAccessScopeResolver.ResolverName);

        group.MapPost("/data-rights-corrections", async (
            Guid propertyId,
            GuestDataRightsCorrectionRequest request,
            HttpContext context,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            string? actor = ResolveActor(context, subjectResolver);
            return actor is null
                ? Results.Unauthorized()
                : (await dispatcher.SendAsync(
                    new ApplyGuestDataRightsCorrectionCommand(
                        request.IdempotencyKey,
                        propertyId,
                        request.CaseId,
                        request.ApprovalRevision,
                        request.GuestId,
                        request.ExpectedVersion,
                        request.DisplayName,
                        request.LegalName,
                        request.Email,
                        request.Phone,
                        request.DateOfBirth,
                        request.NationalityCountryCode,
                        request.PreferredLanguageTag,
                        request.Notes,
                        actor,
                        request.LanguageTags),
                    cancellationToken).ConfigureAwait(false)).ToHttpResult(ErrorStatusCodes);
        })
            .Produces<GuestDataRightsCorrectionReceiptDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                DataRightsAdminPermissionCodes.Execute,
                GuestsPropertyAccessScopeResolver.ResolverName)
            .RequireAssuranceWhenConfigured(
                security.CorrectionExecutionAssurance);

        group.MapPost("/data-rights-restrictions", async (
            Guid propertyId,
            ApplyGuestProcessingRestrictionRequest request,
            HttpContext context,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            string? actor = ResolveActor(context, subjectResolver);
            return actor is null
                ? Results.Unauthorized()
                : (await dispatcher.SendAsync(
                    new ApplyGuestProcessingRestrictionCommand(
                        request.IdempotencyKey,
                        propertyId,
                        request.CaseId,
                        request.ApprovalRevision,
                        request.GuestId,
                        request.ExpectedGuestVersion,
                        request.ExpectedProjectionRevision,
                        actor),
                    cancellationToken).ConfigureAwait(false)).ToHttpResult(ErrorStatusCodes);
        })
            .Produces<GuestProcessingRestrictionReceiptDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                DataRightsAdminPermissionCodes.Restrict,
                GuestsPropertyAccessScopeResolver.ResolverName)
            .RequireAssuranceWhenConfigured(
                security.RestrictionExecutionAssurance);

        group.MapPost("/data-rights-restrictions/{restrictionId:guid}/release", async (
            Guid propertyId,
            Guid restrictionId,
            ReleaseGuestProcessingRestrictionRequest request,
            HttpContext context,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            string? actor = ResolveActor(context, subjectResolver);
            return actor is null
                ? Results.Unauthorized()
                : (await dispatcher.SendAsync(
                    new ReleaseGuestProcessingRestrictionCommand(
                        request.IdempotencyKey,
                        propertyId,
                        restrictionId,
                        request.CaseId,
                        request.ApprovalRevision,
                        request.GuestId,
                        request.ExpectedGuestVersion,
                        request.ExpectedRestrictionVersion,
                        request.ExpectedProjectionRevision,
                        actor),
                    cancellationToken).ConfigureAwait(false)).ToHttpResult(ErrorStatusCodes);
        })
            .Produces<GuestProcessingRestrictionReceiptDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                DataRightsAdminPermissionCodes.Restrict,
                GuestsPropertyAccessScopeResolver.ResolverName)
            .RequireAssuranceWhenConfigured(
                security.RestrictionExecutionAssurance);

        group.MapGet("/{guestId:guid}/data-rights-restrictions", async (
            Guid propertyId,
            Guid guestId,
            int? page,
            int? pageSize,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            (await dispatcher.QueryAsync(
                new ListGuestProcessingRestrictionsQuery(
                    propertyId,
                    guestId,
                    page ?? PageRequest.DefaultPage,
                    pageSize ?? PageRequest.DefaultPageSize),
                cancellationToken).ConfigureAwait(false)).ToHttpResult(ErrorStatusCodes))
            .Produces<GuestProcessingRestrictionListResponse>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                DataRightsAdminPermissionCodes.Restrict,
                GuestsPropertyAccessScopeResolver.ResolverName);

        group.MapPost("/{guestId:guid}/data-holds", async (
            Guid propertyId,
            Guid guestId,
            PlaceGuestDataHoldRequest request,
            HttpContext context,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            string? actor = ResolveActor(context, subjectResolver);
            return actor is null
                ? Results.Unauthorized()
                : (await dispatcher.SendAsync(
                    new PlaceGuestDataHoldCommand(
                        request.IdempotencyKey,
                        propertyId,
                        guestId,
                        request.ExpectedGuestVersion,
                        request.ReasonCode,
                        actor),
                    cancellationToken).ConfigureAwait(false)).ToHttpResult(ErrorStatusCodes);
        })
            .Produces<GuestDataHoldReceiptDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                GuestsAdminPermissionCodes.DataHoldsManage,
                GuestsPropertyAccessScopeResolver.ResolverName);

        group.MapPost("/{guestId:guid}/data-holds/{holdId:guid}/release", async (
            Guid propertyId,
            Guid guestId,
            Guid holdId,
            ReleaseGuestDataHoldRequest request,
            HttpContext context,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            string? actor = ResolveActor(context, subjectResolver);
            Result<GuestDataHoldReceiptDto> result = !request.Confirmed
                ? Result.Failure<GuestDataHoldReceiptDto>(
                    new("Guests.ConfirmationRequired", "Confirmation is required."))
                : actor is null
                    ? Result.Failure<GuestDataHoldReceiptDto>(
                        new("Guests.AuthenticationRequired", "Authentication is required."))
                    : await dispatcher.SendAsync(
                        new ReleaseGuestDataHoldCommand(
                            request.IdempotencyKey,
                            propertyId,
                            guestId,
                            holdId,
                            request.ExpectedGuestVersion,
                            request.ExpectedHoldVersion,
                            actor),
                        cancellationToken).ConfigureAwait(false);
            return actor is null ? Results.Unauthorized() : result.ToHttpResult(ErrorStatusCodes);
        })
            .Produces<GuestDataHoldReceiptDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                GuestsAdminPermissionCodes.DataHoldsManage,
                GuestsPropertyAccessScopeResolver.ResolverName)
            .RequireAssuranceWhenConfigured(
                security.DataHoldReleaseAssurance);

        group.MapGet("/{guestId:guid}/data-holds", async (
            Guid propertyId,
            Guid guestId,
            GuestDataHoldStatus? status,
            int? page,
            int? pageSize,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            (await dispatcher.QueryAsync(
                new ListGuestDataHoldsQuery(
                    propertyId,
                    guestId,
                    status,
                    page ?? PageRequest.DefaultPage,
                    pageSize ?? PageRequest.DefaultPageSize),
                cancellationToken).ConfigureAwait(false)).ToHttpResult(ErrorStatusCodes))
            .Produces<GuestDataHoldListResponse>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                GuestsAdminPermissionCodes.DataHoldsManage,
                GuestsPropertyAccessScopeResolver.ResolverName);

        group.MapPut("/{guestId:guid}", async (
            Guid propertyId,
            Guid guestId,
            GuestProfileUpdateRequest request,
            HttpContext context,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            string? actor = ResolveActor(context, subjectResolver);
            return actor is null
                ? Results.Unauthorized()
                : (await dispatcher.SendAsync(
                    new UpdateGuestProfileCommand(
                        request.OperationId,
                        propertyId,
                        guestId,
                        request.DisplayName,
                        request.LegalName,
                        request.Email,
                        request.Phone,
                        request.DateOfBirth,
                        request.NationalityCountryCode,
                        request.PreferredLanguageTag,
                        request.Notes,
                        request.ExpectedVersion,
                        actor,
                        request.LanguageTags),
                    cancellationToken).ConfigureAwait(false)).ToHttpResult(ErrorStatusCodes);
        })
            .Produces<GuestMutationReceiptDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                GuestsAdminPermissionCodes.Manage,
                GuestsPropertyAccessScopeResolver.ResolverName);

        group.MapPost("/{guestId:guid}/archive", async (
            Guid propertyId,
            Guid guestId,
            ArchiveGuestProfileRequest request,
            HttpContext context,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            string? actor = ResolveActor(context, subjectResolver);
            Result<GuestMutationReceiptDto> result = !request.Confirmed
                ? Result.Failure<GuestMutationReceiptDto>(new("Guests.ConfirmationRequired", "Confirmation is required."))
                : actor is null
                    ? Result.Failure<GuestMutationReceiptDto>(new("Guests.AuthenticationRequired", "Authentication is required."))
                    : await dispatcher.SendAsync(
                        new ArchiveGuestProfileCommand(
                            request.OperationId,
                            propertyId,
                            guestId,
                            request.ExpectedVersion,
                            actor),
                        cancellationToken).ConfigureAwait(false);
            return actor is null ? Results.Unauthorized() : result.ToHttpResult(ErrorStatusCodes);
        })
            .Produces<GuestMutationReceiptDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                GuestsAdminPermissionCodes.Archive,
                GuestsPropertyAccessScopeResolver.ResolverName);
    }

    public sealed record GuestProfileWriteRequest(
        Guid OperationId,
        string DisplayName,
        string? LegalName,
        string? Email,
        string? Phone,
        DateOnly? DateOfBirth,
        string? NationalityCountryCode,
        string? PreferredLanguageTag,
        string? Notes,
        string[]? LanguageTags = null);

    public sealed record GuestProfileUpdateRequest(
        Guid OperationId,
        string DisplayName,
        string? LegalName,
        string? Email,
        string? Phone,
        DateOnly? DateOfBirth,
        string? NationalityCountryCode,
        string? PreferredLanguageTag,
        string? Notes,
        long ExpectedVersion,
        string[]? LanguageTags = null);

    public sealed record GuestDataRightsCorrectionRequest(
        Guid IdempotencyKey,
        Guid CaseId,
        long ApprovalRevision,
        Guid GuestId,
        long ExpectedVersion,
        string DisplayName,
        string? LegalName,
        string? Email,
        string? Phone,
        DateOnly? DateOfBirth,
        string? NationalityCountryCode,
        string? PreferredLanguageTag,
        string? Notes,
        string[]? LanguageTags = null);

    public sealed record ApplyGuestProcessingRestrictionRequest(
        Guid IdempotencyKey,
        Guid CaseId,
        long ApprovalRevision,
        Guid GuestId,
        long ExpectedGuestVersion,
        long ExpectedProjectionRevision);

    public sealed record ReleaseGuestProcessingRestrictionRequest(
        Guid IdempotencyKey,
        Guid CaseId,
        long ApprovalRevision,
        Guid GuestId,
        long ExpectedGuestVersion,
        long ExpectedRestrictionVersion,
        long ExpectedProjectionRevision);

    public sealed record PlaceGuestDataHoldRequest(
        Guid IdempotencyKey,
        long ExpectedGuestVersion,
        string ReasonCode);

    public sealed record ReleaseGuestDataHoldRequest(
        Guid IdempotencyKey,
        long ExpectedGuestVersion,
        long ExpectedHoldVersion,
        bool Confirmed);

    public sealed record ArchiveGuestProfileRequest(
        Guid OperationId,
        long ExpectedVersion,
        bool Confirmed);

    private static async ValueTask<object?> SensitiveResponseFilter(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        MarkSensitiveResponse(context.HttpContext);
        return await next(context).ConfigureAwait(false);
    }

    private static void MarkSensitiveResponse(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.Expires = "0";
    }

    private static string? ResolveActor(HttpContext context, IAccessHttpSubjectResolver subjectResolver)
    {
        AccessSubject? subject = subjectResolver.ResolveSubject(context);
        return subject is null ? null : $"{AccessSubjectKindNames.GetName(subject.Kind)}:{subject.Id}";
    }

    private static readonly ApiErrorStatusCodeMap ErrorStatusCodes = CreateErrorStatusCodes(
        new(GuestsApplicationErrors.GuestNotFound.Code, StatusCodes.Status404NotFound),
        new(GuestsApplicationErrors.CreationOperationConflict.Code, StatusCodes.Status409Conflict),
        new(GuestsApplicationErrors.ManagementOperationInvalid.Code, StatusCodes.Status400BadRequest),
        new(GuestsApplicationErrors.ManagementOperationConflict.Code, StatusCodes.Status409Conflict),
        new(GuestsApplicationErrors.WorkspaceProcessingRestricted.Code, StatusCodes.Status423Locked),
        new(GuestsApplicationErrors.WorkspaceProcessingAdmissionUnavailable.Code, StatusCodes.Status503ServiceUnavailable),
        new(GuestsApplicationErrors.VersionConflict.Code, StatusCodes.Status409Conflict),
        new(GuestsApplicationErrors.GuestArchived.Code, StatusCodes.Status409Conflict),
        new(GuestsApplicationErrors.GuestAlreadyArchived.Code, StatusCodes.Status409Conflict),
        new(GuestsApplicationErrors.DataRightsApprovalRequired.Code, StatusCodes.Status409Conflict),
        new(GuestsApplicationErrors.CorrectionIdempotencyConflict.Code, StatusCodes.Status409Conflict),
        new(GuestsApplicationErrors.CorrectionNoChanges.Code, StatusCodes.Status409Conflict),
        new(GuestsApplicationErrors.CorrectionRequestInvalid.Code, StatusCodes.Status400BadRequest),
        new(GuestsApplicationErrors.RestrictionRequestInvalid.Code, StatusCodes.Status400BadRequest),
        new(GuestsApplicationErrors.RestrictionNotFound.Code, StatusCodes.Status404NotFound),
        new(GuestsApplicationErrors.RestrictionProjectionUnavailable.Code, StatusCodes.Status409Conflict),
        new(GuestsApplicationErrors.RestrictionActiveStateInvalid.Code, StatusCodes.Status409Conflict),
        new(GuestsApplicationErrors.RestrictionOwnerProofInvalid.Code, StatusCodes.Status409Conflict),
        new(GuestsApplicationErrors.RestrictionGuestVersionConflict.Code, StatusCodes.Status409Conflict),
        new(GuestsApplicationErrors.RestrictionIdempotencyConflict.Code, StatusCodes.Status409Conflict),
        new(GuestsApplicationErrors.RestrictionApprovalAlreadyUsed.Code, StatusCodes.Status409Conflict),
        new(GuestsApplicationErrors.RestrictionVersionConflict.Code, StatusCodes.Status409Conflict),
        new(GuestsApplicationErrors.RestrictionAlreadyReleased.Code, StatusCodes.Status409Conflict),
        new(GuestsApplicationErrors.RestrictionProjectionVersionConflict.Code, StatusCodes.Status409Conflict),
        new(GuestsApplicationErrors.RestrictionProjectionStateInvalid.Code, StatusCodes.Status409Conflict),
        new(GuestsApplicationErrors.RestrictionProjectionTransitionInvalid.Code, StatusCodes.Status409Conflict),
        new(GuestsApplicationErrors.DataHoldRequestInvalid.Code, StatusCodes.Status400BadRequest),
        new(GuestsApplicationErrors.DataHoldNotFound.Code, StatusCodes.Status404NotFound),
        new(GuestsApplicationErrors.DataHoldGuestVersionConflict.Code, StatusCodes.Status409Conflict),
        new(GuestsApplicationErrors.DataHoldIdempotencyConflict.Code, StatusCodes.Status409Conflict),
        new(GuestsApplicationErrors.DataHoldVersionConflict.Code, StatusCodes.Status409Conflict),
        new(GuestsApplicationErrors.DataHoldAlreadyReleased.Code, StatusCodes.Status409Conflict),
        new("Guests.ConfirmationRequired", StatusCodes.Status400BadRequest));

    private static ApiErrorStatusCodeMap CreateErrorStatusCodes(params ApiErrorStatusCode[] entries) =>
        ApiErrorStatusCodeMap.Create(entries.Concat(
            GuestsApplicationErrors.CountryPolicyDenials.Select(error =>
                new ApiErrorStatusCode(error.Code, StatusCodes.Status409Conflict))).ToArray());
}
