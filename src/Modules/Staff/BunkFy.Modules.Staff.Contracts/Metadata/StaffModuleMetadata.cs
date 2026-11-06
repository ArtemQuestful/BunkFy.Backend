namespace BunkFy.Modules.Staff.Contracts;

using BunkFy.Modules.DataRights.Contracts;
using Gma.Framework.ModuleComposition;
using Gma.Framework.Messaging;
using Gma.Framework.Modules;
using Gma.Framework.Permissions;
using Gma.Framework.Tasks;
using BunkFy.Modules.Properties.Contracts;

public static class StaffModuleMetadata
{
    public const string Name = "staff";
    public const string Schema = "staff";
    public const string PropertyCreatedHandlerName = "property-created";
    public const string PropertyUpdatedHandlerName = "property-updated";
    public const string PropertyRetiredHandlerName = "property-retired";
    public const string PropertiesProjectionName = "properties";
    public const int PropertiesProjectionVersion = 1;
    public const string ProjectionWorkerGroup = "projection-workers";

    public static ModuleDescriptor Descriptor { get; } = ModuleDescriptor
        .Create(Name)
        .WithSchema(Schema)
        .WithPermissions([
            new ModulePermissionDescriptor(StaffAdminPermissionCodes.Read, "Read the operational staff directory.", PermissionScopeRequirement.Scoped, PermissionScopeGrantPolicy.Descendants),
            new ModulePermissionDescriptor(StaffAdminPermissionCodes.SensitiveProfileRead, "Read sensitive staff employment profiles.", PermissionScopeRequirement.Scoped, PermissionScopeGrantPolicy.Descendants),
            new ModulePermissionDescriptor(StaffAdminPermissionCodes.Create, "Create staff profiles.", PermissionScopeRequirement.Scoped, PermissionScopeGrantPolicy.Descendants),
            new ModulePermissionDescriptor(StaffAdminPermissionCodes.Manage, "Manage staff profiles.", PermissionScopeRequirement.Scoped, PermissionScopeGrantPolicy.Descendants),
            new ModulePermissionDescriptor(StaffAdminPermissionCodes.AccountLinksManage, "Manage Staff account links.", PermissionScopeRequirement.Scoped, PermissionScopeGrantPolicy.Descendants),
            new ModulePermissionDescriptor(StaffAdminPermissionCodes.AssignProperties, "Manage staff property assignments.", PermissionScopeRequirement.Scoped, PermissionScopeGrantPolicy.Descendants),
            new ModulePermissionDescriptor(StaffAdminPermissionCodes.ManageLifecycle, "Manage staff employment lifecycle.", PermissionScopeRequirement.Scoped, PermissionScopeGrantPolicy.Descendants),
            new ModulePermissionDescriptor(StaffAdminPermissionCodes.EmploymentGovernanceManage, "Manage Staff employment-governance policy bindings.", PermissionScopeRequirement.Scoped, PermissionScopeGrantPolicy.Descendants),
            new ModulePermissionDescriptor(StaffAdminPermissionCodes.DataHoldsManage, "Place, review, and release Staff data holds.", PermissionScopeRequirement.Scoped, PermissionScopeGrantPolicy.Descendants)
        ])
        .WithSubscription<PropertyCreatedIntegrationEvent>(PropertiesModuleMetadata.Name, PropertyCreatedHandlerName)
        .WithSubscription<PropertyUpdatedIntegrationEvent>(PropertiesModuleMetadata.Name, PropertyUpdatedHandlerName)
        .WithSubscription<PropertyRetiredIntegrationEvent>(PropertiesModuleMetadata.Name, PropertyRetiredHandlerName)
        .WithPublishedEvent<StaffMemberCreatedIntegrationEvent>()
        .WithPublishedEvent<StaffMemberUpdatedIntegrationEvent>()
        .WithPublishedEvent<StaffMemberLifecycleChangedIntegrationEvent>()
        .WithPublishedEvent<StaffMemberAnonymisedIntegrationEvent>()
        .WithPublishedEvent<StaffAuthSubjectChangedIntegrationEvent>()
        .WithPublishedEvent<StaffPropertyAssignmentChangedIntegrationEvent>()
        .WithPublishedEvent<StaffProcessingRestrictionChangedIntegrationEvent>()
        .WithPublishedEvent<
            StaffIdentityProvisioningAnchorCreatedIntegrationEvent>()
        .WithPublishedEvent<DataRightsTenantCorrectionAppliedIntegrationEvent>()
        .WithTask<RebuildStaffPropertiesPayload>()
        .WithProfile(StaffProfiles.Default)
        .Build();
}
