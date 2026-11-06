namespace BunkFy.Modules.Inventory.Persistence.Repositories;

using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Contracts.Stations;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using BunkFy.Modules.Properties.Contracts;
using Microsoft.EntityFrameworkCore;

internal sealed class StationInventoryLabelReader(InventoryDbContext db) : IStationInventoryLabelReader
{
    public async Task<StationInventoryLabels> ReadAsync(Guid propertyId, IReadOnlyCollection<Guid> inventoryUnitIds,
        CancellationToken cancellationToken = default)
    {
        if (propertyId == Guid.Empty || inventoryUnitIds.Count is < 1 or > 25 ||
            inventoryUnitIds.Any(x => x == Guid.Empty) || inventoryUnitIds.Distinct().Count() != inventoryUnitIds.Count)
        { return new(StationInventoryLabelsState.Invalid, []); }
        try
        {
            var units = await db.InventoryUnits.AsNoTracking().Where(x => x.PropertyId == propertyId && inventoryUnitIds.Contains(x.Id))
                .OrderBy(x => x.Id).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            if (units.Length != inventoryUnitIds.Count)
            { return Incomplete(); }
            var roomIds = units.Select(x => x.RoomId).Distinct().ToArray();
            var rooms = await db.RoomTopology.AsNoTracking().Where(x => x.PropertyId == propertyId && roomIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, cancellationToken).ConfigureAwait(false);
            var beds = await db.BedTopology.AsNoTracking().Where(x => x.PropertyId == propertyId && inventoryUnitIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, cancellationToken).ConfigureAwait(false);
            var configurations = await db.RoomConfigurations.AsNoTracking().Where(x => x.PropertyId == propertyId && roomIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, cancellationToken).ConfigureAwait(false);
            List<StationInventoryLabel> result = [];
            foreach (var unit in units)
            {
                if (!unit.IsKnown || !unit.IsTopologyActive || unit.SourceVersion < 1 || unit.SourceVersion != unit.DetailsVersion ||
                    unit.AvailabilityMutationVersion < 1 || string.IsNullOrWhiteSpace(unit.Label) ||
                    !rooms.TryGetValue(unit.RoomId, out var room) || !room.IsKnown || room.Status != RoomStatus.Active ||
                    room.SourceVersion < 1 || room.SourceVersion != room.DetailsVersion || string.IsNullOrWhiteSpace(room.Name) ||
                    !configurations.TryGetValue(unit.RoomId, out var configuration) || configuration.Version < 1)
                { return Incomplete(); }
                InventoryBedTopology? bed = null;
                if (unit.Kind == InventoryUnitKind.Room)
                {
                    if (unit.Id != room.Id || unit.BedId is not null || configuration.SalesMode != RoomSalesMode.RoomLevel ||
                        unit.Label != room.Name || unit.SourceVersion != room.SourceVersion)
                    { return Incomplete(); }
                }
                else if (unit.Kind == InventoryUnitKind.Bed)
                {
                    if (unit.BedId != unit.Id || configuration.SalesMode != RoomSalesMode.BedLevel ||
                        !beds.TryGetValue(unit.Id, out bed) || bed.RoomId != room.Id || !bed.IsKnown || bed.Status != BedStatus.Active ||
                        bed.SourceVersion < 1 || bed.SourceVersion != bed.DetailsVersion || string.IsNullOrWhiteSpace(bed.Label) ||
                        unit.Label != bed.Label || unit.SourceVersion != bed.SourceVersion)
                    { return Incomplete(); }
                }
                else
                { return Incomplete(); }
                result.Add(new(propertyId, unit.Id, unit.Kind, room.Id, room.Name, unit.BedId, bed?.Label,
                    configuration.Version, unit.AvailabilityMutationVersion, unit.SourceVersion, room.SourceVersion, bed?.SourceVersion));
            }
            return new(StationInventoryLabelsState.Current, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return new(StationInventoryLabelsState.Unavailable, []); }
    }
    private static StationInventoryLabels Incomplete() => new(StationInventoryLabelsState.Incomplete, []);
}
