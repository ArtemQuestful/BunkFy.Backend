namespace BunkFy.Modules.Stations.Api;

using BunkFy.Modules.Stations.Application;
using BunkFy.Modules.Stations.Contracts;
using Gma.Framework.Api.Observability;
using Gma.Framework.Api.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

internal static class StationManagementEndpoints
{
    internal static void Map(IEndpointRouteBuilder endpoints, string moduleName)
    {
        RouteGroupBuilder group = PrimaryGroup(endpoints, "/api/station-management/properties/{propertyId}", moduleName);
        group.MapGet("/stations", ListAsync).WithName("StationsManagementList")
            .Produces<StationListResponse>().RequireTenant();
        group.MapGet("/staff/{staffId}", StaffAsync).WithName("StationsManagementStaffStatus")
            .Produces<StationStaffStatusResponse>().RequireTenant();
        group.MapGet("/operations/{operationId}", OutcomeAsync).WithName("StationsManagementOutcome")
            .Produces<StationManagementHttpResponse>().Produces<StationManagementHttpResponse>(StatusCodes.Status404NotFound).RequireTenant();
        group.MapPost("/operations", ManageAsync).WithName("StationsManagementExecute")
            .Accepts<StationManagementRequest>("application/json").Produces<StationManagementHttpResponse>().RequireTenant();
    }

    internal static RouteGroupBuilder PrimaryGroup(IEndpointRouteBuilder endpoints, string path, string moduleName)
    {
        RouteGroupBuilder group = endpoints.MapGroup(path).WithModuleName(moduleName).WithTags("Stations")
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = StationApiOptions.PrimaryAuthenticationScheme });
        StationHttpSecurity.DescribeFailures(group);
        group.AddEndpointFilter((invocation, next) => invocation.HttpContext.RequestServices
            .GetRequiredService<StationHttpSecurity>().FilterAsync(invocation, next));
        return group;
    }

    private static async Task<IResult> ListAsync(HttpContext context, StationManagementService management,
        [FromRoute] string propertyId, [FromQuery] string? page = null, [FromQuery] string? pageSize = null)
    {
        if (!StationHttpSecurity.Id(propertyId, out Guid property) ||
            !StationHttpSecurity.Query(context, "page", "pageSize") ||
            !StationHttpSecurity.Number(page, 1, 200, out int pageNumber) ||
            !StationHttpSecurity.Number(pageSize, 25, 50, out int size))
        { return StationHttpSecurity.InvalidRequest(); }
        var result = await management.ListAsync(context.User, property, pageNumber, size, context.RequestAborted).ConfigureAwait(false);
        return Results.Json(result, statusCode: StationHttpSecurity.ManagementStatus(result.State));
    }

    private static async Task<IResult> StaffAsync(HttpContext context, StationManagementService management,
        [FromRoute] string propertyId, [FromRoute] string staffId)
    {
        if (!StationHttpSecurity.Id(propertyId, out Guid property) ||
            !StationHttpSecurity.Id(staffId, out Guid staff) || !StationHttpSecurity.Query(context))
        { return StationHttpSecurity.InvalidRequest(); }
        var result = await management.StaffStatusAsync(context.User, property, staff, context.RequestAborted).ConfigureAwait(false);
        return Results.Json(new StationStaffStatusResponse(result.State, result.Item),
            statusCode: StationHttpSecurity.ManagementStatus(result.State));
    }

    private static async Task<IResult> OutcomeAsync(HttpContext context, StationManagementService management,
        [FromRoute] string propertyId, [FromRoute] string operationId)
    {
        if (!StationHttpSecurity.Id(propertyId, out Guid property) ||
            !StationHttpSecurity.Id(operationId, out Guid operation) || !StationHttpSecurity.Query(context))
        { return StationHttpSecurity.InvalidRequest(); }
        return StationHttpSecurity.Management(await management.OutcomeAsync(context.User, operation, property,
            context.RequestAborted).ConfigureAwait(false));
    }

    private static async Task<IResult> ManageAsync(HttpContext context, StationManagementService management,
        StationHttpSecurity security, [FromRoute] string propertyId)
    {
        if (!StationHttpSecurity.Id(propertyId, out Guid property) || !StationHttpSecurity.Query(context))
        { return StationHttpSecurity.InvalidRequest(); }
        StationManagementRequest? request = await StationHttpSecurity.BodyAsync<StationManagementRequest>(context).ConfigureAwait(false);
        if (request is null || !Valid(request))
        { return StationHttpSecurity.InvalidRequest(); }
        var command = new StationManagementCommand(request.Kind, property, request.StationId, request.BrowserSessionId,
            request.StaffMemberId, request.SetupGrantId, request.ExpectedVersion, request.Label);
        StationPairingHandoff handoff = await management.ManageAsync(context.User, request.OperationId, command,
            context.RequestAborted).ConfigureAwait(false);
        security.IssueCookie(context, handoff);
        // The HTTP projection omits internal issuer identity. A replay cannot reissue a lost credential.
        return StationHttpSecurity.Management(handoff.Response);
    }

    private static bool Valid(StationManagementRequest request)
    {
        if (request.OperationId == Guid.Empty || request.ExpectedVersion < 0 ||
            request.StationId == Guid.Empty || request.BrowserSessionId == Guid.Empty ||
            request.StaffMemberId == Guid.Empty || request.SetupGrantId == Guid.Empty)
        { return false; }
        return request.Kind switch
        {
            StationOperationKind.Register => request.Label is { Length: > 0 and <= 100 } label &&
                !string.IsNullOrWhiteSpace(label) && !label.Any(char.IsControl) && request.ExpectedVersion == 0 &&
                request.StationId is null && request.BrowserSessionId is null && request.StaffMemberId is null && request.SetupGrantId is null,
            StationOperationKind.Pair or StationOperationKind.RevokeStation => request.StationId is not null &&
                request.ExpectedVersion > 0 && request.BrowserSessionId is null && request.StaffMemberId is null &&
                request.SetupGrantId is null && request.Label is null,
            StationOperationKind.RegisterStaff or StationOperationKind.UnregisterStaff or StationOperationKind.GrantCheckIn or
                StationOperationKind.RevokeGrant or StationOperationKind.Reset => request.StaffMemberId is not null &&
                request.StationId is null && request.BrowserSessionId is null && request.SetupGrantId is null && request.Label is null,
            StationOperationKind.IssueSetup => request.StaffMemberId is not null && request.StationId is not null &&
                request.BrowserSessionId is not null && request.SetupGrantId is null && request.Label is null,
            StationOperationKind.CancelSetup => request.SetupGrantId is not null && request.StationId is null &&
                request.BrowserSessionId is null && request.StaffMemberId is null && request.Label is null,
            _ => false
        };
    }
}
