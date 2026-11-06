namespace BunkFy.Host.ServiceDefaults.Security.Stations;

using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

internal interface IStationAuthorizationResponseWriter
{
    Task WriteChallengeAsync(HttpContext context);

    Task WriteForbiddenAsync(HttpContext context);
}

internal sealed class StationAuthorizationResponseWriter : IStationAuthorizationResponseWriter
{
    private static readonly byte[] ChallengeBody = Encoding.UTF8.GetBytes(
        "{\"type\":\"about:blank\",\"title\":\"Authentication required\",\"status\":401," +
        "\"code\":\"Station.ActorAuthenticationRequired\"}");

    private static readonly byte[] ForbiddenBody = Encoding.UTF8.GetBytes(
        "{\"type\":\"about:blank\",\"title\":\"Operation forbidden\",\"status\":403," +
        "\"code\":\"Station.OperationForbidden\"}");

    public Task WriteChallengeAsync(HttpContext context) =>
        WriteAsync(
            context,
            StatusCodes.Status401Unauthorized,
            ChallengeBody,
            includeChallenge: true);

    public Task WriteForbiddenAsync(HttpContext context) =>
        WriteAsync(
            context,
            StatusCodes.Status403Forbidden,
            ForbiddenBody,
            includeChallenge: false);

    private static async Task WriteAsync(
        HttpContext context,
        int statusCode,
        byte[] body,
        bool includeChallenge)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";
        context.Response.ContentLength = body.Length;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        if (includeChallenge)
        {
            context.Response.Headers[HeaderNames.WWWAuthenticate] = StationAuthenticationDefaults.WireScheme;
        }

        await context.Response.Body.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
    }
}
