namespace BunkFy.Modules.Retention.Application;

using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Retention.Application.Handlers;
using BunkFy.Modules.Retention.Application.Security;
using BunkFy.Modules.Retention.Application.Tasks;
using BunkFy.Modules.Retention.Contracts;
using Gma.Framework.AccessControl;
using Gma.Framework.Application.Composition;
using Gma.Framework.Messaging;
using Gma.Framework.Observability;
using Gma.Framework.Tasks;
using Gma.Modules.Organizations.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

public static class DependencyInjection
{
    public static IServiceCollection AddRetentionApplication(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSecuritySignalCore();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                ISecuritySignalDefinitionSource,
                RetentionSecuritySignalDefinitions>());
        services.AddApplicationServicesFromAssembly(typeof(DependencyInjection).Assembly);
        services.TryAddScoped<
            IRetentionRunRetryExecutor,
            UnavailableRetentionRunRetryExecutor>();
        services.TryAddScoped<RetentionExecutionMutationCoordinator>();
        services.TryAddScoped<RetentionScopeMutationCoordinator>();
        services.AddIntegrationEventHandler<
            OrganizationChangedIntegrationEvent,
            RetentionOrganizationChangedHandler>(
                RetentionModuleMetadata.Name,
                RetentionModuleMetadata.OrganizationsProducerModuleName);
        services.AddIntegrationEventHandler<
            PropertyCreatedIntegrationEvent,
            RetentionPropertyCreatedHandler>(
                RetentionModuleMetadata.Name,
                PropertiesModuleMetadata.Name);
        services.AddIntegrationEventHandler<
            PropertyUpdatedIntegrationEvent,
            RetentionPropertyUpdatedHandler>(
                RetentionModuleMetadata.Name,
                PropertiesModuleMetadata.Name);
        services.AddIntegrationEventHandler<
            PropertyRetiredIntegrationEvent,
            RetentionPropertyRetiredHandler>(
                RetentionModuleMetadata.Name,
                PropertiesModuleMetadata.Name);
        services.AddIntegrationEventHandler<
            PropertyProcessingPolicyActivatedIntegrationEvent,
            RetentionPropertyProcessingPolicyActivatedHandler>(
                RetentionModuleMetadata.Name,
                PropertiesModuleMetadata.Name);
        services.AddIntegrationEventHandler<
            PropertyProcessingSuspendedIntegrationEvent,
            RetentionPropertyProcessingSuspendedHandler>(
                RetentionModuleMetadata.Name,
                PropertiesModuleMetadata.Name);
        services.AddIntegrationEventHandler<
            RetentionRunRetryRequestedIntegrationEvent,
            RetentionRunRetryRequestedHandler>(
                RetentionModuleMetadata.Name,
                RetentionModuleMetadata.Name);
        services.AddGmaAccessControlPermissionPolicies(
            RetentionModuleMetadata.Descriptor);
        return services;
    }

    public static IServiceCollection AddRetentionTaskHandlers(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddTaskHandler<
            ExecuteRetentionSchedulePayload,
            ExecuteRetentionScheduleTaskHandler>(
                RetentionModuleMetadata.Name,
                handlerTimeout: RetentionTaskExecutionPolicy.HandlerTimeout);
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                ITaskScheduleProvider,
                RetentionScheduleProvider>());
        return services;
    }
}
