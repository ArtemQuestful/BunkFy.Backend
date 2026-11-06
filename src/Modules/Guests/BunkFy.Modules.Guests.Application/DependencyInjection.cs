namespace BunkFy.Modules.Guests.Application;

using BunkFy.DataGovernance;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Guests.Application.Contributors;
using BunkFy.Modules.Guests.Application.Capabilities;
using BunkFy.Modules.Guests.Application.Handlers;
using BunkFy.Modules.Guests.Application.Policies;
using BunkFy.Modules.Guests.Application.Ports;
using BunkFy.Modules.Guests.Application.Tasks;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Retention.Contracts;
using Gma.Framework.AccessControl;
using Gma.Framework.Application.Composition;
using Gma.Framework.Messaging;
using Gma.Framework.ProjectionRebuild.Tasks;
using Gma.Framework.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

public static class DependencyInjection
{
    public static IServiceCollection AddGuestsApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddApplicationServicesFromAssembly(typeof(DependencyInjection).Assembly);
        services.AddGmaAccessControlPermissionPolicies(GuestsModuleMetadata.Descriptor);
        services.TryAddSingleton(_ => CountryPolicyRegistry.Create(
            [],
            [],
            CountryPolicyRuntimeMode.Engineering));
        services.AddOptions<GuestRetentionOptions>()
            .BindConfiguration(GuestRetentionOptions.SectionName)
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IValidateOptions<GuestRetentionOptions>,
                GuestRetentionOptionsValidator>());
        services.TryAddScoped<IGuestCountryPolicyAdmission, GuestCountryPolicyAdmission>();
        services.TryAddScoped<IGuestProfileCreationCapability, GuestProfileCreationCapability>();
        services.TryAddScoped<GuestMutationCoordinator>();
        services.TryAddScoped<
                IGuestAnonymisationEligibilityEvaluator,
                GuestAnonymisationEligibilityEvaluator>();
        services.TryAddScoped<GuestRetentionEligibilityEvaluator>();
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                IRetentionExecutionContributor,
                GuestRetentionContributor>());
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                IDataRightsAnonymisationContributor,
                GuestDataRightsAnonymisationContributor>());
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                IDataRightsRestrictionContributor,
                GuestDataRightsRestrictionContributor>());
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                IDataRightsCorrectionPolicyContributor,
                GuestDataRightsCorrectionPolicyContributor>());
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                IDataRightsAnonymisationRestoreContributor,
                GuestDataRightsAnonymisationRestoreContributor>());
        services.AddIntegrationEventHandler<PropertyCreatedIntegrationEvent, GuestPropertyCreatedHandler>(
            GuestsModuleMetadata.Name,
            PropertiesModuleMetadata.Name);
        services.AddIntegrationEventHandler<PropertyUpdatedIntegrationEvent, GuestPropertyUpdatedHandler>(
            GuestsModuleMetadata.Name,
            PropertiesModuleMetadata.Name);
        services.AddIntegrationEventHandler<
            PropertyTimeZoneChangedIntegrationEvent,
            GuestPropertyTimeZoneChangedHandler>(
                GuestsModuleMetadata.Name,
                PropertiesModuleMetadata.Name);
        services.AddIntegrationEventHandler<PropertyRetiredIntegrationEvent, GuestPropertyRetiredHandler>(
            GuestsModuleMetadata.Name,
            PropertiesModuleMetadata.Name);
        services.AddIntegrationEventHandler<
            PropertyProcessingPolicyActivatedIntegrationEvent,
            GuestPropertyProcessingPolicyActivatedHandler>(
                GuestsModuleMetadata.Name,
                PropertiesModuleMetadata.Name);
        services.AddIntegrationEventHandler<
            PropertyProcessingSuspendedIntegrationEvent,
            GuestPropertyProcessingSuspendedHandler>(
                GuestsModuleMetadata.Name,
                PropertiesModuleMetadata.Name);
        services.AddIntegrationEventHandler<ReservationGuestLinkedIntegrationEvent, ReservationGuestLinkedStayHandler>(
            GuestsModuleMetadata.Name,
            GuestsModuleMetadata.ReservationsProducerModuleName);
        services.AddIntegrationEventHandler<ReservationGuestStayChangedIntegrationEvent, ReservationGuestStayChangedHandler>(
            GuestsModuleMetadata.Name,
            GuestsModuleMetadata.ReservationsProducerModuleName);
        return services;
    }

    public static IServiceCollection AddGuestsTaskHandlers(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddProjectionRebuildTasks();
        services.AddTaskHandler<RebuildGuestsPropertiesPayload, RebuildGuestsPropertiesTaskHandler>(
            GuestsModuleMetadata.Name);
        services.AddTaskHandler<RebuildGuestStayHistoryPayload, RebuildGuestStayHistoryTaskHandler>(
            GuestsModuleMetadata.Name);
        return services;
    }
}
