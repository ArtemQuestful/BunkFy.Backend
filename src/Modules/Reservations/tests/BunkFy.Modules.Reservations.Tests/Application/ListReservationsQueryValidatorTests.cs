namespace BunkFy.Modules.Reservations.Tests.Application;

using BunkFy.Modules.Reservations.Application.Queries;
using BunkFy.Modules.Reservations.Application.Validation;
using BunkFy.Modules.Reservations.Contracts;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ListReservationsQueryValidatorTests
{
    private readonly ListReservationsQueryValidator validator = new();

    [Fact]
    public void Accepts_a_complete_operational_view()
    {
        ListReservationsQuery query = Query(
            ReservationListView.Attention,
            new DateOnly(2026, 8, 10));

        Assert.Empty(this.validator.Validate(query));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Rejects_an_incomplete_operational_view(
        bool includeView,
        bool includeOperatingDate)
    {
        ListReservationsQuery query = Query(
            includeView ? ReservationListView.Attention : null,
            includeOperatingDate ? new DateOnly(2026, 8, 10) : null);

        Assert.Contains(
            "View and OperatingDate must be supplied together.",
            this.validator.Validate(query));
    }

    [Fact]
    public void Rejects_combining_an_operational_view_with_statuses()
    {
        ListReservationsQuery query = Query(
            ReservationListView.Attention,
            new DateOnly(2026, 8, 10)) with
        {
            Statuses = [ReservationStatus.Confirmed]
        };

        Assert.Contains(
            "View cannot be combined with explicit reservation statuses.",
            this.validator.Validate(query));
    }

    private static ListReservationsQuery Query(
        ReservationListView? view,
        DateOnly? operatingDate) => new(
        Guid.NewGuid(),
        Statuses: null,
        Search: null,
        ReservationListOrder.ArrivalAscending,
        Page: 1,
        PageSize: 30,
        View: view,
        OperatingDate: operatingDate);
}
