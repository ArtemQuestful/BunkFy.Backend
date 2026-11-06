namespace BunkFy.Modules.Retention.Contracts;

using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Messaging;
using Gma.Framework.ModuleComposition;
using Gma.Framework.Modules;
using Gma.Framework.Permissions;
using Gma.Framework.Tasks;
using Gma.Modules.Organizations.Contracts;

public static class RetentionModuleMetadata
{
    public const string Name = "retention";
    public const string Schema = "retention";
    public const string WorkerGroup = "retention-workers";
    public const string OrganizationsProducerModuleName = "organizations";
    public const string OrganizationChangedHandlerName = "organization-changed";
    public const string PropertyCreatedHandlerName = "property-created";
    public const string PropertyUpdatedHandlerName = "property-updated";
    public const string PropertyRetiredHandlerName = "property-retired";
    public const string PropertyProcessingPolicyActivatedHandlerName =
        "property-processing-policy-activated";
    public const string PropertyProcessingSuspendedHandlerName =
        "property-processing-suspended";
    public const string RunRetryRequestedHandlerName =
        "retention-run-retry-requested";

    public static ModuleDescriptor Descriptor { get; } = ModuleDescriptor
        .Create(Name)
        .WithSchema(Schema)
        .WithPermissions(
        [
            Permission(RetentionPermissionCodes.Read, "Read retention schedule health."),
            Permission(RetentionPermissionCodes.Manage, "Manage retention schedules."),
            Permission(RetentionPermissionCodes.Retry, "Retry failed retention executions.")
        ])
        .WithSubscription<OrganizationChangedIntegrationEvent>(
            OrganizationsProducerModuleName,
            OrganizationChangedHandlerName)
        .WithSubscription<PropertyCreatedIntegrationEvent>(
            PropertiesModuleMetadata.Name,
            PropertyCreatedHandlerName)
        .WithSubscription<PropertyUpdatedIntegrationEvent>(
            PropertiesModuleMetadata.Name,
            PropertyUpdatedHandlerName)
        .WithSubscription<PropertyRetiredIntegrationEvent>(
            PropertiesModuleMetadata.Name,
            PropertyRetiredHandlerName)
        .WithSubscription<PropertyProcessingPolicyActivatedIntegrationEvent>(
            PropertiesModuleMetadata.Name,
            PropertyProcessingPolicyActivatedHandlerName)
        .WithSubscription<PropertyProcessingSuspendedIntegrationEvent>(
            PropertiesModuleMetadata.Name,
            PropertyProcessingSuspendedHandlerName)
        .WithPublishedEvent<RetentionRunRetryRequestedIntegrationEvent>()
        .WithSubscription<RetentionRunRetryRequestedIntegrationEvent>(
            Name,
            RunRetryRequestedHandlerName)
        .WithTask<ExecuteRetentionSchedulePayload>()
        .WithProfile(RetentionProfiles.Default)
        .Build();

    private static ModulePermissionDescriptor Permission(
        string code,
        string description) => new(
            code,
            description,
            PermissionScopeRequirement.Scoped,
            PermissionScopeGrantPolicy.Descendants);
}
