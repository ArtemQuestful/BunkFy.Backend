namespace BunkFy.Modules.Stations.Api;

using System.Globalization;
using BunkFy.Modules.Reservations.Contracts.Stations;
using BunkFy.Modules.Stations.Application;
using BunkFy.Modules.Stations.Contracts;
using Gma.Framework.Api.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

internal static class StationRuntimeEndpoints
{
    internal static void Map(IEndpointRouteBuilder endpoints, string moduleName)
    {
        RouteGroupBuilder group = endpoints.MapGroup(StationApiOptions.RuntimePath)
            .WithModuleName(moduleName).WithTags("Stations runtime").AllowAnonymous();
        StationHttpSecurity.DescribeFailures(group);
        group.AddEndpointFilter((invocation, next) => invocation.HttpContext.RequestServices
            .GetRequiredService<StationHttpSecurity>().FilterAsync(invocation, next));
        group.MapGet("/current", CurrentAsync).WithName("StationsRuntimeCurrent").Produces<StationCurrentResponse>();
        group.MapGet("/roster", RosterAsync).WithName("StationsRuntimeRoster").Produces<StationRosterResponse>();
        group.MapPost("/unlock", UnlockAsync).WithName("StationsRuntimeUnlock")
            .Accepts<StationUnlockRequest>("application/json").Produces<StationRuntimeResponse>();
        // Switching staff starts by locking; no switch endpoint can inherit the previous actor.
        group.MapPost("/lock", LockAsync).WithName("StationsRuntimeLock")
            .Accepts<StationLockRequest>("application/json").Produces<StationRuntimeResponse>();
        group.MapPost("/activity", ActivityAsync).WithName("StationsRuntimeActivity")
            .Accepts<StationActivityRequest>("application/json").Produces<StationRuntimeResponse>();
        group.MapPost("/setup/redeem", RedeemAsync).WithName("StationsRuntimeRedeemSetup")
            .Accepts<StationRedeemSetupRequest>("application/json").Produces<StationRuntimeResponse>();
        group.MapGet("/departures", DeparturesAsync).WithName("StationsRuntimeDepartures").Produces<StationDeparturesResponse>();
        group.MapPost("/check-out", CheckOutAsync).WithName("StationsRuntimeCheckOut")
            .Accepts<StationCheckOutRequest>("application/json").Produces<StationCheckOutResult>();
        group.MapPost("/check-out/outcome", CheckOutOutcomeAsync).WithName("StationsRuntimeCheckOutOutcome")
            .Accepts<StationCheckOutOutcomeRequest>("application/json").Produces<StationCheckOutOutcome>();
        group.MapGet("/arrivals", ArrivalsAsync).WithName("StationsRuntimeArrivals").Produces<StationArrivalsResponse>();
        group.MapPost("/check-in", CheckInAsync).WithName("StationsRuntimeCheckIn")
            .Accepts<StationCheckInRequest>("application/json").Produces<StationCheckInResult>();
        group.MapPost("/check-in/outcome", CheckInOutcomeAsync).WithName("StationsRuntimeCheckInOutcome")
            .Accepts<StationCheckInOutcomeRequest>("application/json").Produces<StationCheckInOutcome>();
    }

    private static async Task<IResult> CurrentAsync(HttpContext context, StationRuntimeService runtime,
        StationHttpSecurity security, StationFirstJobService jobs, IOptions<StationApiOptions> options)
    {
        if (!StationHttpSecurity.Query(context))
        { return StationHttpSecurity.InvalidRequest(); }
        IResult? rejected = StationHttpSecurity.CheckLane(context, options.Value, out string? credential);
        if (rejected is not null)
        { return rejected; }
        StationCurrentView view = await runtime.ReadViewAsync(credential!, context.RequestAborted).ConfigureAwait(false);
        StationRuntimeResponse result = view.Runtime;
        if (result.State == StationSessionState.Invalid)
        { StationHttpSecurity.ClearCookie(context); }
        StationCurrentResponse response = security.Current(result);
        if (response.Runtime.State is StationSessionState.Locked or StationSessionState.Active)
        { response = response with { PropertyName = view.PropertyName, StaffDisplayName = view.StaffDisplayName }; }
        if (result.State == StationSessionState.Active && result.Session?.Actor is { } actor)
        {
            var availability = await jobs.ReadJobsAsync(credential!, actor, context.RequestAborted).ConfigureAwait(false);
            if (result != await runtime.ReadAsync(credential!, context.RequestAborted).ConfigureAwait(false))
            { return Results.Json(security.Current(new(StationSessionState.StateChanged)), statusCode: StatusCodes.Status409Conflict); }
            response = response with { Jobs = availability };
        }
        return Results.Json(response, statusCode: StationHttpSecurity.RuntimeStatus(result));
    }

    private static async Task<IResult> RosterAsync(HttpContext context, StationRuntimeService runtime,
        IOptions<StationApiOptions> options, [FromQuery] string? search = null,
        [FromQuery] string? page = null, [FromQuery] string? pageSize = null)
    {
        if (!StationHttpSecurity.Query(context, "search", "page", "pageSize") ||
            !StationHttpSecurity.Number(page, 1, 200, out int pageNumber) ||
            !StationHttpSecurity.Number(pageSize, 25, 50, out int size))
        { return StationHttpSecurity.InvalidRequest(); }
        if (search?.Length > 100 || search?.Any(char.IsControl) == true)
        { return StationHttpSecurity.InvalidRequest(); }
        IResult? rejected = StationHttpSecurity.CheckLane(context, options.Value, out string? credential);
        if (rejected is not null)
        { return rejected; }
        var result = await runtime.RosterAsync(credential!, search, pageNumber, size, context.RequestAborted).ConfigureAwait(false);
        return Results.Json(result, statusCode: StationHttpSecurity.RuntimeStatus(new(result.State)));
    }

    private static async Task<IResult> UnlockAsync(HttpContext context, StationRuntimeService runtime, StationHttpSecurity security)
    {
        if (!StationHttpSecurity.Query(context))
        { return StationHttpSecurity.InvalidRequest(); }
        var gate = await security.AdmitRuntimeAsync(context, runtime, true).ConfigureAwait(false);
        if (gate.Failure is not null)
        { return gate.Failure; }
        StationUnlockRequest? request = await StationHttpSecurity.BodyAsync<StationUnlockRequest>(context).ConfigureAwait(false);
        if (request is null || request.OperationId == Guid.Empty || request.StaffMemberId == Guid.Empty ||
            request.ExpectedGeneration < 1 || !StationPinVerifier.IsPin(request.Pin))
        { return StationHttpSecurity.InvalidRequest(); }
        return StationHttpSecurity.Runtime(await runtime.UnlockAsync(gate.Admission!.Credential, request.OperationId,
            request.StaffMemberId, request.ExpectedGeneration, request.Pin, context.RequestAborted).ConfigureAwait(false));
    }

    private static async Task<IResult> LockAsync(HttpContext context, StationRuntimeService runtime, StationHttpSecurity security)
    {
        if (!StationHttpSecurity.Query(context))
        { return StationHttpSecurity.InvalidRequest(); }
        var gate = await security.AdmitRuntimeAsync(context, runtime, true).ConfigureAwait(false);
        if (gate.Failure is not null)
        { return gate.Failure; }
        StationLockRequest? request = await StationHttpSecurity.BodyAsync<StationLockRequest>(context).ConfigureAwait(false);
        if (request is null || request.OperationId == Guid.Empty || request.ExpectedGeneration < 1)
        { return StationHttpSecurity.InvalidRequest(); }
        return StationHttpSecurity.Runtime(await runtime.LockAsync(gate.Admission!.Credential, request.OperationId,
            request.ExpectedGeneration, context.RequestAborted).ConfigureAwait(false));
    }

    private static async Task<IResult> ActivityAsync(HttpContext context, StationRuntimeService runtime, StationHttpSecurity security)
    {
        if (!StationHttpSecurity.Query(context))
        { return StationHttpSecurity.InvalidRequest(); }
        var gate = await security.AdmitRuntimeAsync(context, runtime, true).ConfigureAwait(false);
        if (gate.Failure is not null)
        { return gate.Failure; }
        StationActivityRequest? request = await StationHttpSecurity.BodyAsync<StationActivityRequest>(context).ConfigureAwait(false);
        if (request is null || request.OperationId == Guid.Empty || request.ActorSessionId == Guid.Empty || request.ExpectedGeneration < 1)
        { return StationHttpSecurity.InvalidRequest(); }
        if (!StationHttpSecurity.Actor(gate.Admission!.Session, request.ActorSessionId, request.ExpectedGeneration, out var actor))
        { return StationHttpSecurity.Runtime(new(StationSessionState.StateChanged)); }
        return StationHttpSecurity.Runtime(await runtime.ForegroundActivityAsync(gate.Admission.Credential,
            request.OperationId, actor!, context.RequestAborted).ConfigureAwait(false));
    }

    private static async Task<IResult> RedeemAsync(HttpContext context, StationRuntimeService runtime, StationHttpSecurity security)
    {
        if (!StationHttpSecurity.Query(context))
        { return StationHttpSecurity.InvalidRequest(); }
        var gate = await security.AdmitRuntimeAsync(context, runtime, true).ConfigureAwait(false);
        if (gate.Failure is not null)
        { return gate.Failure; }
        StationRedeemSetupRequest? request = await StationHttpSecurity.BodyAsync<StationRedeemSetupRequest>(context).ConfigureAwait(false);
        if (request is null || request.OperationId == Guid.Empty || request.SetupGrantId == Guid.Empty || !StationPinVerifier.IsPin(request.Pin))
        { return StationHttpSecurity.InvalidRequest(); }
        return StationHttpSecurity.Runtime(await runtime.RedeemSeededSetupAsync(gate.Admission!.Credential,
            request.OperationId, request.SetupGrantId, request.Pin, context.RequestAborted).ConfigureAwait(false));
    }

    private static async Task<IResult> DeparturesAsync(HttpContext context, StationRuntimeService runtime,
        StationFirstJobService jobs, StationHttpSecurity security,
        [FromHeader(Name = StationApiOptions.ActorHeaderName)] string? actorSessionId = null,
        [FromHeader(Name = StationApiOptions.GenerationHeaderName)] string? generation = null,
        [FromQuery] string? pageSize = null, [FromQuery] string? cursor = null)
    {
        if (!StationHttpSecurity.Query(context, "pageSize", "cursor") ||
            context.Request.Headers[StationApiOptions.ActorHeaderName].Count != 1 ||
            context.Request.Headers[StationApiOptions.GenerationHeaderName].Count != 1 ||
            actorSessionId?.Length != 36 || generation is not { Length: > 0 and <= 19 } ||
            !StationHttpSecurity.Number(pageSize, 25, 25, out int size) ||
            !StationHttpSecurity.Id(actorSessionId, out Guid actorId) ||
            !long.TryParse(generation, NumberStyles.None, CultureInfo.InvariantCulture, out long expectedGeneration) || expectedGeneration < 1)
        { return StationHttpSecurity.InvalidRequest(); }
        var gate = await security.AdmitRuntimeAsync(context, runtime, false).ConfigureAwait(false);
        if (gate.Failure is not null)
        { return gate.Failure; }
        if (!StationHttpSecurity.Actor(gate.Admission!.Session, actorId, expectedGeneration, out var actor))
        { return StationHttpSecurity.Runtime(new(StationSessionState.StateChanged)); }
        if (!security.TryDepartureCursor(cursor, gate.Admission.Session, out var after))
        { return StationHttpSecurity.InvalidRequest(); }
        var result = await jobs.ListDeparturesAsync(gate.Admission.Credential, actor!, size, after, context.RequestAborted).ConfigureAwait(false);
        string? continuation = result.Continuation is { } next ? security.ProtectDepartureCursor(next, gate.Admission.Session) : null;
        return Results.Json(new StationDeparturesResponse(result.State, result.Items, continuation, result.PropertyId,
            result.PropertyLocalDate), statusCode: StationHttpSecurity.JobStatus(result.State));
    }

    private static async Task<IResult> CheckOutOutcomeAsync(HttpContext context, StationRuntimeService runtime,
        StationFirstJobService jobs, StationHttpSecurity security)
    {
        if (!StationHttpSecurity.Query(context))
        { return StationHttpSecurity.InvalidRequest(); }
        // POST keeps historical coordinates out of URLs. It remains an outcome-only read, with current device CSRF.
        var gate = await security.AdmitRuntimeAsync(context, runtime, true).ConfigureAwait(false);
        if (gate.Failure is not null)
        { return gate.Failure; }
        StationCheckOutOutcomeRequest? request = await StationHttpSecurity.BodyAsync<StationCheckOutOutcomeRequest>(context).ConfigureAwait(false);
        if (request is null || request.OperationId == Guid.Empty || request.ReservationId == Guid.Empty ||
            request.ExpectedVersion < 1 || request.BrowserSessionId == Guid.Empty || request.ActorSessionId == Guid.Empty ||
            request.ExpectedGeneration < 1)
        { return StationHttpSecurity.InvalidRequest(); }
        if (request.BrowserSessionId != gate.Admission!.Session.BrowserSessionId)
        { return Results.Json(new StationCheckOutOutcome(StationCheckOutOutcomeState.Conflict), statusCode: StatusCodes.Status409Conflict); }
        // Deliberately no current-actor equality gate here: the read validates exact persisted original attribution.
        var result = await jobs.ResolveCheckOutOutcomeAsync(gate.Admission.Credential, request.BrowserSessionId,
            request.ActorSessionId, request.ExpectedGeneration, request.OperationId, request.ReservationId,
            request.ExpectedVersion, context.RequestAborted).ConfigureAwait(false);
        return Results.Json(result, statusCode: result.State switch
        {
            StationCheckOutOutcomeState.ReleasePending or StationCheckOutOutcomeState.Completed or
            StationCheckOutOutcomeState.ReleaseRejected or StationCheckOutOutcomeState.Recorded or StationCheckOutOutcomeState.Pending => StatusCodes.Status200OK,
            StationCheckOutOutcomeState.Conflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status503ServiceUnavailable
        });
    }

    private static async Task<IResult> CheckOutAsync(HttpContext context, StationRuntimeService runtime,
        StationFirstJobService jobs, StationHttpSecurity security)
    {
        if (!StationHttpSecurity.Query(context))
        { return StationHttpSecurity.InvalidRequest(); }
        var gate = await security.AdmitRuntimeAsync(context, runtime, true).ConfigureAwait(false);
        if (gate.Failure is not null)
        { return gate.Failure; }
        StationCheckOutRequest? request = await StationHttpSecurity.BodyAsync<StationCheckOutRequest>(context).ConfigureAwait(false);
        if (request is null || request.OperationId == Guid.Empty || request.ReservationId == Guid.Empty ||
            request.ExpectedVersion < 1 || request.ActorSessionId == Guid.Empty || request.ExpectedGeneration < 1)
        { return StationHttpSecurity.InvalidRequest(); }
        if (!StationHttpSecurity.Actor(gate.Admission!.Session, request.ActorSessionId, request.ExpectedGeneration, out var actor))
        { return StationHttpSecurity.Runtime(new(StationSessionState.StateChanged)); }
        var result = await jobs.CheckOutAsync(gate.Admission.Credential, actor!, request.OperationId, request.ReservationId,
            request.ExpectedVersion, context.RequestAborted).ConfigureAwait(false);
        return Results.Json(result, statusCode: StationHttpSecurity.JobStatus(result.State));
    }

    private static async Task<IResult> ArrivalsAsync(HttpContext context, StationRuntimeService runtime,
        StationFirstJobService jobs, StationHttpSecurity security,
        [FromHeader(Name = StationApiOptions.ActorHeaderName)] string? actorSessionId = null,
        [FromHeader(Name = StationApiOptions.GenerationHeaderName)] string? generation = null,
        [FromQuery] string? pageSize = null, [FromQuery] string? cursor = null)
    {
        if (!StationHttpSecurity.Query(context, "pageSize", "cursor") ||
            context.Request.Headers[StationApiOptions.ActorHeaderName].Count != 1 ||
            context.Request.Headers[StationApiOptions.GenerationHeaderName].Count != 1 ||
            actorSessionId?.Length != 36 || generation is not { Length: > 0 and <= 19 } ||
            !StationHttpSecurity.Number(pageSize, 25, 25, out int size) ||
            !StationHttpSecurity.Id(actorSessionId, out Guid actorId) ||
            !long.TryParse(generation, NumberStyles.None, CultureInfo.InvariantCulture, out long expectedGeneration) || expectedGeneration < 1)
        { return StationHttpSecurity.InvalidRequest(); }
        var gate = await security.AdmitRuntimeAsync(context, runtime, false).ConfigureAwait(false);
        if (gate.Failure is not null)
        { return gate.Failure; }
        if (!StationHttpSecurity.Actor(gate.Admission!.Session, actorId, expectedGeneration, out var actor))
        { return StationHttpSecurity.Runtime(new(StationSessionState.StateChanged)); }
        if (!security.TryCursor(cursor, gate.Admission.Session, out var after))
        { return StationHttpSecurity.InvalidRequest(); }
        var result = await jobs.ListAsync(gate.Admission.Credential, actor!, size, after, context.RequestAborted).ConfigureAwait(false);
        string? continuation = result.Continuation is { } next ? security.ProtectCursor(next, gate.Admission.Session) : null;
        return Results.Json(new StationArrivalsResponse(result.State, result.Items, continuation, result.PropertyId,
            result.PropertyLocalDate), statusCode: StationHttpSecurity.JobStatus(result.State));
    }

    private static async Task<IResult> CheckInOutcomeAsync(HttpContext context, StationRuntimeService runtime,
        StationFirstJobService jobs, StationHttpSecurity security)
    {
        if (!StationHttpSecurity.Query(context))
        { return StationHttpSecurity.InvalidRequest(); }
        // POST keeps historical coordinates out of URLs. It remains an outcome-only read, with current device CSRF.
        var gate = await security.AdmitRuntimeAsync(context, runtime, true).ConfigureAwait(false);
        if (gate.Failure is not null)
        { return gate.Failure; }
        StationCheckInOutcomeRequest? request = await StationHttpSecurity.BodyAsync<StationCheckInOutcomeRequest>(context).ConfigureAwait(false);
        if (request is null || request.OperationId == Guid.Empty || request.ReservationId == Guid.Empty ||
            request.ExpectedVersion < 1 || request.BrowserSessionId == Guid.Empty || request.ActorSessionId == Guid.Empty ||
            request.ExpectedGeneration < 1)
        { return StationHttpSecurity.InvalidRequest(); }
        if (request.BrowserSessionId != gate.Admission!.Session.BrowserSessionId)
        { return Results.Json(new StationCheckInOutcome(StationCheckInOutcomeState.Conflict), statusCode: StatusCodes.Status409Conflict); }
        // Deliberately no current-actor equality gate here: the read validates exact persisted original attribution.
        var result = await jobs.ResolveCheckInOutcomeAsync(gate.Admission.Credential, request.BrowserSessionId,
            request.ActorSessionId, request.ExpectedGeneration, request.OperationId, request.ReservationId,
            request.ExpectedVersion, context.RequestAborted).ConfigureAwait(false);
        return Results.Json(result, statusCode: result.State switch
        {
            StationCheckInOutcomeState.Applied or StationCheckInOutcomeState.Pending => StatusCodes.Status200OK,
            StationCheckInOutcomeState.Conflict => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status503ServiceUnavailable
        });
    }

    private static async Task<IResult> CheckInAsync(HttpContext context, StationRuntimeService runtime,
        StationFirstJobService jobs, StationHttpSecurity security)
    {
        if (!StationHttpSecurity.Query(context))
        { return StationHttpSecurity.InvalidRequest(); }
        var gate = await security.AdmitRuntimeAsync(context, runtime, true).ConfigureAwait(false);
        if (gate.Failure is not null)
        { return gate.Failure; }
        StationCheckInRequest? request = await StationHttpSecurity.BodyAsync<StationCheckInRequest>(context).ConfigureAwait(false);
        if (request is null || request.OperationId == Guid.Empty || request.ReservationId == Guid.Empty ||
            request.ExpectedVersion < 1 || request.ActorSessionId == Guid.Empty || request.ExpectedGeneration < 1)
        { return StationHttpSecurity.InvalidRequest(); }
        if (!StationHttpSecurity.Actor(gate.Admission!.Session, request.ActorSessionId, request.ExpectedGeneration, out var actor))
        { return StationHttpSecurity.Runtime(new(StationSessionState.StateChanged)); }
        var result = await jobs.CheckInAsync(gate.Admission.Credential, actor!, request.OperationId, request.ReservationId,
            request.ExpectedVersion, context.RequestAborted).ConfigureAwait(false);
        return Results.Json(result, statusCode: StationHttpSecurity.JobStatus(result.State));
    }
}
