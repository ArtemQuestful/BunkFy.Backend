namespace Integration.Tests.Support;

using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using BunkFy.Host.ServiceDefaults.Security.Stations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

internal enum StationK0TestMode
{
    FeatureOff,
    Validation,
    Negative,
    Alias
}

internal enum StationK0SigningKey
{
    Station,
    Ordinary,
    Wrong
}

internal sealed record StationK0TokenSpec
{
    public string Issuer { get; init; } = StationK0TestApplication.StationIssuer;

    public string Audience { get; init; } = StationK0TestApplication.StationAudience;

    public string Type { get; init; } = StationK0TestApplication.StationTokenType;

    public string Algorithm { get; init; } = SecurityAlgorithms.HmacSha256;

    public StationK0SigningKey SigningKey { get; init; } = StationK0SigningKey.Station;

    public DateTime? NotBeforeUtc { get; init; }

    public DateTime? ExpiresUtc { get; init; }

    public IReadOnlyList<Claim>? Claims { get; init; }

    public bool Unsigned { get; init; }

    public bool OmitType { get; init; }
}

internal sealed class StationK0TestApplication : IAsyncDisposable
{
    public const string StationIssuer = "urn:bunkfy:test:station";
    public const string StationAudience = "urn:bunkfy:test:public-api";
    public const string StationTokenType = "bunkfy-station+jwt";
    public const string StationIdClaim = "bfx_station_id";
    public const string ActorIdClaim = "bfx_actor_id";
    public const string PropertyIdClaim = "bfx_property_id";

    private const string OrdinaryIssuer = "urn:bunkfy:test:ordinary";
    private const string OrdinaryAudience = "urn:bunkfy:test:ordinary-api";

    private readonly WebApplication app;
    private readonly Uri? baseAddress;
    private readonly byte[] stationKey;
    private readonly byte[] ordinaryKey;
    private readonly StationK0ControllableFeatureState? controllableFeatureState;

    private StationK0TestApplication(
        WebApplication app,
        byte[] stationKey,
        byte[] ordinaryKey,
        StationK0TestSentinels sentinels,
        StationK0AuthenticationObserver observer,
        StationK0AuthorizationObserver authorizationObserver,
        StationK0EvidenceProbe evidenceProbe,
        StationK0LogCollector logs,
        StationK0ControllableFeatureState? controllableFeatureState,
        StationEndpointInventory? inventory,
        Uri? baseAddress,
        StationK0TestMode mode,
        bool useKestrel)
    {
        this.app = app;
        this.stationKey = stationKey;
        this.ordinaryKey = ordinaryKey;
        this.Sentinels = sentinels;
        this.Observer = observer;
        this.AuthorizationObserver = authorizationObserver;
        this.EvidenceProbe = evidenceProbe;
        this.Logs = logs;
        this.controllableFeatureState = controllableFeatureState;
        this.Inventory = inventory;
        this.baseAddress = baseAddress;
        this.Mode = mode;
        this.Transport = useKestrel ? "kestrel-loopback" : "test-server";
    }

    public StationK0TestSentinels Sentinels { get; }

    public StationK0AuthenticationObserver Observer { get; }

    public StationK0AuthorizationObserver AuthorizationObserver { get; }

    public StationK0EvidenceProbe EvidenceProbe { get; }

    public StationK0LogCollector Logs { get; }

    public StationK0ControllableFeatureState ControllableFeatureState =>
        this.controllableFeatureState ?? throw new InvalidOperationException(
            "This Station K0 test application was not created with a controllable feature state.");

    public StationEndpointInventory? Inventory { get; }

    public StationK0TestMode Mode { get; }

    public string Transport { get; }

    public static async Task<StationK0TestApplication> CreateAsync(
        StationK0TestMode mode,
        bool useKestrel = false,
        bool useControllableFeatureState = false)
    {
        if (useControllableFeatureState && mode != StationK0TestMode.Validation)
        {
            throw new ArgumentException(
                "A controllable Station feature state is supported only in validation mode.",
                nameof(mode));
        }

        byte[] stationKey = RandomNumberGenerator.GetBytes(32);
        byte[] ordinaryKey = RandomNumberGenerator.GetBytes(32);
        StationK0TestSentinels sentinels = new();
        StationK0AuthenticationObserver observer = new();
        StationK0AuthorizationObserver authorizationObserver = new();
        StationK0EvidenceProbe evidenceProbe = new();
        StationK0LogCollector logs = new();
        StationK0ControllableFeatureState? controllableFeatureState = useControllableFeatureState
            ? new(new StationFeatureSnapshot(true, 1))
            : null;
        EphemeralStationTokenValidator tokenValidator = new(stationKey);

        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Integration"
        });
        if (useKestrel)
        {
            builder.WebHost.ConfigureKestrel(options =>
                options.Limits.MaxRequestHeadersTotalSize = 32_768);
            builder.WebHost.UseUrls("http://127.0.0.1:0");
        }
        else
        {
            builder.WebHost.UseTestServer();
        }
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Station:Enabled"] = (mode is StationK0TestMode.Validation or StationK0TestMode.Negative or
                StationK0TestMode.Alias)
                .ToString(),
            ["Station:Epoch"] = mode == StationK0TestMode.FeatureOff ? "0" : "1"
        });
        builder.Logging.AddProvider(logs);
        builder.Services.AddSingleton(sentinels);
        builder.Services.AddSingleton<IStationAuthenticationObserver>(observer);
        builder.Services.AddSingleton<IStationAuthorizationObserver>(authorizationObserver);
        builder.Services.AddSingleton<IStationTokenValidator>(tokenValidator);
        builder.Services.AddSingleton(evidenceProbe);
        builder.Services.AddSingleton<IPostConfigureOptions<JwtBearerOptions>, StationK0EvidenceJwtBearerPostConfigure>();
        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = CreateOrdinaryValidationParameters(ordinaryKey);
            });
        if (controllableFeatureState is not null)
        {
            builder.Services.AddSingleton<IStationFeatureState>(controllableFeatureState);
        }

        builder.AddBunkFyStationK0();
        builder.AddBunkFyStationK0ValidationPolicies();
        if (mode == StationK0TestMode.Alias)
        {
            builder.Services.AddAuthorizationBuilder()
                .AddPolicy("station-k0.alias", policy =>
                {
                    policy.AuthenticationSchemes.Add(StationAuthenticationDefaults.SchemeName);
                    policy.RequireAuthenticatedUser();
                });
        }

        WebApplication app = builder.Build();
        app.Use(async (context, next) =>
        {
            evidenceProbe.ObserveRequest(context);
            await next(context).ConfigureAwait(false);
            evidenceProbe.ObserveFinalIdentities(context);
        });
        app.UseAuthentication();
        app.Use(async (context, next) =>
        {
            await next(context).ConfigureAwait(false);
            observer.ObserveFinalIdentities(context);
        });
        app.UseAuthorization();

        app.MapGet("/ordinary", ExecuteOrdinary)
            .WithName("station-k0.ordinary")
            .RequireAuthorization();
        app.MapGet("/station-only-eligible", ExecuteStation)
            .WithName("station-k0.station-only")
            .WithStationOperationClass(StationOperationClass.Eligible)
            .RequireAuthorization(StationAuthenticationDefaults.StationOnlyPolicy);
        app.MapGet("/dual-eligible", ExecuteStation)
            .WithName("station-k0.dual")
            .WithStationOperationClass(StationOperationClass.Eligible)
            .RequireAuthorization(StationAuthenticationDefaults.DualPolicy);

        if (mode == StationK0TestMode.Negative)
        {
            app.MapGet("/station-forbidden-negative", ExecuteStation)
                .WithName("station-k0.forbidden-negative")
                .RequireAuthorization(StationAuthenticationDefaults.StationOnlyPolicy);
        }
        else if (mode == StationK0TestMode.Alias)
        {
            app.MapGet("/station-alias-negative", ExecuteStation)
                .WithName("station-k0.alias-negative")
                .RequireAuthorization("station-k0.alias");
        }

        StationEndpointInventory? inventory = null;
        if (mode is not (StationK0TestMode.Negative or StationK0TestMode.Alias))
        {
            inventory = StationEndpointInventoryBuilder.Build(
                ((IEndpointRouteBuilder)app).DataSources,
                "public",
                app.Services.GetRequiredService<IAuthorizationPolicyProvider>());
            if (inventory.EligibleCount != 2)
            {
                throw new InvalidOperationException(
                    $"Station K0 test host expected two eligible endpoints, found {inventory.EligibleCount}.");
            }
        }

        await app.StartAsync().ConfigureAwait(false);
        Uri? baseAddress = null;
        if (useKestrel)
        {
            IServer server = app.Services.GetRequiredService<IServer>();
            IServerAddressesFeature addresses = server.Features
                .Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("Kestrel did not expose a loopback address.");
            baseAddress = new Uri(AssertSingleAddress(addresses.Addresses));
        }

        return new StationK0TestApplication(
            app,
            stationKey,
            ordinaryKey,
            sentinels,
            observer,
            authorizationObserver,
            evidenceProbe,
            logs,
            controllableFeatureState,
            inventory,
            baseAddress,
            mode,
            useKestrel);
    }

    public HttpClient CreateClient() => this.baseAddress is null
        ? this.app.GetTestClient()
        : new HttpClient { BaseAddress = this.baseAddress };

    public string CreateOrdinaryToken(bool signWithStationKey = false)
    {
        DateTime now = DateTime.UtcNow;
        SigningCredentials signingCredentials = new(
            new SymmetricSecurityKey(signWithStationKey ? this.stationKey : this.ordinaryKey),
            SecurityAlgorithms.HmacSha256);
        JwtSecurityToken token = new(
            OrdinaryIssuer,
            OrdinaryAudience,
            [new Claim(JwtRegisteredClaimNames.Sub, "ordinary-user")],
            now.AddMinutes(-1),
            now.AddMinutes(10),
            signingCredentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string CreateStationToken(StationK0TokenSpec? specification = null)
    {
        StationK0TokenSpec spec = specification ?? new();
        DateTime now = DateTime.UtcNow;
        IReadOnlyList<Claim> claims = spec.Claims ?? CreateRequiredStationClaims();
        SigningCredentials? signingCredentials = spec.Unsigned
            ? null
            : new SigningCredentials(
                new SymmetricSecurityKey(this.ResolveSigningKey(spec)),
                spec.Algorithm);
        JwtSecurityToken token = new(
            spec.Issuer,
            spec.Audience,
            claims,
            spec.NotBeforeUtc ?? now.AddMinutes(-1),
            spec.ExpiresUtc ?? now.AddMinutes(5),
            signingCredentials);
        if (spec.OmitType)
        {
            token.Header.Remove(JwtHeaderParameterNames.Typ);
        }
        else
        {
            token.Header[JwtHeaderParameterNames.Typ] = spec.Type;
        }

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static IReadOnlyList<Claim> CreateRequiredStationClaims(
        long epoch = 1,
        string subject = "station-actor") =>
        [
            new(JwtRegisteredClaimNames.Sub, subject),
            new(StationAuthenticationDefaults.EpochClaim, epoch.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new(StationIdClaim, "station-1"),
            new(ActorIdClaim, "actor-1"),
            new(PropertyIdClaim, "property-1"),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        ];

    public StationEndpointInventory BuildInventoryForEvidence() =>
        StationEndpointInventoryBuilder.Build(
            ((IEndpointRouteBuilder)this.app).DataSources,
            "public",
            this.app.Services.GetRequiredService<IAuthorizationPolicyProvider>());

    public void ResetObservations()
    {
        this.Sentinels.Reset();
        this.Observer.Reset();
        this.AuthorizationObserver.Reset();
        this.EvidenceProbe.Reset();
        this.Logs.Reset();
    }

    public async ValueTask DisposeAsync()
    {
        this.controllableFeatureState?.ReleaseAdmission();
        await this.app.DisposeAsync().ConfigureAwait(false);
        this.controllableFeatureState?.Dispose();
    }

    private static string AssertSingleAddress(ICollection<string> addresses)
    {
        if (addresses.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected one Kestrel loopback address, found {addresses.Count}.");
        }

        return addresses.Single();
    }

    private static IResult ExecuteOrdinary(HttpContext context)
    {
        StationK0TestSentinels sentinels = context.RequestServices
            .GetRequiredService<StationK0TestSentinels>();
        sentinels.MarkAccess();
        sentinels.MarkEndpoint();
        sentinels.MarkDomain();
        return Results.NoContent();
    }

    private static IResult ExecuteStation(StationK0BoundInput input, HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        StationK0TestSentinels sentinels = context.RequestServices
            .GetRequiredService<StationK0TestSentinels>();
        sentinels.MarkAccess();
        sentinels.MarkEndpoint();
        sentinels.MarkDomain();
        return Results.NoContent();
    }

    private static TokenValidationParameters CreateOrdinaryValidationParameters(byte[] key) =>
        new()
        {
            ValidateIssuer = true,
            ValidIssuer = OrdinaryIssuer,
            ValidateAudience = true,
            ValidAudience = OrdinaryAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(key),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
            RequireSignedTokens = true,
            RequireExpirationTime = true
        };

    private byte[] ResolveSigningKey(StationK0TokenSpec specification) =>
        specification.SigningKey switch
        {
            StationK0SigningKey.Station => this.stationKey,
            StationK0SigningKey.Ordinary => this.ordinaryKey,
            StationK0SigningKey.Wrong when specification.Algorithm == SecurityAlgorithms.HmacSha256 =>
                RandomNumberGenerator.GetBytes(32),
            StationK0SigningKey.Wrong => RandomNumberGenerator.GetBytes(64),
            _ => throw new InvalidOperationException("Unknown Station K0 signing-key selection.")
        };

    private sealed class EphemeralStationTokenValidator(byte[] key) : IStationTokenValidator
    {
        private static readonly string[] RequiredClaims =
        [
            JwtRegisteredClaimNames.Sub,
            StationAuthenticationDefaults.EpochClaim,
            StationIdClaim,
            ActorIdClaim,
            PropertyIdClaim,
            JwtRegisteredClaimNames.Jti
        ];

        private readonly JwtSecurityTokenHandler handler = new()
        {
            MapInboundClaims = false
        };

        private readonly TokenValidationParameters parameters = new()
        {
            ValidateIssuer = true,
            ValidIssuer = StationIssuer,
            ValidateAudience = true,
            ValidAudience = StationAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(key),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ValidTypes = [StationTokenType]
        };

        public ValueTask<StationTokenValidationResult> ValidateAsync(
            string token,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool envelopeValidated = false;
            try
            {
                // The envelope is inspected only to reject malformed tokens with a stable,
                // non-secret category. Authority still comes exclusively from ValidateToken.
                // Inspect the raw payload before IdentityModel materializes registered claims;
                // an array-valued duplicate `sub` is rejected during that materialization.
                if (!HasValidRequiredClaims(token))
                {
                    return Failure(StationTokenValidationError.ClaimsInvalid);
                }

                JwtSecurityToken untrustedJwt = this.handler.ReadJwtToken(token);
                if (!string.Equals(
                        untrustedJwt.Header.Alg,
                        SecurityAlgorithms.HmacSha256,
                        StringComparison.Ordinal))
                {
                    return Failure(StationTokenValidationError.AlgorithmInvalid);
                }

                if (!string.Equals(untrustedJwt.Header.Typ, StationTokenType, StringComparison.Ordinal))
                {
                    return Failure(StationTokenValidationError.TypeInvalid);
                }

                envelopeValidated = true;

                ClaimsPrincipal principal = this.handler.ValidateToken(
                    token,
                    this.parameters,
                    out SecurityToken validatedToken);
                if (validatedToken is not JwtSecurityToken jwt ||
                    !string.Equals(jwt.Header.Alg, SecurityAlgorithms.HmacSha256, StringComparison.Ordinal))
                {
                    return ValueTask.FromResult(
                        StationTokenValidationResult.Failure(StationTokenValidationError.AlgorithmInvalid));
                }

                if (!HasValidRequiredClaims(principal.Claims))
                {
                    return Failure(StationTokenValidationError.ClaimsInvalid);
                }

                return ValueTask.FromResult(StationTokenValidationResult.Success(principal));
            }
            catch (SecurityTokenExpiredException)
            {
                return Failure(StationTokenValidationError.Expired);
            }
            catch (SecurityTokenNotYetValidException)
            {
                return Failure(StationTokenValidationError.NotYetValid);
            }
            catch (SecurityTokenInvalidIssuerException)
            {
                return Failure(StationTokenValidationError.IssuerInvalid);
            }
            catch (SecurityTokenInvalidAudienceException)
            {
                return Failure(StationTokenValidationError.AudienceInvalid);
            }
            catch (SecurityTokenInvalidTypeException)
            {
                return Failure(StationTokenValidationError.TypeInvalid);
            }
            catch (SecurityTokenInvalidAlgorithmException)
            {
                return Failure(StationTokenValidationError.AlgorithmInvalid);
            }
            catch (SecurityTokenException)
            {
                return Failure(StationTokenValidationError.Invalid);
            }
            catch (ArgumentException)
            {
                // IdentityModel rejects duplicate registered claims while materializing
                // the principal. Once the compact envelope has passed the explicit
                // algorithm/type/required-claim screens, that is a claims-shape failure.
                return Failure(
                    envelopeValidated
                        ? StationTokenValidationError.ClaimsInvalid
                        : StationTokenValidationError.Invalid);
            }
            catch (JsonException)
            {
                return Failure(StationTokenValidationError.Invalid);
            }
            catch (FormatException)
            {
                return Failure(StationTokenValidationError.Invalid);
            }
        }

        private static bool HasValidRequiredClaims(IEnumerable<Claim> claims)
        {
            Claim[] materializedClaims = claims.ToArray();
            foreach (string claimType in RequiredClaims)
            {
                string[] values = materializedClaims
                    .Where(claim => string.Equals(claim.Type, claimType, StringComparison.Ordinal))
                    .Select(static claim => claim.Value)
                    .ToArray();
                if (values.Length != 1 || string.IsNullOrWhiteSpace(values[0]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool HasValidRequiredClaims(string compactToken)
        {
            string[] segments = compactToken.Split('.');
            if (segments.Length != 3)
            {
                return false;
            }

            using JsonDocument document = JsonDocument.Parse(
                Base64UrlEncoder.DecodeBytes(segments[1]));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (string claimType in RequiredClaims)
            {
                JsonElement[] values = document.RootElement
                    .EnumerateObject()
                    .Where(property => string.Equals(property.Name, claimType, StringComparison.Ordinal))
                    .Select(static property => property.Value)
                    .ToArray();
                if (values.Length != 1 ||
                    values[0].ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(values[0].GetString()))
                {
                    return false;
                }
            }

            return true;
        }

        private static ValueTask<StationTokenValidationResult> Failure(StationTokenValidationError error) =>
            ValueTask.FromResult(StationTokenValidationResult.Failure(error));
    }
}

internal sealed class StationK0ControllableFeatureState(StationFeatureSnapshot initialSnapshot)
    : IStationFeatureState, IDisposable
{
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(30);
    private readonly ManualResetEventSlim admissionReached = new(false);
    private readonly ManualResetEventSlim admissionRelease = new(false);
    private StationFeatureSnapshot snapshot = initialSnapshot;
    private int barrierArmed;
    private int disposed;

    public StationFeatureSnapshot Capture()
    {
        StationFeatureSnapshot beforeBarrier = Volatile.Read(ref this.snapshot);
        if (Interlocked.CompareExchange(ref this.barrierArmed, 0, 1) == 1)
        {
            if (!beforeBarrier.Enabled)
            {
                throw new InvalidOperationException(
                    "The Station K0 admission barrier must be armed while the feature is enabled.");
            }

            this.admissionReached.Set();
            if (!this.admissionRelease.Wait(ReleaseTimeout))
            {
                throw new TimeoutException("The Station K0 admission barrier was not released.");
            }
        }

        return Volatile.Read(ref this.snapshot);
    }

    public void ArmNextCapture()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref this.disposed) != 0, this);
        this.admissionReached.Reset();
        this.admissionRelease.Reset();
        if (Interlocked.CompareExchange(ref this.barrierArmed, 1, 0) != 0)
        {
            throw new InvalidOperationException("The Station K0 admission barrier is already armed.");
        }
    }

    public bool WaitUntilAdmissionReached(TimeSpan timeout) => this.admissionReached.Wait(timeout);

    public void Disable() => Volatile.Write(ref this.snapshot, new StationFeatureSnapshot(false, 0));

    public void ReleaseAdmission()
    {
        if (Volatile.Read(ref this.disposed) == 0)
        {
            this.admissionRelease.Set();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) != 0)
        {
            return;
        }

        this.admissionRelease.Set();
        this.admissionReached.Dispose();
        this.admissionRelease.Dispose();
    }
}

internal sealed class StationK0BoundInput
{
    private StationK0BoundInput()
    {
    }

    public static ValueTask<StationK0BoundInput> BindAsync(
        HttpContext context,
        ParameterInfo parameter)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(parameter);
        StationK0TestSentinels sentinels = context.RequestServices
            .GetRequiredService<StationK0TestSentinels>();
        sentinels.MarkBinder();
        sentinels.MarkValidator();
        return ValueTask.FromResult(new StationK0BoundInput());
    }
}

internal sealed class StationK0TestSentinels
{
    private int binder;
    private int validator;
    private int access;
    private int endpoint;
    private int domain;
    private int mutation;

    public int Binder => Volatile.Read(ref this.binder);

    public int Validator => Volatile.Read(ref this.validator);

    public int Access => Volatile.Read(ref this.access);

    public int Endpoint => Volatile.Read(ref this.endpoint);

    public int Domain => Volatile.Read(ref this.domain);

    public int Mutation => Volatile.Read(ref this.mutation);

    public void MarkBinder() => Interlocked.Increment(ref this.binder);

    public void MarkValidator() => Interlocked.Increment(ref this.validator);

    public void MarkAccess() => Interlocked.Increment(ref this.access);

    public void MarkEndpoint() => Interlocked.Increment(ref this.endpoint);

    public void MarkDomain() => Interlocked.Increment(ref this.domain);

    public void MarkMutation() => Interlocked.Increment(ref this.mutation);

    public void Reset()
    {
        Interlocked.Exchange(ref this.binder, 0);
        Interlocked.Exchange(ref this.validator, 0);
        Interlocked.Exchange(ref this.access, 0);
        Interlocked.Exchange(ref this.endpoint, 0);
        Interlocked.Exchange(ref this.domain, 0);
        Interlocked.Exchange(ref this.mutation, 0);
    }
}

internal sealed class StationK0AuthenticationObserver : IStationAuthenticationObserver
{
    private readonly ConcurrentQueue<StationAuthenticationObservation> observations = new();
    private readonly ConcurrentQueue<StationK0FinalIdentityObservation> finalIdentities = new();

    public IReadOnlyList<StationAuthenticationObservation> Observations => this.observations.ToArray();

    public IReadOnlyList<StationK0FinalIdentityObservation> FinalIdentities => this.finalIdentities.ToArray();

    public void Observe(StationAuthenticationObservation observation) =>
        this.observations.Enqueue(observation);

    public void ObserveFinalIdentities(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        string[] authenticationTypes = context.User.Identities
            .Where(static identity => identity.IsAuthenticated)
            .Select(static identity => identity.AuthenticationType ?? string.Empty)
            .Order(StringComparer.Ordinal)
            .ToArray();
        this.finalIdentities.Enqueue(new(authenticationTypes));
    }

    public void Reset()
    {
        while (this.observations.TryDequeue(out _))
        {
        }

        while (this.finalIdentities.TryDequeue(out _))
        {
        }
    }
}

internal sealed record StationK0FinalIdentityObservation(
    IReadOnlyList<string> AuthenticationTypes);

internal sealed class StationK0AuthorizationObserver : IStationAuthorizationObserver
{
    private readonly ConcurrentQueue<StationAuthorizationObservation> observations = new();

    public IReadOnlyList<StationAuthorizationObservation> Observations => this.observations.ToArray();

    public void Observe(StationAuthorizationObservation observation) =>
        this.observations.Enqueue(observation);

    public void Reset()
    {
        while (this.observations.TryDequeue(out _))
        {
        }
    }
}

internal sealed record StationK0LogEntry(
    string Category,
    LogLevel Level,
    string Message,
    string ExceptionText);

internal sealed class StationK0LogCollector : ILoggerProvider
{
    private readonly ConcurrentQueue<StationK0LogEntry> entries = new();

    public IReadOnlyList<StationK0LogEntry> Entries => this.entries.ToArray();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, this.entries);

    public void Reset()
    {
        while (this.entries.TryDequeue(out _))
        {
        }
    }

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(
        string categoryName,
        ConcurrentQueue<StationK0LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            entries.Enqueue(new(
                categoryName,
                logLevel,
                formatter(state, exception),
                exception?.ToString() ?? string.Empty));
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
