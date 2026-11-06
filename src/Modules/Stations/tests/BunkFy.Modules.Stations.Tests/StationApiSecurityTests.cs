namespace BunkFy.Modules.Stations.Tests;

using System.Text;
using BunkFy.Modules.Stations.Api;
using BunkFy.Modules.Stations.Application;
using BunkFy.Modules.Stations.Contracts;
using Gma.Framework.Runtime.Time;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>Transport unit boundaries only; real PostgreSQL and browser acceptance are separate.</summary>
public sealed class StationApiSecurityTests
{
    private const string Origin = "https://station.example.test";
    private const string Credential = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static StationApiOptions Settings => new() { Enabled = true, AllowedOrigins = [Origin] };
    private static StationSessionSnapshot Session => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 4,
        null, StationDomainTests.Now.AddDays(1));

    [Theory]
    [InlineData("authorization")]
    [InlineData("empty-authorization")]
    [InlineData("primary-access")]
    [InlineData("primary-refresh")]
    [InlineData("duplicate-cookie")]
    [InlineData("missing-cookie")]
    [InlineData("malformed-cookie")]
    [InlineData("legacy-token")]
    [InlineData("api-key")]
    [InlineData("tenant")]
    [InlineData("property")]
    public async Task Mixed_runtime_lanes_stop_before_authentication_body_or_owners(string condition)
    {
        var context = RuntimeContext();
        switch (condition)
        {
            case "authorization":
                context.Request.Headers.Authorization = "Bearer synthetic";
                break;
            case "empty-authorization":
                context.Request.Headers.Authorization = "";
                break;
            case "primary-access":
                context.Request.Headers.Cookie += "; gma.auth.access=synthetic";
                break;
            case "primary-refresh":
                context.Request.Headers.Cookie += "; gma.auth.refresh=synthetic";
                break;
            case "duplicate-cookie":
                context.Request.Headers.Cookie += $"; {StationApiOptions.CookieName}={Credential}";
                break;
            case "missing-cookie":
                context.Request.Headers.Remove("Cookie");
                break;
            case "malformed-cookie":
                context.Request.Headers.Cookie = $"{StationApiOptions.CookieName}=bad";
                break;
            case "legacy-token":
                context.Request.Headers["X-Station-Token"] = "synthetic";
                break;
            case "api-key":
                context.Request.Headers["X-Api-Key"] = "synthetic";
                break;
            case "tenant":
                context.Request.Headers["X-Tenant-Id"] = Guid.NewGuid().ToString("D");
                break;
            case "property":
                context.Request.Headers["X-Property-Id"] = Guid.NewGuid().ToString("D");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(condition));
        }
        context.Request.Body = new ThrowOnReadStream();
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(_ => { });
        services.AddSingleton<IOptions<StationApiOptions>>(Options.Create(Settings));
        using var provider = services.BuildServiceProvider();
        context.RequestServices = provider;
        context.Response.Body = new MemoryStream();
        bool authenticationReached = false;
        var app = new ApplicationBuilder(provider);
        app.UseStationHttpSecurity();
        app.Run(_ => { authenticationReached = true; throw new InvalidOperationException("Auth must not run."); });
        await app.Build()(context);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.False(authenticationReached);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
    }

    [Theory]
    [InlineData("http", 400)]
    [InlineData("missing-origin", 403)]
    [InlineData("foreign-origin", 403)]
    [InlineData("null-origin", 403)]
    [InlineData("duplicate-origin", 403)]
    [InlineData("cross-site", 403)]
    [InlineData("navigation", 403)]
    public void Runtime_origin_and_fetch_guards_are_explicit(string condition, int status)
    {
        var context = RuntimeContext();
        switch (condition)
        {
            case "http":
                context.Request.Scheme = "http";
                break;
            case "missing-origin":
                context.Request.Headers.Remove("Origin");
                break;
            case "foreign-origin":
                context.Request.Headers.Origin = "https://other.example.test";
                break;
            case "null-origin":
                context.Request.Headers.Origin = "null";
                break;
            case "duplicate-origin":
                context.Request.Headers.Origin = new[] { Origin, Origin };
                break;
            case "cross-site":
                context.Request.Headers["Sec-Fetch-Site"] = "cross-site";
                break;
            case "navigation":
                context.Request.Headers["Sec-Fetch-Mode"] = "navigate";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(condition));
        }
        AssertStatus(status, StationHttpSecurity.CheckLane(context, Settings, out _));
    }

    [Fact]
    public void Primary_setup_requires_one_bearer_and_rejects_station_cookie()
    {
        var context = RuntimeContext();
        context.Request.Path = "/api/station-setup/properties/example/pin";
        context.Request.Headers.Authorization = "Bearer synthetic-primary";
        AssertStatus(401, StationHttpSecurity.CheckLane(context, Settings, out _));
        context.Request.Headers.Remove("Cookie");
        Assert.Null(StationHttpSecurity.CheckLane(context, Settings, out _));
        context.Request.Headers.Authorization = new[] { "Bearer first", "Bearer second" };
        AssertStatus(401, StationHttpSecurity.CheckLane(context, Settings, out _));
    }

    [Fact]
    public void Pairing_cookie_is_secret_host_only_secure_http_only_strict_and_path_scoped()
    {
        var context = RuntimeContext();
        var security = Security(new Clock());
        var response = new StationManagementResponse(StationManagementState.Applied,
            new(Kind: StationOperationKind.Pair));
        security.IssueCookie(context, new(response, Credential));
        string cookie = Assert.Single(context.Response.Headers.SetCookie)!;
        Assert.StartsWith(StationApiOptions.CookieName + "=", cookie, StringComparison.Ordinal);
        Assert.Contains("path=/api/station-runtime", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", cookie, StringComparison.OrdinalIgnoreCase);
        context.Response.Headers.Clear();
        StationHttpSecurity.ClearCookie(context);
        string cleared = Assert.Single(context.Response.Headers.SetCookie)!;
        Assert.Contains("path=/api/station-runtime", cleared, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cleared, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(StationSessionState.HandoffPending)]
    [InlineData(StationSessionState.Invalid)]
    [InlineData(StationSessionState.Unavailable)]
    [InlineData(StationSessionState.StateChanged)]
    public void No_csrf_or_coordinates_are_emitted_for_pending_or_failed_sessions(StationSessionState state)
    {
        var result = Security(new Clock()).Current(new(state, Session));
        Assert.Equal(new(state), result.Runtime);
        Assert.Null(result.CsrfToken);
        Assert.Null(result.CsrfExpiresAtUtc);
    }

    [Theory]
    [InlineData("valid", 0, 1)]
    [InlineData("missing", 403, 0)]
    [InlineData("tampered", 403, 0)]
    [InlineData("expired", 403, 0)]
    [InlineData("generation", 409, 1)]
    [InlineData("browser", 409, 1)]
    [InlineData("station", 409, 1)]
    public async Task Csrf_is_bound_to_current_station_browser_generation_and_expiry(string condition, int status, int reads)
    {
        var clock = new Clock();
        var security = Security(clock);
        var session = Session;
        var current = security.Current(new(StationSessionState.Locked, session));
        var context = RuntimeContext();
        context.Request.Headers[StationApiOptions.CsrfHeaderName] = current.CsrfToken;
        switch (condition)
        {
            case "valid":
                break;
            case "missing":
                context.Request.Headers.Remove(StationApiOptions.CsrfHeaderName);
                break;
            case "tampered":
                context.Request.Headers[StationApiOptions.CsrfHeaderName] = "invalid-protected-token";
                break;
            case "expired":
                clock.UtcNow = clock.UtcNow.AddMinutes(6);
                break;
            case "generation":
                session = session with { Generation = 5 };
                break;
            case "browser":
                session = session with { BrowserSessionId = Guid.NewGuid() };
                break;
            case "station":
                session = session with { StationId = Guid.NewGuid() };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(condition));
        }
        var reader = new Reader(new(StationSessionState.Locked, session));
        var result = await security.AdmitRuntimeAsync(context, reader, true);
        Assert.Equal(reads, reader.Reads);
        if (status == 0)
        {
            Assert.Null(result.Failure);
            Assert.NotNull(result.Admission);
        }
        else
        {
            Assert.Null(result.Admission);
            AssertStatus(status, result.Failure);
        }
    }

    [Theory]
    [InlineData("{\"operationId\":\"00000000-0000-0000-0000-000000000001\",\"expectedGeneration\":1,\"tenant\":\"injected\"}")]
    [InlineData("{\"expectedGeneration\":1,\"expectedGeneration\":2}")]
    [InlineData("{\"expectedGeneration\":{\"value\":1}}")]
    [InlineData("[]")]
    [InlineData("malformed")]
    public async Task Manual_request_reader_rejects_unknown_duplicate_nested_and_malformed_fields(string body)
    {
        var context = RuntimeContext();
        var services = new ServiceCollection();
        services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(_ => { });
        using var provider = services.BuildServiceProvider();
        context.RequestServices = provider;
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        Assert.Null(await StationHttpSecurity.BodyAsync<StationLockRequest>(context));
    }

    [Fact]
    public async Task Disabled_module_maps_no_routes_and_requires_no_station_services()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.Sources.Clear();
        var module = new StationsModule();
        module.AddServices(builder);
        await using var app = builder.Build();
        module.MapEndpoints(app);
        Assert.Empty(((IEndpointRouteBuilder)app).DataSources);
        Assert.Null(app.Services.GetService<StationRuntimeService>());
    }

    [Fact]
    public void Enabled_unsupported_provider_fails_before_mapping()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Stations:Http:Enabled"] = "true",
            ["Stations:Http:AllowedOrigins:0"] = Origin,
            ["Persistence:Provider"] = "SqlServer"
        });
        Assert.Throws<NotSupportedException>(() => new StationsModule().AddServices(builder));
    }

    private static StationHttpSecurity Security(Clock clock) => new(new EphemeralDataProtectionProvider(),
        Options.Create(Settings), Options.Create(new StationOptions { PairingDays = 30 }), clock);

    private static DefaultHttpContext RuntimeContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("station.example.test");
        context.Request.Path = "/api/station-runtime/unlock";
        context.Request.Method = "POST";
        context.Request.Headers.Cookie = $"{StationApiOptions.CookieName}={Credential}";
        context.Request.Headers.Origin = Origin;
        context.Request.Headers["Sec-Fetch-Site"] = "same-origin";
        context.Request.Headers["Sec-Fetch-Mode"] = "cors";
        context.Request.Headers["Sec-Fetch-Dest"] = "empty";
        return context;
    }

    private static void AssertStatus(int expected, IResult? result) =>
        Assert.Equal(expected, Assert.IsType<IStatusCodeHttpResult>(result, exactMatch: false).StatusCode);
    private sealed class Clock : ISystemClock { public DateTimeOffset UtcNow { get; set; } = StationDomainTests.Now; }
    private sealed class Reader(StationRuntimeResponse response) : IStationSessionReader
    {
        public int Reads { get; private set; }
        public Task<StationRuntimeResponse> ReadAsync(string credential, CancellationToken cancellationToken = default)
        { this.Reads++; Assert.Equal(Credential, credential); return Task.FromResult(response); }
    }
    private sealed class ThrowOnReadStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Body must not be read.");
    }
}
