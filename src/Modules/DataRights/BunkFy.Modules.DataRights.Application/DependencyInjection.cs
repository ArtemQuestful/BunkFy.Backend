namespace BunkFy.Modules.DataRights.Application;

using BunkFy.DataGovernance;
using BunkFy.Modules.DataRights.Application.Authorization;
using BunkFy.Modules.DataRights.Application.Handlers;
using BunkFy.Modules.DataRights.Application.Policies;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Application.Production;
using BunkFy.Modules.DataRights.Application.Security;
using BunkFy.Modules.DataRights.Application.Tasks;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Contracts.Authorization;
using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.AccessControl;
using Gma.Framework.Application.Composition;
using Gma.Framework.Messaging;
using Gma.Framework.Observability;
using Gma.Framework.ProjectionRebuild.Tasks;
using Gma.Framework.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

public static class DependencyInjection
{
    public static IServiceCollection AddDataRightsApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSecuritySignalCore();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                ISecuritySignalDefinitionSource,
                DataRightsApprovalSecuritySignalDefinitions>());
        services.AddApplicationServicesFromAssembly(typeof(DependencyInjection).Assembly);
        services.AddScoped<IDataRightsOperationApprovalGate, DataRightsOperationApprovalGate>();
        services.AddScoped<
            IDataRightsCorrectionExecutionGate,
            DataRightsCorrectionExecutionGate>();
        services.TryAddScoped<DataRightsCorrectionCompletionCoordinator>();
        services.TryAddScoped<DataRightsCaseMutationCoordinator>();
        services.TryAddScoped<DataRightsExecutionMutationCoordinator>();
        services.TryAddSingleton(_ => CountryPolicyRegistry.Create(
            [],
            [],
            CountryPolicyRuntimeMode.Engineering));
        services.TryAddScoped<
            IDataRightsAnonymisationApprovalPolicy,
            DataRightsAnonymisationApprovalPolicy>();
        services.TryAddScoped<
            IDataRightsResponseDeadlinePolicy,
            GuestDataRightsResponseDeadlinePolicy>();
        services.TryAddScoped<DataRightsRequiredCompanionExpander>();
        services.TryAddScoped<
            IDataRightsRestoreCoordinator,
            DataRightsRestoreCoordinator>();
        services.TryAddScoped<TenantTerminationCancellationCoordinator>();
        services.TryAddScoped<TenantTerminationStartCoordinator>();
        services.TryAddScoped<TenantTerminationMutationCoordinator>();
        services.TryAddScoped<TenantTerminationPhasePlanner>();
        services.TryAddScoped<TenantTerminationPhaseEvaluator>();
        services.TryAddScoped<TenantTerminationVerificationPlanner>();
        services.TryAddScoped<
            ITenantTerminationProductionCatalog,
            TenantTerminationProductionCatalog>();
        services.TryAddScoped<
            ITenantTerminationProductionReadinessProbe,
            TenantTerminationProductionReadinessProbe>();
        services.TryAddScoped<
            ITenantTerminationCoordinationSignal,
            TenantTerminationCoordinationSignal>();
        services.TryAddScoped<
            IDataRightsExportAssembler,
            DataRightsExportAssembler>();
        services.TryAddScoped<
            ITenantTerminationExportFragmentAssembler,
            TenantTerminationExportFragmentAssembler>();
        services.AddIntegrationEventHandler<
            PropertyCreatedIntegrationEvent,
            DataRightsPropertyCreatedHandler>(
                DataRightsModuleMetadata.Name,
                PropertiesModuleMetadata.Name);
        services.AddIntegrationEventHandler<
            PropertyUpdatedIntegrationEvent,
            DataRightsPropertyUpdatedHandler>(
                DataRightsModuleMetadata.Name,
                PropertiesModuleMetadata.Name);
        services.AddIntegrationEventHandler<
            PropertyRetiredIntegrationEvent,
            DataRightsPropertyRetiredHandler>(
                DataRightsModuleMetadata.Name,
                PropertiesModuleMetadata.Name);
        services.AddIntegrationEventHandler<
            PropertyProcessingPolicyActivatedIntegrationEvent,
            DataRightsPropertyProcessingPolicyActivatedHandler>(
                DataRightsModuleMetadata.Name,
                PropertiesModuleMetadata.Name);
        services.AddIntegrationEventHandler<
            PropertyProcessingSuspendedIntegrationEvent,
            DataRightsPropertyProcessingSuspendedHandler>(
                DataRightsModuleMetadata.Name,
                PropertiesModuleMetadata.Name);
        services.AddIntegrationEventHandler<
            DataRightsCorrectionAppliedIntegrationEvent,
            GuestDataRightsCorrectionAppliedHandler>(
                DataRightsModuleMetadata.Name,
                DataRightsModuleMetadata.GuestsProducerModuleName);
        services.AddIntegrationEventHandler<
            DataRightsCorrectionAppliedIntegrationEvent,
            ReservationDataRightsCorrectionAppliedHandler>(
                DataRightsModuleMetadata.Name,
                DataRightsModuleMetadata.ReservationsProducerModuleName);
        services.AddIntegrationEventHandler<
            DataRightsTenantCorrectionAppliedIntegrationEvent,
            StaffDataRightsCorrectionAppliedHandler>(
                DataRightsModuleMetadata.Name,
                DataRightsModuleMetadata.StaffProducerModuleName);
        services.AddIntegrationEventHandler<
            DataRightsTenantCorrectionAppliedIntegrationEvent,
            WorkspacesDataRightsCorrectionAppliedHandler>(
                DataRightsModuleMetadata.Name,
                DataRightsModuleMetadata.WorkspacesProducerModuleName);
        services.AddGmaAccessControlPermissionPolicies(DataRightsModuleMetadata.Descriptor);
        return services;
    }

    public static IServiceCollection AddDataRightsTaskHandlers(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddProjectionRebuildTasks();
        services.AddDataRightsTenantTerminationTaskScheduling();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<
            ITaskScheduleProvider,
            DataRightsResponseDeadlineAlertScheduleProvider>());
        services.TryAddScoped<
            DataRightsAnonymisationExecutionReconciler>();
        services.AddTaskHandler<
            RebuildDataRightsPropertiesPayload,
            RebuildDataRightsPropertiesTaskHandler>(DataRightsModuleMetadata.Name);
        services.AddIntegrationEventHandler<
            DataRightsAnonymisationExecutionPreparedIntegrationEvent,
            DataRightsAnonymisationExecutionPreparedHandler>(
                DataRightsModuleMetadata.Name,
                DataRightsModuleMetadata.Name);
        services.AddIntegrationEventHandler<
            DataRightsAnonymisationExecutionPreparedIntegrationEventV2,
            DataRightsAnonymisationExecutionPreparedV2Handler>(
                DataRightsModuleMetadata.Name,
                DataRightsModuleMetadata.Name);
        services.AddIntegrationEventHandler<
            DataRightsAnonymisationWorkItemTerminalIntegrationEvent,
            DataRightsAnonymisationWorkItemTerminalHandler>(
                DataRightsModuleMetadata.Name,
                DataRightsModuleMetadata.Name);
        services.AddIntegrationEventHandler<
            DataRightsAnonymisationWorkItemTerminalIntegrationEventV2,
            DataRightsAnonymisationWorkItemTerminalV2Handler>(
                DataRightsModuleMetadata.Name,
                DataRightsModuleMetadata.Name);
        services.AddIntegrationEventHandler<
            DataRightsExportArtifactRequestedIntegrationEvent,
            DataRightsExportArtifactRequestedHandler>(
                DataRightsModuleMetadata.Name,
                DataRightsModuleMetadata.Name);
        services.AddIntegrationEventHandler<
            TenantTerminationCoordinationRequestedIntegrationEvent,
            TenantTerminationCoordinationRequestedHandler>(
                DataRightsModuleMetadata.Name,
                DataRightsModuleMetadata.Name);
        services.AddTaskHandler<
            ExecuteDataRightsAnonymisationPayload,
            ExecuteDataRightsAnonymisationTaskHandler>(
                DataRightsModuleMetadata.Name,
                handlerTimeout:
                    DataRightsTaskExecutionPolicy
                        .AnonymisationOwnerHandlerTimeout);
        services.AddTaskHandler<
            ExecuteDataRightsAnonymisationPayloadV2,
            ExecuteDataRightsAnonymisationTaskV2Handler>(
                DataRightsModuleMetadata.Name,
                handlerTimeout:
                    DataRightsTaskExecutionPolicy
                        .AnonymisationOwnerHandlerTimeout);
        services.AddTaskHandler<
            GenerateDataRightsExportPayload,
            GenerateDataRightsExportTaskHandler>(
                DataRightsModuleMetadata.Name);
        services.AddTaskHandler<
            DeleteExpiredDataRightsExportArtifactPayload,
            DeleteExpiredDataRightsExportArtifactTaskHandler>(
                DataRightsModuleMetadata.Name);
        services.AddTaskHandler<
            ExecuteTenantTerminationOwnerWorkPayload,
            ExecuteTenantTerminationOwnerWorkTaskHandler>(
                DataRightsModuleMetadata.Name,
                handlerTimeout:
                    DataRightsTaskExecutionPolicy
                        .TenantTerminationOwnerHandlerTimeout);
        services.AddTaskHandler<
            ExecuteTenantTerminationExportOwnerWorkPayload,
            ExecuteTenantTerminationExportOwnerWorkTaskHandler>(
                DataRightsModuleMetadata.Name,
                handlerTimeout:
                    DataRightsTaskExecutionPolicy
                        .TenantTerminationOwnerHandlerTimeout);
        services.AddTaskHandler<
            ExecuteGlobalTenantTerminationOwnerWorkPayload,
            ExecuteGlobalTenantTerminationOwnerWorkTaskHandler>(
                DataRightsModuleMetadata.Name,
                handlerTimeout:
                    DataRightsTaskExecutionPolicy
                        .TenantTerminationOwnerHandlerTimeout);
        services.AddTaskHandler<
            GenerateTenantTerminationExportArtifactPayload,
            GenerateTenantTerminationExportArtifactTaskHandler>(
                DataRightsModuleMetadata.Name);
        services.AddTaskHandler<
            DeleteExpiredTenantTerminationExportArtifactPayload,
            DeleteExpiredTenantTerminationExportArtifactTaskHandler>(
                DataRightsModuleMetadata.Name);
        services.AddTaskHandler<
            DeleteExpiredTenantTerminationExportFragmentPayload,
            DeleteExpiredTenantTerminationExportFragmentTaskHandler>(
                DataRightsModuleMetadata.Name);
        services.AddTaskHandler<
            VerifyTenantTerminationPayload,
            VerifyTenantTerminationTaskHandler>(
                DataRightsModuleMetadata.Name,
                handlerTimeout:
                    DataRightsTaskExecutionPolicy
                        .TenantTerminationVerificationHandlerTimeout);
        services.AddTaskHandler<
            DispatchDataRightsResponseDeadlineAlertsPayload,
            DispatchDataRightsResponseDeadlineAlertsTaskHandler>(
                DataRightsModuleMetadata.Name);
        return services;
    }

    public static IServiceCollection
        AddDataRightsTenantTerminationTaskScheduling(
            this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<
            ITenantTerminationTaskScheduler,
            TenantTerminationTaskScheduler>();
        services.TryAddScoped<
            ITenantTerminationExportRetentionScheduler,
            TenantTerminationExportRetentionScheduler>();
        return services;
    }

    public static IServiceCollection
        AddDataRightsUnavailableTenantTerminationTaskScheduling(
            this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<
            ITenantTerminationTaskScheduler,
            UnavailableTenantTerminationTaskScheduler>();
        services.TryAddScoped<
            ITenantTerminationExportRetentionScheduler,
            UnavailableTenantTerminationExportRetentionScheduler>();
        return services;
    }
}
