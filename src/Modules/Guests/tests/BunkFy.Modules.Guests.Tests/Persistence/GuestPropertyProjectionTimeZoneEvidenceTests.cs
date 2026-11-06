namespace BunkFy.Modules.Guests.Tests.Persistence;

using BunkFy.Modules.Guests.Application.Ports;
using BunkFy.Modules.Guests.Persistence;
using BunkFy.Modules.Properties.Contracts;
using Xunit;

[Trait("Category", "Unit")]
public sealed class GuestPropertyProjectionTimeZoneEvidenceTests
{
    [Fact]
    public void Dedicated_evidence_survives_matching_topology_catch_up()
    {
        GuestPropertyProjection property = Create(version: 2);

        property.ApplyTimeZone(
            "Europe/Paris",
            "Europe/Paris",
            PropertyTimeZoneStatus.Canonical,
            "catalog-v1",
            GuestPropertyTimeZoneEvidenceSource.Dedicated,
            sourceVersion: 3);
        property.ApplyTopology(
            "Property v3",
            "Europe/Paris",
            PropertyStatus.Active,
            sourceVersion: 3);
        property.ApplyTimeZone(
            "Europe/Paris",
            "Europe/Paris",
            PropertyTimeZoneStatus.Canonical,
            "catalog-v1",
            GuestPropertyTimeZoneEvidenceSource.Generic,
            sourceVersion: 3);

        Assert.Equal("Property v3", property.Name);
        Assert.Equal("Europe/Paris", property.TimeZoneId);
        Assert.Equal(
            GuestPropertyTimeZoneEvidenceSource.Dedicated,
            property.TimeZoneEvidenceSource);
        Assert.Equal(3, property.TimeZoneEvidenceSourceVersion);
        Assert.Equal(3, property.TopologySourceVersion);
    }

    [Fact]
    public void Future_evidence_is_not_regressed_by_older_topology()
    {
        GuestPropertyProjection property = Create(version: 2);
        property.ApplyTimeZone(
            "Europe/Paris",
            "Europe/Paris",
            PropertyTimeZoneStatus.Canonical,
            "catalog-v1",
            GuestPropertyTimeZoneEvidenceSource.Dedicated,
            sourceVersion: 4);

        property.ApplyTopology(
            "Property v3",
            "Europe/Berlin",
            PropertyStatus.Active,
            sourceVersion: 3);

        Assert.Equal("Europe/Paris", property.TimeZoneId);
        Assert.Equal(4, property.TimeZoneEvidenceSourceVersion);
        Assert.Equal(3, property.TopologySourceVersion);
    }

    [Fact]
    public void Equal_version_raw_mismatch_fails_without_partial_update()
    {
        GuestPropertyProjection property = Create(version: 2);
        property.ApplyTimeZone(
            "Europe/Paris",
            "Europe/Paris",
            PropertyTimeZoneStatus.Canonical,
            "catalog-v1",
            GuestPropertyTimeZoneEvidenceSource.Dedicated,
            sourceVersion: 3);

        InvalidOperationException failure = Assert.Throws<
            InvalidOperationException>(() => property.ApplyTopology(
                "Conflicting name",
                "Europe/Berlin",
                PropertyStatus.Active,
                sourceVersion: 3));

        Assert.Equal(
            "Equal-version property time-zone evidence conflicts with the topology projection.",
            failure.Message);
        Assert.Equal("Property", property.Name);
        Assert.Equal(2, property.TopologySourceVersion);
        Assert.Equal("Europe/Paris", property.TimeZoneId);
    }

    [Fact]
    public void Dedicated_enriches_generic_at_the_same_coordinate()
    {
        GuestPropertyProjection property = Create(version: 2);
        property.ApplyTopology(
            "Property v3",
            "Europe/Paris",
            PropertyStatus.Active,
            sourceVersion: 3);
        property.ApplyTimeZone(
            "Europe/Paris",
            "Europe/Paris",
            PropertyTimeZoneStatus.Canonical,
            "catalog-v1",
            GuestPropertyTimeZoneEvidenceSource.Generic,
            sourceVersion: 3);

        property.ApplyTimeZone(
            "Europe/Paris",
            "Europe/Paris",
            PropertyTimeZoneStatus.Canonical,
            "catalog-v1",
            GuestPropertyTimeZoneEvidenceSource.Dedicated,
            sourceVersion: 3);

        Assert.Equal(
            GuestPropertyTimeZoneEvidenceSource.Dedicated,
            property.TimeZoneEvidenceSource);
    }

    [Fact]
    public void Conflicting_same_coordinate_evidence_fails_stably()
    {
        GuestPropertyProjection property = Create(version: 2);
        property.ApplyTimeZone(
            "Europe/Paris",
            "Europe/Paris",
            PropertyTimeZoneStatus.Canonical,
            "catalog-v1",
            GuestPropertyTimeZoneEvidenceSource.Dedicated,
            sourceVersion: 3);

        InvalidOperationException failure = Assert.Throws<
            InvalidOperationException>(() => property.ApplyTimeZone(
                "Europe/Berlin",
                "Europe/Berlin",
                PropertyTimeZoneStatus.Canonical,
                "catalog-v1",
                GuestPropertyTimeZoneEvidenceSource.Generic,
                sourceVersion: 3));

        Assert.Equal(
            "Equal-version property time-zone evidence conflicts with the existing projection.",
            failure.Message);
    }

    [Fact]
    public void Same_version_rebuild_can_refresh_alias_catalog_evidence()
    {
        GuestPropertyProjection property = Create(version: 2);
        property.ApplyTopology(
            "Property v3",
            "US/Eastern",
            PropertyStatus.Retired,
            sourceVersion: 3);
        property.ApplyTimeZone(
            "US/Eastern",
            "America/New_York",
            PropertyTimeZoneStatus.Alias,
            "catalog-v1",
            GuestPropertyTimeZoneEvidenceSource.Rebuild,
            sourceVersion: 3);

        property.ApplyTimeZone(
            "US/Eastern",
            "America/New_York",
            PropertyTimeZoneStatus.Alias,
            "catalog-v2",
            GuestPropertyTimeZoneEvidenceSource.Rebuild,
            sourceVersion: 3);
        property.ApplyTimeZone(
            "US/Eastern",
            "America/New_York",
            PropertyTimeZoneStatus.Alias,
            "catalog-v2",
            GuestPropertyTimeZoneEvidenceSource.Rebuild,
            sourceVersion: 3);

        Assert.Equal(PropertyTimeZoneStatus.Alias, property.TimeZoneStatus);
        Assert.Equal("America/New_York", property.CanonicalTimeZoneId);
        Assert.Equal("catalog-v2", property.TimeZoneCatalogVersion);
    }

    [Fact]
    public void Dedicated_alias_evidence_is_rejected()
    {
        GuestPropertyProjection property = Create(version: 2);

        ArgumentException failure = Assert.Throws<ArgumentException>(() =>
            property.ApplyTimeZone(
                "US/Eastern",
                "America/New_York",
                PropertyTimeZoneStatus.Alias,
                "catalog-v1",
                GuestPropertyTimeZoneEvidenceSource.Dedicated,
                sourceVersion: 3));

        Assert.Equal("timeZoneId", failure.ParamName);
    }

    private static GuestPropertyProjection Create(long version) =>
        new(
            "tenant-a",
            Guid.NewGuid(),
            "Property",
            "UTC",
            PropertyStatus.Active,
            version);
}
