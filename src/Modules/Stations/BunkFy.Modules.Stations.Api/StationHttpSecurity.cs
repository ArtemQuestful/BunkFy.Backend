namespace BunkFy.Modules.Stations.Api;

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using BunkFy.Modules.Reservations.Contracts.Stations;
using BunkFy.Modules.Stations.Application;
using BunkFy.Modules.Stations.Contracts;
using Gma.Framework.Runtime.Time;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

/// <summary>Station transport only. Application services remain the owners of all authority.</summary>
public sealed class StationHttpSecurity(IDataProtectionProvider protection, IOptions<StationApiOptions> options,
    IOptions<StationOptions> coreOptions, ISystemClock clock)
{
    private const int MaximumBodyBytes = 8192;
    private static readonly ConditionalWeakTable<JsonSerializerOptions, JsonSerializerOptions> RequestJsonOptions = [];
    private readonly IDataProtector csrf = protection.CreateProtector("BunkFy.Stations.Api.Csrf.v1");
    private readonly IDataProtector departureCursors = protection.CreateProtector("BunkFy.Stations.Api.DeparturesCursor.v1");
    private readonly IDataProtector cursors = protection.CreateProtector("BunkFy.Stations.Api.ArrivalsCursor.v1");

    internal static bool IsStationPath(PathString path) => path.StartsWithSegments(StationApiOptions.RuntimePath) ||
        path.StartsWithSegments("/api/station-management") || path.StartsWithSegments("/api/station-setup");

    internal static void MarkSensitive(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.Expires = "0";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers.XContentTypeOptions = "nosniff";
    }

    internal static IResult? CheckLane(HttpContext context, StationApiOptions settings, out string? credential)
    {
        credential = null;
        HttpRequest request = context.Request;
        bool runtime = request.Path.StartsWithSegments(StationApiOptions.RuntimePath);
        if (!request.IsHttps)
        { return Failure("station.https_required", StatusCodes.Status400BadRequest); }
        if (request.QueryString.Value?.Length > 8192 || request.Headers.Cookie.Sum(h => h?.Length ?? 0) > 8192)
        { return Failure("station.request_invalid", StatusCodes.Status400BadRequest); }

        int count = 0;
        bool primaryCookie = false;
        foreach (string? line in request.Headers.Cookie)
        {
            if (line is null)
            { continue; }
            foreach (string part in line.Split(';'))
            {
                string segment = part.Trim();
                int separator = segment.IndexOf('=', StringComparison.Ordinal);
                string name = separator < 0 ? segment : segment[..separator].Trim();
                if (name is "gma.auth.access" or "gma.auth.refresh")
                { primaryCookie = true; }
                if (name == StationApiOptions.CookieName)
                {
                    count++;
                    credential = separator < 0 ? null : segment[(separator + 1)..];
                }
            }
        }
        // No legacy header/JWS, API-key, primary-cookie or caller scope lane is accepted at runtime.
        bool alternate = request.Headers.ContainsKey("X-Station-Credential") ||
            request.Headers.ContainsKey("X-Station-Token") || request.Headers.ContainsKey("X-Api-Key");
        if (runtime)
        {
            if (request.Headers.ContainsKey("Authorization") || primaryCookie || alternate ||
                request.Headers.ContainsKey("X-Tenant-Id") || request.Headers.ContainsKey("X-Property-Id") ||
                count != 1 || credential is null || StationCredentialEncoding.Digest(credential) is null)
            { return Failure("station.credential_lane_invalid", StatusCodes.Status401Unauthorized); }
        }
        else
        {
            StringValues authorization = request.Headers.Authorization;
            if (count != 0 || alternate || authorization.Count != 1 || authorization[0] is not { } value ||
                value.Length is < 8 or > 8192 || !value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
                value[7..].Any(c => char.IsWhiteSpace(c) || c == ','))
            { return Failure("station.primary_credential_required", StatusCodes.Status401Unauthorized); }
        }
        return CheckOrigin(request, settings, runtime && !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method));
    }

    private static IResult? CheckOrigin(HttpRequest request, StationApiOptions settings, bool unsafeRuntime)
    {
        StringValues origin = request.Headers.Origin;
        if ((unsafeRuntime && origin.Count != 1) || origin.Count > 1 ||
            (origin.Count == 1 && (origin[0] is not { } value || !StationApiOptions.IsOrigin(value) ||
                !settings.AllowedOrigins.Contains(value, StringComparer.Ordinal))))
        { return Failure("station.origin_denied", StatusCodes.Status403Forbidden); }
        StringValues site = request.Headers["Sec-Fetch-Site"];
        StringValues mode = request.Headers["Sec-Fetch-Mode"];
        StringValues destination = request.Headers["Sec-Fetch-Dest"];
        if (site.Count > 1 || (site.Count == 1 && site[0] != "same-origin") ||
            mode.Count > 1 || (mode.Count == 1 && mode[0] is not ("cors" or "same-origin")) ||
            destination.Count > 1 || (destination.Count == 1 && destination[0] != "empty"))
        { return Failure("station.fetch_context_denied", StatusCodes.Status403Forbidden); }
        return null;
    }

    internal async ValueTask<object?> FilterAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        HttpContext context = invocation.HttpContext;
        MarkSensitive(context);
        IResult? rejected = CheckLane(context, options.Value, out _);
        if (rejected is not null)
        { return rejected; }
        try
        { return await next(invocation).ConfigureAwait(false); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Deliberately do not log exceptions/payloads on this PIN/credential boundary.
            return Failure("station.unavailable", StatusCodes.Status503ServiceUnavailable);
        }
    }

    internal async Task<(RuntimeAdmission? Admission, IResult? Failure)> AdmitRuntimeAsync(
        HttpContext context, IStationSessionReader runtime, bool unsafeRequest)
    {
        IResult? failure = CheckLane(context, options.Value, out string? credential);
        if (failure is not null)
        { return (null, failure); }
        CsrfBinding? token = null;
        if (unsafeRequest)
        {
            StringValues header = context.Request.Headers[StationApiOptions.CsrfHeaderName];
            if (header.Count != 1 || header[0] is not { Length: > 0 and <= 4096 } encoded ||
                (token = Unprotect<CsrfBinding>(this.csrf, encoded)) is null ||
                token.ExpiresAtUtc <= clock.UtcNow || token.ExpiresAtUtc > clock.UtcNow.AddMinutes(options.Value.CsrfMinutes) ||
                token.StationId == Guid.Empty || token.BrowserSessionId == Guid.Empty || token.Generation < 1)
            { return (null, Failure("station.csrf_invalid", StatusCodes.Status403Forbidden)); }
        }
        StationRuntimeResponse current = await runtime.ReadAsync(credential!, context.RequestAborted).ConfigureAwait(false);
        if (current.State is not (StationSessionState.Active or StationSessionState.Locked) || current.Session is not { } session)
        {
            if (current.State == StationSessionState.Invalid)
            { ClearCookie(context); }
            // Never pass along unexpected coordinates in a failed or pending admission.
            return (null, Runtime(new(current.State)));
        }
        if (token is not null && (token.StationId != session.StationId || token.BrowserSessionId != session.BrowserSessionId ||
            token.Generation != session.Generation))
        { return (null, Failure("station.csrf_changed", StatusCodes.Status409Conflict)); }
        return (new(credential!, current), null);
    }

    internal StationCurrentResponse Current(StationRuntimeResponse response)
    {
        if (response.State is not (StationSessionState.Active or StationSessionState.Locked) || response.Session is not { } session)
        { return new(new(response.State)); }
        DateTimeOffset expires = clock.UtcNow.AddMinutes(options.Value.CsrfMinutes);
        if (session.PairingExpiresAtUtc < expires)
        { expires = session.PairingExpiresAtUtc; }
        var token = new CsrfBinding(session.StationId, session.BrowserSessionId, session.Generation, expires);
        return new(response, this.csrf.Protect(JsonSerializer.Serialize(token)), expires);
    }

    internal void IssueCookie(HttpContext context, StationPairingHandoff handoff)
    {
        if (handoff.Credential is not { } credential)
        { return; }
        if (!context.Request.IsHttps || handoff.Response.State != StationManagementState.Applied ||
            handoff.Response.Receipt?.Kind is not (StationOperationKind.Register or StationOperationKind.Pair) ||
            StationCredentialEncoding.Digest(credential) is null)
        { throw new InvalidOperationException("Station pairing cookie could not be issued."); }
        var cookie = CookieOptions();
        cookie.Expires = clock.UtcNow.AddDays(coreOptions.Value.PairingDays);
        context.Response.Cookies.Append(StationApiOptions.CookieName, credential, cookie);
    }

    internal static void ClearCookie(HttpContext context) => context.Response.Cookies.Delete(StationApiOptions.CookieName, CookieOptions());

    private static CookieOptions CookieOptions() => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = StationApiOptions.RuntimePath,
        IsEssential = true
        // No Domain: the cookie is host-only. Path is also retained when clearing it.
    };

    internal string ProtectCursor(StationArrivalCursor cursor, StationSessionSnapshot session)
    {
        var value = new CursorBinding(session.StationId, session.BrowserSessionId, session.Generation,
            clock.UtcNow.AddMinutes(5), cursor);
        return this.cursors.Protect(JsonSerializer.Serialize(value));
    }

    internal bool TryCursor(string? encoded, StationSessionSnapshot session, out StationArrivalCursor? cursor)
    {
        cursor = null;
        if (encoded is null)
        { return true; }
        if (encoded.Length is < 1 or > 4096 || Unprotect<CursorBinding>(this.cursors, encoded) is not { } token ||
            token.ExpiresAtUtc <= clock.UtcNow || token.ExpiresAtUtc > clock.UtcNow.AddMinutes(5) ||
            token.StationId != session.StationId || token.BrowserSessionId != session.BrowserSessionId ||
            token.Generation != session.Generation || token.Cursor is not { } found || found.Arrival == default ||
            found.ReservationId == Guid.Empty || found.NormalizedGuestName is not { Length: > 0 and <= 300 })
        { return false; }
        cursor = found;
        return true;
    }

    internal string ProtectDepartureCursor(StationDepartureCursor cursor, StationSessionSnapshot session)
    {
        var value = new DepartureCursorBinding(session.StationId, session.BrowserSessionId, session.Generation,
            clock.UtcNow.AddMinutes(5), cursor);
        return this.departureCursors.Protect(JsonSerializer.Serialize(value));
    }

    internal bool TryDepartureCursor(string? encoded, StationSessionSnapshot session, out StationDepartureCursor? cursor)
    {
        cursor = null;
        if (encoded is null)
        { return true; }
        if (encoded.Length is < 1 or > 4096 || Unprotect<DepartureCursorBinding>(this.departureCursors, encoded) is not { } token ||
            token.ExpiresAtUtc <= clock.UtcNow || token.ExpiresAtUtc > clock.UtcNow.AddMinutes(5) ||
            token.StationId != session.StationId || token.BrowserSessionId != session.BrowserSessionId ||
            token.Generation != session.Generation || token.Cursor is not { } found || found.Departure == default ||
            found.ReservationId == Guid.Empty || found.NormalizedGuestName is not { Length: > 0 and <= 300 })
        { return false; }
        cursor = found;
        return true;
    }

    private static T? Unprotect<T>(IDataProtector protector, string value) where T : class
    {
        try
        { return JsonSerializer.Deserialize<T>(protector.Unprotect(value)); }
        catch (CryptographicException) { return null; }
        catch (JsonException) { return null; }
        catch (FormatException) { return null; }
    }

    internal static async Task<T?> BodyAsync<T>(HttpContext context) where T : class
    {
        HttpRequest request = context.Request;
        if (!request.HasJsonContentType() || request.ContentLength is > MaximumBodyBytes ||
            request.Headers.ContainsKey("Content-Encoding"))
        { return null; }
        byte[] buffer = new byte[MaximumBodyBytes + 1];
        try
        {
            int length = 0;
            while (length < buffer.Length)
            {
                int read = await request.Body.ReadAsync(buffer.AsMemory(length), context.RequestAborted).ConfigureAwait(false);
                if (read == 0)
                { break; }
                length += read;
            }
            if (length is 0 or > MaximumBodyBytes)
            { return null; }
            using JsonDocument json = JsonDocument.Parse(buffer.AsMemory(0, length), new() { MaxDepth = 4 });
            if (json.RootElement.ValueKind != JsonValueKind.Object)
            { return null; }
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty field in json.RootElement.EnumerateObject())
            {
                if (!names.Add(field.Name) || field.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                { return null; }
            }
            // Preserve the host's wire converters, including its enum representation.
            var hostJson = context.RequestServices
                .GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
            var requestJson = RequestJsonOptions.GetValue(hostJson, static source => new JsonSerializerOptions(source)
            {
                PropertyNameCaseInsensitive = false,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                MaxDepth = 4
            });
            return json.RootElement.Deserialize<T>(requestJson);
        }
        catch (JsonException) { return null; }
        catch (BadHttpRequestException) { return null; }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    internal static bool Query(HttpContext context, params string[] allowed) =>
        context.Request.Query.All(pair => allowed.Contains(pair.Key, StringComparer.Ordinal) && pair.Value.Count == 1 &&
            pair.Value[0] is { Length: <= 4096 });

    internal static bool Number(string? text, int fallback, int maximum, out int value)
    {
        value = fallback;
        return text is null ||
            (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 1 && value <= maximum);
    }

    internal static bool Id(string? value, out Guid id) => Guid.TryParseExact(value, "D", out id) && id != Guid.Empty;

    internal static bool Actor(StationSessionSnapshot session, Guid actorSessionId, long generation, out StationActorCoordinate? actor)
    {
        actor = session.Actor;
        return actor is not null && actorSessionId != Guid.Empty && generation > 0 &&
            actor.ActorSessionId == actorSessionId && actor.Generation == generation && session.Generation == generation;
    }

    internal static IResult InvalidRequest() => Failure("station.request_invalid", StatusCodes.Status400BadRequest);
    internal static IResult Failure(string code, int statusCode) => Results.Json(new StationApiFailure(code), statusCode: statusCode);
    internal static IResult Management(StationManagementResponse response)
    {
        StationManagementHttpResponse body = ManagementResponse(response);
        return Results.Json(body, statusCode: ManagementStatus(body.State));
    }

    internal static StationManagementHttpResponse ManagementResponse(StationManagementResponse response)
    {
        if (response.State != StationManagementState.Applied)
        { return new(response.State); }
        if (response.Receipt is not { } receipt)
        { return new(StationManagementState.StateChanged); }
        StationManagementHttpReceipt? projected = receipt.Kind switch
        {
            StationOperationKind.Register or StationOperationKind.Pair when receipt.IssuerKind == StationSetupIssuerKind.Manager &&
                Present(receipt.StationId) && Present(receipt.BrowserSessionId) && Present(receipt.PropertyId) &&
                Present(receipt.IssuerSessionId) && receipt.Version > 0 =>
                new(receipt.Kind, StationId: receipt.StationId, BrowserSessionId: receipt.BrowserSessionId,
                    PropertyId: receipt.PropertyId, Version: receipt.Version, OriginalIssuerSessionId: receipt.IssuerSessionId),
            StationOperationKind.RevokeStation when Present(receipt.StationId) && Present(receipt.PropertyId) && receipt.Version > 0 =>
                new(receipt.Kind, StationId: receipt.StationId, PropertyId: receipt.PropertyId, Version: receipt.Version),
            StationOperationKind.RegisterStaff or StationOperationKind.UnregisterStaff or
                StationOperationKind.GrantCheckIn or StationOperationKind.RevokeGrant or
                StationOperationKind.GrantCheckOut or StationOperationKind.RevokeCheckOutGrant when
                    Present(receipt.PropertyId) && Present(receipt.StaffMemberId) && receipt.Version > 0 =>
                new(receipt.Kind, PropertyId: receipt.PropertyId, StaffMemberId: receipt.StaffMemberId, Version: receipt.Version),
            StationOperationKind.Reset or StationOperationKind.OwnPin when Present(receipt.StaffMemberId) && receipt.Version > 0 =>
                new(receipt.Kind, StaffMemberId: receipt.StaffMemberId, Version: receipt.Version),
            StationOperationKind.IssueSetup when Present(receipt.StationId) && Present(receipt.BrowserSessionId) &&
                Present(receipt.PropertyId) && Present(receipt.StaffMemberId) && Present(receipt.SetupGrantId) && receipt.Version >= 0 =>
                new(receipt.Kind, StationId: receipt.StationId, BrowserSessionId: receipt.BrowserSessionId,
                    PropertyId: receipt.PropertyId, StaffMemberId: receipt.StaffMemberId,
                    SetupGrantId: receipt.SetupGrantId, Version: receipt.Version),
            StationOperationKind.CancelSetup when Present(receipt.SetupGrantId) => new(receipt.Kind, SetupGrantId: receipt.SetupGrantId),
            _ => null
        };
        return projected is null ? new(StationManagementState.StateChanged) : new(response.State, projected);
    }

    private static bool Present(Guid? value) => value is { } id && id != Guid.Empty;

    internal static void DescribeFailures(RouteGroupBuilder group)
    {
        // A failure can contain a transport code or an existing typed owner state. Do not
        // falsely describe all of these existing bodies as one transport-error schema.
        foreach (int status in new[] { 400, 401, 403, 409, 429, 503 })
        {
            group.WithMetadata(new ProducesResponseTypeMetadata(status, typeof(object), ["application/json"])
            {
                Description = "Station transport failure or guest-free owner state; inspect code or state."
            });
        }
    }

    internal static int ManagementStatus(StationManagementState state) => state switch
    {
        StationManagementState.Applied => StatusCodes.Status200OK,
        StationManagementState.Denied => StatusCodes.Status403Forbidden,
        StationManagementState.NotFound => StatusCodes.Status404NotFound,
        StationManagementState.StateChanged => StatusCodes.Status409Conflict,
        StationManagementState.CapacityReached => StatusCodes.Status429TooManyRequests,
        _ => StatusCodes.Status503ServiceUnavailable
    };
    internal static IResult Runtime(StationRuntimeResponse response) => Results.Json(response, statusCode: RuntimeStatus(response));
    internal static int RuntimeStatus(StationRuntimeResponse response) => response.Outcome switch
    {
        StationCoreOutcome.Throttled => StatusCodes.Status429TooManyRequests,
        StationCoreOutcome.Rejected => StatusCodes.Status403Forbidden,
        StationCoreOutcome.Conflict => StatusCodes.Status409Conflict,
        StationCoreOutcome.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => response.State switch
        {
            StationSessionState.Locked or StationSessionState.Active or StationSessionState.HandoffPending => StatusCodes.Status200OK,
            StationSessionState.Invalid => StatusCodes.Status401Unauthorized,
            StationSessionState.StateChanged => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status503ServiceUnavailable
        }
    };
    internal static int JobStatus(StationReservationState state) => state switch
    {
        StationReservationState.Ready or StationReservationState.Applied => StatusCodes.Status200OK,
        StationReservationState.Denied => StatusCodes.Status403Forbidden,
        StationReservationState.Conflict or StationReservationState.Incomplete => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status503ServiceUnavailable
    };

    internal sealed class RuntimeAdmission(string credential, StationRuntimeResponse current)
    {
        internal string Credential { get; } = credential;
        internal StationRuntimeResponse Current { get; } = current;
        internal StationSessionSnapshot Session => this.Current.Session!;
        public override string ToString() => nameof(RuntimeAdmission);
    }
    private sealed record CsrfBinding(Guid StationId, Guid BrowserSessionId, long Generation, DateTimeOffset ExpiresAtUtc);
    private sealed record DepartureCursorBinding(Guid StationId, Guid BrowserSessionId, long Generation,
        DateTimeOffset ExpiresAtUtc, StationDepartureCursor Cursor);
    private sealed record CursorBinding(Guid StationId, Guid BrowserSessionId, long Generation,
        DateTimeOffset ExpiresAtUtc, StationArrivalCursor Cursor);
}
