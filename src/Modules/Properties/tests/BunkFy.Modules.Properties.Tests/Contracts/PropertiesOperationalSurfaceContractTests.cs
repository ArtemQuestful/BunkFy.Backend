namespace BunkFy.Modules.Properties.Tests.Contracts;

using BunkFy.Modules.Properties.Contracts;
using Xunit;

[Trait("Category", "Unit")]
public sealed class PropertiesOperationalSurfaceContractTests
{
    [Fact]
    public void Directory_contracts_are_minimal_and_publish_continuation_metadata()
    {
        AssertProperties<PropertyListItemDto>(
            "CanonicalTimeZoneId", "Code", "Name", "ProcessingStatus", "PropertyId", "Status",
            "TimeZoneCatalogVersion", "TimeZoneCorrectionAllowed", "TimeZoneId", "TimeZoneObservedAtUtc",
            "TimeZoneStatus", "Version");
        AssertProperties<PropertyListResponse>("HasMore", "Page", "PageSize", "Properties");
        AssertProperties<RoomListItemDto>(
            "BuildingLabel", "FloorLabel", "Name", "PropertyId", "RoomId", "Status", "Version");
        AssertProperties<RoomListResponse>("HasMore", "Page", "PageSize", "Rooms");
        AssertProperties<BedListItemDto>(
            "BedId", "Label", "PropertyId", "RoomId", "RoomVersion", "Status", "Version");
        AssertProperties<BedListResponse>("Beds", "HasMore", "Page", "PageSize");
    }

    [Fact]
    public void Time_zone_runtime_evidence_contracts_publish_observation_timestamps()
    {
        AssertProperties<PropertyTimeZoneCatalogPageDto>(
            "CatalogVersion", "HasMore", "NextCursor", "ObservedAtUtc", "TimeZones");
        AssertProperties<PropertyTimeZoneCompliancePageDto>(
            "CatalogVersion", "HasMore", "NextCursor", "ObservedAtUtc", "Properties");
        AssertProperties<PropertyTimeZoneRecoveryDto>(
            "CurrentCanonicalTimeZoneId", "CurrentTimeZoneId", "CurrentTimeZoneObservedAtUtc",
            "CurrentTimeZoneStatus", "CurrentVersion", "Receipt");
    }

    [Fact]
    public void Ordinary_mutation_receipts_remain_minimal()
    {
        AssertProperties<PropertyMutationReceiptDto>(
            "ProcessingStatus", "PropertyId", "Status", "Version");
        AssertProperties<RoomMutationReceiptDto>("PropertyId", "RoomId", "Status", "Version");
        AssertProperties<BedMutationReceiptDto>(
            "BedId", "PropertyId", "RoomId", "RoomVersion", "Status", "Version");
        AssertProperties<BedBatchMutationReceiptDto>(
            "AffectedBedCount", "PropertyId", "RoomId", "RoomVersion");
    }

    private static void AssertProperties<T>(params string[] expected)
    {
        string[] actual = typeof(T)
            .GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
    }
}
