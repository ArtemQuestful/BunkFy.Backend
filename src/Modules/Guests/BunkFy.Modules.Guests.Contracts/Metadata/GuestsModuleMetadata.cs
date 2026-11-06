namespace BunkFy.Modules.Guests.Contracts;

using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.DataRights.Contracts;
using Gma.Framework.Messaging;
using Gma.Framework.ModuleComposition;
using Gma.Framework.Modules;
using Gma.Framework.Permissions;
using Gma.Framework.Tasks;

public static class GuestsModuleMetadata
{
    public const string Name = "guests";
    public const string Schema = "guests";
    public const string PropertyCreatedHandlerName = "property-created";
    public const string PropertyUpdatedHandlerName = "property-updated";
    public const string PropertyTimeZoneChangedHandlerName =
        "property-time-zone-changed";
    public const string PropertyRetiredHandlerName = "property-retired";
    public const string PropertyProcessingPolicyActivatedHandlerName = "property-processing-policy-activated";
    public const string PropertyProcessingSuspendedHandlerName = "property-processing-suspended";
    public const string PropertiesProjectionName = "properties";
    public const int PropertiesProjectionVersion = 3;
    public const string StayHistoryProjectionName = "reservation-stay-history";
    public const int StayHistoryProjectionVersion = 1;
    public const string ProjectionWorkerGroup = "projection-workers";
    public const string ReservationsProducerModuleName = "reservations";
    public const string ReservationGuestLinkedHandlerName = "reservation-guest-linked";
    public const string ReservationGuestStayChangedHandlerName = "reservation-guest-stay-changed";

    public static ModuleDescriptor Descriptor { get; } = ModuleDescriptor
        .Create(Name)
        .WithSchema(Schema)
        .WithPermissions([
            new ModulePermissionDescriptor(GuestsAdminPermissionCodes.Read, "Read guest records.", PermissionScopeRequirement.Scoped, PermissionScopeGrantPolicy.Descendants),
            new ModulePermissionDescriptor(GuestsAdminPermissionCodes.Create, "Create guest records.", PermissionScopeRequirement.Scoped, PermissionScopeGrantPolicy.Descendants),
            new ModulePermissionDescriptor(GuestsAdminPermissionCodes.Manage, "Manage guest records.", PermissionScopeRequirement.Scoped, PermissionScopeGrantPolicy.Descendants),
            new ModulePermissionDescriptor(GuestsAdminPermissionCodes.Archive, "Archive guest records.", PermissionScopeRequirement.Scoped, PermissionScopeGrantPolicy.Descendants),
            new ModulePermissionDescriptor(GuestsAdminPermissionCodes.DataHoldsManage, "Place, review, and release Guest data holds.", PermissionScopeRequirement.Scoped, PermissionScopeGrantPolicy.Descendants)
        ])
        .WithSubscription<PropertyCreatedIntegrationEvent>(PropertiesModuleMetadata.Name, PropertyCreatedHandlerName)
        .WithSubscription<PropertyUpdatedIntegrationEvent>(PropertiesModuleMetadata.Name, PropertyUpdatedHandlerName)
        .WithSubscription<PropertyTimeZoneChangedIntegrationEvent>(
            PropertiesModuleMetadata.Name,
            PropertyTimeZoneChangedHandlerName)
        .WithSubscription<PropertyRetiredIntegrationEvent>(PropertiesModuleMetadata.Name, PropertyRetiredHandlerName)
        .WithSubscription<PropertyProcessingPolicyActivatedIntegrationEvent>(
            PropertiesModuleMetadata.Name,
            PropertyProcessingPolicyActivatedHandlerName)
        .WithSubscription<PropertyProcessingSuspendedIntegrationEvent>(
            PropertiesModuleMetadata.Name,
            PropertyProcessingSuspendedHandlerName)
        .WithSubscription<ReservationGuestLinkedIntegrationEvent>(ReservationsProducerModuleName, ReservationGuestLinkedHandlerName)
        .WithSubscription<ReservationGuestStayChangedIntegrationEvent>(ReservationsProducerModuleName, ReservationGuestStayChangedHandlerName)
        .WithPublishedEvent<GuestProfileCreatedIntegrationEvent>()
        .WithPublishedEvent<GuestProfileUpdatedIntegrationEvent>()
        .WithPublishedEvent<GuestProfileArchivedIntegrationEvent>()
        .WithPublishedEvent<GuestProfileAnonymisedIntegrationEvent>()
        .WithPublishedEvent<GuestProcessingRestrictionChangedIntegrationEvent>()
        .WithPublishedEvent<DataRightsCorrectionAppliedIntegrationEvent>()
        .WithTask<RebuildGuestsPropertiesPayload>()
        .WithTask<RebuildGuestStayHistoryPayload>()
        .WithProfile(GuestsProfiles.Default)
        .Build();
}
