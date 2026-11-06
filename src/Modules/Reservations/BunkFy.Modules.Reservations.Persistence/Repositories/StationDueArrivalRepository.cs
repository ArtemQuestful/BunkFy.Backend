namespace BunkFy.Modules.Reservations.Persistence.Repositories;

using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Contracts.Stations;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using Microsoft.EntityFrameworkCore;

internal sealed class StationDueArrivalRepository(ReservationsDbContext db) : IStationDueArrivalRepository
{
    public bool SupportsStationOperations => db.Database.IsNpgsql();

    public async Task<StationDueArrivalPage> ListAsync(Guid propertyId, DateOnly localDate, int pageSize,
        StationArrivalCursor? after, CancellationToken cancellationToken)
    {
        if (!this.SupportsStationOperations)
        { return new(StationReservationState.Unsupported, []); }
        if (pageSize is < 1 or > 25 || propertyId == Guid.Empty || localDate == default ||
            (after is not null && (after.ReservationId == Guid.Empty || after.Arrival == default ||
                string.IsNullOrWhiteSpace(after.NormalizedGuestName) || after.NormalizedGuestName.Length > Reservation.PrimaryGuestNameMaxLength)))
        { return new(StationReservationState.Conflict, []); }
        IQueryable<Reservation> query = this.Due(propertyId, localDate);
        if (after is not null)
        {
            query = query.Where(x => EF.Functions.GreaterThan(
                ValueTuple.Create(x.Arrival, x.PrimaryGuestNameSearch, x.Id),
                ValueTuple.Create(after.Arrival, after.NormalizedGuestName, after.ReservationId)));
        }
        var rows = await query.OrderBy(x => x.Arrival).ThenBy(x => x.PrimaryGuestNameSearch).ThenBy(x => x.Id)
            .Select(x => new Candidate(x.Id, x.PrimaryGuestName, x.PrimaryGuestNameSearch, x.Arrival, x.Departure,
                x.Version, x.AllocationId!.Value, x.AllocationVersion!.Value)).Take(pageSize + 1)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var allocations = await this.ReadAllocationsAsync(rows.Select(x => x.AllocationId).ToArray(), cancellationToken).ConfigureAwait(false);
        HashSet<Guid> admitted = [];
        List<StationDueArrival> items = [];
        Candidate? last = null;
        foreach (var row in rows)
        {
            if (items.Count == pageSize)
            { break; }
            var item = Map(row, propertyId, allocations);
            if (item is null)
            { return new(StationReservationState.Incomplete, []); }
            int count = admitted.Union(item.Units.Select(x => x.InventoryUnitId)).Count();
            if (count > 100)
            { break; }
            admitted.UnionWith(item.Units.Select(x => x.InventoryUnitId));
            items.Add(item);
            last = row;
        }
        return new(StationReservationState.Ready, items, rows.Length > items.Count && last is not null
            ? new(last.Arrival, last.NormalizedGuestName, last.ReservationId) : null);
    }

    public async Task<StationDueArrival?> FindAsync(Guid propertyId, Guid reservationId, DateOnly localDate, CancellationToken cancellationToken)
    {
        if (!this.SupportsStationOperations)
        { return null; }
        var row = await this.Due(propertyId, localDate).Where(x => x.Id == reservationId)
            .Select(x => new Candidate(x.Id, x.PrimaryGuestName, x.PrimaryGuestNameSearch, x.Arrival, x.Departure,
                x.Version, x.AllocationId!.Value, x.AllocationVersion!.Value)).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return row is null ? null : Map(row, propertyId,
            await this.ReadAllocationsAsync([row.AllocationId], cancellationToken).ConfigureAwait(false));
    }

    public Task<bool> IsVisibleAsync(Guid propertyId, Guid reservationId, CancellationToken cancellationToken) =>
        ReservationVisibilityQueries.Ordinary(db).AnyAsync(x => x.PropertyId == propertyId && x.Id == reservationId, cancellationToken);

    private IQueryable<Reservation> Due(Guid propertyId, DateOnly localDate) => ReservationVisibilityQueries.Ordinary(db)
        .AsNoTracking().Where(x => x.PropertyId == propertyId && x.Status == ReservationState.Confirmed &&
            x.AllocationId != null && x.AllocationVersion > 0 && x.PendingAllocationAmendmentId == null &&
            x.Arrival <= localDate && localDate < x.Departure);

    private async Task<AllocationRows> ReadAllocationsAsync(Guid[] allocationIds, CancellationToken ct)
    {
        var allocations = await db.InventoryAllocationProjections.AsNoTracking().Where(x => allocationIds.Contains(x.Id))
            .Include(x => x.Units).ToArrayAsync(ct).ConfigureAwait(false);
        var ids = allocations.SelectMany(x => x.Units.Select(u => u.InventoryUnitId)).Distinct().ToArray();
        var units = await db.InventoryUnitProjections.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct).ConfigureAwait(false);
        return new(allocations.ToDictionary(x => x.Id), units);
    }

    private static StationDueArrival? Map(Candidate row, Guid propertyId, AllocationRows values)
    {
        if (!values.Allocations.TryGetValue(row.AllocationId, out var allocation) || !allocation.IsKnown ||
            allocation.PropertyId != propertyId || allocation.ReservationId != row.ReservationId || allocation.Version != row.AllocationVersion ||
            allocation.Arrival != row.Arrival || allocation.Departure != row.Departure || allocation.Status != InventoryAllocationStatus.Active ||
            allocation.Units.Count is < 1 or > 100)
        { return null; }
        List<StationAllocationUnit> units = [];
        foreach (var id in allocation.Units.Select(x => x.InventoryUnitId).Order())
        {
            if (!values.Units.TryGetValue(id, out var unit) || unit.PropertyId != propertyId || !unit.IsTopologyActive || !unit.IsSellable ||
                unit.UnitVersion < 1 || unit.ConfigurationVersion < 1 || unit.RoomId == Guid.Empty ||
                unit.Kind is not (InventoryUnitKind.Room or InventoryUnitKind.Bed))
            { return null; }
            units.Add(new(unit.Id, unit.RoomId, unit.BedId, (int)unit.Kind, unit.ConfigurationVersion, unit.UnitVersion));
        }
        return new(row.ReservationId, row.PrimaryGuestName, row.Arrival, row.Departure, row.ExpectedVersion,
            row.AllocationId, row.AllocationVersion, units);
    }
    private sealed record Candidate(Guid ReservationId, string PrimaryGuestName, string NormalizedGuestName, DateOnly Arrival,
        DateOnly Departure, long ExpectedVersion, Guid AllocationId, long AllocationVersion);
    private sealed record AllocationRows(Dictionary<Guid, ReservationInventoryAllocationProjection> Allocations,
        Dictionary<Guid, ReservationInventoryUnitProjection> Units);
}
