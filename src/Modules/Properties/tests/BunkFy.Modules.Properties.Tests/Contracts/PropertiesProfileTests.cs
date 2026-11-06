namespace BunkFy.Modules.Properties.Tests;

using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Permissions;
using Gma.Framework.Messaging;
using Gma.Framework.ModuleComposition;
using Gma.Framework.Tenancy;
using Xunit;

[Trait("Category", "Unit")]
public sealed class PropertiesProfileTests
{
    [Fact]
    public void Default_profile_documents_properties_runtime_dependencies()
    {
        ModuleProfileDescriptor profile = PropertiesProfiles.Default;

        Assert.Equal(PropertiesModuleMetadata.Name, profile.ModuleName);
        Assert.Equal(PropertiesProfiles.DefaultName, profile.ProfileName);
        Assert.Contains(profile.Provides, feature => feature.Id == PropertiesCompositionFeatures.PhysicalSetup);
        Assert.Contains(profile.Requires, feature => feature.Id == TenancyCompositionFeatures.Context);
        Assert.Contains(profile.Requires, feature => feature.Id == MessagingCompositionFeatures.Outbox);
    }

    [Fact]
    public void Descriptor_exposes_profile_permissions_and_events()
    {
        ModuleProfileDescriptor profile = Assert.Single(PropertiesModuleMetadata.Descriptor.GetCompositionProfiles());

        Assert.Equal(PropertiesProfiles.DefaultName, profile.ProfileName);
        IReadOnlyCollection<ModulePermissionDescriptor> permissions = PropertiesModuleMetadata.Descriptor.GetPermissions();
        Assert.Equal(5, permissions.Count);
        Assert.All(permissions, permission => Assert.Equal(PermissionScopeRequirement.Scoped, permission.ScopeRequirement));
        Assert.All(permissions, permission => Assert.Equal(PermissionScopeGrantPolicy.Descendants, permission.ScopeGrantPolicy));
        Assert.Equal(16, PropertiesModuleMetadata.Descriptor.GetPublishedEvents().Count);
        Assert.Equal(2, PropertiesModuleMetadata.Descriptor.GetSubscriptions().Count);
        Assert.Contains(
            PropertiesModuleMetadata.Descriptor.GetPublishedEvents(),
            publishedEvent => publishedEvent.EventType == PropertyRetiredIntegrationEvent.EventType);
        Assert.Contains(
            PropertiesModuleMetadata.Descriptor.GetPublishedEvents(),
            publishedEvent => publishedEvent.EventType ==
                PropertyTimeZoneChangedIntegrationEvent.EventType);
        Assert.Contains(
            PropertiesModuleMetadata.Descriptor.GetPublishedEvents(),
            publishedEvent => publishedEvent.EventType == BedRetirementFinalizedIntegrationEvent.EventType);
        Assert.Contains(
            PropertiesModuleMetadata.Descriptor.GetPublishedEvents(),
            publishedEvent => publishedEvent.EventType == RoomRetirementFinalizedIntegrationEvent.EventType);
        Assert.Contains(
            PropertiesModuleMetadata.Descriptor.GetSubscriptions(),
            subscription => subscription.EventType == BedRetirementFinalizationRequestedIntegrationEvent.EventType &&
                subscription.ProducerModule == PropertiesModuleMetadata.RetirementProducerModuleName);
        Assert.Contains(
            PropertiesModuleMetadata.Descriptor.GetSubscriptions(),
            subscription => subscription.EventType == RoomRetirementFinalizationRequestedIntegrationEvent.EventType &&
                subscription.ProducerModule == PropertiesModuleMetadata.RetirementProducerModuleName);
    }
}
