namespace Integration.Tests.Support;

using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

internal sealed record StationK0SchemeProbeObservation(
    string Scheme,
    string Stage,
    string Category);

internal sealed record StationK0RequestProbeObservation(
    string Method,
    string Path,
    int AuthorizationFieldValueCount,
    IReadOnlyList<int> AuthorizationFieldValueLengths);

internal sealed record StationK0FinalIdentityProbeObservation(
    IReadOnlyList<string> AuthenticationTypes);

internal sealed class StationK0EvidenceProbe
{
    private readonly ConcurrentQueue<StationK0SchemeProbeObservation> schemeObservations = new();
    private readonly ConcurrentQueue<StationK0RequestProbeObservation> requestObservations = new();
    private readonly ConcurrentQueue<StationK0FinalIdentityProbeObservation> finalIdentityObservations = new();

    public IReadOnlyList<StationK0SchemeProbeObservation> SchemeObservations =>
        this.schemeObservations.ToArray();

    public IReadOnlyList<StationK0RequestProbeObservation> RequestObservations =>
        this.requestObservations.ToArray();

    public IReadOnlyList<StationK0FinalIdentityProbeObservation> FinalIdentityObservations =>
        this.finalIdentityObservations.ToArray();

    public void ObserveScheme(string scheme, string stage, string category) =>
        this.schemeObservations.Enqueue(new(scheme, stage, category));

    public void ObserveRequest(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        string[] authorizationValues = context.Request.Headers.Authorization
            .Select(static value => value ?? string.Empty)
            .ToArray();
        this.requestObservations.Enqueue(new(
            context.Request.Method,
            context.Request.Path.Value ?? "/",
            authorizationValues.Length,
            authorizationValues.Select(static value => value.Length).ToArray()));
    }

    public void ObserveFinalIdentities(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        string[] authenticationTypes = context.User.Identities
            .Where(static identity => identity.IsAuthenticated)
            .Select(static identity => identity.AuthenticationType ?? string.Empty)
            .Order(StringComparer.Ordinal)
            .ToArray();
        this.finalIdentityObservations.Enqueue(new(authenticationTypes));
    }

    public void Reset()
    {
        Clear(this.schemeObservations);
        Clear(this.requestObservations);
        Clear(this.finalIdentityObservations);
    }

    private static void Clear<T>(ConcurrentQueue<T> queue)
    {
        while (queue.TryDequeue(out _))
        {
        }
    }
}

internal sealed class StationK0EvidenceStartupFilter(StationK0EvidenceProbe probe) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => application =>
    {
        application.Use(async (context, nextMiddleware) =>
        {
            probe.ObserveRequest(context);
            await nextMiddleware(context).ConfigureAwait(false);
            probe.ObserveFinalIdentities(context);
        });
        next(application);
    };
}

internal sealed class StationK0EvidenceJwtBearerPostConfigure(StationK0EvidenceProbe probe)
    : IPostConfigureOptions<JwtBearerOptions>
{
    public void PostConfigure(string? name, JwtBearerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!string.Equals(name, JwtBearerDefaults.AuthenticationScheme, StringComparison.Ordinal))
        {
            return;
        }

        JwtBearerEvents events = options.Events;
        Func<MessageReceivedContext, Task> previousMessageReceived = events.OnMessageReceived;
        Func<TokenValidatedContext, Task> previousTokenValidated = events.OnTokenValidated;
        Func<AuthenticationFailedContext, Task> previousAuthenticationFailed = events.OnAuthenticationFailed;
        Func<JwtBearerChallengeContext, Task> previousChallenge = events.OnChallenge;
        Func<ForbiddenContext, Task> previousForbidden = events.OnForbidden;

        events.OnMessageReceived = async context =>
        {
            probe.ObserveScheme(JwtBearerDefaults.AuthenticationScheme, "authenticate", "invoked");
            await previousMessageReceived(context).ConfigureAwait(false);
        };
        events.OnTokenValidated = async context =>
        {
            await previousTokenValidated(context).ConfigureAwait(false);
            probe.ObserveScheme(JwtBearerDefaults.AuthenticationScheme, "authenticate", "succeeded");
        };
        events.OnAuthenticationFailed = async context =>
        {
            await previousAuthenticationFailed(context).ConfigureAwait(false);
            probe.ObserveScheme(
                JwtBearerDefaults.AuthenticationScheme,
                "authenticate",
                $"failed:{context.Exception.GetType().Name}");
        };
        events.OnChallenge = async context =>
        {
            await previousChallenge(context).ConfigureAwait(false);
            probe.ObserveScheme(JwtBearerDefaults.AuthenticationScheme, "authorization", "challenge");
        };
        events.OnForbidden = async context =>
        {
            await previousForbidden(context).ConfigureAwait(false);
            probe.ObserveScheme(JwtBearerDefaults.AuthenticationScheme, "authorization", "forbidden");
        };
    }
}
