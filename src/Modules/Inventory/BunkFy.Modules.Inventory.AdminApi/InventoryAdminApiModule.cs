namespace BunkFy.Modules.Inventory.AdminApi;

using System.Security.Claims;
using BunkFy.Modules.Inventory.Admin.Contracts;
using BunkFy.Modules.Inventory.Application;
using BunkFy.Modules.Inventory.Application.Commands;
using BunkFy.Modules.Inventory.Application.Queries;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Persistence;
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

public sealed class InventoryAdminApiModule : IAdminApiModule
{
    public string Name => InventoryModuleMetadata.Name;

    public void AddServices(IHostApplicationBuilder builder)
    {
        builder.SelectModuleProfile(InventoryProfiles.Default, "BunkFy.Modules.Inventory.AdminApi");
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IStartupFilter, InventoryAdminNoStoreStartupFilter>());
        builder.Services.AddInventoryApplication();
        builder.AddInventoryPersistence();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder inventory = endpoints.MapGroup("/api/admin/inventory")
            .WithModuleName(this.Name)
            .WithTags("Inventory Admin")
            .RequireAuthorization();
        inventory.AddEndpointFilter(SensitiveResponseFilter);

        inventory.MapGet("/properties/{propertyId:guid}/rooms", async (
            Guid propertyId,
            int? page,
            int? pageSize,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(InventoryAdminOperationNames.RoomsList, InventoryAdminPermissions.Read),
                requireTenant: true,
                token => dispatcher.QueryAsync(
                    new ListRoomInventoryQuery(
                        propertyId,
                        page ?? PageRequest.DefaultPage,
                        pageSize ?? PageRequest.DefaultPageSize),
                    token),
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<RoomInventoryListResponse>(StatusCodes.Status200OK);

        inventory.MapPut("/properties/{propertyId:guid}/rooms/{roomId:guid}/sales-mode", async (
            Guid propertyId,
            Guid roomId,
            ConfigureSalesModeRequest request,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(InventoryAdminOperationNames.RoomsConfigure, InventoryAdminPermissions.Configure),
                requireTenant: true,
                token => dispatcher.SendAsync(
                    new ConfigureRoomSalesModeCommand(
                        request.OperationId,
                        propertyId,
                        roomId,
                        request.SalesMode,
                        request.ExpectedVersion),
                    token),
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<RoomInventoryMutationReceiptDto>(StatusCodes.Status200OK);

        inventory.MapGet("/properties/{propertyId:guid}/rooms/{roomId:guid}/change-impact", async (
            Guid propertyId,
            Guid roomId,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(InventoryAdminOperationNames.RoomsConfigure, InventoryAdminPermissions.Configure),
                requireTenant: true,
                token => dispatcher.QueryAsync(new GetRoomInventoryChangeImpactQuery(propertyId, roomId), token),
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<RoomInventoryChangeImpactDto>(StatusCodes.Status200OK);

        inventory.MapGet("/properties/{propertyId:guid}/availability", async (
            Guid propertyId,
            DateOnly arrival,
            DateOnly departure,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(InventoryAdminOperationNames.AvailabilityRead, InventoryAdminPermissions.Read),
                requireTenant: true,
                token => dispatcher.QueryAsync(new GetInventoryAvailabilityQuery(propertyId, arrival, departure), token),
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<InventoryAvailabilityResponse>(StatusCodes.Status200OK);

        inventory.MapGet("/properties/{propertyId:guid}/blocks", async (
            Guid propertyId,
            Guid? inventoryUnitId,
            bool? includeReleased,
            DateOnly? overlapsFrom,
            DateOnly? overlapsTo,
            int? page,
            int? pageSize,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(InventoryAdminOperationNames.BlocksList, InventoryAdminPermissions.Read),
                requireTenant: true,
                token => dispatcher.QueryAsync(
                    new ListManualInventoryBlocksQuery(
                        propertyId,
                        inventoryUnitId,
                        includeReleased ?? false,
                        page ?? PageRequest.DefaultPage,
                        pageSize ?? PageRequest.DefaultPageSize,
                        overlapsFrom,
                        overlapsTo),
                    token),
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<ManualInventoryBlockListResponse>(StatusCodes.Status200OK);

        inventory.MapPost("/properties/{propertyId:guid}/blocks", async (
            Guid propertyId,
            CreateManualBlockRequest request,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(InventoryAdminOperationNames.BlocksCreate, InventoryAdminPermissions.BlocksManage),
                requireTenant: true,
                token => dispatcher.SendAsync(
                    new CreateManualInventoryBlockCommand(
                        request.OperationId,
                        propertyId,
                        request.InventoryUnitId,
                        request.Arrival,
                        request.Departure,
                        request.Reason,
                        Actor(httpContext)),
                    token),
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<ManualInventoryBlockMutationReceiptDto>(StatusCodes.Status200OK);

        inventory.MapPost("/properties/{propertyId:guid}/block-groups/preview", async (
            Guid propertyId,
            PreviewManualBlockGroupRequest request,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(
                    InventoryAdminOperationNames.BlockGroupsPreview,
                    InventoryAdminPermissions.BlockGroupsManage),
                requireTenant: true,
                token => dispatcher.QueryAsync(
                    new PreviewManualInventoryBlockGroupQuery(
                        propertyId,
                        request.Target,
                        request.Arrival,
                        request.Departure,
                        request.Reason,
                        request.BlockGroupId,
                        request.ExpectedVersion),
                    token),
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<ManualInventoryBlockGroupSelectionPreviewDto>(StatusCodes.Status200OK);

        inventory.MapGet("/properties/{propertyId:guid}/block-groups", async (
            Guid propertyId,
            ManualInventoryBlockGroupStatus? status,
            string? cursor,
            int? pageSize,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(
                    InventoryAdminOperationNames.BlockGroupsList,
                    InventoryAdminPermissions.Read),
                requireTenant: true,
                token => dispatcher.QueryAsync(
                    new ListManualInventoryBlockGroupsQuery(
                        propertyId,
                        status,
                        cursor,
                        pageSize ?? PageRequest.DefaultPageSize),
                    token),
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<ManualInventoryBlockGroupListResponse>(StatusCodes.Status200OK);

        inventory.MapGet("/properties/{propertyId:guid}/block-groups/{blockGroupId:guid}", async (
            Guid propertyId,
            Guid blockGroupId,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(
                    InventoryAdminOperationNames.BlockGroupsGet,
                    InventoryAdminPermissions.Read),
                requireTenant: true,
                token => dispatcher.QueryAsync(
                    new GetManualInventoryBlockGroupQuery(
                        propertyId,
                        blockGroupId),
                    token),
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<ManualInventoryBlockGroupDto>(StatusCodes.Status200OK);

        inventory.MapGet("/properties/{propertyId:guid}/block-groups/{blockGroupId:guid}/members", async (
            Guid propertyId,
            Guid blockGroupId,
            ManualInventoryBlockStatus? status,
            string? cursor,
            int? pageSize,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(
                    InventoryAdminOperationNames.BlockGroupMembersList,
                    InventoryAdminPermissions.Read),
                requireTenant: true,
                token => dispatcher.QueryAsync(
                    new ListManualInventoryBlockGroupMembersQuery(
                        propertyId,
                        blockGroupId,
                        status,
                        cursor,
                        pageSize ?? PageRequest.DefaultPageSize),
                    token),
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<ManualInventoryBlockGroupMemberListResponse>(StatusCodes.Status200OK);

        inventory.MapPost("/properties/{propertyId:guid}/block-groups", async (
            Guid propertyId,
            CreateManualBlockGroupRequest request,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(
                    InventoryAdminOperationNames.BlockGroupsCreate,
                    InventoryAdminPermissions.BlockGroupsManage),
                requireTenant: true,
                token => dispatcher.SendAsync(
                    new CreateManualInventoryBlockGroupCommand(
                        request.OperationId,
                        propertyId,
                        request.Target,
                        request.Arrival,
                        request.Departure,
                        request.Reason,
                        request.ExpectedSelectionDigest,
                        request.ExpectedAffectedBlockCount,
                        request.Confirmed,
                        Actor(httpContext)),
                    token),
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<ManualInventoryBlockGroupMutationReceiptDto>(StatusCodes.Status200OK);

        inventory.MapPut("/properties/{propertyId:guid}/block-groups/{blockGroupId:guid}", async (
            Guid propertyId,
            Guid blockGroupId,
            ReplaceManualBlockGroupRequest request,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(
                    InventoryAdminOperationNames.BlockGroupsReplace,
                    InventoryAdminPermissions.BlockGroupsManage),
                requireTenant: true,
                token => dispatcher.SendAsync(
                    new ReplaceManualInventoryBlockGroupCommand(
                        request.OperationId,
                        propertyId,
                        blockGroupId,
                        request.ExpectedVersion,
                        request.Target,
                        request.Arrival,
                        request.Departure,
                        request.Reason,
                        request.ExpectedSelectionDigest,
                        request.ExpectedAffectedBlockCount,
                        request.Confirmed,
                        Actor(httpContext)),
                    token),
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<ManualInventoryBlockGroupMutationReceiptDto>(StatusCodes.Status200OK);

        inventory.MapPost("/properties/{propertyId:guid}/blocks/{blockId:guid}/release", async (
            Guid propertyId,
            Guid blockId,
            ReleaseManualBlockRequest request,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(
                    InventoryAdminOperationNames.BlocksRelease,
                    InventoryAdminPermissions.BlocksManage),
                requireTenant: true,
                token => dispatcher.SendAsync(
                    new ReleaseManualInventoryBlockCommand(
                        request.OperationId,
                        propertyId,
                        blockId,
                        request.ExpectedVersion,
                        Actor(httpContext)),
                    token),
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<ManualInventoryBlockMutationReceiptDto>(StatusCodes.Status200OK);

        inventory.MapPost("/properties/{propertyId:guid}/block-groups/{blockGroupId:guid}/release", async (
            Guid propertyId,
            Guid blockGroupId,
            ReleaseManualBlockGroupRequest request,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(
                    InventoryAdminOperationNames.BlockGroupsRelease,
                    InventoryAdminPermissions.BlockGroupsManage),
                requireTenant: true,
                token => dispatcher.SendAsync(
                    new ReleaseManualInventoryBlockGroupCommand(
                        request.OperationId,
                        propertyId,
                        blockGroupId,
                        request.ExpectedVersion,
                        request.Confirmed,
                        Actor(httpContext)),
                    token),
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<ManualInventoryBlockGroupMutationReceiptDto>(StatusCodes.Status200OK);

        inventory.MapGet("/properties/{propertyId:guid}/block-group-create-operations/{operationId:guid}", async (
            Guid propertyId,
            Guid operationId,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(
                    InventoryAdminOperationNames.BlockGroupCreateOperationsGet,
                    InventoryAdminPermissions.BlockGroupsManage),
                requireTenant: true,
                token => dispatcher.QueryAsync(
                    new GetManualInventoryBlockGroupCreateOperationQuery(
                        propertyId,
                        operationId),
                    token),
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<ManualInventoryBlockGroupOperationDto>(StatusCodes.Status200OK);

        inventory.MapGet("/properties/{propertyId:guid}/block-groups/{blockGroupId:guid}/operations/{operationId:guid}", async (
            Guid propertyId,
            Guid blockGroupId,
            Guid operationId,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(
                    InventoryAdminOperationNames.BlockGroupOperationsGet,
                    InventoryAdminPermissions.BlockGroupsManage),
                requireTenant: true,
                token => dispatcher.QueryAsync(
                    new GetManualInventoryBlockGroupOperationQuery(
                        propertyId,
                        blockGroupId,
                        operationId),
                    token),
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<ManualInventoryBlockGroupOperationDto>(StatusCodes.Status200OK);

        inventory.MapPost("/properties/{propertyId:guid}/rooms/{roomId:guid}/beds/{bedId:guid}/retirement", async (
            Guid propertyId,
            Guid roomId,
            Guid bedId,
            RequestBedRetirementRequest request,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(InventoryAdminOperationNames.BedRetirementsRequest, InventoryAdminPermissions.Retire),
                requireTenant: true,
                token => dispatcher.SendAsync(
                    new RequestBedRetirementCommand(
                        request.OperationId,
                        propertyId,
                        roomId,
                        bedId,
                        request.Confirmed,
                        request.Reason,
                        Actor(httpContext)),
                    token),
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<BedRetirementDto>(StatusCodes.Status200OK);

        inventory.MapGet("/properties/{propertyId:guid}/bed-retirements/{topologyChangeId:guid}", async (
            Guid propertyId,
            Guid topologyChangeId,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(InventoryAdminOperationNames.BedRetirementsGet, InventoryAdminPermissions.Retire),
                requireTenant: true,
                token => dispatcher.QueryAsync(new GetBedRetirementQuery(propertyId, topologyChangeId), token),
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<BedRetirementDto>(StatusCodes.Status200OK);

        inventory.MapPost("/properties/{propertyId:guid}/bed-retirements/{topologyChangeId:guid}/retry", async (
            Guid propertyId,
            Guid topologyChangeId,
            RetryRetirementRequest request,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(InventoryAdminOperationNames.BedRetirementsRetry, InventoryAdminPermissions.Retire),
                requireTenant: true,
                token => dispatcher.SendAsync(
                    new RetryBedRetirementCommand(
                        request.OperationId,
                        propertyId,
                        topologyChangeId,
                        request.ExpectedVersion),
                    token),
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<BedRetirementDto>(StatusCodes.Status200OK);

        inventory.MapPost("/properties/{propertyId:guid}/bed-retirements/{topologyChangeId:guid}/cancel", async (
            Guid propertyId,
            Guid topologyChangeId,
            CancelRetirementRequest request,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(InventoryAdminOperationNames.BedRetirementsCancel, InventoryAdminPermissions.Retire),
                requireTenant: true,
                token => dispatcher.SendAsync(
                    new CancelBedRetirementCommand(
                        request.OperationId,
                        propertyId,
                        topologyChangeId,
                        request.ExpectedVersion,
                        request.Confirmed,
                        request.Reason,
                        Actor(httpContext)),
                    token),
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<BedRetirementDto>(StatusCodes.Status200OK);

        inventory.MapPost("/properties/{propertyId:guid}/rooms/{roomId:guid}/retirement", async (
            Guid propertyId,
            Guid roomId,
            RequestRoomRetirementRequest request,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(InventoryAdminOperationNames.RoomRetirementsRequest, InventoryAdminPermissions.Retire),
                requireTenant: true,
                token => dispatcher.SendAsync(
                    new RequestRoomRetirementCommand(
                        request.OperationId,
                        propertyId,
                        roomId,
                        request.Confirmed,
                        request.Reason,
                        Actor(httpContext)),
                    token),
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<RoomRetirementDto>(StatusCodes.Status200OK);

        inventory.MapGet("/properties/{propertyId:guid}/room-retirements/{topologyChangeId:guid}", async (
            Guid propertyId,
            Guid topologyChangeId,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(InventoryAdminOperationNames.RoomRetirementsGet, InventoryAdminPermissions.Retire),
                requireTenant: true,
                token => dispatcher.QueryAsync(new GetRoomRetirementQuery(propertyId, topologyChangeId), token),
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<RoomRetirementDto>(StatusCodes.Status200OK);

        inventory.MapPost("/properties/{propertyId:guid}/room-retirements/{topologyChangeId:guid}/retry", async (
            Guid propertyId,
            Guid topologyChangeId,
            RetryRetirementRequest request,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(InventoryAdminOperationNames.RoomRetirementsRetry, InventoryAdminPermissions.Retire),
                requireTenant: true,
                token => dispatcher.SendAsync(
                    new RetryRoomRetirementCommand(
                        request.OperationId,
                        propertyId,
                        topologyChangeId,
                        request.ExpectedVersion),
                    token),
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<RoomRetirementDto>(StatusCodes.Status200OK);

        inventory.MapPost("/properties/{propertyId:guid}/room-retirements/{topologyChangeId:guid}/cancel", async (
            Guid propertyId,
            Guid topologyChangeId,
            CancelRetirementRequest request,
            HttpContext httpContext,
            AdminApiExecutor executor,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            await executor.ExecuteAsync(
                httpContext,
                AdminOperation.Create(InventoryAdminOperationNames.RoomRetirementsCancel, InventoryAdminPermissions.Retire),
                requireTenant: true,
                token => dispatcher.SendAsync(
                    new CancelRoomRetirementCommand(
                        request.OperationId,
                        propertyId,
                        topologyChangeId,
                        request.ExpectedVersion,
                        request.Confirmed,
                        request.Reason,
                        Actor(httpContext)),
                    token),
                cancellationToken,
                errorStatusCodes: AdminErrorStatusCodes).ConfigureAwait(false))
            .Produces<RoomRetirementDto>(StatusCodes.Status200OK);
    }

    public sealed record ConfigureSalesModeRequest(
        Guid OperationId,
        InventorySalesMode SalesMode,
        long ExpectedVersion);
    public sealed record CreateManualBlockRequest(
        Guid OperationId,
        Guid InventoryUnitId,
        DateOnly Arrival,
        DateOnly Departure,
        string Reason);
    public sealed record CreateManualBlockGroupRequest(
        Guid OperationId,
        InventoryBlockTarget Target,
        DateOnly Arrival,
        DateOnly Departure,
        string Reason,
        string ExpectedSelectionDigest,
        int ExpectedAffectedBlockCount,
        bool Confirmed);
    public sealed record PreviewManualBlockGroupRequest(
        InventoryBlockTarget Target,
        DateOnly Arrival,
        DateOnly Departure,
        string Reason,
        Guid? BlockGroupId = null,
        long? ExpectedVersion = null);
    public sealed record ReplaceManualBlockGroupRequest(
        Guid OperationId,
        long ExpectedVersion,
        InventoryBlockTarget Target,
        DateOnly Arrival,
        DateOnly Departure,
        string Reason,
        string ExpectedSelectionDigest,
        int ExpectedAffectedBlockCount,
        bool Confirmed);
    public sealed record ReleaseManualBlockRequest(
        Guid OperationId,
        long ExpectedVersion);
    public sealed record ReleaseManualBlockGroupRequest(
        Guid OperationId,
        long ExpectedVersion,
        bool Confirmed);
    public sealed record RequestBedRetirementRequest(
        Guid OperationId,
        bool Confirmed,
        string Reason);
    public sealed record RequestRoomRetirementRequest(
        Guid OperationId,
        bool Confirmed,
        string Reason);
    public sealed record RetryRetirementRequest(
        Guid OperationId,
        long ExpectedVersion);
    public sealed record CancelRetirementRequest(
        Guid OperationId,
        long ExpectedVersion,
        bool Confirmed,
        string Reason);

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

    private static readonly ApiErrorStatusCodeMap AdminErrorStatusCodes = ApiErrorStatusCodeMap.Create(
        new(InventoryApplicationErrors.ConfirmationRequired.Code, StatusCodes.Status400BadRequest),
        new(InventoryApplicationErrors.BlockGroupConfirmationRequired.Code, StatusCodes.Status400BadRequest),
        new(InventoryApplicationErrors.BlockGroupSelectionDigestInvalid.Code, StatusCodes.Status400BadRequest),
        new(InventoryApplicationErrors.BlockGroupCursorInvalid.Code, StatusCodes.Status400BadRequest),
        new(InventoryApplicationErrors.ManagementOperationInvalid.Code, StatusCodes.Status400BadRequest),
        new(InventoryApplicationErrors.ManagementOperationConflict.Code, StatusCodes.Status409Conflict),
        new(InventoryApplicationErrors.BlockGroupSelectionMismatch.Code, StatusCodes.Status409Conflict),
        new(InventoryApplicationErrors.BlockGroupTargetTooLarge.Code, StatusCodes.Status422UnprocessableEntity),
        new(Domain.Errors.InventoryDomainErrors.BlockGroupActorInvalid.Code, StatusCodes.Status400BadRequest),
        new(InventoryApplicationErrors.RetirementRequestConflict.Code, StatusCodes.Status409Conflict),
        new(InventoryApplicationErrors.WorkspaceProcessingRestricted.Code, StatusCodes.Status423Locked),
        new(InventoryApplicationErrors.WorkspaceProcessingAdmissionUnavailable.Code, StatusCodes.Status503ServiceUnavailable),
        new(InventoryApplicationErrors.PropertyNotFound.Code, StatusCodes.Status404NotFound),
        new(InventoryApplicationErrors.RoomNotFound.Code, StatusCodes.Status404NotFound),
        new(InventoryApplicationErrors.RoomRetired.Code, StatusCodes.Status409Conflict),
        new(InventoryApplicationErrors.BedLevelRequiresBeds.Code, StatusCodes.Status409Conflict),
        new(InventoryApplicationErrors.SalesModeInvalid.Code, StatusCodes.Status409Conflict),
        new(InventoryApplicationErrors.VersionConflict.Code, StatusCodes.Status409Conflict),
        new(InventoryApplicationErrors.RoomHasActiveClaims.Code, StatusCodes.Status409Conflict),
        new(InventoryApplicationErrors.InventoryUnitNotFound.Code, StatusCodes.Status404NotFound),
        new(InventoryApplicationErrors.InventoryUnitInactive.Code, StatusCodes.Status409Conflict),
        new(InventoryApplicationErrors.InventoryUnitNotSellable.Code, StatusCodes.Status409Conflict),
        new(InventoryApplicationErrors.BlockNotFound.Code, StatusCodes.Status404NotFound),
        new(InventoryApplicationErrors.BlockGroupNotFound.Code, StatusCodes.Status404NotFound),
        new(InventoryApplicationErrors.BlockGroupOperationNotFound.Code, StatusCodes.Status404NotFound),
        new(InventoryApplicationErrors.BlockTargetInvalid.Code, StatusCodes.Status400BadRequest),
        new(InventoryApplicationErrors.BlockTargetEmpty.Code, StatusCodes.Status409Conflict),
        new(InventoryApplicationErrors.BlockOverlap.Code, StatusCodes.Status409Conflict),
        new(InventoryApplicationErrors.BlockAllocationConflict.Code, StatusCodes.Status409Conflict),
        new(InventoryApplicationErrors.StayRangeInvalid.Code, StatusCodes.Status400BadRequest),
        new(InventoryApplicationErrors.BlockReasonInvalid.Code, StatusCodes.Status400BadRequest),
        new(InventoryApplicationErrors.BlockAlreadyReleased.Code, StatusCodes.Status409Conflict),
        new(InventoryApplicationErrors.BedRetirementNotFound.Code, StatusCodes.Status404NotFound),
        new(InventoryApplicationErrors.BedRetirementRetryInvalid.Code, StatusCodes.Status409Conflict),
        new(InventoryApplicationErrors.BedRetirementStillDraining.Code, StatusCodes.Status409Conflict),
        new(InventoryApplicationErrors.BedRetirementInProgress.Code, StatusCodes.Status409Conflict),
        new(InventoryApplicationErrors.RoomRetirementNotFound.Code, StatusCodes.Status404NotFound),
        new(InventoryApplicationErrors.RoomRetirementRetryInvalid.Code, StatusCodes.Status409Conflict),
        new(InventoryApplicationErrors.RoomRetirementStillDraining.Code, StatusCodes.Status409Conflict),
        new(InventoryApplicationErrors.RoomRetirementInProgress.Code, StatusCodes.Status409Conflict),
        new(Domain.Errors.InventoryDomainErrors.BedRetirementRequestInvalid.Code, StatusCodes.Status400BadRequest),
        new(Domain.Errors.InventoryDomainErrors.BedRetirementCancellationRequestInvalid.Code, StatusCodes.Status400BadRequest),
        new(Domain.Errors.InventoryDomainErrors.BedRetirementTransitionInvalid.Code, StatusCodes.Status409Conflict),
        new(Domain.Errors.InventoryDomainErrors.RoomRetirementRequestInvalid.Code, StatusCodes.Status400BadRequest),
        new(Domain.Errors.InventoryDomainErrors.RoomRetirementCancellationRequestInvalid.Code, StatusCodes.Status400BadRequest),
        new(Domain.Errors.InventoryDomainErrors.RoomRetirementTransitionInvalid.Code, StatusCodes.Status409Conflict));

    private static string Actor(HttpContext context)
    {
        string identity = context.User.FindFirst("sub")?.Value
            ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? context.User.Identity?.Name
            ?? $"authenticated:{context.User.Identity?.AuthenticationType ?? "unknown"}";
        return $"admin-api:{identity}";
    }
}
