namespace BunkFy.Modules.Stations.Api;

using BunkFy.Modules.Stations.Application;
using BunkFy.Modules.Stations.Contracts;
using Gma.Framework.Tenancy.AccessControl.AspNetCore;
using Gma.Framework.Api.Observability;
using Gma.Framework.Api.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

internal static class StationWorkspaceSettingsEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints, string moduleName)
    {
        RouteGroupBuilder group = endpoints.MapGroup("/api/station-settings")
            .WithModuleName(moduleName)
            .WithTags("Stations")
            .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = StationApiOptions.PrimaryAuthenticationScheme
            });

        group.MapGet("/workspace", async (
            HttpContext context,
            IStationWorkspaceSettingsReader settings,
            CancellationToken cancellationToken) =>
        {
            MarkNoStore(context);
            StationWorkspaceSettings value = await settings
                .ReadWorkspaceSettingsAsync(cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(new StationWorkspaceSettingsResponse(
                value.SharedStationPinEnabled,
                value.Revision));
        })
            .WithName("StationsWorkspaceSettingsGet")
            .Produces<StationWorkspaceSettingsResponse>()
            .RequireTenant();

        group.MapPut("/workspace", async (
            UpdateStationWorkspaceSettingsRequest request,
            HttpContext context,
            IStationWorkspaceSettingsStore settings,
            CancellationToken cancellationToken) =>
        {
            MarkNoStore(context);
            if (request.ExpectedRevision < 0)
            {
                return Results.BadRequest();
            }

            StationWorkspaceSettingsUpdateResult result = await settings
                .UpdateWorkspaceSettingsAsync(
                    request.SharedStationPinEnabled,
                    request.ExpectedRevision,
                    cancellationToken)
                .ConfigureAwait(false);
            return result.State switch
            {
                StationWorkspaceSettingsUpdateState.Applied => Results.Ok(
                    new StationWorkspaceSettingsResponse(
                        result.Settings.SharedStationPinEnabled,
                        result.Settings.Revision)),
                StationWorkspaceSettingsUpdateState.Conflict => Results.Conflict(
                    new StationWorkspaceSettingsResponse(
                        result.Settings.SharedStationPinEnabled,
                        result.Settings.Revision)),
                _ => Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
            };
        })
            .WithName("StationsWorkspaceSettingsUpdate")
            .Accepts<UpdateStationWorkspaceSettingsRequest>("application/json")
            .Produces<StationWorkspaceSettingsResponse>()
            .Produces<StationWorkspaceSettingsResponse>(StatusCodes.Status409Conflict)
            .RequireTenant()
            .RequireTenantPermission(StationsPermissionCodes.Manage);
    }

    private static void MarkNoStore(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.Expires = "0";
    }
}
