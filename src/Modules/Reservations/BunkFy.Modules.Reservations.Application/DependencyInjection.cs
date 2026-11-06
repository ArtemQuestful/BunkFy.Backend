namespace BunkFy.Modules.Reservations.Application;

using BunkFy.DataGovernance;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Reservations.Application.Capabilities;
using BunkFy.Modules.Reservations.Application.Contributors;
using BunkFy.Modules.Reservations.Application.External;
using BunkFy.Modules.Reservations.Application.Handlers;
using BunkFy.Modules.Reservations.Application.Policies;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Application.StayAmendments;
using BunkFy.Modules.Reservations.Application.Tasks;
using BunkFy.Modules.Reservations.Contracts;
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
    public static IServiceCollection AddReservationsApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddApplicationServicesFromAssembly(typeof(DependencyInjection).Assembly);
        services.AddGmaAccessControlPermissionPolicies(ReservationsModuleMetadata.Descriptor);
        services.TryAddSingleton(_ => CountryPolicyRegistry.Create(
            [],
            [],
            CountryPolicyRuntimeMode.Engineering));
        services.AddOptions<ReservationRetentionOptions>()
            .BindConfiguration(
                ReservationRetentionOptions.SectionName)
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IValidateOptions<ReservationRetentionOptions>,
                ReservationRetentionOptionsValidator>());
        services.TryAddScoped<IReservationCountryPolicyAdmission, ReservationCountryPolicyAdmission>();
        services.TryAddScoped<BunkFy.Modules.Reservations.Contracts.Stations.IStationReservationOperations,
            BunkFy.Modules.Reservations.Application.Stations.StationReservationOperations>();
        services.TryAddScoped<BunkFy.Modules.Reservations.Contracts.Stations.IStationDepartureOperations,
            BunkFy.Modules.Reservations.Application.Stations.StationDepartureOperations>();
        services.TryAddScoped<BunkFy.Modules.Reservations.Contracts.Stations.IStationCheckOutOutcomeReader,
            BunkFy.Modules.Reservations.Application.Stations.StationCheckOutOutcomeReader>();
        services.TryAddScoped<BunkFy.Modules.Reservations.Contracts.Stations.IStationCheckInOutcomeReader,
            BunkFy.Modules.Reservations.Application.Stations.StationCheckInOutcomeReader>();
        services.TryAddScoped<
            IReservationAnonymisationEligibilityEvaluator,
            ReservationAnonymisationEligibilityEvaluator>();
        services.TryAddScoped<
            ReservationRetentionEligibilityEvaluator>();
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                IRetentionExecutionContributor,
                ReservationRetentionContributor>());
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                IDataRightsAnonymisationContributor,
                ReservationDataRightsAnonymisationContributor>());
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                IDataRightsRestrictionContributor,
                ReservationDataRightsRestrictionContributor>());
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                IDataRightsAnonymisationRestoreContributor,
                ReservationDataRightsAnonymisationRestoreContributor>());
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                IDataRightsCorrectionPolicyContributor,
                ReservationDataRightsCorrectionPolicyContributor>());
        services.AddIntegrationEventHandler<InventoryAllocationConfirmedIntegrationEvent, InventoryAllocationConfirmedHandler>(
            ReservationsModuleMetadata.Name,
            InventoryModuleMetadata.Name);
        services.AddIntegrationEventHandler<InventoryAllocationRejectedIntegrationEvent, InventoryAllocationRejectedHandler>(
            ReservationsModuleMetadata.Name,
            InventoryModuleMetadata.Name);
        services.AddIntegrationEventHandler<InventoryAllocationReleasedIntegrationEvent, InventoryAllocationReleasedHandler>(
            ReservationsModuleMetadata.Name,
            InventoryModuleMetadata.Name);
        services.AddIntegrationEventHandler<InventoryAllocationReleaseRejectedIntegrationEvent, InventoryAllocationReleaseRejectedHandler>(
            ReservationsModuleMetadata.Name,
            InventoryModuleMetadata.Name);
        services.AddIntegrationEventHandler<InventoryAllocationAmendmentConfirmedIntegrationEvent, InventoryAllocationAmendmentConfirmedHandler>(
            ReservationsModuleMetadata.Name,
            InventoryModuleMetadata.Name);
        services.AddIntegrationEventHandler<InventoryAllocationAmendmentRejectedIntegrationEvent, InventoryAllocationAmendmentRejectedHandler>(
            ReservationsModuleMetadata.Name,
            InventoryModuleMetadata.Name);
        services.AddIntegrationEventHandler<InventoryUnitDefinitionChangedIntegrationEvent, InventoryUnitDefinitionChangedHandler>(
            ReservationsModuleMetadata.Name,
            InventoryModuleMetadata.Name);
        services.AddIntegrationEventHandler<ManualInventoryBlockCreatedIntegrationEvent, InventoryManualBlockCreatedHandler>(
            ReservationsModuleMetadata.Name,
            InventoryModuleMetadata.Name);
        services.AddIntegrationEventHandler<ManualInventoryBlockReleasedIntegrationEvent, InventoryManualBlockReleasedHandler>(
            ReservationsModuleMetadata.Name,
            InventoryModuleMetadata.Name);
        services.AddIntegrationEventHandler<ExternalReservationCreateRequestedIntegrationEvent, ExternalReservationCreateRequestedHandler>(
            ReservationsModuleMetadata.Name,
            ReservationsModuleMetadata.ExternalOperationSourceModuleName);
        services.AddIntegrationEventHandler<ExternalReservationGuestDetailsChangeRequestedIntegrationEvent, ExternalReservationGuestDetailsChangeRequestedHandler>(
            ReservationsModuleMetadata.Name,
            ReservationsModuleMetadata.ExternalOperationSourceModuleName);
        services.AddIntegrationEventHandler<ExternalReservationAmendmentRequestedIntegrationEvent, ExternalReservationAmendmentRequestedHandler>(
            ReservationsModuleMetadata.Name,
            ReservationsModuleMetadata.ExternalOperationSourceModuleName);
        services.AddIntegrationEventHandler<ExternalReservationCancellationRequestedIntegrationEvent, ExternalReservationCancellationRequestedHandler>(
            ReservationsModuleMetadata.Name,
            ReservationsModuleMetadata.ExternalOperationSourceModuleName);
        services.AddIntegrationEventHandler<GuestProfileCreatedIntegrationEvent, GuestProfileCreatedProjectionHandler>(
            ReservationsModuleMetadata.Name,
            GuestsModuleMetadata.Name);
        services.AddIntegrationEventHandler<GuestProfileUpdatedIntegrationEvent, GuestProfileUpdatedProjectionHandler>(
            ReservationsModuleMetadata.Name,
            GuestsModuleMetadata.Name);
        services.AddIntegrationEventHandler<GuestProfileArchivedIntegrationEvent, GuestProfileArchivedProjectionHandler>(
            ReservationsModuleMetadata.Name,
            GuestsModuleMetadata.Name);
        services.AddIntegrationEventHandler<
            GuestProfileAnonymisedIntegrationEvent,
            GuestProfileAnonymisedProjectionHandler>(
                ReservationsModuleMetadata.Name,
                GuestsModuleMetadata.Name);
        services.AddIntegrationEventHandler<
            GuestProcessingRestrictionChangedIntegrationEvent,
            GuestProcessingRestrictionChangedProjectionHandler>(
                ReservationsModuleMetadata.Name,
                GuestsModuleMetadata.Name);
        services.AddIntegrationEventHandler<PropertyCreatedIntegrationEvent, ReservationPropertyCreatedHandler>(
            ReservationsModuleMetadata.Name,
            PropertiesModuleMetadata.Name);
        services.AddIntegrationEventHandler<PropertyUpdatedIntegrationEvent, ReservationPropertyUpdatedHandler>(
            ReservationsModuleMetadata.Name,
            PropertiesModuleMetadata.Name);
        services.AddIntegrationEventHandler<PropertyRetiredIntegrationEvent, ReservationPropertyRetiredHandler>(
            ReservationsModuleMetadata.Name,
            PropertiesModuleMetadata.Name);
        services.AddIntegrationEventHandler<
            PropertyProcessingPolicyActivatedIntegrationEvent,
            ReservationPropertyProcessingPolicyActivatedHandler>(
                ReservationsModuleMetadata.Name,
                PropertiesModuleMetadata.Name);
        services.AddIntegrationEventHandler<
            PropertyProcessingSuspendedIntegrationEvent,
            ReservationPropertyProcessingSuspendedHandler>(
                ReservationsModuleMetadata.Name,
                PropertiesModuleMetadata.Name);
        services.TryAddScoped<ExternalReservationOperationCoordinator>();
        services.TryAddScoped<ReservationInboxDomainEventDispatcher>();
        services.TryAddScoped<ReservationMutationCoordinator>();
        services.TryAddScoped<ReservationManagementLifecycleCoordinator>();
        services.TryAddScoped<ReservationStayAmendmentCoordinator>();
        services.TryAddScoped<
            IReservationGuestRecordLinkCapability,
            ReservationGuestRecordLinkCapability>();

        return services;
    }

    public static IServiceCollection AddReservationsTaskHandlers(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddProjectionRebuildTasks();
        services.AddTaskHandler<RebuildReservationInventoryProjectionPayload, RebuildReservationInventoryProjectionTaskHandler>(
            ReservationsModuleMetadata.Name);
        services.AddTaskHandler<RebuildReservationGuestProfilesPayload, RebuildReservationGuestProfilesTaskHandler>(
            ReservationsModuleMetadata.Name);
        services.AddTaskHandler<
            RebuildReservationGuestRestrictionsPayload,
            RebuildReservationGuestRestrictionsTaskHandler>(
                ReservationsModuleMetadata.Name);
        services.AddTaskHandler<
            RebuildReservationProcessingRestrictionsPayload,
            RebuildReservationProcessingRestrictionsTaskHandler>(
                ReservationsModuleMetadata.Name);
        services.AddTaskHandler<RebuildReservationPropertiesPayload, RebuildReservationPropertiesTaskHandler>(
            ReservationsModuleMetadata.Name);
        services.AddTaskHandler<DispatchReservationArrivalRemindersPayload, DispatchReservationArrivalRemindersTaskHandler>(
            ReservationsModuleMetadata.Name);
        services.AddTaskHandler<
            ExecuteReservationGuestRecordLinkPayload,
            ExecuteReservationGuestRecordLinkTaskHandler>(
                ReservationsModuleMetadata.Name);
        services.AddIntegrationEventHandler<
            ReservationGuestRecordLinkReadyIntegrationEvent,
            ReservationGuestRecordLinkReadyHandler>(
                ReservationsModuleMetadata.Name,
                ReservationsModuleMetadata.Name);
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<ITaskScheduleProvider, ReservationReminderScheduleProvider>());

        return services;
    }
}
