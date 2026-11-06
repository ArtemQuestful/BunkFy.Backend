namespace BunkFy.Modules.Properties.Tests;

using System.Text.Json;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Aggregates;
using Gma.Framework.Naming;
using Xunit;

[Trait("Category", "Unit")]
public sealed class PropertiesIntegrationEventContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Guid EventId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid PropertyId = Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff");
    private static readonly Guid RoomId = Guid.Parse("cccccccc-dddd-eeee-ffff-aaaaaaaaaaaa");
    private static readonly Guid BedId = Guid.Parse("dddddddd-eeee-ffff-aaaa-bbbbbbbbbbbb");
    private static readonly DateTimeOffset OccurredAtUtc = new(2026, 7, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Contract_limits_match_domain_limits()
    {
        Assert.Equal(Property.ActorIdMaxLength, PropertiesContractLimits.ActorIdMaxLength);
        Assert.Equal(Property.PropertyNameMaxLength, PropertiesContractLimits.PropertyNameMaxLength);
        Assert.Equal(Property.PropertyCodeMaxLength, PropertiesContractLimits.PropertyCodeMaxLength);
        Assert.Equal(Property.TimeZoneIdMaxLength, PropertiesContractLimits.TimeZoneIdMaxLength);
        Assert.Equal(Room.RoomNameMaxLength, PropertiesContractLimits.RoomNameMaxLength);
        Assert.Equal(Room.PhysicalLabelMaxLength, PropertiesContractLimits.PhysicalLabelMaxLength);
        Assert.Equal(Room.BedLabelMaxLength, PropertiesContractLimits.BedLabelMaxLength);
    }

    [Fact]
    public void Property_retired_event_bounds_actor_at_the_contract_edge()
    {
        PropertyRetiredIntegrationEvent integrationEvent = new(
            EventId,
            "tenant-a",
            OccurredAtUtc,
            PropertyId,
            2,
            $"  {new string('a', PropertiesContractLimits.ActorIdMaxLength)}  ");

        Assert.Equal(PropertiesContractLimits.ActorIdMaxLength, integrationEvent.ActorId!.Length);
        Assert.Throws<ArgumentException>(() => new PropertyRetiredIntegrationEvent(
            EventId,
            "tenant-a",
            OccurredAtUtc,
            PropertyId,
            2,
            new string('a', PropertiesContractLimits.ActorIdMaxLength + 1)));
        Assert.Throws<ArgumentException>(() => new PropertyRetiredIntegrationEvent(
            EventId,
            "tenant-a",
            OccurredAtUtc,
            PropertyId,
            2,
            "user:bad\0actor"));
    }

    [Fact]
    public void Properties_subjects_support_default_and_configured_application_namespaces()
    {
        Assert.Equal("gma.properties.property-created.v2", PropertiesIntegrationSubjects.PropertyCreated);
        Assert.Equal("bunkfy.properties.property-updated.v2", PropertiesIntegrationSubjects.CreatePropertyUpdated("bunkfy"));
        Assert.Equal(
            "bunkfy.properties.property-time-zone-changed.v1",
            PropertiesIntegrationSubjects.CreatePropertyTimeZoneChanged(
                "bunkfy"));
        Assert.Equal("bunkfy.properties.property-retired.v1", PropertiesIntegrationSubjects.CreatePropertyRetired("bunkfy"));
        Assert.Equal("bunkfy.properties.room-created.v2", PropertiesIntegrationSubjects.CreateRoomCreated("bunkfy"));
        Assert.Equal("bunkfy.properties.room-retired.v2", PropertiesIntegrationSubjects.CreateRoomRetired("bunkfy"));
        Assert.Equal("bunkfy.properties.bed-added.v2", PropertiesIntegrationSubjects.CreateBedAdded("bunkfy"));
        Assert.Equal("bunkfy.properties.bed-retired.v2", PropertiesIntegrationSubjects.CreateBedRetired("bunkfy"));
        Assert.Equal(
            "bunkfy.properties.property-processing-policy-activated.v1",
            PropertiesIntegrationSubjects.CreatePropertyProcessingPolicyActivated("bunkfy"));
        Assert.Equal(
            "bunkfy.properties.property-processing-suspended.v1",
            PropertiesIntegrationSubjects.CreatePropertyProcessingSuspended("bunkfy"));
    }

    [Fact]
    public void Processing_events_are_self_contained_even_without_acknowledgements()
    {
        PropertyGovernancePolicyBinding binding = new(
            "GB",
            "gb-hostel",
            1,
            "eu-west-2",
            "uk-no-transfer",
            "guest-operational",
            1,
            new string('a', PropertiesContractLimits.ContentSha256Length),
            OccurredAtUtc.AddDays(-1),
            OccurredAtUtc.AddDays(30),
            OccurredAtUtc,
            []);

        PropertyProcessingPolicyActivatedIntegrationEvent activated = new(
            EventId,
            "tenant-a",
            OccurredAtUtc,
            PropertyId,
            binding,
            2);
        PropertyProcessingSuspendedIntegrationEvent suspended = new(
            EventId,
            "tenant-a",
            OccurredAtUtc.AddMinutes(1),
            PropertyId,
            binding,
            3);

        Assert.Same(binding, activated.Binding);
        Assert.Empty(activated.Binding.Acknowledgements);
        Assert.Same(binding, suspended.Binding);
        Assert.Empty(suspended.Binding.Acknowledgements);
    }

    [Fact]
    public void Created_events_normalize_metadata_and_snapshots()
    {
        PropertyCreatedIntegrationEvent propertyEvent = CreatePropertyCreatedEvent(
            tenantId: " tenant-a ",
            name: " Property name ",
            code: " prop-1 ",
            timeZoneId: " Europe/Moscow ");
        RoomCreatedIntegrationEvent roomEvent = CreateRoomCreatedEvent(
            name: " Room 101 ",
            buildingLabel: " Main ",
            floorLabel: " 01 ");
        BedAddedIntegrationEvent bedEvent = CreateBedAddedEvent(label: " Bed A ");

        Assert.Equal("tenant-a", propertyEvent.TenantId);
        Assert.Equal("Property name", propertyEvent.Name);
        Assert.Equal("prop-1", propertyEvent.Code);
        Assert.Equal("Europe/Moscow", propertyEvent.TimeZoneId);
        Assert.Equal(1, propertyEvent.PropertyVersion);
        Assert.Equal("Room 101", roomEvent.Name);
        Assert.Equal("Main", roomEvent.BuildingLabel);
        Assert.Equal("01", roomEvent.FloorLabel);
        Assert.Equal("Bed A", bedEvent.Label);
        Assert.Equal(1, bedEvent.BedVersion);
    }

    [Fact]
    public void Property_event_round_trips_through_web_json_with_stable_status_names()
    {
        PropertyUpdatedIntegrationEvent integrationEvent = CreatePropertyUpdatedEvent();

        string json = JsonSerializer.Serialize(integrationEvent, JsonOptions);
        PropertyUpdatedIntegrationEvent? deserialized =
            JsonSerializer.Deserialize<PropertyUpdatedIntegrationEvent>(json, JsonOptions);

        Assert.Contains("\"status\":\"active\"", json, StringComparison.Ordinal);
        Assert.NotNull(deserialized);
        Assert.Equal(integrationEvent, deserialized);
        Assert.Equal(PropertyStatus.Active, JsonSerializer.Deserialize<PropertyStatus>("\"Active\"", JsonOptions));
    }

    [Fact]
    public void Time_zone_changed_event_round_trips_complete_catalog_provenance()
    {
        PropertyTimeZoneChangedIntegrationEvent integrationEvent = new(
            EventId,
            "tenant-a",
            OccurredAtUtc,
            PropertyId,
            "UTC",
            "Etc/UTC",
            "Etc/UTC",
            PropertyTimeZoneChangeKind.Canonicalized,
            "TZDB: 2026c (mapping: $Revision$)",
            2);

        string json = JsonSerializer.Serialize(integrationEvent, JsonOptions);
        PropertyTimeZoneChangedIntegrationEvent? deserialized =
            JsonSerializer.Deserialize<
                PropertyTimeZoneChangedIntegrationEvent>(
                    json,
                    JsonOptions);

        Assert.Contains(
            "\"changeKind\":\"canonicalized\"",
            json,
            StringComparison.Ordinal);
        Assert.Equal(integrationEvent, deserialized);
        Assert.Throws<ArgumentOutOfRangeException>(() => new
            PropertyTimeZoneChangedIntegrationEvent(
                EventId,
                "tenant-a",
                OccurredAtUtc,
                PropertyId,
                "Etc/UTC",
                "Etc/UTC",
                "Etc/UTC",
                PropertyTimeZoneChangeKind.Unchanged,
                "TZDB: 2026c",
                2));
        Assert.Throws<ArgumentException>(() => new
            PropertyTimeZoneChangedIntegrationEvent(
                EventId,
                "tenant-a",
                OccurredAtUtc,
                PropertyId,
                "Etc/UTC",
                "Etc/UTC",
                "Etc/UTC",
                PropertyTimeZoneChangeKind.Canonicalized,
                "TZDB: 2026c",
                2));
        Assert.Throws<ArgumentException>(() => new
            PropertyTimeZoneChangedIntegrationEvent(
                EventId,
                "tenant-a",
                OccurredAtUtc,
                PropertyId,
                "UTC",
                null,
                "Etc/UTC",
                PropertyTimeZoneChangeKind.Canonicalized,
                "TZDB: 2026c",
                2));
        Assert.Throws<ArgumentException>(() => new
            PropertyTimeZoneChangedIntegrationEvent(
                EventId,
                "tenant-a",
                OccurredAtUtc,
                PropertyId,
                "UTC",
                "Etc/UTC",
                "Etc/UTC",
                PropertyTimeZoneChangeKind.Changed,
                "TZDB: 2026c",
                2));
    }

    [Theory]
    [InlineData(RoomStatus.Active, "active")]
    [InlineData(RoomStatus.Retired, "retired")]
    public void Room_status_names_use_stable_wire_names(RoomStatus status, string expected)
        => Assert.Equal(expected, RoomStatusNames.ToWireName(status));

    [Theory]
    [InlineData(BedStatus.Active, "active")]
    [InlineData(BedStatus.Retired, "retired")]
    public void Bed_status_names_use_stable_wire_names(BedStatus status, string expected)
        => Assert.Equal(expected, BedStatusNames.ToWireName(status));

    [Theory]
    [InlineData(PropertyTimeZoneChangeKind.Created, "created")]
    [InlineData(PropertyTimeZoneChangeKind.Unchanged, "unchanged")]
    [InlineData(PropertyTimeZoneChangeKind.Canonicalized, "canonicalized")]
    [InlineData(PropertyTimeZoneChangeKind.Changed, "changed")]
    public void Time_zone_change_kind_uses_strict_stable_wire_names(
        PropertyTimeZoneChangeKind kind,
        string expected)
    {
        Assert.Equal(
            $"\"{expected}\"",
            JsonSerializer.Serialize(kind, JsonOptions));
        Assert.Equal(
            kind,
            JsonSerializer.Deserialize<PropertyTimeZoneChangeKind>(
                $"\"{expected}\"",
                JsonOptions));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("\"Created\"")]
    [InlineData("\"unknown\"")]
    public void Time_zone_change_kind_rejects_numeric_or_unknown_values(
        string json) =>
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<PropertyTimeZoneChangeKind>(
                json,
                JsonOptions));

    [Theory]
    [InlineData("1")]
    [InlineData("\"unknown\"")]
    [InlineData("\"archived\"")]
    public void Status_json_rejects_numeric_or_unknown_values(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PropertyStatus>(json, JsonOptions));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RoomStatus>(json, JsonOptions));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<BedStatus>(json, JsonOptions));
    }

    [Fact]
    public void Status_json_rejects_unknown_writes()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(PropertyStatus.Unknown, JsonOptions));
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize((RoomStatus)999, JsonOptions));
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize((BedStatus)999, JsonOptions));
    }

    [Fact]
    public void Properties_events_reject_invalid_metadata_and_snapshots()
    {
        Assert.Throws<ArgumentException>(() => CreatePropertyCreatedEvent(eventId: Guid.Empty));
        Assert.Throws<ArgumentException>(() => CreatePropertyCreatedEvent(tenantId: " "));
        Assert.Throws<ArgumentException>(() => CreatePropertyCreatedEvent(tenantId: new string('x', TenantIds.MaxLength + 1)));
        Assert.Throws<ArgumentException>(() => CreatePropertyCreatedEvent(occurredAtUtc: default(DateTimeOffset)));
        Assert.Throws<ArgumentException>(() => CreatePropertyCreatedEvent(propertyId: Guid.Empty));
        Assert.Throws<ArgumentException>(() => CreatePropertyCreatedEvent(name: " "));
        Assert.Throws<ArgumentException>(() => CreatePropertyCreatedEvent(name: new string('x', PropertiesContractLimits.PropertyNameMaxLength + 1)));
        Assert.Throws<ArgumentException>(() => CreatePropertyCreatedEvent(code: " "));
        Assert.Throws<ArgumentException>(() => CreatePropertyCreatedEvent(timeZoneId: " "));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreatePropertyCreatedEvent(status: PropertyStatus.Unknown));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreatePropertyCreatedEvent(propertyVersion: 0));
        Assert.Throws<ArgumentException>(() => CreateRoomCreatedEvent(roomId: Guid.Empty));
        Assert.Throws<ArgumentException>(() => CreateRoomCreatedEvent(name: new string('x', PropertiesContractLimits.RoomNameMaxLength + 1)));
        Assert.Throws<ArgumentException>(() => CreateRoomCreatedEvent(buildingLabel: new string('x', PropertiesContractLimits.PhysicalLabelMaxLength + 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateRoomCreatedEvent(status: RoomStatus.Unknown));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateRoomCreatedEvent(roomVersion: 0));
        Assert.Throws<ArgumentException>(() => CreateBedAddedEvent(bedId: Guid.Empty));
        Assert.Throws<ArgumentException>(() => CreateBedAddedEvent(label: new string('x', PropertiesContractLimits.BedLabelMaxLength + 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateBedAddedEvent(status: BedStatus.Unknown));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateBedAddedEvent(bedVersion: 0));
    }

    [Fact]
    public void Projection_export_normalizes_snapshot_data_and_maps_undefined_statuses_to_unknown()
    {
        PropertyTopologyProjectionExport export = new(
            " tenant-a ",
            PropertyId,
            " Property name ",
            " prop-1 ",
            " Europe/Moscow ",
            (PropertyStatus)999,
            7,
            [
                new RoomTopologyProjectionExport(
                    PropertyId,
                    RoomId,
                    " Room 101 ",
                    " Main ",
                    " 01 ",
                    (RoomStatus)999,
                    8,
                    [
                        new BedTopologyProjectionExport(PropertyId, RoomId, BedId, " Bed A ", (BedStatus)999, 9)
                    ])
            ]);

        RoomTopologyProjectionExport room = Assert.Single(export.Rooms);
        BedTopologyProjectionExport bed = Assert.Single(room.Beds);

        Assert.Equal("tenant-a", export.TenantId);
        Assert.Equal("Property name", export.Name);
        Assert.Equal("prop-1", export.Code);
        Assert.Equal("Europe/Moscow", export.TimeZoneId);
        Assert.Equal(PropertyStatus.Unknown, export.Status);
        Assert.Equal(7, export.Version);
        Assert.Equal("Room 101", room.Name);
        Assert.Equal("Main", room.BuildingLabel);
        Assert.Equal("01", room.FloorLabel);
        Assert.Equal(RoomStatus.Unknown, room.Status);
        Assert.Equal(8, room.Version);
        Assert.Equal("Bed A", bed.Label);
        Assert.Equal(BedStatus.Unknown, bed.Status);
        Assert.Equal(9, bed.Version);
    }

    [Fact]
    public void Projection_export_rejects_invalid_snapshot_data()
    {
        Assert.Throws<ArgumentException>(() => CreateProjectionExport(tenantId: " "));
        Assert.Throws<ArgumentException>(() => CreateProjectionExport(tenantId: new string('x', TenantIds.MaxLength + 1)));
        Assert.Throws<ArgumentException>(() => CreateProjectionExport(propertyId: Guid.Empty));
        Assert.Throws<ArgumentException>(() => CreateProjectionExport(name: " "));
        Assert.Throws<ArgumentException>(() => CreateProjectionExport(code: " "));
        Assert.Throws<ArgumentException>(() => CreateProjectionExport(timeZoneId: " "));
        Assert.Throws<ArgumentException>(() => new RoomTopologyProjectionExport(PropertyId, Guid.Empty, "Room 101", null, null, RoomStatus.Active, 1));
        Assert.Throws<ArgumentException>(() => new BedTopologyProjectionExport(PropertyId, RoomId, Guid.Empty, "Bed A", BedStatus.Active, 1));
        Assert.Throws<ArgumentException>(() => new BedTopologyProjectionExport(PropertyId, RoomId, BedId, " ", BedStatus.Active, 1));
    }

    private static PropertyCreatedIntegrationEvent CreatePropertyCreatedEvent(
        Guid? eventId = null,
        string tenantId = "tenant-a",
        DateTimeOffset? occurredAtUtc = null,
        Guid? propertyId = null,
        string name = "Property name",
        string code = "PROP-1",
        string timeZoneId = "Europe/Moscow",
        PropertyStatus status = PropertyStatus.Active,
        long propertyVersion = 1) =>
        new(
            eventId ?? EventId,
            tenantId,
            occurredAtUtc ?? OccurredAtUtc,
            propertyId ?? PropertyId,
            name,
            code,
            timeZoneId,
            status,
            propertyVersion);

    private static PropertyUpdatedIntegrationEvent CreatePropertyUpdatedEvent() =>
        new(EventId, "tenant-a", OccurredAtUtc, PropertyId, "Property name", "PROP-1", "Europe/Moscow", PropertyStatus.Active, 2);

    private static RoomCreatedIntegrationEvent CreateRoomCreatedEvent(
        Guid? roomId = null,
        string name = "Room 101",
        string? buildingLabel = "Main",
        string? floorLabel = "01",
        RoomStatus status = RoomStatus.Active,
        long roomVersion = 1) =>
        new(EventId, "tenant-a", OccurredAtUtc, PropertyId, roomId ?? RoomId, name, buildingLabel, floorLabel, status, roomVersion);

    private static BedAddedIntegrationEvent CreateBedAddedEvent(
        Guid? bedId = null,
        string label = "Bed A",
        BedStatus status = BedStatus.Active,
        long roomVersion = 2,
        long bedVersion = 1) =>
        new(EventId, "tenant-a", OccurredAtUtc, PropertyId, RoomId, bedId ?? BedId, label, status, roomVersion, bedVersion);

    private static PropertyTopologyProjectionExport CreateProjectionExport(
        string tenantId = "tenant-a",
        Guid? propertyId = null,
        string name = "Property name",
        string code = "PROP-1",
        string timeZoneId = "Europe/Moscow",
        PropertyStatus status = PropertyStatus.Active) =>
        new(tenantId, propertyId ?? PropertyId, name, code, timeZoneId, status, 1);
}
