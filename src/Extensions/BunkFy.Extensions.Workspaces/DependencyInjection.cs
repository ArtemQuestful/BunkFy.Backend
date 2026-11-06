namespace BunkFy.Extensions.Workspaces;

using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.AccessControl;
using Gma.Framework.Messaging;
using Gma.Framework.Observability;
using Gma.Modules.AccessControl.Contracts;
using Gma.Modules.Organizations.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

public static class DependencyInjection
{
    public static IServiceCollection AddBunkFySupportAccess(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<BunkFySupportAccessOptions>()
            .Bind(configuration.GetSection(BunkFySupportAccessOptions.SectionName))
            .Validate(
                options => options.MaximumGrantMinutes is >= 1 and <= 1440,
                "BunkFy:SupportAccess:MaximumGrantMinutes must be from 1 through 1440.")
            .ValidateOnStart();
        services.AddSecuritySignalCore();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            ISecuritySignalDefinitionSource,
            SupportAccessSecuritySignalDefinitions>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<
            IAccessRoleAssignmentPolicy,
            WorkspaceRoleAssignmentPolicy>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<
            IAccessRoleAssignmentLifecycleObserver,
            SupportAccessLifecycleObserver>());
        return services;
    }

    public static IServiceCollection AddBunkFyWorkspaces(
        this IServiceCollection services,
        Action<BunkFyWorkspacesOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configure is not null)
        {
            services.Configure(configure);
        }
        else
        {
            services.AddOptions<BunkFyWorkspacesOptions>();
        }

        services.AddOptions<BunkFyWorkspacesOptions>()
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.GlobalAuthScopeId),
                "A global Auth scope id is required.")
            .ValidateOnStart();

        services.TryAddEnumerable(ServiceDescriptor.Scoped<
            IAccessProfileAssignmentPolicy,
            WorkspaceAccessProfileAssignmentPolicy>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<
            IAccessRoleAssignmentPolicy,
            WorkspaceOperationalRoleAssignmentPolicy>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<
            IAccessRoleAssignmentPolicy,
            WorkspaceOwnerRoleAssignmentPolicy>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<
            IAccessProfileMutationAdmissionPolicy,
            WorkspaceAccessProfileMutationAdmissionPolicy>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<
            IAccessDecisionProvider,
            WorkspaceOwnerMembershipAccessDecisionProvider>());
        services.TryAddScoped<IWorkspaceStaffStationAdmissionObserver, WorkspaceStaffStationAdmissionObserver>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<
            IOrganizationMutationAdmissionPolicy,
            WorkspaceOrganizationMutationAdmissionPolicy>());

        services.AddIntegrationEventHandler<
            OrganizationMembershipChangedIntegrationEvent,
            OrganizationOwnerStaffBootstrapHandler>(
            StaffModuleMetadata.Name,
            OrganizationsModuleMetadata.Name);
        services.AddIntegrationEventHandler<
            WorkspaceStaffOnboardingIdentityAnchorResolvedIntegrationEvent,
            WorkspaceStaffOnboardingIdentityAnchorResolutionHandler>(
            StaffModuleMetadata.Name,
            WorkspacesModuleMetadata.Name);

        return services;
    }

    public static IServiceCollection AddBunkFyWorkspaceAdmission(
        this IServiceCollection services,
        IConfiguration configuration,
        bool requireExplicitPolicy)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        bool passwordRegistrationEnabled = ReadBoolean(
            configuration,
            "Auth:SelfRegistration:PasswordEnabled",
            defaultValue: true);
        bool externalRegistrationEnabled = ReadBoolean(
            configuration,
            "Auth:SelfRegistration:ExternalEnabled",
            defaultValue: true);
        bool selfServiceWorkspaceCreationEnabled = ReadBoolean(
            configuration,
            "Organizations:SelfServiceCreationEnabled",
            defaultValue: false);

        services.AddOptions<BunkFyWorkspaceAdmissionOptions>()
            .Configure(options =>
            {
                options.AccountRegistration = ReadEnum<BunkFyAccountRegistrationMode>(
                    configuration,
                    $"{BunkFyWorkspaceAdmissionOptions.SectionName}:AccountRegistration");
                options.WorkspaceCreation = ReadEnum<BunkFyWorkspaceCreationMode>(
                    configuration,
                    $"{BunkFyWorkspaceAdmissionOptions.SectionName}:WorkspaceCreation");
                options.RequireVerifiedEmailForWorkspaceCreation = ReadBoolean(
                    configuration,
                    $"{BunkFyWorkspaceAdmissionOptions.SectionName}:RequireVerifiedEmailForWorkspaceCreation",
                    defaultValue: true);
            })
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<BunkFyWorkspaceAdmissionOptions>>(
            new BunkFyWorkspaceAdmissionOptionsValidator(
                requireExplicitPolicy,
                passwordRegistrationEnabled,
                externalRegistrationEnabled,
                selfServiceWorkspaceCreationEnabled)));
        services.TryAddEnumerable(ServiceDescriptor.Scoped<
            IOrganizationCreationAdmissionPolicy,
            BunkFyWorkspaceAdmissionPolicy>());

        return services;
    }

    private static bool ReadBoolean(
        IConfiguration configuration,
        string key,
        bool defaultValue)
    {
        string? value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return bool.TryParse(value, out bool parsed)
            ? parsed
            : throw new InvalidOperationException($"{key} must be either true or false.");
    }

    private static TEnum ReadEnum<TEnum>(IConfiguration configuration, string key)
        where TEnum : struct, Enum
    {
        string? value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            return default;
        }

        return Enum.TryParse(value, ignoreCase: true, out TEnum parsed)
            ? parsed
            : (TEnum)Enum.ToObject(typeof(TEnum), -1);
    }
}
