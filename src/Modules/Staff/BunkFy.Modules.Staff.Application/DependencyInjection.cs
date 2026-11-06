namespace BunkFy.Modules.Staff.Application;

using BunkFy.DataGovernance;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Retention.Contracts;
using BunkFy.Modules.Staff.Application.Contributors;
using BunkFy.Modules.Staff.Application.Policies;
using Gma.Framework.AccessControl;
using Gma.Framework.Application.Composition;
using Gma.Framework.Messaging;
using Gma.Framework.ProjectionRebuild.Tasks;
using Gma.Framework.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Staff.Application.Handlers;
using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Application.Tasks;
using BunkFy.Modules.Staff.Contracts;

public static class DependencyInjection
{
    public static IServiceCollection AddStaffApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(_ => CountryPolicyRegistry.Create(
            [],
            [],
            CountryPolicyRuntimeMode.Engineering));
        services.AddOptions<StaffRetentionOptions>()
            .BindConfiguration(StaffRetentionOptions.SectionName)
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IValidateOptions<StaffRetentionOptions>,
                StaffRetentionOptionsValidator>());
        services.AddApplicationServicesFromAssembly(typeof(DependencyInjection).Assembly);
        services.TryAddScoped<IStaffIdentityBootstrapper, StaffIdentityBootstrapper>();
        services.TryAddScoped<
            IStaffIdentityProvisioningAnchorCutover,
            StaffIdentityProvisioningAnchorCutover>();
        services.TryAddScoped<
            IStaffWorkspaceOnboardingIdentityAnchorOutcomeReader,
            StaffWorkspaceOnboardingIdentityAnchorLifecycle>();
        services.TryAddScoped<
            IStaffWorkspaceOnboardingIdentityAnchorResolutionRecorder,
            StaffWorkspaceOnboardingIdentityAnchorLifecycle>();
        services.TryAddScoped<IStaffOnboardingProvisioner, StaffOnboardingProvisioner>();
        services.TryAddScoped<
            IStaffPropertyAssignmentProvisioner,
            StaffPropertyAssignmentProvisioner>();
        services.TryAddScoped<StaffMemberMutationCoordinator>();
        services.TryAddScoped<StaffOnboardingProvisioningCoordinator>();
        services.TryAddScoped<
            IStaffIdentityProvisioningAnchorWriter,
            StaffIdentityProvisioningAnchorWriter>();
        services.TryAddScoped<
            StaffWorkspaceOnboardingAnchorCutoverCoordinator>();
        services.TryAddScoped<
            StaffWorkspaceOnboardingIdentityAnchorLifecycleCoordinator>();
        services.TryAddScoped<StaffProfileUpdateCoordinator>();
        services.TryAddScoped<StaffAuthSubjectChangeCoordinator>();
        services.TryAddScoped<StaffLifecycleChangeCoordinator>();
        services.TryAddScoped<StaffPropertyAssignmentChangeCoordinator>();
        services.TryAddScoped<StaffLifecyclePolicyEvaluator>();
        services.TryAddScoped<StaffRetentionEligibilityEvaluator>();
        services.TryAddScoped<StaffRetentionPrerequisiteEvaluator>();
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                IRetentionExecutionContributor,
                StaffRetentionContributor>());
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                IDataRightsCorrectionPolicyContributor,
                StaffDataRightsCorrectionPolicyContributor>());
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                IDataRightsRestrictionContributor,
                StaffDataRightsRestrictionContributor>());
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                IDataRightsAnonymisationPolicyContributor,
                StaffDataRightsAnonymisationPolicyContributor>());
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                IDataRightsAnonymisationContributorV2,
                StaffDataRightsAnonymisationContributor>());
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<
                IDataRightsAnonymisationRestoreContributorV3,
                StaffDataRightsAnonymisationRestoreContributor>());
        services.AddGmaAccessControlPermissionPolicies(StaffModuleMetadata.Descriptor);
        services.AddIntegrationEventHandler<PropertyCreatedIntegrationEvent, StaffPropertyCreatedHandler>(
            StaffModuleMetadata.Name, PropertiesModuleMetadata.Name);
        services.AddIntegrationEventHandler<PropertyUpdatedIntegrationEvent, StaffPropertyUpdatedHandler>(
            StaffModuleMetadata.Name, PropertiesModuleMetadata.Name);
        services.AddIntegrationEventHandler<PropertyRetiredIntegrationEvent, StaffPropertyRetiredHandler>(
            StaffModuleMetadata.Name, PropertiesModuleMetadata.Name);
        return services;
    }

    public static IServiceCollection AddStaffTaskHandlers(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddProjectionRebuildTasks();
        services.AddTaskHandler<RebuildStaffPropertiesPayload, RebuildStaffPropertiesTaskHandler>(
            StaffModuleMetadata.Name);
        return services;
    }
}
