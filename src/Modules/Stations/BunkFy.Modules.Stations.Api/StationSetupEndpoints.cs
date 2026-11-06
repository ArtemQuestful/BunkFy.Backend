namespace BunkFy.Modules.Stations.Api;

using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Stations.Application;
using BunkFy.Modules.Stations.Contracts;
using Gma.Framework.Api.Tenancy;
using Gma.Framework.Scoping;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

internal static class StationSetupEndpoints
{
    internal static void Map(IEndpointRouteBuilder endpoints, string moduleName)
    {
        RouteGroupBuilder group = StationManagementEndpoints.PrimaryGroup(endpoints,
            "/api/station-setup/properties/{propertyId}", moduleName);
        group.MapGet("/pin", StatusAsync).WithName("StationsOwnPinStatus")
            .Produces<StationOwnPinStatusResponse>().RequireTenant();
        group.MapPut("/pin", SetAsync).WithName("StationsOwnPinSet")
            .Accepts<StationOwnPinRequest>("application/json").Produces<StationManagementHttpResponse>().RequireTenant();
        group.MapGet("/operations/{operationId}", OutcomeAsync).WithName("StationsOwnPinOutcome")
            .Produces<StationManagementHttpResponse>().Produces<StationManagementHttpResponse>(StatusCodes.Status404NotFound).RequireTenant();
    }

    private static async Task<IResult> StatusAsync(HttpContext context, StationPrimaryAdmission primary,
        IStaffOperationalIdentityReader identities, IScopeContext scope, StationManagementService management,
        [FromRoute] string propertyId)
    {
        if (!StationHttpSecurity.Id(propertyId, out Guid property) || !StationHttpSecurity.Query(context))
        { return StationHttpSecurity.InvalidRequest(); }
        var own = await OwnStaffAsync(context, property, primary, identities, scope).ConfigureAwait(false);
        if (own.Failure is not null)
        { return own.Failure; }
        var result = await management.OwnPinStatusAsync(context.User, property, own.StaffId,
            context.RequestAborted).ConfigureAwait(false);
        return Results.Json(result, statusCode: StationHttpSecurity.ManagementStatus(result.State));
    }

    private static async Task<IResult> SetAsync(HttpContext context, StationPrimaryAdmission primary,
        IStaffOperationalIdentityReader identities, IScopeContext scope, StationManagementService management,
        [FromRoute] string propertyId)
    {
        if (!StationHttpSecurity.Id(propertyId, out Guid property) || !StationHttpSecurity.Query(context))
        { return StationHttpSecurity.InvalidRequest(); }
        StationOwnPinRequest? request = await StationHttpSecurity.BodyAsync<StationOwnPinRequest>(context).ConfigureAwait(false);
        if (request is null || request.OperationId == Guid.Empty || request.ExpectedRevision < 0 || !StationPinVerifier.IsPin(request.Pin))
        { return StationHttpSecurity.InvalidRequest(); }
        var own = await OwnStaffAsync(context, property, primary, identities, scope).ConfigureAwait(false);
        if (own.Failure is not null)
        { return own.Failure; }
        return StationHttpSecurity.Management(await management.SetOwnPinAsync(context.User, property, own.StaffId,
            request.ExpectedRevision, request.OperationId, request.Pin, context.RequestAborted).ConfigureAwait(false));
    }

    private static async Task<IResult> OutcomeAsync(HttpContext context, StationPrimaryAdmission primary,
        IStaffOperationalIdentityReader identities, IScopeContext scope, StationManagementService management,
        [FromRoute] string propertyId, [FromRoute] string operationId)
    {
        if (!StationHttpSecurity.Id(propertyId, out Guid property) ||
            !StationHttpSecurity.Id(operationId, out Guid operation) || !StationHttpSecurity.Query(context))
        { return StationHttpSecurity.InvalidRequest(); }
        var own = await OwnStaffAsync(context, property, primary, identities, scope).ConfigureAwait(false);
        if (own.Failure is not null)
        { return own.Failure; }
        var result = await management.OutcomeAsync(context.User, operation, property, context.RequestAborted).ConfigureAwait(false);
        if (result.Receipt is { } receipt && (receipt.Kind != StationOperationKind.OwnPin || receipt.StaffMemberId != own.StaffId))
        { return StationHttpSecurity.Management(new(StationManagementState.Denied)); }
        return StationHttpSecurity.Management(result);
    }

    private static async Task<(Guid StaffId, IResult? Failure)> OwnStaffAsync(HttpContext context, Guid property,
        StationPrimaryAdmission primary, IStaffOperationalIdentityReader identities, IScopeContext scope)
    {
        StationPrimaryObservation admission = await primary.OwnOutcomeAsync(context.User, property,
            context.RequestAborted).ConfigureAwait(false);
        if (admission.State != StationAdmissionState.Current || admission.Issuer is not { } issuer)
        {
            StationManagementState state = admission.State switch
            {
                StationAdmissionState.Unavailable => StationManagementState.Unavailable,
                StationAdmissionState.StateChanged => StationManagementState.StateChanged,
                _ => StationManagementState.Denied
            };
            return (Guid.Empty, StationHttpSecurity.Management(new(state)));
        }
        // Staff owns the exact current subject-to-staff link. Use its minimized contract,
        // rather than accepting a client StaffMemberId or depending on another module's application.
        if (!scope.IsEnabled || scope.ScopeId is not { } tenant)
        { return (Guid.Empty, StationHttpSecurity.Management(new(StationManagementState.Denied))); }
        StaffOperationalIdentitySnapshot? own = await identities.FindAsync(tenant, issuer.SubjectId,
            context.RequestAborted).ConfigureAwait(false);
        if (own is null || own.StaffMemberId == Guid.Empty || own.AuthSubjectId != issuer.SubjectId)
        { return (Guid.Empty, StationHttpSecurity.Management(new(StationManagementState.Denied))); }
        return (own.StaffMemberId, null);
    }
}
