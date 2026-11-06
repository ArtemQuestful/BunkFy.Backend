namespace BunkFy.Host.ServiceDefaults.Security.Stations;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public static class StationSecurityExtensions
{
    public static IHostApplicationBuilder AddBunkFyStationK0(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (builder.Services.Any(descriptor => descriptor.ServiceType == typeof(StationK0RegistrationMarker)))
        {
            return builder;
        }

        EnsureAuthorizationResultHandlerRegistrationIsCompatible(builder.Services);
        builder.Services.AddSingleton<StationK0RegistrationMarker>();
        builder.Services.TryAddSingleton<IStationTokenValidator, DisabledStationTokenValidator>();
        builder.Services.TryAddSingleton<IStationFeatureState, ConfigurationStationFeatureState>();
        builder.Services.TryAddSingleton<IStationAuthenticationObserver, NullStationAuthenticationObserver>();
        builder.Services.TryAddSingleton<IStationAuthorizationObserver, NullStationAuthorizationObserver>();
        builder.Services.TryAddSingleton<IStationAuthorizationResponseWriter, StationAuthorizationResponseWriter>();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAuthorizationHandler, StationAdmissionHandler>());
        builder.Services.RemoveAll<IAuthorizationMiddlewareResultHandler>();
        builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler,
            StationAuthorizationMiddlewareResultHandler>();

        builder.Services
            .AddAuthentication()
            .AddScheme<StationAuthenticationOptions, StationAuthenticationHandler>(
                StationAuthenticationDefaults.SchemeName,
                _ => { });

        return builder;
    }

    internal static IHostApplicationBuilder AddBunkFyStationK0ValidationPolicies(
        this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!builder.Environment.IsEnvironment("Integration"))
        {
            throw new InvalidOperationException(
                "Station K0 validation policies can be registered only by the isolated Integration test host.");
        }

        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(StationAuthenticationDefaults.StationOnlyPolicy, policy =>
            {
                policy.AuthenticationSchemes.Add(StationAuthenticationDefaults.SchemeName);
                policy.Requirements.Add(StationAdmissionRequirement.Instance);
            })
            .AddPolicy(StationAuthenticationDefaults.DualPolicy, policy =>
            {
                policy.AuthenticationSchemes.Add("Bearer");
                policy.AuthenticationSchemes.Add(StationAuthenticationDefaults.SchemeName);
                policy.Requirements.Add(StationAdmissionRequirement.Instance);
            });

        return builder;
    }

    public static void ValidateBunkFyStationK0(
        this WebApplication app,
        string hostSurface)
    {
        ArgumentNullException.ThrowIfNull(app);
        ValidateAuthorizationResultHandlerOwnership(app.Services);
        IEndpointRouteBuilder routeBuilder = app;
        StationEndpointInventory inventory = StationEndpointInventoryBuilder.Build(
            routeBuilder.DataSources,
            hostSurface,
            app.Services.GetRequiredService<IAuthorizationPolicyProvider>());
        StationFeatureSnapshot feature = app.Services.GetRequiredService<IStationFeatureState>().Capture();

        if (feature.Enabled || feature.Epoch != 0)
        {
            throw new InvalidOperationException(
                "BunkFy station K0 production hosts require feature off and epoch zero.");
        }

        if (inventory.EligibleCount != 0)
        {
            throw new InvalidOperationException(
                $"BunkFy station K0 host '{hostSurface}' unexpectedly has eligible endpoints.");
        }

        string sanitizedConfiguration = JsonSerializer.Serialize(new
        {
            enabled = feature.Enabled,
            epoch = feature.Epoch,
            schemeRegistered = true,
            mode = "production-equivalent",
            issuer = (string?)null,
            audience = (string?)null,
            algorithm = (string?)null,
            keyId = (string?)null,
            keyPresent = false,
            eligibleEndpointCount = inventory.EligibleCount,
            endpointInventoryHash = inventory.CanonicalSha256
        });
        string configurationDigest = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(sanitizedConfiguration)));
        ILogger logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("BunkFy.Host.ServiceDefaults.Security.Stations");
        logger.LogInformation(
            "Station K0 inventory validated for {HostSurface}: {EndpointCount} endpoints, {EligibleCount} eligible, " +
            "inventory {InventoryHash}, configuration {ConfigurationDigest}.",
            hostSurface,
            inventory.Payload.Endpoints.Count,
            inventory.EligibleCount,
            inventory.CanonicalSha256,
            configurationDigest);
    }

    internal static void ValidateAuthorizationResultHandlerOwnership(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        IAuthorizationMiddlewareResultHandler[] handlers = services
            .GetServices<IAuthorizationMiddlewareResultHandler>()
            .ToArray();
        if (handlers.Length != 1 || handlers[0] is not StationAuthorizationMiddlewareResultHandler)
        {
            throw new InvalidOperationException(
                "BunkFy station K0 requires exclusive ownership of the authorization middleware result handler. " +
                "Registering another handler before or after Station K0 is not supported because silently " +
                "discarding or bypassing ordinary authorization behavior would be unsafe.");
        }
    }

    private static void EnsureAuthorizationResultHandlerRegistrationIsCompatible(IServiceCollection services)
    {
        ServiceDescriptor[] registrations = services
            .Where(descriptor => descriptor.ServiceType == typeof(IAuthorizationMiddlewareResultHandler))
            .ToArray();
        bool onlyFrameworkDefault = registrations.Length <= 1 && registrations.All(descriptor =>
            descriptor.ImplementationType == typeof(AuthorizationMiddlewareResultHandler));
        if (!onlyFrameworkDefault)
        {
            throw new InvalidOperationException(
                "BunkFy station K0 cannot replace a custom authorization middleware result handler. " +
                "Compose the behaviors explicitly before enabling Station K0.");
        }
    }

    private sealed class StationK0RegistrationMarker;
}
