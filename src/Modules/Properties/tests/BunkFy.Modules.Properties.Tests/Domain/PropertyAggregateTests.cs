namespace BunkFy.Modules.Properties.Tests;

using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Domain.Errors;
using BunkFy.Modules.Properties.Domain.Events;
using BunkFy.Modules.Properties.Domain.ValueObjects;
using Gma.Framework.Naming;
using Gma.Framework.Results;
using Xunit;

[Trait("Category", "Unit")]
public sealed class PropertyAggregateTests
{
    [Fact]
    public void Create_normalizes_tenant_and_property_code()
    {
        Result<Property> result = CreateProperty(" tenant-a ", code: " HOSTEL-ONE ");

        Assert.True(result.IsSuccess);
        Assert.Equal("tenant-a", result.Value.ScopeId);
        Assert.Equal("hostel-one", result.Value.Code.Value);
        Assert.Equal(1, result.Value.Version);
        PropertyCreatedDomainEvent domainEvent = Assert.IsType<PropertyCreatedDomainEvent>(Assert.Single(result.Value.DomainEvents));
        Assert.Equal("hostel-one", domainEvent.Code);
        Assert.Equal(1, domainEvent.PropertyVersion);
    }

    [Fact]
    public void Creation_matching_uses_normalized_property_details()
    {
        Property property = CreateProperty("tenant-a").Value;
        PropertyDetails same = PropertyDetails.Create(
            "  Hostel One  ",
            " HOSTEL-ONE ",
            " UTC ").Value;
        PropertyDetails changed = PropertyDetails.Create(
            "Hostel Two",
            "hostel-one",
            "UTC").Value;

        Assert.True(property.MatchesCreation(same));
        Assert.False(property.MatchesCreation(changed));
    }

    [Fact]
    public void Create_rejects_invalid_required_values()
    {
        Assert.Equal(PropertiesDomainErrors.TenantRequired, CreateProperty(" ").Error);
        Assert.Equal(PropertiesDomainErrors.TenantInvalid, CreateProperty(new string('x', TenantIds.MaxLength + 1)).Error);
        Assert.Equal(PropertiesDomainErrors.PropertyIdRequired, CreateProperty("tenant-a", propertyId: Guid.Empty).Error);
        Assert.Equal(PropertiesDomainErrors.DomainEventIdRequired, CreateProperty("tenant-a", eventId: Guid.Empty).Error);
        Assert.Equal(PropertiesDomainErrors.PropertyNameRequired, CreateProperty("tenant-a", name: " ").Error);
        Assert.Equal(PropertiesDomainErrors.PropertyCodeInvalid, CreateProperty("tenant-a", code: "-bad").Error);
        Assert.Equal(PropertiesDomainErrors.TimeZoneInvalid, CreateProperty("tenant-a", timeZoneId: "Invalid/Zone").Error);
    }

    [Theory]
    [InlineData("UTC")]
    [InlineData("Pacific Standard Time")]
    [InlineData("Historical/UnknownZone")]
    public void Create_with_restored_details_rejects_non_primary_time_zones(
        string timeZoneId)
    {
        PropertyDetails restored = PropertyDetails.RestorePersistedTimeZone(
            "Hostel One",
            "hostel-one",
            timeZoneId).Value;

        Result<Property> result = Property.Create(
            Guid.NewGuid(),
            "tenant-a",
            restored,
            Guid.NewGuid(),
            GovernanceNow);

        Assert.Equal(PropertiesDomainErrors.TimeZoneInvalid, result.Error);
    }

    [Theory]
    [InlineData("UTC")]
    [InlineData("Pacific Standard Time")]
    [InlineData("Historical/UnknownZone")]
    public void Time_zone_mutation_rejects_restored_non_primary_targets(
        string timeZoneId)
    {
        Property property = CreateProperty("tenant-a").Value;
        property.ClearDomainEvents();
        PropertyTimeZoneId restored =
            PropertyTimeZoneId.RestorePersisted(timeZoneId);

        Result<PropertyDetailsUpdateOutcome> evaluation =
            property.EvaluateTimeZoneChange(restored, property.Version);
        Result<PropertyDetailsUpdateOutcome> mutation = property.SetTimeZone(
            restored,
            property.Version,
            Guid.NewGuid(),
            GovernanceNow);

        Assert.Equal(PropertiesDomainErrors.TimeZoneInvalid, evaluation.Error);
        Assert.Equal(PropertiesDomainErrors.TimeZoneInvalid, mutation.Error);
        Assert.Equal("Etc/UTC", property.TimeZoneId.Value);
        Assert.Equal(1, property.Version);
        Assert.Empty(property.DomainEvents);
    }

    [Fact]
    public void Update_changes_setup_values_and_raises_event()
    {
        Property property = CreateProperty("tenant-a").Value;
        property.ClearDomainEvents();

        Result result = property.Update("Updated", "updated", "UTC", property.Version, Guid.NewGuid(), DateTimeOffset.UtcNow);

        Assert.True(result.IsSuccess);
        Assert.Equal("Updated", property.Name.Value);
        Assert.Equal("updated", property.Code.Value);
        Assert.Equal(2, property.Version);
        PropertyUpdatedDomainEvent domainEvent = Assert.IsType<PropertyUpdatedDomainEvent>(Assert.Single(property.DomainEvents));
        Assert.Equal(2, domainEvent.PropertyVersion);
    }

    [Fact]
    public void Normalized_no_change_update_keeps_version_and_emits_no_event()
    {
        Property property = CreateProperty("tenant-a").Value;
        property.ClearDomainEvents();
        PropertyDetails details = PropertyDetails.Create(
            "  Hostel One  ",
            " HOSTEL-ONE ",
            " UTC ").Value;

        Result<PropertyDetailsUpdateOutcome> result =
            property.UpdateDetails(
                details,
                property.Version,
                Guid.Empty,
                DateTimeOffset.UtcNow);

        Assert.True(result.IsSuccess);
        Assert.Equal(PropertyDetailsUpdateOutcome.Unchanged, result.Value);
        Assert.Equal(1, property.Version);
        Assert.Null(property.UpdatedAtUtc);
        Assert.Empty(property.DomainEvents);
    }

    [Fact]
    public void Generic_details_update_cannot_bypass_the_time_zone_protocol()
    {
        Property property = CreateProperty("tenant-a").Value;
        property.ClearDomainEvents();
        PropertyDetails changedZone = PropertyDetails.Create(
            "Updated",
            "updated",
            "Europe/London").Value;

        Result<PropertyDetailsUpdateOutcome> result =
            property.UpdateDetails(
                changedZone,
                property.Version,
                Guid.NewGuid(),
                DateTimeOffset.UtcNow);

        Assert.Equal(
            PropertiesDomainErrors.TimeZoneDedicatedOperationRequired,
            result.Error);
        Assert.Equal("Hostel One", property.Name.Value);
        Assert.Equal("hostel-one", property.Code.Value);
        Assert.Equal("Etc/UTC", property.TimeZoneId.Value);
        Assert.Equal(1, property.Version);
        Assert.Null(property.UpdatedAtUtc);
        Assert.Empty(property.DomainEvents);
    }

    [Fact]
    public void Public_generic_update_cannot_change_the_time_zone()
    {
        Property property = CreateProperty("tenant-a").Value;
        property.ClearDomainEvents();

        Result result = property.Update(
            "Updated",
            "updated",
            "Europe/London",
            property.Version,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow);

        Assert.Equal(
            PropertiesDomainErrors.TimeZoneDedicatedOperationRequired,
            result.Error);
        Assert.Equal("Etc/UTC", property.TimeZoneId.Value);
        Assert.Equal(1, property.Version);
        Assert.Empty(property.DomainEvents);
    }

    [Fact]
    public void Stale_version_is_rejected_without_mutation()
    {
        Property property = CreateProperty("tenant-a").Value;

        Result result = property.Update("Updated", "updated", "UTC", expectedVersion: 99, Guid.NewGuid(), DateTimeOffset.UtcNow);

        Assert.Equal(PropertiesDomainErrors.VersionConflict, result.Error);
        Assert.Equal(1, property.Version);
        Assert.Equal("Hostel One", property.Name.Value);
    }

    [Fact]
    public void Retire_is_terminal_and_versioned()
    {
        Property property = CreateProperty("tenant-a").Value;
        property.ClearDomainEvents();

        Result result = property.Retire(property.Version, Guid.NewGuid(), DateTimeOffset.UtcNow);

        Assert.True(result.IsSuccess);
        Assert.Equal(PropertyState.Retired, property.Status);
        Assert.Equal(2, property.Version);
        Assert.NotNull(property.RetiredAtUtc);
        PropertyRetiredDomainEvent domainEvent = Assert.IsType<PropertyRetiredDomainEvent>(Assert.Single(property.DomainEvents));
        Assert.Equal(2, domainEvent.PropertyVersion);
        Assert.Equal(PropertiesDomainErrors.PropertyRetired, property.RegisterRoom(property.Version).Error);
        Assert.Equal(
            PropertiesDomainErrors.PropertyRetired,
            property.Update("Nope", "nope", "UTC", property.Version, Guid.NewGuid(), DateTimeOffset.UtcNow).Error);
        Assert.Equal(
            PropertiesDomainErrors.PropertyAlreadyRetired,
            property.Retire(property.Version, Guid.NewGuid(), DateTimeOffset.UtcNow).Error);
    }

    [Fact]
    public void Retire_rejects_invalid_actor_without_mutating_the_property()
    {
        Property property = CreateProperty("tenant-a").Value;
        property.ClearDomainEvents();

        Result result = property.Retire(
            property.Version,
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            new string('a', Property.ActorIdMaxLength + 1));

        Assert.Equal(PropertiesDomainErrors.ActorIdInvalid, result.Error);
        Assert.Equal(PropertyState.Active, property.Status);
        Assert.Equal(1, property.Version);
        Assert.Null(property.RetiredAtUtc);
        Assert.Empty(property.DomainEvents);
    }

    [Fact]
    public void Processing_activation_supports_policies_without_acknowledgements()
    {
        Property property = CreateProperty("tenant-a").Value;
        PropertyGovernanceBinding binding = CreateGovernanceBinding();
        property.ClearDomainEvents();

        Result result = property.ActivateProcessing(
            binding,
            [],
            property.Version,
            Guid.NewGuid(),
            GovernanceNow,
            " user:owner ");

        Assert.True(result.IsSuccess);
        Assert.Equal(PropertyProcessingState.Enabled, property.ProcessingState);
        Assert.Same(binding, property.GovernanceBinding);
        Assert.Empty(property.GovernanceAcknowledgements);
        Assert.Equal(2, property.Version);
        PropertyProcessingPolicyActivatedDomainEvent domainEvent =
            Assert.IsType<PropertyProcessingPolicyActivatedDomainEvent>(Assert.Single(property.DomainEvents));
        Assert.Same(binding, domainEvent.Binding);
        Assert.Empty(domainEvent.Acknowledgements);
        Assert.Equal("user:owner", domainEvent.ActorId);
        Assert.Equal(property.Version, domainEvent.PropertyVersion);
    }

    [Fact]
    public void Processing_suspension_retains_the_complete_binding_for_projectors()
    {
        Property property = CreateProperty("tenant-a").Value;
        PropertyGovernanceBinding binding = CreateGovernanceBinding();
        PropertyGovernanceAcknowledgement acknowledgement =
            PropertyGovernanceAcknowledgement.Create("operator-notice", 1).Value;
        Assert.True(property.ActivateProcessing(
            binding,
            [acknowledgement],
            property.Version,
            Guid.NewGuid(),
            GovernanceNow,
            "user:owner").IsSuccess);
        property.ClearDomainEvents();

        Result result = property.SuspendProcessing(
            property.Version,
            Guid.NewGuid(),
            GovernanceNow.AddMinutes(1),
            "user:owner");

        Assert.True(result.IsSuccess);
        Assert.Equal(PropertyProcessingState.Suspended, property.ProcessingState);
        Assert.Same(binding, property.GovernanceBinding);
        PropertyProcessingSuspendedDomainEvent domainEvent =
            Assert.IsType<PropertyProcessingSuspendedDomainEvent>(Assert.Single(property.DomainEvents));
        Assert.Same(binding, domainEvent.Binding);
        Assert.Equal(acknowledgement, Assert.Single(domainEvent.Acknowledgements));
        Assert.Equal(property.Version, domainEvent.PropertyVersion);
    }

    [Fact]
    public void Processing_lifecycle_rejects_stale_versions_and_retired_properties_without_mutation()
    {
        Property property = CreateProperty("tenant-a").Value;
        PropertyGovernanceBinding binding = CreateGovernanceBinding();
        property.ClearDomainEvents();

        Result stale = property.ActivateProcessing(
            binding,
            [],
            expectedVersion: 99,
            Guid.NewGuid(),
            GovernanceNow,
            "user:owner");

        Assert.Equal(PropertiesDomainErrors.VersionConflict, stale.Error);
        Assert.Equal(PropertyProcessingState.Unconfigured, property.ProcessingState);
        Assert.Null(property.GovernanceBinding);
        Assert.Empty(property.DomainEvents);

        Assert.True(property.Retire(property.Version, Guid.NewGuid(), GovernanceNow).IsSuccess);
        property.ClearDomainEvents();
        Result retired = property.ActivateProcessing(
            binding,
            [],
            property.Version,
            Guid.NewGuid(),
            GovernanceNow.AddMinutes(1),
            "user:owner");

        Assert.Equal(PropertiesDomainErrors.PropertyRetired, retired.Error);
        Assert.Equal(PropertyProcessingState.Unconfigured, property.ProcessingState);
        Assert.Null(property.GovernanceBinding);
        Assert.Empty(property.DomainEvents);
    }

    private static readonly DateTimeOffset GovernanceNow =
        new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    private static PropertyGovernanceBinding CreateGovernanceBinding() =>
        PropertyGovernanceBinding.Create(
            "GB",
            "gb-hostel",
            1,
            "eu-west-2",
            "uk-no-transfer",
            "guest-operational",
            1,
            new string('a', Property.ContentSha256Length),
            GovernanceNow.AddDays(-1),
            GovernanceNow.AddDays(30),
            GovernanceNow).Value;

    private static Result<Property> CreateProperty(
        string tenantId,
        string name = "Hostel One",
        string code = "hostel-one",
        string timeZoneId = "UTC",
        Guid? propertyId = null,
        Guid? eventId = null) =>
        Property.Create(
            propertyId ?? Guid.NewGuid(),
            tenantId,
            name,
            code,
            timeZoneId,
            eventId ?? Guid.NewGuid(),
            DateTimeOffset.UtcNow);
}
