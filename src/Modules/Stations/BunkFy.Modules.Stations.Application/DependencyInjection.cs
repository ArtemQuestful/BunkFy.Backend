namespace BunkFy.Modules.Stations.Application;

using BunkFy.Modules.Stations.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Gma.Framework.Security;
using Gma.Framework.AccessControl;

public static class DependencyInjection
{
    public static IServiceCollection AddStationsCore(this IServiceCollection services, Action<StationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.AddOptions<StationOptions>().Configure(configure).Validate(o => o.IsValid(), "Invalid station core configuration.").ValidateOnStart();
        services.TryAddSingleton<IStationPinVerifier, StationPinVerifier>();
        return services;
    }

    /// <summary>Explicit module registration only; no host opts in or activates a route here.</summary>
    public static IServiceCollection AddStationsRuntime(this IServiceCollection services)
    {
        services.TryAddScoped<StationAdmissionCoordinator>();
        services.TryAddScoped<StationHandoffAdmission>();
        services.TryAddScoped<StationRuntimeService>();
        services.TryAddScoped<StationFirstJobService>();
        services.TryAddScoped<IStationSessionReader>(s => s.GetRequiredService<StationRuntimeService>());
        return services;
    }

    public static IServiceCollection AddStationsManagement(this IServiceCollection services,
        AuthenticationAssuranceRequirement destructiveRequirement, string globalAuthScopeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(globalAuthScopeId);
        if (globalAuthScopeId.Trim() != globalAuthScopeId)
        { throw new ArgumentException("Invalid Auth scope.", nameof(globalAuthScopeId)); }
        services.AddSingleton(new StationPrimaryAssurance(destructiveRequirement));
        services.Configure<StationOptions>(o => o.ManagementAuthScopeId = globalAuthScopeId);
        services.TryAddScoped<StationPrimaryAdmission>();
        services.TryAddScoped<StationManagementService>();
        services.AddGmaAccessControlPermissionPolicies(StationsModuleMetadata.Descriptor);
        return services;
    }
}
