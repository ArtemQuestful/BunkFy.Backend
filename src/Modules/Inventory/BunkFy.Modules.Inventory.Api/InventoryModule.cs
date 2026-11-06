namespace BunkFy.Modules.Inventory.Api;

using BunkFy.Modules.Inventory.Application;
using BunkFy.Modules.Inventory.Application.Commands;
using BunkFy.Modules.Inventory.Application.Queries;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Persistence;
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

public sealed class InventoryModule : IModule
{
    public string Name => InventoryModuleMetadata.Name;

    public void AddServices(IHostApplicationBuilder builder)
    {
        builder.SelectModuleProfile(InventoryProfiles.Default, "BunkFy.Modules.Inventory.Api");
        builder.Services.AddOptions<InventoryApiSecurityOptions>();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Scoped<IAccessHttpScopeResolver, InventoryPropertyAccessScopeResolver>());
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IStartupFilter, InventoryNoStoreStartupFilter>());
        builder.Services.AddInventoryApplication();
        builder.AddInventoryPersistence();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        InventoryApiSecurityOptions security = endpoints.ServiceProvider
            .GetRequiredService<IOptions<InventoryApiSecurityOptions>>()
            .Value;
        RouteGroupBuilder inventory = endpoints.MapGroup("/api/inventory")
            .WithModuleName(this.Name)
            .WithTags("Inventory")
            .RequireAuthorization();
        inventory.AddEndpointFilter(SensitiveResponseFilter);

        inventory.MapGet("/properties/{propertyId:guid}/rooms", async (
            Guid propertyId,
            int? page,
            int? pageSize,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            (await dispatcher.QueryAsync(
                new ListRoomInventoryQuery(
                    propertyId,
                    page ?? PageRequest.DefaultPage,
                    pageSize ?? PageRequest.DefaultPageSize),
                cancellationToken).ConfigureAwait(false)).ToHttpResult(PublicErrorStatusCodes))
            .Produces<RoomInventoryListResponse>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                InventoryAdminPermissionCodes.Read,
                InventoryPropertyAccessScopeResolver.ResolverName);

        inventory.MapPut("/properties/{propertyId:guid}/rooms/{roomId:guid}/sales-mode", async (
            Guid propertyId,
            Guid roomId,
            ConfigureSalesModeRequest request,
            HttpContext httpContext,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            (await dispatcher.SendAsync(
                new ConfigureRoomSalesModeCommand(
                    request.OperationId,
                    propertyId,
                    roomId,
                    request.SalesMode,
                    request.ExpectedVersion,
                    ResolveActor(httpContext, subjectResolver)),
                cancellationToken).ConfigureAwait(false)).ToHttpResult(PublicErrorStatusCodes))
            .Produces<RoomInventoryMutationReceiptDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                InventoryAdminPermissionCodes.Configure,
                InventoryPropertyAccessScopeResolver.ResolverName);

        inventory.MapGet("/properties/{propertyId:guid}/rooms/{roomId:guid}/change-impact", async (
            Guid propertyId,
            Guid roomId,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            (await dispatcher.QueryAsync(
                new GetRoomInventoryChangeImpactQuery(propertyId, roomId),
                cancellationToken).ConfigureAwait(false)).ToHttpResult(PublicErrorStatusCodes))
            .Produces<RoomInventoryChangeImpactDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                InventoryAdminPermissionCodes.Configure,
                InventoryPropertyAccessScopeResolver.ResolverName);

        inventory.MapGet("/properties/{propertyId:guid}/rooms/{roomId:guid}/retirement", async (
            Guid propertyId,
            Guid roomId,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            (await dispatcher.QueryAsync(
                new GetInventoryRetirementContextQuery(propertyId, roomId),
                cancellationToken).ConfigureAwait(false)).ToHttpResult(PublicErrorStatusCodes))
            .Produces<InventoryRetirementContextDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                InventoryAdminPermissionCodes.Retire,
                InventoryPropertyAccessScopeResolver.ResolverName);

        inventory.MapGet("/properties/{propertyId:guid}/rooms/{roomId:guid}/beds/{bedId:guid}/retirement", async (
            Guid propertyId,
            Guid roomId,
            Guid bedId,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            (await dispatcher.QueryAsync(
                new GetInventoryRetirementContextQuery(propertyId, roomId, bedId),
                cancellationToken).ConfigureAwait(false)).ToHttpResult(PublicErrorStatusCodes))
            .Produces<InventoryRetirementContextDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                InventoryAdminPermissionCodes.Retire,
                InventoryPropertyAccessScopeResolver.ResolverName);

        inventory.MapGet("/properties/{propertyId:guid}/availability", async (
            Guid propertyId,
            DateOnly arrival,
            DateOnly departure,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            (await dispatcher.QueryAsync(
                new GetInventoryAvailabilityQuery(propertyId, arrival, departure),
                cancellationToken).ConfigureAwait(false)).ToHttpResult(PublicErrorStatusCodes))
            .Produces<InventoryAvailabilityResponse>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                InventoryAdminPermissionCodes.Read,
                InventoryPropertyAccessScopeResolver.ResolverName);

        inventory.MapGet("/properties/{propertyId:guid}/blocks", async (
            Guid propertyId,
            Guid? inventoryUnitId,
            bool? includeReleased,
            DateOnly? overlapsFrom,
            DateOnly? overlapsTo,
            int? page,
            int? pageSize,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            (await dispatcher.QueryAsync(
                new ListManualInventoryBlocksQuery(
                    propertyId,
                    inventoryUnitId,
                    includeReleased ?? false,
                    page ?? PageRequest.DefaultPage,
                    pageSize ?? PageRequest.DefaultPageSize,
                    overlapsFrom,
                    overlapsTo),
                cancellationToken).ConfigureAwait(false)).ToHttpResult(PublicErrorStatusCodes))
            .Produces<ManualInventoryBlockListResponse>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                InventoryAdminPermissionCodes.Read,
                InventoryPropertyAccessScopeResolver.ResolverName);

        inventory.MapPost("/properties/{propertyId:guid}/blocks", async (
            Guid propertyId,
            CreateManualBlockRequest request,
            HttpContext httpContext,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            string? actor = ResolveActor(httpContext, subjectResolver);
            return actor is null
                ? Results.Unauthorized()
                : (await dispatcher.SendAsync(
                    new CreateManualInventoryBlockCommand(
                        request.OperationId,
                        propertyId,
                        request.InventoryUnitId,
                        request.Arrival,
                        request.Departure,
                        request.Reason,
                        actor),
                    cancellationToken).ConfigureAwait(false)).ToHttpResult(PublicErrorStatusCodes);
        })
            .Produces<ManualInventoryBlockMutationReceiptDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                InventoryAdminPermissionCodes.BlocksManage,
                InventoryPropertyAccessScopeResolver.ResolverName);

        inventory.MapPost("/properties/{propertyId:guid}/block-groups/preview", async (
            Guid propertyId,
            PreviewManualBlockGroupRequest request,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            (await dispatcher.QueryAsync(
                new PreviewManualInventoryBlockGroupQuery(
                    propertyId,
                    request.Target,
                    request.Arrival,
                    request.Departure,
                    request.Reason,
                    request.BlockGroupId,
                    request.ExpectedVersion),
                cancellationToken).ConfigureAwait(false)).ToHttpResult(PublicErrorStatusCodes))
            .Produces<ManualInventoryBlockGroupSelectionPreviewDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                InventoryAdminPermissionCodes.BlockGroupsManage,
                InventoryPropertyAccessScopeResolver.ResolverName);

        inventory.MapGet("/properties/{propertyId:guid}/block-groups", async (
            Guid propertyId,
            ManualInventoryBlockGroupStatus? status,
            string? cursor,
            int? pageSize,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            (await dispatcher.QueryAsync(
                new ListManualInventoryBlockGroupsQuery(
                    propertyId,
                    status,
                    cursor,
                    pageSize ?? PageRequest.DefaultPageSize),
                cancellationToken).ConfigureAwait(false)).ToHttpResult(PublicErrorStatusCodes))
            .Produces<ManualInventoryBlockGroupListResponse>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                InventoryAdminPermissionCodes.Read,
                InventoryPropertyAccessScopeResolver.ResolverName);

        inventory.MapGet("/properties/{propertyId:guid}/block-groups/{blockGroupId:guid}", async (
            Guid propertyId,
            Guid blockGroupId,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            (await dispatcher.QueryAsync(
                new GetManualInventoryBlockGroupQuery(propertyId, blockGroupId),
                cancellationToken).ConfigureAwait(false)).ToHttpResult(PublicErrorStatusCodes))
            .Produces<ManualInventoryBlockGroupDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                InventoryAdminPermissionCodes.Read,
                InventoryPropertyAccessScopeResolver.ResolverName);

        inventory.MapGet("/properties/{propertyId:guid}/block-groups/{blockGroupId:guid}/members", async (
            Guid propertyId,
            Guid blockGroupId,
            ManualInventoryBlockStatus? status,
            string? cursor,
            int? pageSize,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            (await dispatcher.QueryAsync(
                new ListManualInventoryBlockGroupMembersQuery(
                    propertyId,
                    blockGroupId,
                    status,
                    cursor,
                    pageSize ?? PageRequest.DefaultPageSize),
                cancellationToken).ConfigureAwait(false)).ToHttpResult(PublicErrorStatusCodes))
            .Produces<ManualInventoryBlockGroupMemberListResponse>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                InventoryAdminPermissionCodes.Read,
                InventoryPropertyAccessScopeResolver.ResolverName);

        inventory.MapPost("/properties/{propertyId:guid}/block-groups", async (
            Guid propertyId,
            CreateManualBlockGroupRequest request,
            HttpContext httpContext,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            string? actor = ResolveActor(httpContext, subjectResolver);
            return actor is null
                ? Results.Unauthorized()
                : (await dispatcher.SendAsync(
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
                        actor),
                    cancellationToken).ConfigureAwait(false)).ToHttpResult(PublicErrorStatusCodes);
        })
            .Produces<ManualInventoryBlockGroupMutationReceiptDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                InventoryAdminPermissionCodes.BlockGroupsManage,
                InventoryPropertyAccessScopeResolver.ResolverName);

        inventory.MapPut("/properties/{propertyId:guid}/block-groups/{blockGroupId:guid}", async (
            Guid propertyId,
            Guid blockGroupId,
            ReplaceManualBlockGroupRequest request,
            HttpContext httpContext,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            string? actor = ResolveActor(httpContext, subjectResolver);
            return actor is null
                ? Results.Unauthorized()
                : (await dispatcher.SendAsync(
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
                        actor),
                    cancellationToken).ConfigureAwait(false)).ToHttpResult(PublicErrorStatusCodes);
        })
            .Produces<ManualInventoryBlockGroupMutationReceiptDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                InventoryAdminPermissionCodes.BlockGroupsManage,
                InventoryPropertyAccessScopeResolver.ResolverName);

        inventory.MapPost("/properties/{propertyId:guid}/blocks/{blockId:guid}/release", async (
            Guid propertyId,
            Guid blockId,
            ReleaseManualBlockRequest request,
            HttpContext httpContext,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            string? actor = ResolveActor(httpContext, subjectResolver);
            return actor is null
                ? Results.Unauthorized()
                : (await dispatcher.SendAsync(
                    new ReleaseManualInventoryBlockCommand(
                        request.OperationId,
                        propertyId,
                        blockId,
                        request.ExpectedVersion,
                        actor),
                    cancellationToken).ConfigureAwait(false)).ToHttpResult(PublicErrorStatusCodes);
        })
            .Produces<ManualInventoryBlockMutationReceiptDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                InventoryAdminPermissionCodes.BlocksManage,
                InventoryPropertyAccessScopeResolver.ResolverName);

        inventory.MapPost("/properties/{propertyId:guid}/block-groups/{blockGroupId:guid}/release", async (
            Guid propertyId,
            Guid blockGroupId,
            ReleaseManualBlockGroupRequest request,
            HttpContext httpContext,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            string? actor = ResolveActor(httpContext, subjectResolver);
            return actor is null
                ? Results.Unauthorized()
                : (await dispatcher.SendAsync(
                    new ReleaseManualInventoryBlockGroupCommand(
                        request.OperationId,
                        propertyId,
                        blockGroupId,
                        request.ExpectedVersion,
                        request.Confirmed,
                        actor),
                    cancellationToken).ConfigureAwait(false)).ToHttpResult(PublicErrorStatusCodes);
        })
            .Produces<ManualInventoryBlockGroupMutationReceiptDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                InventoryAdminPermissionCodes.BlockGroupsManage,
                InventoryPropertyAccessScopeResolver.ResolverName);

        inventory.MapGet("/properties/{propertyId:guid}/block-group-create-operations/{operationId:guid}", async (
            Guid propertyId,
            Guid operationId,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            (await dispatcher.QueryAsync(
                new GetManualInventoryBlockGroupCreateOperationQuery(
                    propertyId,
                    operationId),
                cancellationToken).ConfigureAwait(false)).ToHttpResult(PublicErrorStatusCodes))
            .Produces<ManualInventoryBlockGroupOperationDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                InventoryAdminPermissionCodes.BlockGroupsManage,
                InventoryPropertyAccessScopeResolver.ResolverName);

        inventory.MapGet("/properties/{propertyId:guid}/block-groups/{blockGroupId:guid}/operations/{operationId:guid}", async (
            Guid propertyId,
            Guid blockGroupId,
            Guid operationId,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            (await dispatcher.QueryAsync(
                new GetManualInventoryBlockGroupOperationQuery(
                    propertyId,
                    blockGroupId,
                    operationId),
                cancellationToken).ConfigureAwait(false)).ToHttpResult(PublicErrorStatusCodes))
            .Produces<ManualInventoryBlockGroupOperationDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                InventoryAdminPermissionCodes.BlockGroupsManage,
                InventoryPropertyAccessScopeResolver.ResolverName);

        RouteHandlerBuilder requestBedRetirement = inventory.MapPost("/properties/{propertyId:guid}/rooms/{roomId:guid}/beds/{bedId:guid}/retirement", async (
            Guid propertyId,
            Guid roomId,
            Guid bedId,
            RequestBedRetirementRequest request,
            HttpContext httpContext,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            Gma.Framework.AccessControl.AccessSubject? subject = subjectResolver.ResolveSubject(httpContext);
            return subject is null
                ? Results.Unauthorized()
                : (await dispatcher.SendAsync(
                    new RequestBedRetirementCommand(
                        request.OperationId,
                        propertyId,
                        roomId,
                        bedId,
                        request.Confirmed,
                        request.Reason,
                        $"{Gma.Framework.AccessControl.AccessSubjectKindNames.GetName(subject.Kind)}:{subject.Id}"),
                    cancellationToken).ConfigureAwait(false)).ToHttpResult(PublicErrorStatusCodes);
        })
            .Produces<BedRetirementDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                InventoryAdminPermissionCodes.Retire,
                InventoryPropertyAccessScopeResolver.ResolverName);
        RequireAssuranceWhenConfigured(
            requestBedRetirement,
            security.TopologyRetirementAssurance);

        inventory.MapGet("/properties/{propertyId:guid}/bed-retirements/{topologyChangeId:guid}", async (
            Guid propertyId,
            Guid topologyChangeId,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            (await dispatcher.QueryAsync(
                new GetBedRetirementQuery(propertyId, topologyChangeId),
                cancellationToken).ConfigureAwait(false)).ToHttpResult(PublicErrorStatusCodes))
            .Produces<BedRetirementDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                InventoryAdminPermissionCodes.Retire,
                InventoryPropertyAccessScopeResolver.ResolverName);

        inventory.MapPost("/properties/{propertyId:guid}/bed-retirements/{topologyChangeId:guid}/retry", async (
            Guid propertyId,
            Guid topologyChangeId,
            RetryRetirementRequest request,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            (await dispatcher.SendAsync(
                new RetryBedRetirementCommand(
                    request.OperationId,
                    propertyId,
                    topologyChangeId,
                    request.ExpectedVersion),
                cancellationToken).ConfigureAwait(false)).ToHttpResult(PublicErrorStatusCodes))
            .Produces<BedRetirementDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                InventoryAdminPermissionCodes.Retire,
                InventoryPropertyAccessScopeResolver.ResolverName);

        inventory.MapPost("/properties/{propertyId:guid}/bed-retirements/{topologyChangeId:guid}/cancel", async (
            Guid propertyId,
            Guid topologyChangeId,
            CancelRetirementRequest request,
            HttpContext httpContext,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            string? actor = ResolveActor(httpContext, subjectResolver);
            return actor is null
                ? Results.Unauthorized()
                : (await dispatcher.SendAsync(
                    new CancelBedRetirementCommand(
                        request.OperationId,
                        propertyId,
                        topologyChangeId,
                        request.ExpectedVersion,
                        request.Confirmed,
                        request.Reason,
                        actor),
                    cancellationToken).ConfigureAwait(false)).ToHttpResult(PublicErrorStatusCodes);
        })
            .Produces<BedRetirementDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                InventoryAdminPermissionCodes.Retire,
                InventoryPropertyAccessScopeResolver.ResolverName);

        RouteHandlerBuilder requestRoomRetirement = inventory.MapPost("/properties/{propertyId:guid}/rooms/{roomId:guid}/retirement", async (
            Guid propertyId,
            Guid roomId,
            RequestRoomRetirementRequest request,
            HttpContext httpContext,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            Gma.Framework.AccessControl.AccessSubject? subject = subjectResolver.ResolveSubject(httpContext);
            return subject is null
                ? Results.Unauthorized()
                : (await dispatcher.SendAsync(
                    new RequestRoomRetirementCommand(
                        request.OperationId,
                        propertyId,
                        roomId,
                        request.Confirmed,
                        request.Reason,
                        $"{Gma.Framework.AccessControl.AccessSubjectKindNames.GetName(subject.Kind)}:{subject.Id}"),
                    cancellationToken).ConfigureAwait(false)).ToHttpResult(PublicErrorStatusCodes);
        })
            .Produces<RoomRetirementDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                InventoryAdminPermissionCodes.Retire,
                InventoryPropertyAccessScopeResolver.ResolverName);
        RequireAssuranceWhenConfigured(
            requestRoomRetirement,
            security.TopologyRetirementAssurance);

        inventory.MapGet("/properties/{propertyId:guid}/room-retirements/{topologyChangeId:guid}", async (
            Guid propertyId,
            Guid topologyChangeId,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            (await dispatcher.QueryAsync(
                new GetRoomRetirementQuery(propertyId, topologyChangeId),
                cancellationToken).ConfigureAwait(false)).ToHttpResult(PublicErrorStatusCodes))
            .Produces<RoomRetirementDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                InventoryAdminPermissionCodes.Retire,
                InventoryPropertyAccessScopeResolver.ResolverName);

        inventory.MapPost("/properties/{propertyId:guid}/room-retirements/{topologyChangeId:guid}/retry", async (
            Guid propertyId,
            Guid topologyChangeId,
            RetryRetirementRequest request,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
            (await dispatcher.SendAsync(
                new RetryRoomRetirementCommand(
                    request.OperationId,
                    propertyId,
                    topologyChangeId,
                    request.ExpectedVersion),
                cancellationToken).ConfigureAwait(false)).ToHttpResult(PublicErrorStatusCodes))
            .Produces<RoomRetirementDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                InventoryAdminPermissionCodes.Retire,
                InventoryPropertyAccessScopeResolver.ResolverName);

        inventory.MapPost("/properties/{propertyId:guid}/room-retirements/{topologyChangeId:guid}/cancel", async (
            Guid propertyId,
            Guid topologyChangeId,
            CancelRetirementRequest request,
            HttpContext httpContext,
            IAccessHttpSubjectResolver subjectResolver,
            IRequestDispatcher dispatcher,
            CancellationToken cancellationToken) =>
        {
            string? actor = ResolveActor(httpContext, subjectResolver);
            return actor is null
                ? Results.Unauthorized()
                : (await dispatcher.SendAsync(
                    new CancelRoomRetirementCommand(
                        request.OperationId,
                        propertyId,
                        topologyChangeId,
                        request.ExpectedVersion,
                        request.Confirmed,
                        request.Reason,
                        actor),
                    cancellationToken).ConfigureAwait(false)).ToHttpResult(PublicErrorStatusCodes);
        })
            .Produces<RoomRetirementDto>(StatusCodes.Status200OK)
            .RequireTenant()
            .RequireResolvedScopePermission(
                InventoryAdminPermissionCodes.Retire,
                InventoryPropertyAccessScopeResolver.ResolverName);
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

    private static string? ResolveActor(HttpContext context, IAccessHttpSubjectResolver subjectResolver)
    {
        Gma.Framework.AccessControl.AccessSubject? subject = subjectResolver.ResolveSubject(context);
        return subject is null
            ? null
            : $"{Gma.Framework.AccessControl.AccessSubjectKindNames.GetName(subject.Kind)}:{subject.Id}";
    }

    private static RouteHandlerBuilder RequireAssuranceWhenConfigured(
        RouteHandlerBuilder endpoint,
        AuthenticationAssuranceRequirement? requirement) =>
        requirement is null
            ? endpoint
            : endpoint.RequireAuthenticationAssurance(requirement);

    private static readonly ApiErrorStatusCodeMap PublicErrorStatusCodes = ApiErrorStatusCodeMap.Create(
        new(InventoryApplicationErrors.AccessDenied.Code, StatusCodes.Status403Forbidden),
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
}
