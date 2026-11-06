namespace BunkFy.Modules.Reservations.Tests;

using Gma.Framework.Pagination;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Persistence;
using BunkFy.Modules.Reservations.Persistence.Repositories;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ReservationRepositoryPaginationTests
{
    [Fact]
    public async Task List_projects_summary_rows_with_stable_look_ahead_paging()
    {
        await using ReservationsDbContext dbContext = CreateDbContext();
        Guid propertyId = Guid.NewGuid();
        Reservation first = CreateReservation(propertyId, "Grace Hopper", "ada@example.test", new DateOnly(2026, 8, 1));
        Reservation second = CreateReservation(propertyId, "Ada Lovelace", null, new DateOnly(2026, 8, 3));
        Reservation excludedBySearch = CreateReservation(propertyId, "Linus Torvalds", null, new DateOnly(2026, 8, 2));
        Reservation excludedByProperty = CreateReservation(Guid.NewGuid(), "Ada Elsewhere", null, new DateOnly(2026, 8, 4));
        Assert.True(first.ConfirmAllocation(
            first.AllocationRequestId,
            Guid.NewGuid(),
            allocationVersion: 1,
            Guid.NewGuid(),
            first.CreatedAtUtc.AddMinutes(1)).IsSuccess);
        TestScopeContext scopeContext = new();
        ReservationProcessingRestrictionProjectionRepository projections =
            new(dbContext, scopeContext);
        ReservationRepository repository = new(dbContext, projections);
        foreach (Reservation reservation in new[]
                 {
                     first,
                     second,
                     excludedBySearch,
                     excludedByProperty
                 })
        {
            await repository.AddAsync(reservation, CancellationToken.None);
        }

        await dbContext.SaveChangesAsync();

        ReservationListResponse firstPage = await repository.ListAsync(
            propertyId,
            [ReservationStatus.PendingAllocation, ReservationStatus.Confirmed],
            "ADA",
            ReservationListOrder.ArrivalAscending,
            PageRequest.Normalize(page: 1, pageSize: 1),
            CancellationToken.None);
        ReservationListResponse secondPage = await repository.ListAsync(
            propertyId,
            [ReservationStatus.PendingAllocation, ReservationStatus.Confirmed],
            "ADA",
            ReservationListOrder.ArrivalAscending,
            PageRequest.Normalize(page: 2, pageSize: 1),
            CancellationToken.None);

        ReservationListItemDto firstItem = Assert.Single(firstPage.Reservations);
        Assert.Equal(first.Id, firstItem.ReservationId);
        Assert.Equal(1, firstItem.InventoryUnitCount);
        Assert.Equal(first.RequestedUnits.Select(unit => unit.InventoryUnitId), firstItem.InventoryUnitIds);
        Assert.True(firstItem.HoldsInventory);
        Assert.True(firstPage.HasMore);
        Assert.Equal(2, secondPage.Page);
        Assert.Equal(1, secondPage.PageSize);
        ReservationListItemDto secondItem = Assert.Single(secondPage.Reservations);
        Assert.Equal(second.Id, secondItem.ReservationId);
        Assert.False(secondItem.HoldsInventory);
        Assert.False(secondPage.HasMore);
    }

    [Fact]
    public async Task List_window_returns_only_reservations_overlapping_the_visible_schedule()
    {
        await using ReservationsDbContext dbContext = CreateDbContext();
        Guid propertyId = Guid.NewGuid();
        Reservation before = CreateReservation(
            propertyId,
            "Before window",
            null,
            new DateOnly(2026, 7, 28));
        Reservation spanning = CreateReservation(
            propertyId,
            "Spanning window",
            null,
            new DateOnly(2026, 7, 31));
        Reservation inside = CreateReservation(
            propertyId,
            "Inside window",
            null,
            new DateOnly(2026, 8, 4));
        Reservation after = CreateReservation(
            propertyId,
            "After window",
            null,
            new DateOnly(2026, 8, 12));
        TestScopeContext scopeContext = new();
        ReservationProcessingRestrictionProjectionRepository projections =
            new(dbContext, scopeContext);
        ReservationRepository repository = new(dbContext, projections);
        foreach (Reservation reservation in new[] { before, spanning, inside, after })
        {
            await repository.AddAsync(reservation, CancellationToken.None);
        }

        await dbContext.SaveChangesAsync();

        ReservationListResponse result = await repository.ListWindowAsync(
            propertyId,
            statuses: null,
            search: null,
            order: ReservationListOrder.ArrivalAscending,
            pageRequest: PageRequest.Normalize(page: 1, pageSize: 20),
            overlapsFrom: new DateOnly(2026, 8, 1),
            overlapsTo: new DateOnly(2026, 8, 10),
            view: null,
            operatingDate: null,
            cancellationToken: CancellationToken.None);

        Assert.Equal(
            [spanning.Id, inside.Id],
            result.Reservations.Select(item => item.ReservationId));
        Assert.False(result.HasMore);
    }

    [Fact]
    public async Task List_window_applies_operational_views_before_paging()
    {
        await using ReservationsDbContext dbContext = CreateDbContext();
        Guid propertyId = Guid.NewGuid();
        DateOnly operatingDate = new(2026, 8, 10);
        Reservation pendingFuture = CreateReservation(
            propertyId,
            "Pending future",
            null,
            operatingDate.AddDays(2));
        Reservation confirmedFuture = CreateConfirmedReservation(
            propertyId,
            "Confirmed future",
            operatingDate.AddDays(1));
        Reservation confirmedOverdue = CreateConfirmedReservation(
            propertyId,
            "Confirmed overdue",
            operatingDate.AddDays(-2));
        Reservation checkedIn = CreateCheckedInReservation(
            propertyId,
            "Checked in",
            operatingDate.AddDays(-1));
        Reservation checkedInOverdue = CreateCheckedInReservation(
            propertyId,
            "Checkout overdue",
            operatingDate.AddDays(-3));
        Reservation cancelled = CreateCancelledReservation(
            propertyId,
            "Cancelled",
            operatingDate.AddDays(4));
        TestScopeContext scopeContext = new();
        ReservationProcessingRestrictionProjectionRepository projections =
            new(dbContext, scopeContext);
        ReservationRepository repository = new(dbContext, projections);
        foreach (Reservation reservation in new[]
                 {
                     pendingFuture,
                     confirmedFuture,
                     confirmedOverdue,
                     checkedIn,
                     checkedInOverdue,
                     cancelled
                 })
        {
            await repository.AddAsync(reservation, CancellationToken.None);
        }

        await dbContext.SaveChangesAsync();

        ReservationListResponse upcoming = await ListViewAsync(
            repository,
            propertyId,
            ReservationListView.Upcoming,
            operatingDate);
        ReservationListResponse inHouse = await ListViewAsync(
            repository,
            propertyId,
            ReservationListView.InHouse,
            operatingDate);
        ReservationListResponse attention = await ListViewAsync(
            repository,
            propertyId,
            ReservationListView.Attention,
            operatingDate);
        ReservationListResponse closed = await ListViewAsync(
            repository,
            propertyId,
            ReservationListView.Closed,
            operatingDate);

        Assert.Equal(
            [confirmedFuture.Id, pendingFuture.Id],
            upcoming.Reservations.Select(item => item.ReservationId));
        Assert.Equal(
            [checkedInOverdue.Id, checkedIn.Id],
            inHouse.Reservations.Select(item => item.ReservationId));
        Assert.Equal(
            [checkedInOverdue.Id, confirmedOverdue.Id, pendingFuture.Id],
            attention.Reservations.Select(item => item.ReservationId));
        Assert.Equal(
            [cancelled.Id],
            closed.Reservations.Select(item => item.ReservationId));
    }

    private static Task<ReservationListResponse> ListViewAsync(
        ReservationRepository repository,
        Guid propertyId,
        ReservationListView view,
        DateOnly operatingDate) =>
        repository.ListWindowAsync(
            propertyId,
            statuses: null,
            search: null,
            order: view == ReservationListView.Closed
                ? ReservationListOrder.DepartureDescending
                : ReservationListOrder.ArrivalAscending,
            pageRequest: PageRequest.Normalize(page: 1, pageSize: 20),
            overlapsFrom: null,
            overlapsTo: null,
            view,
            operatingDate,
            cancellationToken: CancellationToken.None);

    private static Reservation CreateConfirmedReservation(
        Guid propertyId,
        string guestName,
        DateOnly arrival)
    {
        Reservation reservation = CreateReservation(propertyId, guestName, null, arrival);
        Assert.True(reservation.ConfirmAllocation(
            reservation.AllocationRequestId,
            Guid.NewGuid(),
            allocationVersion: 1,
            Guid.NewGuid(),
            reservation.CreatedAtUtc.AddMinutes(1)).IsSuccess);
        return reservation;
    }

    private static Reservation CreateCheckedInReservation(
        Guid propertyId,
        string guestName,
        DateOnly arrival)
    {
        Reservation reservation = CreateConfirmedReservation(propertyId, guestName, arrival);
        Assert.True(reservation.CheckIn(
            reservation.Version,
            arrival,
            "operator-a",
            Guid.NewGuid(),
            reservation.CreatedAtUtc.AddMinutes(2)).IsSuccess);
        return reservation;
    }

    private static Reservation CreateCancelledReservation(
        Guid propertyId,
        string guestName,
        DateOnly arrival)
    {
        Reservation reservation = CreateReservation(propertyId, guestName, null, arrival);
        Assert.True(reservation.RequestCancellation(
            reservation.Version,
            Guid.NewGuid(),
            Guid.NewGuid(),
            reservation.CreatedAtUtc.AddMinutes(1)).IsSuccess);
        Assert.True(reservation.RejectAllocation(
            reservation.AllocationRequestId,
            ReservationAllocationRejection.AllocationConflict,
            Guid.NewGuid(),
            reservation.CreatedAtUtc.AddMinutes(2)).IsSuccess);
        return reservation;
    }

    private static Reservation CreateReservation(
        Guid propertyId,
        string guestName,
        string? email,
        DateOnly arrival)
    {
        DateTimeOffset now = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);
        return Reservation.Create(
            Guid.NewGuid(),
            "tenant-a",
            propertyId,
            Guid.NewGuid(),
            arrival,
            arrival.AddDays(2),
            [Guid.NewGuid()],
            guestName,
            email,
            null,
            1,
            ReservationSource.Direct,
            null,
            null,
            null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            ReservationDetailsChangeOrigin.Staff,
            null,
            null,
            null,
            Guid.NewGuid(),
            now).Value;
    }

    private static ReservationsDbContext CreateDbContext()
    {
        DbContextOptions<ReservationsDbContext> options = new DbContextOptionsBuilder<ReservationsDbContext>()
            .UseInMemoryDatabase($"reservation-pagination-{Guid.NewGuid():N}")
            .Options;
        return new(options, new TestScopeContext());
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }
}
