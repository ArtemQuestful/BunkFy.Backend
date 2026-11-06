namespace BunkFy.Modules.Reservations.AdminApi;

using System.Security.Claims;
using Gma.Framework.Administration;
using Gma.Framework.Administration.Api;
using Gma.Framework.Api.Observability;
using Gma.Framework.Api.Results;
using Gma.Framework.Cqrs;
using Gma.Framework.ModuleComposition;
using Gma.Framework.Pagination;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using BunkFy.Modules.Reservations.Admin.Contracts;
using BunkFy.Modules.Reservations.Application;
using BunkFy.Modules.Reservations.Application.Commands;
using BunkFy.Modules.Reservations.Application.Queries;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Persistence;

public sealed class ReservationsAdminApiModule : IAdminApiModule
{
    public string Name => ReservationsModuleMetadata.Name;

    public void AddServices(IHostApplicationBuilder builder)
    {
        builder.SelectModuleProfile(ReservationsProfiles.Default, "BunkFy.Modules.Reservations.AdminApi");
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IStartupFilter, ReservationsAdminNoStoreStartupFilter>());
        builder.Services.AddReservationsApplication();
        builder.AddReservationsPersistence();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder group = endpoints.MapGroup("/api/admin/reservations/properties/{propertyId:guid}")
            .WithModuleName(this.Name)
            .WithTags("Reservations Admin")
            .RequireAuthorization();

        group.MapGet("/operations-snapshot", async (
            Guid propertyId,
            [AsParameters] OperationsSnapshotRequest request,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            MarkPersonalDataResponse(httpContext);
            return await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(
                    ReservationsAdminOperationNames.OperationsSnapshot,
                    ReservationsAdminPermissions.Read),
                requireTenant: true,
                token => dispatcher.QueryAsync(
                    new GetReservationOperationsSnapshotQuery(
                        propertyId,
                        request.LocalDate,
                        request.UpcomingLimit ?? ReservationsContractLimits.DefaultOperationsSnapshotUpcomingLimit),
                    token),
                cancellationToken,
                errorStatusCodes: ErrorStatusCodes).ConfigureAwait(false);
        })
            .Produces<ReservationOperationsSnapshotDto>(StatusCodes.Status200OK);

        group.MapGet("", async (
            Guid propertyId,
            ReservationStatus? status,
            DateOnly? overlapsFrom,
            DateOnly? overlapsTo,
            int? page,
            int? pageSize,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            MarkPersonalDataResponse(httpContext);
            return await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(ReservationsAdminOperationNames.List, ReservationsAdminPermissions.Read),
                requireTenant: true,
                token => dispatcher.QueryAsync(
                    new ListReservationsQuery(
                        propertyId,
                        status.HasValue ? [status.Value] : null,
                        null,
                        ReservationListOrder.CreatedDescending,
                        page ?? PageRequest.DefaultPage,
                        pageSize ?? PageRequest.DefaultPageSize,
                        overlapsFrom,
                        overlapsTo),
                    token),
                cancellationToken,
                errorStatusCodes: ErrorStatusCodes).ConfigureAwait(false);
        })
            .Produces<ReservationListResponse>(StatusCodes.Status200OK);

        group.MapGet("/stay-amendments/recovery", async (
            Guid propertyId,
            ReservationStayAmendmentOutcome? cursorOutcome,
            DateTimeOffset? cursorUpdatedAtUtc,
            Guid? cursorOperationId,
            Guid? cursorReservationId,
            int? pageSize,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            MarkPersonalDataResponse(httpContext);
            return await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(
                    ReservationsAdminOperationNames.ListStayAmendmentRecovery,
                    ReservationsAdminPermissions.Read),
                requireTenant: true,
                token => dispatcher.QueryAsync(
                    new ListReservationStayAmendmentRecoveryQuery(
                        propertyId,
                        CreateStayAmendmentRecoveryCursor(
                            cursorOutcome,
                            cursorUpdatedAtUtc,
                            cursorOperationId,
                            cursorReservationId),
                        pageSize ?? StayAmendmentRecoveryDefaultPageSize),
                    token),
                cancellationToken,
                errorStatusCodes: ErrorStatusCodes).ConfigureAwait(false);
        })
            .Produces<ReservationStayAmendmentRecoveryPageDto>(StatusCodes.Status200OK);

        group.MapGet("/{reservationId:guid}", async (
            Guid propertyId,
            Guid reservationId,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            MarkPersonalDataResponse(httpContext);
            return await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(ReservationsAdminOperationNames.Get, ReservationsAdminPermissions.Read),
                requireTenant: true,
                token => dispatcher.QueryAsync(new GetReservationQuery(propertyId, reservationId), token),
                cancellationToken,
                errorStatusCodes: ErrorStatusCodes).ConfigureAwait(false);
        })
            .Produces<ReservationDto>(StatusCodes.Status200OK);

        group.MapGet("/{reservationId:guid}/details-history", async (
            Guid propertyId,
            Guid reservationId,
            int? page,
            int? pageSize,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            MarkPersonalDataResponse(httpContext);
            return await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(ReservationsAdminOperationNames.History, ReservationsAdminPermissions.Read),
                requireTenant: true,
                token => dispatcher.QueryAsync(
                    new GetReservationDetailsHistoryQuery(
                        propertyId,
                        reservationId,
                        page ?? PageRequest.DefaultPage,
                        pageSize ?? PageRequest.DefaultPageSize),
                    token),
                cancellationToken,
                errorStatusCodes: ErrorStatusCodes).ConfigureAwait(false);
        })
            .Produces<ReservationDetailsHistoryListResponse>(StatusCodes.Status200OK);

        group.MapGet("/{reservationId:guid}/stay-amendments/{operationId:guid}", async (
            Guid propertyId,
            Guid reservationId,
            Guid operationId,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            MarkPersonalDataResponse(httpContext);
            return await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(
                    ReservationsAdminOperationNames.GetStayAmendment,
                    ReservationsAdminPermissions.Read),
                requireTenant: true,
                token => dispatcher.QueryAsync(
                    new GetReservationStayAmendmentQuery(propertyId, reservationId, operationId),
                    token),
                cancellationToken,
                errorStatusCodes: ErrorStatusCodes).ConfigureAwait(false);
        })
            .Produces<ReservationStayAmendmentReceiptDto>(StatusCodes.Status200OK);

        group.MapPost("", async (
            Guid propertyId,
            CreateReservationRequest request,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            return await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(ReservationsAdminOperationNames.Create, ReservationsAdminPermissions.Create),
                requireTenant: true,
                token => dispatcher.SendAsync(
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
                        request.ExpectedDepartureTime),
                    token),
                cancellationToken,
                errorStatusCodes: ErrorStatusCodes).ConfigureAwait(false);
        })
            .Produces<ReservationMutationReceiptDto>(StatusCodes.Status200OK);

        group.MapPost("/{reservationId:guid}/cancel", async (
            Guid propertyId,
            Guid reservationId,
            CancelReservationRequest request,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            return await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(ReservationsAdminOperationNames.Cancel, ReservationsAdminPermissions.Cancel),
                requireTenant: true,
                token => dispatcher.SendAsync(
                    new CancelReservationCommand(
                        request.OperationId,
                        propertyId,
                        reservationId,
                        request.ExpectedVersion),
                    token),
                cancellationToken,
                errorStatusCodes: ErrorStatusCodes).ConfigureAwait(false);
        })
            .Produces<ReservationMutationReceiptDto>(StatusCodes.Status200OK);

        group.MapPut("/{reservationId:guid}/guests", async (
            Guid propertyId,
            Guid reservationId,
            LinkReservationGuestRequest request,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(ReservationsAdminOperationNames.LinkGuest, ReservationsAdminPermissions.ManageGuests),
                requireTenant: true,
                token => dispatcher.SendAsync(
                    new LinkReservationGuestCommand(
                        propertyId,
                        reservationId,
                        request.GuestId,
                        request.Role,
                        request.ReplaceExistingRole,
                        request.ExpectedVersion,
                        Actor(httpContext)),
                    token),
                cancellationToken,
                errorStatusCodes: ErrorStatusCodes).ConfigureAwait(false))
            .Produces<ReservationMutationReceiptDto>(StatusCodes.Status200OK);

        group.MapPut("/{reservationId:guid}/inventory", async (
            Guid propertyId,
            Guid reservationId,
            ReassignReservationInventoryRequest request,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(
                    ReservationsAdminOperationNames.ReassignInventory,
                    ReservationsAdminPermissions.Manage),
                requireTenant: true,
                token => dispatcher.SendAsync(
                    new ReassignReservationInventoryCommand(
                        propertyId,
                        reservationId,
                        request.AmendmentRequestId,
                        request.InventoryUnitIds,
                        request.ExpectedDetailsRevision,
                        Actor(httpContext)),
                    token),
                cancellationToken,
                errorStatusCodes: ErrorStatusCodes).ConfigureAwait(false))
            .Produces<ReservationMutationReceiptDto>(StatusCodes.Status200OK);

        group.MapPut("/{reservationId:guid}/stay-amendments/{operationId:guid}", async (
            Guid propertyId,
            Guid reservationId,
            Guid operationId,
            AmendReservationStayRequest request,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            MarkPersonalDataResponse(httpContext);
            return await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(
                    ReservationsAdminOperationNames.AmendStay,
                    ReservationsAdminPermissions.Manage),
                requireTenant: true,
                token => request.Confirmed
                    ? dispatcher.SendAsync(
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
                            Actor(httpContext)),
                        token)
                    : Task.FromResult(
                        Gma.Framework.Results.Result.Failure<ReservationStayAmendmentReceiptDto>(
                            AdminErrors.ConfirmationRequired)),
                cancellationToken,
                errorStatusCodes: ErrorStatusCodes).ConfigureAwait(false);
        })
            .Produces<ReservationStayAmendmentReceiptDto>(StatusCodes.Status200OK);

        group.MapPost("/{reservationId:guid}/stay-amendments/{operationId:guid}/reconcile", async (
            Guid propertyId,
            Guid reservationId,
            Guid operationId,
            ReconcileReservationStayAmendmentRequest request,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            MarkPersonalDataResponse(httpContext);
            return await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(
                    ReservationsAdminOperationNames.ReconcileStayAmendment,
                    ReservationsAdminPermissions.Manage),
                requireTenant: true,
                token => request.Confirmed
                    ? dispatcher.SendAsync(
                        new ReconcileReservationStayAmendmentCommand(
                            propertyId,
                            reservationId,
                            operationId,
                            request.ExpectedOperationVersion,
                            Actor(httpContext)),
                        token)
                    : Task.FromResult(
                        Gma.Framework.Results.Result.Failure<ReservationStayAmendmentReceiptDto>(
                            AdminErrors.ConfirmationRequired)),
                cancellationToken,
                errorStatusCodes: ErrorStatusCodes).ConfigureAwait(false);
        })
            .Produces<ReservationStayAmendmentReceiptDto>(StatusCodes.Status200OK);

        group.MapPost("/{reservationId:guid}/check-in", async (
            Guid propertyId,
            Guid reservationId,
            StayLifecycleRequest request,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(ReservationsAdminOperationNames.CheckIn, ReservationsAdminPermissions.CheckIn),
                requireTenant: true,
                token => request.Confirmed
                    ? dispatcher.SendAsync(
                        new CheckInReservationCommand(
                            request.OperationId,
                            propertyId,
                            reservationId,
                            request.BusinessDate,
                            request.ExpectedVersion,
                            Actor(httpContext)),
                        token)
                    : Task.FromResult(Gma.Framework.Results.Result.Failure<ReservationMutationReceiptDto>(AdminErrors.ConfirmationRequired)),
                cancellationToken,
                errorStatusCodes: ErrorStatusCodes).ConfigureAwait(false))
            .Produces<ReservationMutationReceiptDto>(StatusCodes.Status200OK);

        group.MapPost("/{reservationId:guid}/no-show", async (
            Guid propertyId,
            Guid reservationId,
            StayLifecycleRequest request,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(ReservationsAdminOperationNames.NoShow, ReservationsAdminPermissions.NoShow),
                requireTenant: true,
                token => request.Confirmed
                    ? dispatcher.SendAsync(
                        new MarkReservationNoShowCommand(
                            request.OperationId,
                            propertyId,
                            reservationId,
                            request.BusinessDate,
                            request.ExpectedVersion,
                            Actor(httpContext)),
                        token)
                    : Task.FromResult(Gma.Framework.Results.Result.Failure<ReservationMutationReceiptDto>(AdminErrors.ConfirmationRequired)),
                cancellationToken,
                errorStatusCodes: ErrorStatusCodes).ConfigureAwait(false))
            .Produces<ReservationMutationReceiptDto>(StatusCodes.Status200OK);

        group.MapPost("/{reservationId:guid}/check-out", async (
            Guid propertyId,
            Guid reservationId,
            StayLifecycleRequest request,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(ReservationsAdminOperationNames.CheckOut, ReservationsAdminPermissions.CheckOut),
                requireTenant: true,
                token => request.Confirmed
                    ? dispatcher.SendAsync(
                        new CheckOutReservationCommand(
                            request.OperationId,
                            propertyId,
                            reservationId,
                            request.BusinessDate,
                            request.ExpectedVersion,
                            Actor(httpContext)),
                        token)
                    : Task.FromResult(Gma.Framework.Results.Result.Failure<ReservationMutationReceiptDto>(AdminErrors.ConfirmationRequired)),
                cancellationToken,
                errorStatusCodes: ErrorStatusCodes).ConfigureAwait(false))
            .Produces<ReservationMutationReceiptDto>(StatusCodes.Status200OK);
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

    public sealed record StayLifecycleRequest(
        Guid OperationId,
        DateOnly BusinessDate,
        long ExpectedVersion,
        bool Confirmed);

    public sealed record OperationsSnapshotRequest(
        DateOnly? LocalDate,
        int? UpcomingLimit);

    public sealed record LinkReservationGuestRequest(
        Guid GuestId,
        ReservationGuestRoleKind Role,
        bool ReplaceExistingRole,
        long ExpectedVersion);

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
        long ExpectedDetailsRevision,
        bool Confirmed);

    public sealed record ReconcileReservationStayAmendmentRequest(
        long ExpectedOperationVersion,
        bool Confirmed);

    private static readonly ApiErrorStatusCodeMap ErrorStatusCodes = ApiErrorStatusCodeMap.Create(
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

    private static void MarkPersonalDataResponse(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.Expires = "0";
    }

    private static string Actor(HttpContext context)
    {
        string identity = context.User.FindFirst("sub")?.Value
            ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? context.User.Identity?.Name
            ?? $"authenticated:{context.User.Identity?.AuthenticationType ?? "unknown"}";
        return $"admin-api:{identity}";
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
