namespace BunkFy.Modules.Guests.Tests;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Messaging;
using Gma.Framework.ModuleComposition;
using Gma.Framework.Permissions;
using Gma.Framework.Tasks;
using Xunit;

[Trait("Category", "Unit")]
public sealed class GuestsProfileTests
{
    [Fact]
    public void Descriptor_exposes_scoped_permissions_subscriptions_and_privacy_safe_events()
    {
        IReadOnlyCollection<ModulePermissionDescriptor> permissions = GuestsModuleMetadata.Descriptor.GetPermissions();

        Assert.Equal(5, permissions.Count);
        Assert.All(permissions, permission => Assert.Equal(PermissionScopeRequirement.Scoped, permission.ScopeRequirement));
        Assert.Equal(8, GuestsModuleMetadata.Descriptor.GetSubscriptions().Count);
        Assert.Contains(
            GuestsModuleMetadata.Descriptor.GetSubscriptions(),
            subscription =>
                subscription.EventType ==
                    PropertyTimeZoneChangedIntegrationEvent.EventType &&
                subscription.ProducerModule ==
                    PropertiesModuleMetadata.Name);
        Assert.Contains(
            GuestsModuleMetadata.Descriptor.GetSubscriptions(),
            subscription =>
                subscription.EventType == PropertyProcessingPolicyActivatedIntegrationEvent.EventType &&
                subscription.ProducerModule == PropertiesModuleMetadata.Name);
        Assert.Contains(
            GuestsModuleMetadata.Descriptor.GetSubscriptions(),
            subscription =>
                subscription.EventType == PropertyProcessingSuspendedIntegrationEvent.EventType &&
                subscription.ProducerModule == PropertiesModuleMetadata.Name);
        Assert.Equal(6, GuestsModuleMetadata.Descriptor.GetPublishedEvents().Count);
        Assert.Contains(
            GuestsModuleMetadata.Descriptor.GetPublishedEvents(),
            published =>
                published.EventType == GuestProcessingRestrictionChangedIntegrationEvent.EventType);
        Assert.Contains(
            GuestsModuleMetadata.Descriptor.GetPublishedEvents(),
            published =>
                published.EventType == DataRightsCorrectionAppliedIntegrationEvent.EventType);
        Assert.Equal(2, GuestsModuleMetadata.Descriptor.GetTasks().Count);
        Assert.Single(GuestsModuleMetadata.Descriptor.GetCompositionProfiles());

        string[] forbiddenProperties =
        [
            "DisplayName", "LegalName", "Email", "Phone", "DateOfBirth",
            "NationalityCountryCode", "PreferredLanguageTag", "LanguageTags", "Notes"
        ];
        Type[] eventTypes =
        [
            typeof(GuestProfileCreatedIntegrationEvent),
            typeof(GuestProfileUpdatedIntegrationEvent),
            typeof(GuestProfileArchivedIntegrationEvent),
            typeof(GuestProfileAnonymisedIntegrationEvent),
            typeof(GuestProcessingRestrictionChangedIntegrationEvent),
            typeof(DataRightsCorrectionAppliedIntegrationEvent),
            typeof(ReservationGuestLinkedIntegrationEvent),
            typeof(ReservationGuestStayChangedIntegrationEvent)
        ];
        Assert.All(eventTypes, eventType => Assert.DoesNotContain(
            eventType.GetProperties(),
            property => forbiddenProperties.Contains(property.Name, StringComparer.Ordinal)));
        Assert.EndsWith(".guests.guest-profile-created.v1", GuestsIntegrationSubjects.CreateProfileCreated(), StringComparison.Ordinal);
        Assert.EndsWith(
            ".guests.guest-profile-anonymised.v1",
            GuestsIntegrationSubjects.CreateProfileAnonymised(),
            StringComparison.Ordinal);
        Assert.EndsWith(
            ".guests.guest-processing-restriction-changed.v1",
            GuestsIntegrationSubjects.CreateProcessingRestrictionChanged(),
            StringComparison.Ordinal);
        Assert.EndsWith(".reservations.reservation-guest-linked.v1", GuestsIntegrationSubjects.CreateReservationGuestLinked(), StringComparison.Ordinal);
    }
}
