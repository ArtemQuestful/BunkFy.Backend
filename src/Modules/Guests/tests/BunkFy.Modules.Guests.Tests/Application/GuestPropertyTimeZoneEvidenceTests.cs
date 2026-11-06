namespace BunkFy.Modules.Guests.Tests.Application;

using BunkFy.Modules.Guests.Application.Handlers;
using BunkFy.Modules.Guests.Application.Policies;
using BunkFy.Modules.Guests.Application.Ports;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.TimeZones;
using Xunit;

[Trait("Category", "Unit")]
public sealed class GuestPropertyTimeZoneEvidenceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Generic_topology_is_classified_by_the_embedded_catalog()
    {
        var cases = new[]
        {
            new
            {
                TimeZoneId = "Etc/UTC",
                Canonical = (string?)"Etc/UTC",
                Status = PropertyTimeZoneStatus.Canonical,
                Catalog = (string?)TimeZoneCatalog.Default.CatalogVersion
            },
            new
            {
                TimeZoneId = "UTC",
                Canonical = (string?)"Etc/UTC",
                Status = PropertyTimeZoneStatus.Alias,
                Catalog = (string?)TimeZoneCatalog.Default.CatalogVersion
            },
            new
            {
                TimeZoneId = "Pacific Standard Time",
                Canonical = (string?)null,
                Status = PropertyTimeZoneStatus.Legacy,
                Catalog = (string?)null
            },
            new
            {
                TimeZoneId = "Missing/Zone",
                Canonical = (string?)null,
                Status = PropertyTimeZoneStatus.Unrecognized,
                Catalog = (string?)null
            }
        };

        foreach (var item in cases)
        {
            var repository = new RecordingPropertyRepository();
            var handler = new GuestPropertyCreatedHandler(repository);
            Guid propertyId = Guid.NewGuid();

            await handler.HandleAsync(
                new(
                    Guid.NewGuid(),
                    "tenant-a",
                    Now,
                    propertyId,
                    "Test Property",
                    "TEST",
                    item.TimeZoneId,
                    PropertyStatus.Active,
                    propertyVersion: 3),
                CancellationToken.None);

            GuestPropertyTimeZoneWriteModel evidence =
                Assert.Single(repository.TimeZones);
            Assert.Equal(item.TimeZoneId, evidence.TimeZoneId);
            Assert.Equal(item.Canonical, evidence.CanonicalTimeZoneId);
            Assert.Equal(item.Status, evidence.TimeZoneStatus);
            Assert.Equal(item.Catalog, evidence.TimeZoneCatalogVersion);
            Assert.Equal(
                GuestPropertyTimeZoneEvidenceSource.Generic,
                evidence.TimeZoneEvidenceSource);
            Assert.Equal(3, evidence.TimeZoneEvidenceSourceVersion);
            Assert.Equal(
                propertyId,
                Assert.Single(repository.Topologies).PropertyId);
        }
    }

    [Fact]
    public async Task Dedicated_event_carries_producer_catalog_evidence()
    {
        var repository = new RecordingPropertyRepository();
        var handler =
            new GuestPropertyTimeZoneChangedHandler(repository);

        await handler.HandleAsync(
            new(
                Guid.NewGuid(),
                "tenant-a",
                Now,
                Guid.NewGuid(),
                previousTimeZoneId: "UTC",
                previousCanonicalTimeZoneId: "Etc/UTC",
                timeZoneId: "Etc/UTC",
                PropertyTimeZoneChangeKind.Canonicalized,
                TimeZoneCatalog.Default.CatalogVersion,
                propertyVersion: 4),
            CancellationToken.None);

        GuestPropertyTimeZoneWriteModel evidence =
            Assert.Single(repository.TimeZones);
        Assert.Equal(PropertyTimeZoneStatus.Canonical, evidence.TimeZoneStatus);
        Assert.Equal("Etc/UTC", evidence.TimeZoneId);
        Assert.Equal("Etc/UTC", evidence.CanonicalTimeZoneId);
        Assert.Equal(
            TimeZoneCatalog.Default.CatalogVersion,
            evidence.TimeZoneCatalogVersion);
        Assert.Equal(
            GuestPropertyTimeZoneEvidenceSource.Dedicated,
            evidence.TimeZoneEvidenceSource);
        Assert.Equal(4, evidence.TimeZoneEvidenceSourceVersion);
        Assert.Empty(repository.Topologies);
    }

    [Fact]
    public void Rebuild_uses_the_same_embedded_catalog_classification()
    {
        Guid propertyId = Guid.NewGuid();

        GuestPropertyTimeZoneWriteModel evidence =
            GuestPropertyTimeZoneEvidenceClassifier.Classify(
                "tenant-a",
                propertyId,
                "UTC",
                GuestPropertyTimeZoneEvidenceSource.Rebuild,
                sourceVersion: 7);

        Assert.Equal("UTC", evidence.TimeZoneId);
        Assert.Equal("Etc/UTC", evidence.CanonicalTimeZoneId);
        Assert.Equal(PropertyTimeZoneStatus.Alias, evidence.TimeZoneStatus);
        Assert.Equal(
            TimeZoneCatalog.Default.CatalogVersion,
            evidence.TimeZoneCatalogVersion);
        Assert.Equal(
            GuestPropertyTimeZoneEvidenceSource.Rebuild,
            evidence.TimeZoneEvidenceSource);
        Assert.Equal(7, evidence.TimeZoneEvidenceSourceVersion);
    }

    [Fact]
    public void Dedicated_evidence_must_not_use_the_consumer_classifier()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            GuestPropertyTimeZoneEvidenceClassifier.Classify(
                "tenant-a",
                Guid.NewGuid(),
                "Etc/UTC",
                GuestPropertyTimeZoneEvidenceSource.Dedicated,
                sourceVersion: 7));
    }

    private sealed class RecordingPropertyRepository
        : IGuestPropertyProjectionRepository
    {
        public List<GuestPropertyTopologyWriteModel> Topologies { get; } = [];
        public List<GuestPropertyTimeZoneWriteModel> TimeZones { get; } = [];

        public Task ApplyTopologyAsync(
            GuestPropertyTopologyWriteModel property,
            CancellationToken cancellationToken)
        {
            this.Topologies.Add(property);
            return Task.CompletedTask;
        }

        public Task ApplyTimeZoneAsync(
            GuestPropertyTimeZoneWriteModel property,
            CancellationToken cancellationToken)
        {
            this.TimeZones.Add(property);
            return Task.CompletedTask;
        }

        public Task ApplyPolicyAsync(
            GuestPropertyPolicyWriteModel property,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GuestPropertyPolicySnapshot?> GetPolicyAsync(
            Guid propertyId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
