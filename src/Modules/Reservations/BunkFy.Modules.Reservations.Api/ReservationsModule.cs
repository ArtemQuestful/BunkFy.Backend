namespace BunkFy.Modules.Reservations.Api;

using Gma.Framework.AccessControl.AspNetCore;
using Gma.Framework.Api.Modules;
using Gma.Framework.Api.Observability;
using Gma.Framework.Api.Results;
using Gma.Framework.Api.Tenancy;
using Gma.Framework.Cqrs;
using Gma.Framework.ModuleComposition;
using Gma.Framework.Pagination;
using Gma.Framework.Security;
using Gma.Framework.Security.AspNetCore;
using Gma.Framework.Tenancy.AccessControl.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using BunkFy.Modules.Reservations.Application;
using BunkFy.Modules.Reservations.Application.Commands;
using BunkFy.Modules.Reservations.Application.Queries;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Persistence;
using BunkFy.Modules.DataRights.Contracts;

public sealed class ReservationsModule : IModule
{
    public string Name => ReservationsModuleMetadata.Name;

    public void AddServices(IHostApplicationBuilder builder)
    {
        builder.SelectModuleProfile(ReservationsProfiles.Default, "BunkFy.Modules.Reservations.Api");
        builder.Services.AddOptions<ReservationsApiSecurityOptions>();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IAccessHttpScopeResolver, ReservationsPropertyAccessScopeResolver>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IStartupFilter, ReservationsNoStoreStartupFilter>());
        builder.Services.AddReservationsApplication();
        builder.AddReservationsPersistence();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ReservationsApiSecurityOptions security = endpoints.ServiceProvider
            .GetRequiredService<IOptions<ReservationsApiSecurityOptions>>()
            .Value;
        RouteGroupBuilder group = endpoints.MapGroup("/api/reservations/properties/{propertyId:guid}")
            .WithModuleName(this.Name)
            .WithTags("Reservations")
            .RequireAuthorization();

        group.MapGet("/operations-snapshot", async (
            Guid propertyId,
            [AsParameters] OperationsSnapshotRequest request,
            HttpContext httpContext,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            MarkPersonalDataResponse(httpContext);
            return (await dispatcher.QueryAsync(
                new GetReservationOperationsSnapshotQuery(
                    propertyId,
                    request.LocalDate,
                    request.UpcomingLimit ?? ReservationsContractLimits.DefaultOperationsSnapshotUpcomingLimit),
                cancellationToken).ConfigureAwait(false)).ToHttpResult(ErrorStatusCodes);
        })
            .Produces<ReservationOperationsSnapshotDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                ReservationsAdminPermissionCodes.Read,
                ReservationsPropertyAccessScopeResolver.ResolverName);

        group.MapPost("", async (
            Guid propertyId,
            CreateReservationRequest request,
            HttpContext httpContext,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            (await dispatcher.SendAsync(
                new CreateReservationCommand(
                    request.OperationId,
                    propertyId,
                    request.Arrival,
                    request.Departure,
                    request.InventoryUnitIds,
                    request.PrimaryGuestName,
                    request.Email,
                    request.Phone,
                    request.GuestCount,
                    request.SourceKind,
                    request.SourceSystem,
                    request.SourceReference,
                    request.Notes,
                    request.ExpectedArrivalTime,
                    request.ExpectedDepartureTime,
                    ResolveActor(httpContext, subjectResolver)),
                cancellationToken).ConfigureAwait(false)).ToHttpResult(ErrorStatusCodes))
            .Produces<ReservationMutationReceiptDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                ReservationsAdminPermissionCodes.Create,
                ReservationsPropertyAccessScopeResolver.ResolverName);

        RouteHandlerBuilder correction = group.MapPost("/data-rights-corrections", async (
            Guid propertyId,
            ReservationDataRightsCorrectionRequest request,
            HttpContext httpContext,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            string? actorId = ResolveActor(httpContext, subjectResolver);
            return actorId is null
                ? Results.Unauthorized()
                : (await dispatcher.SendAsync(
                    new ApplyReservationDataRightsCorrectionCommand(
                        request.ExecutionId,
                        propertyId,
                        request.CaseId,
                        request.ApprovalRevision,
                        request.ReservationId,
                        request.ExpectedVersion,
                        request.ExpectedDetailsRevision,
                        request.PrimaryGuestName,
                        request.Email,
                        request.Phone,
                        request.GuestCount,
                        request.Notes,
                        request.ExpectedArrivalTime,
                        request.ExpectedDepartureTime,
                        actorId),
                    cancellationToken).ConfigureAwait(false))
                    .ToHttpResult(ErrorStatusCodes);
        })
            .Produces<ReservationDataRightsCorrectionReceiptDto>(
                StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                DataRightsAdminPermissionCodes.Execute,
                ReservationsPropertyAccessScopeResolver.ResolverName);
        RequireAssuranceWhenConfigured(
            correction,
            security.CorrectionExecutionAssurance);

        group.MapGet("", async (
            Guid propertyId,
            ReservationStatus[]? status,
            string? search,
            ReservationListOrder? order,
            ReservationListView? view,
            DateOnly? operatingDate,
            DateOnly? overlapsFrom,
            DateOnly? overlapsTo,
            int? page,
            int? pageSize,
            HttpContext httpContext,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            MarkPersonalDataResponse(httpContext);
            return (await dispatcher.QueryAsync(
                new ListReservationsQuery(
                    propertyId,
                    status,
                    search,
                    order ?? ReservationListOrder.CreatedDescending,
                    page ?? PageRequest.DefaultPage,
                    pageSize ?? PageRequest.DefaultPageSize,
                    overlapsFrom,
                    overlapsTo,
                    view,
                    operatingDate),
                cancellationToken).ConfigureAwait(false)).ToHttpResult(ErrorStatusCodes);
        })
            .Produces<ReservationListResponse>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                ReservationsAdminPermissionCodes.Read,
                ReservationsPropertyAccessScopeResolver.ResolverName);

        group.MapGet("/stay-amendments/recovery", async (
            Guid propertyId,
            ReservationStayAmendmentOutcome? cursorOutcome,
            DateTimeOffset? cursorUpdatedAtUtc,
            Guid? cursorOperationId,
            Guid? cursorReservationId,
            int? pageSize,
            HttpContext httpContext,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            MarkPersonalDataResponse(httpContext);
            return (await dispatcher.QueryAsync(
                new ListReservationStayAmendmentRecoveryQuery(
                    propertyId,
                    CreateStayAmendmentRecoveryCursor(
                        cursorOutcome,
                        cursorUpdatedAtUtc,
                        cursorOperationId,
                        cursorReservationId),
                    pageSize ?? StayAmendmentRecoveryDefaultPageSize),
                cancellationToken).ConfigureAwait(false)).ToHttpResult(ErrorStatusCodes);
        })
            .Produces<ReservationStayAmendmentRecoveryPageDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                ReservationsAdminPermissionCodes.Read,
                ReservationsPropertyAccessScopeResolver.ResolverName);

        group.MapGet("/{reservationId:guid}", async (
            Guid propertyId,
            Guid reservationId,
            HttpContext httpContext,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            MarkPersonalDataResponse(httpContext);
            return (await dispatcher.QueryAsync(
                new GetReservationQuery(propertyId, reservationId),
                cancellationToken).ConfigureAwait(false)).ToHttpResult(ErrorStatusCodes);
        })
            .Produces<ReservationDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                ReservationsAdminPermissionCodes.Read,
                ReservationsPropertyAccessScopeResolver.ResolverName);

        group.MapGet("/{reservationId:guid}/details-history", async (
            Guid propertyId,
            Guid reservationId,
            int? page,
            int? pageSize,
            HttpContext httpContext,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            MarkPersonalDataResponse(httpContext);
            return (await dispatcher.QueryAsync(
                new GetReservationDetailsHistoryQuery(
                    propertyId,
                    reservationId,
                    page ?? PageRequest.DefaultPage,
                    pageSize ?? PageRequest.DefaultPageSize),
                cancellationToken).ConfigureAwait(false)).ToHttpResult(ErrorStatusCodes);
        })
            .Produces<ReservationDetailsHistoryListResponse>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                ReservationsAdminPermissionCodes.Read,
                ReservationsPropertyAccessScopeResolver.ResolverName);

        group.MapGet("/{reservationId:guid}/stay-amendments/{operationId:guid}", async (
            Guid propertyId,
            Guid reservationId,
            Guid operationId,
            HttpContext httpContext,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            MarkPersonalDataResponse(httpContext);
            return (await dispatcher.QueryAsync(
                new GetReservationStayAmendmentQuery(propertyId, reservationId, operationId),
                cancellationToken).ConfigureAwait(false)).ToHttpResult(ErrorStatusCodes);
        })
            .Produces<ReservationStayAmendmentReceiptDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                ReservationsAdminPermissionCodes.Read,
                ReservationsPropertyAccessScopeResolver.ResolverName);

        group.MapPut("/{reservationId:guid}/guest-details", async (
            Guid propertyId,
            Guid reservationId,
            UpdateReservationGuestDetailsRequest request,
            HttpContext httpContext,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            Gma.Framework.AccessControl.AccessSubject? subject = subjectResolver.ResolveSubject(httpContext);
            if (subject is null)
            {
                return Results.Unauthorized();
            }

            return (await dispatcher.SendAsync(
                new UpdateReservationGuestDetailsCommand(
                    request.OperationId,
                    propertyId,
                    reservationId,
                    request.PrimaryGuestName,
                    request.Email,
                    request.Phone,
                    request.GuestCount,
                    request.Notes,
                    request.ExpectedDetailsRevision,
                    ReservationDetailsChangeOriginKind.Staff,
                    $"{Gma.Framework.AccessControl.AccessSubjectKindNames.GetName(subject.Kind)}:{subject.Id}",
                    request.ExpectedArrivalTime,
                    request.ExpectedDepartureTime),
                cancellationToken).ConfigureAwait(false)).ToHttpResult(ErrorStatusCodes);
        })
            .Produces<ReservationMutationReceiptDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                ReservationsAdminPermissionCodes.Manage,
                ReservationsPropertyAccessScopeResolver.ResolverName);

        group.MapPut("/{reservationId:guid}/inventory", async (
            Guid propertyId,
            Guid reservationId,
            ReassignReservationInventoryRequest request,
            HttpContext httpContext,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            string? actorId = ResolveActor(httpContext, subjectResolver);
            return actorId is null
                ? Results.Unauthorized()
                : (await dispatcher.SendAsync(
                    new ReassignReservationInventoryCommand(
                        propertyId,
                        reservationId,
                        request.AmendmentRequestId,
                        request.InventoryUnitIds,
                        request.ExpectedDetailsRevision,
                        actorId),
                    cancellationToken).ConfigureAwait(false)).ToHttpResult(ErrorStatusCodes);
        })
            .Produces<ReservationMutationReceiptDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                ReservationsAdminPermissionCodes.Manage,
                ReservationsPropertyAccessScopeResolver.ResolverName);

        group.MapPut("/{reservationId:guid}/stay-amendments/{operationId:guid}", async (
            Guid propertyId,
            Guid reservationId,
            Guid operationId,
            AmendReservationStayRequest request,
            HttpContext httpContext,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            MarkPersonalDataResponse(httpContext);
            string? actorId = ResolveActor(httpContext, subjectResolver);
            return actorId is null
                ? Results.Unauthorized()
                : (await dispatcher.SendAsync(
                    new AmendReservationStayCommand(
                        operationId,
                        propertyId,
                        reservationId,
                        request.Arrival,
                        request.Departure,
                        request.ExpectedArrivalTime,
                        request.ExpectedDepartureTime,
                        request.InventoryUnitIds,
                        request.ExpectedDetailsRevision,
                        actorId),
                    cancellationToken).ConfigureAwait(false)).ToHttpResult(ErrorStatusCodes);
        })
            .Produces<ReservationStayAmendmentReceiptDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                ReservationsAdminPermissionCodes.Manage,
                ReservationsPropertyAccessScopeResolver.ResolverName);

        group.MapPost("/{reservationId:guid}/stay-amendments/{operationId:guid}/reconcile", async (
            Guid propertyId,
            Guid reservationId,
            Guid operationId,
            ReconcileReservationStayAmendmentRequest request,
            HttpContext httpContext,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            MarkPersonalDataResponse(httpContext);
            string? actorId = ResolveActor(httpContext, subjectResolver);
            return actorId is null
                ? Results.Unauthorized()
                : (await dispatcher.SendAsync(
                    new ReconcileReservationStayAmendmentCommand(
                        propertyId,
                        reservationId,
                        operationId,
                        request.ExpectedOperationVersion,
                        actorId),
                    cancellationToken).ConfigureAwait(false)).ToHttpResult(ErrorStatusCodes);
        })
            .Produces<ReservationStayAmendmentReceiptDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                ReservationsAdminPermissionCodes.Manage,
                ReservationsPropertyAccessScopeResolver.ResolverName);

        group.MapPut("/{reservationId:guid}/guests", async (
            Guid propertyId,
            Guid reservationId,
            LinkReservationGuestRequest request,
            HttpContext httpContext,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            string? actorId = ResolveActor(httpContext, subjectResolver);
            return actorId is null
                ? Results.Unauthorized()
                : (await dispatcher.SendAsync(
                    new LinkReservationGuestCommand(
                        propertyId,
                        reservationId,
                        request.GuestId,
                        request.Role,
                        request.ReplaceExistingRole,
                        request.ExpectedVersion,
                        actorId),
                    cancellationToken).ConfigureAwait(false)).ToHttpResult(ErrorStatusCodes);
        })
            .Produces<ReservationMutationReceiptDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                ReservationsAdminPermissionCodes.ManageGuests,
                ReservationsPropertyAccessScopeResolver.ResolverName);

        group.MapPost("/{reservationId:guid}/cancel", async (
            Guid propertyId,
            Guid reservationId,
            CancelReservationRequest request,
            HttpContext httpContext,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            (await dispatcher.SendAsync(
                new CancelReservationCommand(
                    request.OperationId,
                    propertyId,
                    reservationId,
                    request.ExpectedVersion,
                    ResolveActor(httpContext, subjectResolver)),
                cancellationToken).ConfigureAwait(false)).ToHttpResult(ErrorStatusCodes))
            .Produces<ReservationMutationReceiptDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                ReservationsAdminPermissionCodes.Cancel,
                ReservationsPropertyAccessScopeResolver.ResolverName);

        group.MapPost("/{reservationId:guid}/check-in", async (
            Guid propertyId,
            Guid reservationId,
            StayLifecycleRequest request,
            HttpContext httpContext,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            string? actorId = ResolveActor(httpContext, subjectResolver);
            return actorId is null
                ? Results.Unauthorized()
                : (await dispatcher.SendAsync(
                    new CheckInReservationCommand(
                        request.OperationId,
                        propertyId,
                        reservationId,
                        request.BusinessDate,
                        request.ExpectedVersion,
                        actorId),
                    cancellationToken).ConfigureAwait(false)).ToHttpResult(ErrorStatusCodes);
        })
            .Produces<ReservationMutationReceiptDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                ReservationsAdminPermissionCodes.CheckIn,
                ReservationsPropertyAccessScopeResolver.ResolverName);

        group.MapPost("/{reservationId:guid}/no-show", async (
            Guid propertyId,
            Guid reservationId,
            StayLifecycleRequest request,
            HttpContext httpContext,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            string? actorId = ResolveActor(httpContext, subjectResolver);
            return actorId is null
                ? Results.Unauthorized()
                : (await dispatcher.SendAsync(
                    new MarkReservationNoShowCommand(
                        request.OperationId,
                        propertyId,
                        reservationId,
                        request.BusinessDate,
                        request.ExpectedVersion,
                        actorId),
                    cancellationToken).ConfigureAwait(false)).ToHttpResult(ErrorStatusCodes);
        })
            .Produces<ReservationMutationReceiptDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                ReservationsAdminPermissionCodes.NoShow,
                ReservationsPropertyAccessScopeResolver.ResolverName);

        group.MapPost("/{reservationId:guid}/check-out", async (
            Guid propertyId,
            Guid reservationId,
            StayLifecycleRequest request,
            HttpContext httpContext,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            string? actorId = ResolveActor(httpContext, subjectResolver);
            return actorId is null
                ? Results.Unauthorized()
                : (await dispatcher.SendAsync(
                    new CheckOutReservationCommand(
                        request.OperationId,
                        propertyId,
                        reservationId,
                        request.BusinessDate,
                        request.ExpectedVersion,
                        actorId),
                    cancellationToken).ConfigureAwait(false)).ToHttpResult(ErrorStatusCodes);
        })
            .Produces<ReservationMutationReceiptDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                ReservationsAdminPermissionCodes.CheckOut,
                ReservationsPropertyAccessScopeResolver.ResolverName);
    }

    public sealed record CreateReservationRequest(
        Guid OperationId,
        DateOnly Arrival,
        DateOnly Departure,
        TimeOnly? ExpectedArrivalTime,
        TimeOnly? ExpectedDepartureTime,
        IReadOnlyCollection<Guid> InventoryUnitIds,
        string PrimaryGuestName,
        string? Email,
        string? Phone,
        int GuestCount,
        ReservationSourceKind SourceKind,
        string? SourceSystem,
        string? SourceReference,
        string? Notes);

    public sealed record CancelReservationRequest(Guid OperationId, long ExpectedVersion);

    public sealed record ReservationDataRightsCorrectionRequest(
        Guid ExecutionId,
        Guid CaseId,
        long ApprovalRevision,
        Guid ReservationId,
        long ExpectedVersion,
        long ExpectedDetailsRevision,
        string PrimaryGuestName,
        string? Email,
        string? Phone,
        int GuestCount,
        string? Notes,
        TimeOnly? ExpectedArrivalTime,
        TimeOnly? ExpectedDepartureTime);

    public sealed record StayLifecycleRequest(
        Guid OperationId,
        DateOnly BusinessDate,
        long ExpectedVersion);

    public sealed record OperationsSnapshotRequest(
        DateOnly? LocalDate,
        int? UpcomingLimit);

    public sealed record LinkReservationGuestRequest(
        Guid GuestId,
        ReservationGuestRoleKind Role,
        bool ReplaceExistingRole,
        long ExpectedVersion);

    public sealed record UpdateReservationGuestDetailsRequest(
        Guid OperationId,
        string PrimaryGuestName,
        string? Email,
        string? Phone,
        int GuestCount,
        string? Notes,
        TimeOnly? ExpectedArrivalTime,
        TimeOnly? ExpectedDepartureTime,
        long ExpectedDetailsRevision);

    public sealed record ReassignReservationInventoryRequest(
        Guid AmendmentRequestId,
        IReadOnlyCollection<Guid> InventoryUnitIds,
        long ExpectedDetailsRevision);

    public sealed record AmendReservationStayRequest(
        DateOnly Arrival,
        DateOnly Departure,
        TimeOnly? ExpectedArrivalTime,
        TimeOnly? ExpectedDepartureTime,
        IReadOnlyCollection<Guid> InventoryUnitIds,
        long ExpectedDetailsRevision);

    public sealed record ReconcileReservationStayAmendmentRequest(long ExpectedOperationVersion);

    private static readonly ApiErrorStatusCodeMap ErrorStatusCodes = CreateErrorStatusCodes(
        new(ReservationsApplicationErrors.WorkspaceProcessingRestricted.Code, StatusCodes.Status423Locked),
        new(ReservationsApplicationErrors.WorkspaceProcessingAdmissionUnavailable.Code, StatusCodes.Status503ServiceUnavailable),
        new(ReservationsApplicationErrors.PropertyNotFound.Code, StatusCodes.Status404NotFound),
        new(ReservationsApplicationErrors.PropertyInactive.Code, StatusCodes.Status409Conflict),
        new(ReservationsApplicationErrors.PropertyTimeZoneUnavailable.Code, StatusCodes.Status503ServiceUnavailable),
        new(ReservationsApplicationErrors.OperationsSnapshotLimitInvalid.Code, StatusCodes.Status400BadRequest),
        new(ReservationsApplicationErrors.ReservationNotFound.Code, StatusCodes.Status404NotFound),
        new(ReservationsApplicationErrors.ExternalSourceAlreadyExists.Code, StatusCodes.Status409Conflict),
        new(ReservationsApplicationErrors.CreationOperationConflict.Code, StatusCodes.Status409Conflict),
        new(ReservationsApplicationErrors.ManagementOperationInvalid.Code, StatusCodes.Status400BadRequest),
        new(ReservationsApplicationErrors.ManagementOperationConflict.Code, StatusCodes.Status409Conflict),
        new(ReservationsApplicationErrors.InventoryUnitNotFound.Code, StatusCodes.Status409Conflict),
        new(ReservationsApplicationErrors.InventoryUnitPropertyMismatch.Code, StatusCodes.Status400BadRequest),
        new(ReservationsApplicationErrors.ExpectedStayTimeInvalid.Code, StatusCodes.Status400BadRequest),
        new(ReservationsApplicationErrors.SourceInvalid.Code, StatusCodes.Status400BadRequest),
        new(ReservationsApplicationErrors.VersionConflict.Code, StatusCodes.Status409Conflict),
        new(ReservationsApplicationErrors.DetailsRevisionConflict.Code, StatusCodes.Status409Conflict),
        new(ReservationsApplicationErrors.DetailsChangeProvenanceInvalid.Code, StatusCodes.Status400BadRequest),
        new(ReservationsApplicationErrors.DataRightsApprovalRequired.Code, StatusCodes.Status409Conflict),
        new(ReservationsApplicationErrors.CorrectionRequestInvalid.Code, StatusCodes.Status400BadRequest),
        new(ReservationsApplicationErrors.CorrectionIdempotencyConflict.Code, StatusCodes.Status409Conflict),
        new(ReservationsApplicationErrors.CorrectionNoChanges.Code, StatusCodes.Status409Conflict),
        new(ReservationsApplicationErrors.AllocationAmendmentInProgress.Code, StatusCodes.Status409Conflict),
        new(ReservationsApplicationErrors.AllocationAmendmentInvalid.Code, StatusCodes.Status409Conflict),
        new(ReservationsApplicationErrors.StayAmendmentOperationNotFound.Code, StatusCodes.Status404NotFound),
        new(ReservationsApplicationErrors.StayAmendmentOperationConflict.Code, StatusCodes.Status409Conflict),
        new(ReservationsApplicationErrors.StayAmendmentOperationVersionConflict.Code, StatusCodes.Status409Conflict),
        new(ReservationsApplicationErrors.StayAmendmentReconcileInvalid.Code, StatusCodes.Status409Conflict),
        new(ReservationsApplicationErrors.StayAmendmentReconcileTooSoon.Code, StatusCodes.Status429TooManyRequests),
        new(ReservationsApplicationErrors.StayAmendmentRequestInvalid.Code, StatusCodes.Status400BadRequest),
        new(ReservationsApplicationErrors.StayBusinessDateInvalid.Code, StatusCodes.Status400BadRequest),
        new(ReservationsApplicationErrors.StayProvenanceInvalid.Code, StatusCodes.Status400BadRequest),
        new(ReservationsApplicationErrors.GuestNotLinkable.Code, StatusCodes.Status409Conflict),
        new(ReservationsApplicationErrors.ReservationGuestLinkInvalid.Code, StatusCodes.Status400BadRequest),
        new(ReservationsApplicationErrors.ReservationGuestRoleOccupied.Code, StatusCodes.Status409Conflict),
        new(ReservationsApplicationErrors.InvalidTransition.Code, StatusCodes.Status409Conflict));

    private static ApiErrorStatusCodeMap CreateErrorStatusCodes(params ApiErrorStatusCode[] entries) =>
        ApiErrorStatusCodeMap.Create(entries.Concat(
            ReservationsApplicationErrors.CountryPolicyDenials.Select(error =>
                new ApiErrorStatusCode(error.Code, StatusCodes.Status409Conflict))).ToArray());

    private static void MarkPersonalDataResponse(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.Expires = "0";
    }

    private static RouteHandlerBuilder RequireAssuranceWhenConfigured(
        RouteHandlerBuilder endpoint,
        AuthenticationAssuranceRequirement? requirement) =>
        requirement is null
            ? endpoint
            : endpoint.RequireAuthenticationAssurance(requirement);

    private static string? ResolveActor(HttpContext context, IAccessHttpSubjectResolver subjectResolver)
    {
        Gma.Framework.AccessControl.AccessSubject? subject = subjectResolver.ResolveSubject(context);
        return subject is null
            ? null
            : $"{Gma.Framework.AccessControl.AccessSubjectKindNames.GetName(subject.Kind)}:{subject.Id}";
    }

    private static ReservationStayAmendmentRecoveryCursorDto? CreateStayAmendmentRecoveryCursor(
        ReservationStayAmendmentOutcome? outcome,
        DateTimeOffset? updatedAtUtc,
        Guid? operationId,
        Guid? reservationId) =>
        outcome is null && updatedAtUtc is null && operationId is null && reservationId is null
            ? null
            : new ReservationStayAmendmentRecoveryCursorDto(
                outcome ?? ReservationStayAmendmentOutcome.Unknown,
                updatedAtUtc ?? default,
                operationId ?? Guid.Empty,
                reservationId ?? Guid.Empty);

    private const int StayAmendmentRecoveryDefaultPageSize = 50;
}
