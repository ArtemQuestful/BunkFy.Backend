namespace BunkFy.Modules.Reservations.Tests.Contracts;

using System.Text.Json.Serialization;
using BunkFy.Modules.Reservations.Contracts;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ReservationOperationalSurfaceContractTests
{
    [Fact]
    public void Mutation_receipt_contains_only_identity_state_and_concurrency_fields()
    {
        AssertProperties<ReservationMutationReceiptDto>(
            "DetailsRevision",
            "PropertyId",
            "ReservationId",
            "Status",
            "Version");
    }

    [Fact]
    public void Directory_item_contains_only_operational_summary_fields()
    {
        AssertProperties<ReservationListItemDto>(
            "Arrival",
            "Departure",
            "ExpectedArrivalTime",
            "ExpectedDepartureTime",
            "GuestCount",
            "HoldsInventory",
            "InventoryUnitCount",
            "InventoryUnitIds",
            "PrimaryGuestName",
            "PropertyId",
            "ReservationId",
            "SourceKind",
            "Status");
    }

    [Fact]
    public void Detail_contract_exposes_server_owned_inventory_hold_truth()
    {
        AssertRequiredBoolean<ReservationDto>(nameof(ReservationDto.HoldsInventory));
        AssertRequiredBoolean<ReservationListItemDto>(
            nameof(ReservationListItemDto.HoldsInventory));
    }

    [Fact]
    public void List_and_history_envelopes_publish_lookahead_pagination()
    {
        AssertProperties<ReservationListResponse>("HasMore", "Page", "PageSize", "Reservations");
        AssertProperties<ReservationDetailsHistoryListResponse>("HasMore", "Items", "Page", "PageSize");
    }

    [Fact]
    public void Operations_snapshot_contract_is_explicit_bounded_and_property_local()
    {
        AssertProperties<ReservationOperationsCountDto>("GuestCount", "ReservationCount");
        AssertProperties<ReservationOperationsCohortCountsDto>(
            "ConfirmedArrivalsOnLocalDate",
            "CurrentlyInHouse",
            "ScheduledDeparturesOnLocalDate");
        AssertProperties<ReservationOperationsAttentionCountsDto>(
            "AllocationRejected",
            "ArrivalBeforeLocalDateStillConfirmed",
            "CancellationPending",
            "CheckoutPending",
            "DepartureBeforeLocalDateStillInHouse",
            "NoShowPending",
            "PendingAllocation",
            "Total");
        AssertProperties<ReservationOperationsSnapshotDto>(
            "Attention",
            "Cohorts",
            "DateSource",
            "HasMoreUpcoming",
            "LocalDate",
            "ObservedAtUtc",
            "PropertyId",
            "TimeZoneId",
            "Upcoming",
            "UpcomingLimit");

        Assert.Equal(25, ReservationsContractLimits.DefaultOperationsSnapshotUpcomingLimit);
        Assert.Equal(50, ReservationsContractLimits.MaximumOperationsSnapshotUpcomingLimit);
        Assert.Equal(42, ReservationsContractLimits.MaximumScheduleWindowDays);
        Assert.False(
            typeof(ReservationOperationsSnapshotDto)
                .GetProperty(nameof(ReservationOperationsSnapshotDto.TimeZoneId))!
                .PropertyType.IsGenericType);
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

    private static void AssertRequiredBoolean<T>(string propertyName)
    {
        System.Reflection.PropertyInfo property = typeof(T).GetProperty(propertyName)!;

        Assert.Equal(typeof(bool), property.PropertyType);
        Assert.NotNull(
            property.GetCustomAttributes(typeof(JsonRequiredAttribute), inherit: false)
                .SingleOrDefault());
    }
}
