namespace BunkFy.Modules.Properties.AdminApi;

using BunkFy.Modules.Properties.Admin.Contracts;
using BunkFy.Modules.Properties.Application;
using BunkFy.Modules.Properties.Application.Commands;
using BunkFy.Modules.Properties.Application.Queries;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Errors;
using BunkFy.Modules.Properties.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Gma.Framework.Administration;
using Gma.Framework.Administration.Api;
using Gma.Framework.Api.Observability;
using Gma.Framework.Api.Results;
using Gma.Framework.Cqrs;
using Gma.Framework.ModuleComposition;
using Gma.Framework.Pagination;
using Gma.Framework.Results;

public sealed class PropertiesAdminApiModule : IAdminApiModule
{
    public string Name => PropertiesModuleMetadata.Name;

    public void AddServices(IHostApplicationBuilder builder)
    {
        builder.SelectModuleProfile(PropertiesProfiles.Default, "BunkFy.Modules.Properties.AdminApi");
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IStartupFilter, PropertiesAdminNoStoreStartupFilter>());
        builder.Services.AddPropertiesApplication();
        builder.AddPropertiesPersistence();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder properties = endpoints.MapGroup("/api/admin/properties")
            .WithModuleName(this.Name)
            .WithTags("Properties Admin")
            .RequireAuthorization();
        properties.AddEndpointFilter(SensitiveResponseFilter);

        properties.MapGet("/", async (
            int? page,
            int? pageSize,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(PropertiesAdminOperationNames.PropertiesList, PropertiesAdminPermissions.Read),
                requireTenant: true,
                token => dispatcher.QueryAsync(
                    new ListPropertiesQuery(page ?? PageRequest.DefaultPage, pageSize ?? PageRequest.DefaultPageSize),
                    token),
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<PropertyListResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        properties.MapGet("/time-zones/catalog", async (
            string? search,
            string? countryCode,
            string? cursor,
            int? pageSize,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(
                    PropertiesAdminOperationNames.TimeZonesCatalog,
                    PropertiesAdminPermissions.Read),
                requireTenant: true,
                token => dispatcher.QueryAsync(
                    new ListPropertyTimeZoneCatalogQuery(
                        search,
                        countryCode,
                        cursor,
                        pageSize ?? 50),
                    token),
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<PropertyTimeZoneCatalogPageDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        properties.MapGet("/{propertyId:guid}/time-zones/catalog", async (
            Guid propertyId,
            string? search,
            string? countryCode,
            string? cursor,
            int? pageSize,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(
                    PropertiesAdminOperationNames.TimeZonesCatalog,
                    PropertiesAdminPermissions.Read),
                requireTenant: true,
                token => dispatcher.QueryAsync(
                    new ListPropertyTimeZoneCatalogQuery(
                        search,
                        countryCode,
                        cursor,
                        pageSize ?? 50),
                    token),
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<PropertyTimeZoneCatalogPageDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        properties.MapGet("/time-zones/compliance", async (
            string? cursor,
            int? pageSize,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(
                    PropertiesAdminOperationNames.TimeZoneComplianceList,
                    PropertiesAdminPermissions.TimeZonesManage),
                requireTenant: true,
                token => dispatcher.QueryAsync(
                    new ListPropertyTimeZoneComplianceQuery(cursor, pageSize ?? 50),
                    token),
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<PropertyTimeZoneCompliancePageDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        properties.MapGet("/{propertyId:guid}", async (
            Guid propertyId,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(PropertiesAdminOperationNames.PropertiesGet, PropertiesAdminPermissions.Read),
                requireTenant: true,
                token => dispatcher.QueryAsync(new GetPropertyQuery(propertyId), token),
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<PropertyDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        properties.MapGet("/{propertyId:guid}/time-zone/operations/{operationId:guid}", async (
            Guid propertyId,
            Guid operationId,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(
                    PropertiesAdminOperationNames.PropertyTimeZoneOperationGet,
                    PropertiesAdminPermissions.TimeZonesManage),
                requireTenant: true,
                token => dispatcher.QueryAsync(
                    new GetPropertyTimeZoneRecoveryQuery(propertyId, operationId),
                    token),
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<PropertyTimeZoneRecoveryDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        properties.MapPut("/{propertyId:guid}/time-zone", async (
            Guid propertyId,
            SetPropertyTimeZoneRequest request,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(
                    PropertiesAdminOperationNames.PropertyTimeZoneSet,
                    PropertiesAdminPermissions.TimeZonesManage),
                requireTenant: true,
                token =>
                {
                    Result<string> actor = ResolveAdminActor(
                        httpContext.RequestServices.GetRequiredService<IAdminActorContext>());
                    return actor.IsFailure
                        ? Task.FromResult(Result.Failure<SetPropertyTimeZoneReceiptDto>(
                            actor.Error))
                        : dispatcher.SendAsync(
                            new SetPropertyTimeZoneCommand(
                                propertyId,
                                request.OperationId,
                                request.TimeZoneId,
                                request.Confirmed,
                                request.ExpectedVersion,
                                actor.Value),
                            token);
                },
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<SetPropertyTimeZoneReceiptDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        properties.MapPost("/", async (
            PropertyCreateRequest request,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(PropertiesAdminOperationNames.PropertiesCreate, PropertiesAdminPermissions.PropertiesManage),
                requireTenant: true,
                token =>
                {
                    Result<string> actor = ResolveAdminActor(
                        httpContext.RequestServices.GetRequiredService<IAdminActorContext>());
                    return actor.IsFailure
                        ? Task.FromResult(Result.Failure<PropertyMutationReceiptDto>(
                            actor.Error))
                        : dispatcher.SendAsync(
                            new CreatePropertyCommand(
                                request.OperationId,
                                request.Name,
                                request.Code,
                                request.TimeZoneId,
                                actor.Value),
                            token);
                },
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<PropertyMutationReceiptDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        properties.MapPut("/{propertyId:guid}", async (
            Guid propertyId,
            PropertyUpdateRequest request,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(PropertiesAdminOperationNames.PropertiesUpdate, PropertiesAdminPermissions.PropertiesManage),
                requireTenant: true,
                token => dispatcher.SendAsync(
                    new UpdatePropertyCommand(
                        propertyId,
                        request.OperationId,
                        request.Name,
                        request.Code,
                        request.TimeZoneId,
                        request.ExpectedVersion),
                    token),
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<PropertyMutationReceiptDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        properties.MapPost("/{propertyId:guid}/retire", async (
            Guid propertyId,
            RetirePropertyRequest request,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(PropertiesAdminOperationNames.PropertiesRetire, PropertiesAdminPermissions.PropertiesManage),
                requireTenant: true,
                token => dispatcher.SendAsync(
                    new RetirePropertyCommand(
                        propertyId,
                        request.OperationId,
                        request.Confirmed,
                        request.ExpectedVersion),
                    token),
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<PropertyMutationReceiptDto>(StatusCodes.Status200OK);

        properties.MapGet("/{propertyId:guid}/rooms", async (
            Guid propertyId,
            int? page,
            int? pageSize,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(PropertiesAdminOperationNames.RoomsList, PropertiesAdminPermissions.Read),
                requireTenant: true,
                token => dispatcher.QueryAsync(
                    new ListRoomsQuery(propertyId, page ?? PageRequest.DefaultPage, pageSize ?? PageRequest.DefaultPageSize),
                    token),
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<RoomListResponse>(StatusCodes.Status200OK);

        properties.MapPost("/{propertyId:guid}/rooms", async (
            Guid propertyId,
            RoomCreateRequest request,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(PropertiesAdminOperationNames.RoomsCreate, PropertiesAdminPermissions.RoomsManage),
                requireTenant: true,
                token => dispatcher.SendAsync(
                    new CreateRoomCommand(
                        request.OperationId,
                        propertyId,
                        request.ExpectedPropertyVersion,
                        request.Name,
                        request.BuildingLabel,
                        request.FloorLabel),
                    token),
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<RoomMutationReceiptDto>(StatusCodes.Status200OK);

        properties.MapGet("/{propertyId:guid}/rooms/{roomId:guid}", async (
            Guid propertyId,
            Guid roomId,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(PropertiesAdminOperationNames.RoomsGet, PropertiesAdminPermissions.Read),
                requireTenant: true,
                token => dispatcher.QueryAsync(new GetRoomQuery(propertyId, roomId), token),
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<RoomDto>(StatusCodes.Status200OK);

        properties.MapPut("/{propertyId:guid}/rooms/{roomId:guid}", async (
            Guid propertyId,
            Guid roomId,
            RoomUpdateRequest request,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(PropertiesAdminOperationNames.RoomsUpdate, PropertiesAdminPermissions.RoomsManage),
                requireTenant: true,
                token => dispatcher.SendAsync(
                    new UpdateRoomCommand(
                        request.OperationId,
                        propertyId,
                        roomId,
                        request.ExpectedVersion,
                        request.Name,
                        request.BuildingLabel,
                        request.FloorLabel),
                    token),
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<RoomMutationReceiptDto>(StatusCodes.Status200OK);

        properties.MapPost("/{propertyId:guid}/rooms/{roomId:guid}/retire", async (
            Guid propertyId,
            Guid roomId,
            RetireRoomRequest request,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(PropertiesAdminOperationNames.RoomsRetire, PropertiesAdminPermissions.RoomsManage),
                requireTenant: true,
                token => request.Confirmed
                    ? dispatcher.SendAsync(
                        new RetireRoomCommand(propertyId, roomId, request.ExpectedVersion, request.CascadeBeds),
                        token)
                    : Task.FromResult(Result.Failure<Unit>(AdminErrors.ConfirmationRequired)),
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces(StatusCodes.Status204NoContent);

        properties.MapGet("/{propertyId:guid}/rooms/{roomId:guid}/beds", async (
            Guid propertyId,
            Guid roomId,
            int? page,
            int? pageSize,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(PropertiesAdminOperationNames.BedsList, PropertiesAdminPermissions.Read),
                requireTenant: true,
                token => dispatcher.QueryAsync(
                    new ListBedsQuery(propertyId, roomId, page ?? PageRequest.DefaultPage, pageSize ?? PageRequest.DefaultPageSize),
                    token),
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<BedListResponse>(StatusCodes.Status200OK);

        properties.MapPost("/{propertyId:guid}/rooms/{roomId:guid}/beds", async (
            Guid propertyId,
            Guid roomId,
            BedWriteRequest request,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(PropertiesAdminOperationNames.BedsAdd, PropertiesAdminPermissions.BedsManage),
                requireTenant: true,
                token => dispatcher.SendAsync(
                    new AddBedCommand(
                        request.OperationId,
                        propertyId,
                        roomId,
                        request.ExpectedRoomVersion,
                        request.Label),
                    token),
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<BedMutationReceiptDto>(StatusCodes.Status200OK);

        properties.MapPost("/{propertyId:guid}/rooms/{roomId:guid}/beds/batch", async (
            Guid propertyId,
            Guid roomId,
            BedBatchWriteRequest request,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(PropertiesAdminOperationNames.BedsAddBatch, PropertiesAdminPermissions.BedsManage),
                requireTenant: true,
                token => dispatcher.SendAsync(
                    new AddBedsCommand(
                        request.OperationId,
                        propertyId,
                        roomId,
                        request.ExpectedRoomVersion,
                        request.Labels),
                    token),
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<BedBatchMutationReceiptDto>(StatusCodes.Status200OK);

        properties.MapPut("/{propertyId:guid}/rooms/{roomId:guid}/beds/{bedId:guid}", async (
            Guid propertyId,
            Guid roomId,
            Guid bedId,
            BedWriteRequest request,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(PropertiesAdminOperationNames.BedsUpdate, PropertiesAdminPermissions.BedsManage),
                requireTenant: true,
                token => dispatcher.SendAsync(
                    new UpdateBedCommand(
                        request.OperationId,
                        propertyId,
                        roomId,
                        bedId,
                        request.ExpectedRoomVersion,
                        request.Label),
                    token),
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<BedMutationReceiptDto>(StatusCodes.Status200OK);

        properties.MapPost("/{propertyId:guid}/rooms/{roomId:guid}/beds/{bedId:guid}/retire", async (
            Guid propertyId,
            Guid roomId,
            Guid bedId,
            RetireBedRequest request,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(PropertiesAdminOperationNames.BedsRetire, PropertiesAdminPermissions.BedsManage),
                requireTenant: true,
                token => request.Confirmed
                    ? dispatcher.SendAsync(
                        new RetireBedCommand(propertyId, roomId, bedId, request.ExpectedRoomVersion),
                        token)
                    : Task.FromResult(Result.Failure<Unit>(AdminErrors.ConfirmationRequired)),
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces(StatusCodes.Status204NoContent);
    }

    public sealed record PropertyCreateRequest(
        Guid OperationId,
        string Name,
        string Code,
        string TimeZoneId);
    public sealed record PropertyUpdateRequest(
        Guid OperationId,
        string Name,
        string Code,
        string? TimeZoneId,
        long ExpectedVersion);
    public sealed record SetPropertyTimeZoneRequest(
        Guid OperationId,
        string TimeZoneId,
        bool Confirmed,
        long ExpectedVersion);
    public sealed record RetirePropertyRequest(
        Guid OperationId,
        bool Confirmed,
        long ExpectedVersion);
    public sealed record RoomCreateRequest(
        Guid OperationId,
        string Name,
        long ExpectedPropertyVersion,
        string? BuildingLabel = null,
        string? FloorLabel = null);
    public sealed record RoomUpdateRequest(
        Guid OperationId,
        string Name,
        long ExpectedVersion,
        string? BuildingLabel = null,
        string? FloorLabel = null);
    public sealed record RetireRoomRequest(bool Confirmed, long ExpectedVersion, bool CascadeBeds = false);
    public sealed record BedWriteRequest(Guid OperationId, string Label, long ExpectedRoomVersion);
    public sealed record BedBatchWriteRequest(
        Guid OperationId,
        IReadOnlyCollection<string> Labels,
        long ExpectedRoomVersion);
    public sealed record RetireBedRequest(bool Confirmed, long ExpectedRoomVersion);

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

    private static Result<string> ResolveAdminActor(IAdminActorContext actorContext)
    {
        ArgumentNullException.ThrowIfNull(actorContext);
        if (actorContext.Actor is not { } actor)
        {
            return Result.Failure<string>(AdminErrors.Unauthorized);
        }

        string actorId = $"admin-api:{actor.Id}";
        return IsValidPropertiesActor(actorId)
            ? Result.Success(actorId)
            : Result.Failure<string>(PropertiesDomainErrors.ActorIdInvalid);
    }

    private static bool IsValidPropertiesActor(string actorId) =>
        actorId.Length <= PropertiesContractLimits.ActorIdMaxLength &&
        !actorId.Any(char.IsControl);

    private static readonly ApiErrorStatusCodeMap AdminErrorStatusCodes = CreateErrorStatusCodes(
        new(AdminErrors.Unauthorized.Code, StatusCodes.Status401Unauthorized),
        new(PropertiesDomainErrors.ActorIdInvalid.Code, StatusCodes.Status400BadRequest),
        new(PropertiesDomainErrors.TimeZoneRequired.Code, StatusCodes.Status400BadRequest),
        new(PropertiesDomainErrors.TimeZoneTooLong.Code, StatusCodes.Status400BadRequest),
        new(PropertiesDomainErrors.TimeZoneInvalid.Code, StatusCodes.Status400BadRequest),
        new(PropertiesApplicationErrors.CreationOperationInvalid.Code, StatusCodes.Status400BadRequest),
        new(PropertiesApplicationErrors.ManagementOperationInvalid.Code, StatusCodes.Status400BadRequest),
        new(PropertiesApplicationErrors.ConfirmationRequired.Code, StatusCodes.Status400BadRequest),
        new(PropertiesApplicationErrors.TimeZoneQueryInvalid.Code, StatusCodes.Status400BadRequest),
        new(PropertiesApplicationErrors.BedBatchRequired.Code, StatusCodes.Status400BadRequest),
        new(PropertiesApplicationErrors.BedBatchTooLarge.Code, StatusCodes.Status400BadRequest),
        new(PropertiesApplicationErrors.PropertyNotFound.Code, StatusCodes.Status404NotFound),
        new(PropertiesApplicationErrors.TimeZoneOperationNotFound.Code, StatusCodes.Status404NotFound),
        new(PropertiesApplicationErrors.RoomNotFound.Code, StatusCodes.Status404NotFound),
        new(PropertiesApplicationErrors.BedNotFound.Code, StatusCodes.Status404NotFound),
        new(PropertiesApplicationErrors.PropertyCodeAlreadyExists.Code, StatusCodes.Status409Conflict),
        new(PropertiesApplicationErrors.CreationOperationConflict.Code, StatusCodes.Status409Conflict),
        new(PropertiesApplicationErrors.ManagementOperationConflict.Code, StatusCodes.Status409Conflict),
        new(PropertiesApplicationErrors.TimeZoneDedicatedOperationRequired.Code, StatusCodes.Status409Conflict),
        new(PropertiesApplicationErrors.RoomAlreadyExists.Code, StatusCodes.Status409Conflict),
        new(PropertiesApplicationErrors.BedAlreadyExists.Code, StatusCodes.Status409Conflict),
        new(PropertiesApplicationErrors.PropertyStatusUnknown.Code, StatusCodes.Status409Conflict),
        new(PropertiesApplicationErrors.PropertyAlreadyRetired.Code, StatusCodes.Status409Conflict),
        new(PropertiesApplicationErrors.PropertyRetired.Code, StatusCodes.Status409Conflict),
        new(PropertiesApplicationErrors.ProcessingLifecycleRestricted.Code, StatusCodes.Status423Locked),
        new(PropertiesApplicationErrors.ProcessingLifecycleAdmissionUnavailable.Code, StatusCodes.Status503ServiceUnavailable),
        new(PropertiesApplicationErrors.WorkspaceProcessingRestricted.Code, StatusCodes.Status423Locked),
        new(PropertiesApplicationErrors.WorkspaceProcessingAdmissionUnavailable.Code, StatusCodes.Status503ServiceUnavailable),
        new(PropertiesApplicationErrors.TimeSourceUnavailable.Code, StatusCodes.Status503ServiceUnavailable),
        new(PropertiesApplicationErrors.TimeZoneRuntimeUnavailable.Code, StatusCodes.Status503ServiceUnavailable),
        new(PropertiesApplicationErrors.PropertyHasActiveRooms.Code, StatusCodes.Status409Conflict),
        new(PropertiesApplicationErrors.VersionConflict.Code, StatusCodes.Status409Conflict),
        new(PropertiesApplicationErrors.RoomStatusUnknown.Code, StatusCodes.Status409Conflict),
        new(PropertiesApplicationErrors.RoomRetired.Code, StatusCodes.Status409Conflict),
        new(PropertiesApplicationErrors.RoomHasActiveBeds.Code, StatusCodes.Status409Conflict),
        new(PropertiesApplicationErrors.BedStatusUnknown.Code, StatusCodes.Status409Conflict),
        new(PropertiesApplicationErrors.BedAlreadyRetired.Code, StatusCodes.Status409Conflict),
        new(PropertiesApplicationErrors.BedRetirementRequiresInventory.Code, StatusCodes.Status409Conflict),
        new(PropertiesApplicationErrors.RoomRetirementRequiresInventory.Code, StatusCodes.Status409Conflict));

    private static ApiErrorStatusCodeMap CreateErrorStatusCodes(params ApiErrorStatusCode[] entries) =>
        ApiErrorStatusCodeMap.Create(entries.Concat(
            PropertiesApplicationErrors.CountryPolicyDenials.Select(error =>
                new ApiErrorStatusCode(error.Code, StatusCodes.Status409Conflict))).ToArray());
}
