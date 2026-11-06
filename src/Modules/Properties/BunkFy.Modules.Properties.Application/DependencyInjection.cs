namespace BunkFy.Modules.Properties.Application;

using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Application.Handlers;
using Gma.Framework.Messaging;
using Gma.Framework.AccessControl;
using Gma.Framework.Application.Composition;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using BunkFy.DataGovernance;
using BunkFy.TimeZones;
public static class DependencyInjection
{
    public static IServiceCollection AddPropertiesApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddGmaAccessControlPermissionPolicies(PropertiesModuleMetadata.Descriptor);
        services.TryAddSingleton(_ => CountryPolicyRegistry.Create(
            [],
            [],
            CountryPolicyRuntimeMode.Engineering,
            EmbeddedTzdbCountryPolicyTimeZoneRules.Instance));
        services.TryAddSingleton(
            TimeZoneRuntimeCompatibilityProbe.Default);
        services.AddScoped<PropertiesMutationCoordinator>();
        services.TryAddScoped<PropertyMutationOperationJournal>();
        services.TryAddScoped<PropertyDetailsUpdateCoordinator>();
        services.AddApplicationServicesFromAssembly(typeof(DependencyInjection).Assembly);
        services.AddIntegrationEventHandler<
            BedRetirementFinalizationRequestedIntegrationEvent,
            BedRetirementFinalizationRequestedHandler>(
                PropertiesModuleMetadata.Name,
                PropertiesModuleMetadata.RetirementProducerModuleName);
        services.AddIntegrationEventHandler<
            RoomRetirementFinalizationRequestedIntegrationEvent,
            RoomRetirementFinalizationRequestedHandler>(
                PropertiesModuleMetadata.Name,
                PropertiesModuleMetadata.RetirementProducerModuleName);

        return services;
    }
}
