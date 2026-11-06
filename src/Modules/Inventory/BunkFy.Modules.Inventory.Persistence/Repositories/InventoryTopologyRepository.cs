namespace BunkFy.Modules.Inventory.Persistence.Repositories;

using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

internal sealed class InventoryTopologyRepository(InventoryDbContext dbContext) : IInventoryTopologyRepository
{
    private const string PropertyLockPrefix = "bunkfy:inventory:topology:property:";
    private const string RoomLockPrefix = "bunkfy:inventory:topology:room:";
    private const string BedLockPrefix = "bunkfy:inventory:topology:bed:";

    public async Task<bool> ApplyPropertyAsync(
        InventoryPropertyTopologyWriteModel property,
        CancellationToken cancellationToken)
    {
        await this.AcquireTopologyLocksAsync(
            property.ScopeId,
            property.PropertyId,
            roomId: null,
            bedId: null,
            cancellationToken).ConfigureAwait(false);
        InventoryPropertyTopology? projection = dbContext.PropertyTopology.Local
            .FirstOrDefault(item => item.Id == property.PropertyId && item.ScopeId == property.ScopeId) ??
            await dbContext.PropertyTopology
                .FirstOrDefaultAsync(item => item.Id == property.PropertyId, cancellationToken)
                .ConfigureAwait(false);

        bool created = projection is null;
        if (created)
        {
            projection = InventoryPropertyTopology.Create(property.PropertyId, property.ScopeId);
            dbContext.PropertyTopology.Add(projection);
        }

        long previousSourceVersion = projection!.SourceVersion;
        long previousDetailsVersion = projection.DetailsVersion;
        projection.Apply(
            property.Name,
            property.Code,
            property.TimeZoneId,
            property.Status,
            property.SourceVersion);
        return created ||
            projection.SourceVersion != previousSourceVersion ||
            projection.DetailsVersion != previousDetailsVersion;
    }

    public async Task<bool> ApplyRoomAsync(
        InventoryRoomTopologyWriteModel room,
        CancellationToken cancellationToken)
    {
        await this.AcquireTopologyLocksAsync(
            room.ScopeId,
            room.PropertyId,
            room.RoomId,
            bedId: null,
            cancellationToken).ConfigureAwait(false);
        bool changed = await this.EnsurePropertyPlaceholderAsync(
            room.ScopeId,
            room.PropertyId,
            cancellationToken).ConfigureAwait(false);
        InventoryRoomTopology? projection = dbContext.RoomTopology.Local
            .FirstOrDefault(item => item.Id == room.RoomId && item.ScopeId == room.ScopeId) ??
            await dbContext.RoomTopology
                .FirstOrDefaultAsync(item => item.Id == room.RoomId, cancellationToken)
                .ConfigureAwait(false);

        bool created = projection is null;
        if (created)
        {
            projection = InventoryRoomTopology.Create(room.RoomId, room.ScopeId, room.PropertyId);
            dbContext.RoomTopology.Add(projection);
        }

        long previousSourceVersion = projection!.SourceVersion;
        long previousDetailsVersion = projection.DetailsVersion;
        projection.Apply(
            room.PropertyId,
            room.Name,
            room.BuildingLabel,
            room.FloorLabel,
            room.Status,
            room.SourceVersion);
        changed |= created ||
            projection.SourceVersion != previousSourceVersion ||
            projection.DetailsVersion != previousDetailsVersion;
        changed |= await this.ApplyInventoryUnitAsync(
            room.ScopeId,
            room.PropertyId,
            room.RoomId,
            null,
            InventoryUnitKind.Room,
            room.Name,
            room.Status == RoomStatus.Active,
            room.SourceVersion,
            cancellationToken).ConfigureAwait(false);
        return changed;
    }

    public async Task<bool> ApplyBedAsync(
        InventoryBedTopologyWriteModel bed,
        CancellationToken cancellationToken)
    {
        await this.AcquireTopologyLocksAsync(
            bed.ScopeId,
            bed.PropertyId,
            bed.RoomId,
            bed.BedId,
            cancellationToken).ConfigureAwait(false);
        bool changed = await this.EnsurePropertyPlaceholderAsync(
            bed.ScopeId,
            bed.PropertyId,
            cancellationToken).ConfigureAwait(false);
        (InventoryRoomTopology _, bool roomCreated) = await this.EnsureRoomPlaceholderAsync(
            bed.ScopeId,
            bed.PropertyId,
            bed.RoomId,
            cancellationToken).ConfigureAwait(false);
        changed |= roomCreated;

        InventoryBedTopology? projection = dbContext.BedTopology.Local
            .FirstOrDefault(item => item.Id == bed.BedId && item.ScopeId == bed.ScopeId) ??
            await dbContext.BedTopology
                .FirstOrDefaultAsync(item => item.Id == bed.BedId, cancellationToken)
                .ConfigureAwait(false);
        bool created = projection is null;
        if (created)
        {
            projection = InventoryBedTopology.Create(bed.BedId, bed.ScopeId, bed.PropertyId, bed.RoomId);
            dbContext.BedTopology.Add(projection);
        }

        long previousSourceVersion = projection!.SourceVersion;
        long previousDetailsVersion = projection.DetailsVersion;
        projection.Apply(bed.PropertyId, bed.RoomId, bed.Label, bed.Status, bed.BedSourceVersion);
        changed |= created ||
            projection.SourceVersion != previousSourceVersion ||
            projection.DetailsVersion != previousDetailsVersion;
        changed |= await this.ApplyInventoryUnitAsync(
            bed.ScopeId,
            bed.PropertyId,
            bed.RoomId,
            bed.BedId,
            InventoryUnitKind.Bed,
            bed.Label,
            bed.Status == BedStatus.Active,
            bed.BedSourceVersion,
            cancellationToken).ConfigureAwait(false);
        return changed;
    }

    public async Task<InventoryRoomTopologySnapshot?> GetRoomAsync(
        Guid propertyId,
        Guid roomId,
        CancellationToken cancellationToken) =>
        await dbContext.RoomTopology
            .AsNoTracking()
            .Where(room => room.Id == roomId && room.PropertyId == propertyId && room.IsKnown)
            .Select(room => new InventoryRoomTopologySnapshot(
                room.PropertyId,
                room.Id,
                room.Status,
                dbContext.BedTopology.Count(bed =>
                    bed.RoomId == room.Id && bed.IsKnown && bed.Status == BedStatus.Active)))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyCollection<InventoryUnitDefinitionSnapshot>> GetUnitDefinitionsAsync(
        Guid propertyId,
        Guid? roomId,
        Guid? inventoryUnitId,
        bool touchVersions,
        CancellationToken cancellationToken)
    {
        IQueryable<InventoryUnit> query = dbContext.InventoryUnits.Where(unit => unit.PropertyId == propertyId);
        if (roomId.HasValue)
        {
            query = query.Where(unit => unit.RoomId == roomId.Value);
        }

        if (inventoryUnitId.HasValue)
        {
            query = query.Where(unit => unit.Id == inventoryUnitId.Value);
        }

        List<InventoryUnit> units = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<InventoryUnit> localUnits = dbContext.InventoryUnits.Local.Where(unit =>
            unit.PropertyId == propertyId &&
            (!roomId.HasValue || unit.RoomId == roomId.Value) &&
            (!inventoryUnitId.HasValue || unit.Id == inventoryUnitId.Value));
        foreach (InventoryUnit local in localUnits)
        {
            if (units.All(unit => unit.Id != local.Id))
            {
                units.Add(local);
            }
        }

        InventoryPropertyTopology? property = dbContext.PropertyTopology.Local
            .FirstOrDefault(item => item.Id == propertyId) ??
            await dbContext.PropertyTopology.FirstOrDefaultAsync(item => item.Id == propertyId, cancellationToken)
                .ConfigureAwait(false);
        Guid[] roomIds = units.Select(unit => unit.RoomId).Distinct().ToArray();
        List<InventoryRoomTopology> rooms = roomIds.Length == 0
            ? []
            : await dbContext.RoomTopology.Where(item => roomIds.Contains(item.Id)).ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        foreach (InventoryRoomTopology local in dbContext.RoomTopology.Local.Where(item => roomIds.Contains(item.Id)))
        {
            if (rooms.All(item => item.Id != local.Id))
            {
                rooms.Add(local);
            }
        }

        List<RoomInventoryConfiguration> configurations = roomIds.Length == 0
            ? []
            : await dbContext.RoomConfigurations.Where(item => roomIds.Contains(item.Id)).ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        foreach (RoomInventoryConfiguration local in dbContext.RoomConfigurations.Local.Where(item => roomIds.Contains(item.Id)))
        {
            if (configurations.All(item => item.Id != local.Id))
            {
                configurations.Add(local);
            }
        }

        Dictionary<Guid, InventoryRoomTopology> roomsById = rooms.ToDictionary(item => item.Id);
        Dictionary<Guid, RoomInventoryConfiguration> configurationsByRoom = configurations.ToDictionary(item => item.Id);
        BedRetirementProcess[] localBedRetirements = dbContext.BedRetirements.Local.ToArray();
        Guid[] trackedBedRetirementIds = localBedRetirements.Select(process => process.Id).ToArray();
        List<BedRetirementProcess> drains = roomIds.Length == 0
            ? []
            : await dbContext.BedRetirements
                .Where(process => !trackedBedRetirementIds.Contains(process.Id) &&
                    roomIds.Contains(process.RoomId) &&
                    (process.State == InventoryRetirementProcessState.Draining ||
                     process.State == InventoryRetirementProcessState.FinalizationRequested ||
                     process.State == InventoryRetirementProcessState.FinalizedAwaitingTopology ||
                     process.State == InventoryRetirementProcessState.Rejected))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        foreach (BedRetirementProcess local in localBedRetirements.Where(process =>
                     roomIds.Contains(process.RoomId) && BedRetirementProcess.IsDrainActive(process.State)))
        {
            if (drains.All(process => process.Id != local.Id))
            {
                drains.Add(local);
            }
        }

        RoomRetirementProcess[] localRoomRetirements = dbContext.RoomRetirements.Local.ToArray();
        Guid[] trackedRoomRetirementIds = localRoomRetirements.Select(process => process.Id).ToArray();
        List<RoomRetirementProcess> roomDrains = roomIds.Length == 0
            ? []
            : await dbContext.RoomRetirements
                .Where(process => !trackedRoomRetirementIds.Contains(process.Id) &&
                    roomIds.Contains(process.RoomId) &&
                    (process.State == InventoryRetirementProcessState.Draining ||
                     process.State == InventoryRetirementProcessState.FinalizationRequested ||
                     process.State == InventoryRetirementProcessState.FinalizedAwaitingTopology ||
                     process.State == InventoryRetirementProcessState.Rejected))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        foreach (RoomRetirementProcess local in localRoomRetirements.Where(process =>
                     roomIds.Contains(process.RoomId) && RoomRetirementProcess.IsDrainActive(process.State)))
        {
            if (roomDrains.All(process => process.Id != local.Id))
            {
                roomDrains.Add(local);
            }
        }

        HashSet<Guid> drainedBedIds = drains.Select(process => process.BedId).ToHashSet();
        HashSet<Guid> fullyDrainingRoomIds = roomDrains.Select(process => process.RoomId).ToHashSet();
        HashSet<Guid> drainingRoomIds = drains
            .Select(process => process.RoomId)
            .Concat(fullyDrainingRoomIds)
            .ToHashSet();
        List<InventoryUnitDefinitionSnapshot> snapshots = [];
        foreach (InventoryUnit unit in units.Where(item => item.IsKnown).OrderBy(item => item.RoomId).ThenBy(item => item.Id))
        {
            if (touchVersions)
            {
                unit.TouchAvailability();
            }

            bool topologyActive = property is { IsKnown: true, Status: PropertyStatus.Active } &&
                                  roomsById.TryGetValue(unit.RoomId, out InventoryRoomTopology? room) &&
                                  room.IsKnown &&
                                  room.Status == RoomStatus.Active &&
                                  unit.IsTopologyActive;
            RoomInventoryConfiguration? configuration = configurationsByRoom.GetValueOrDefault(unit.RoomId);
            RoomSalesMode salesMode = configuration?.SalesMode ?? RoomSalesMode.Unconfigured;
            bool sellable = topologyActive &&
                            ((unit.Kind == InventoryUnitKind.Room && salesMode == RoomSalesMode.RoomLevel) ||
                             (unit.Kind == InventoryUnitKind.Bed && salesMode == RoomSalesMode.BedLevel)) &&
                            (unit.Kind != InventoryUnitKind.Room || !drainingRoomIds.Contains(unit.RoomId)) &&
                            (unit.Kind != InventoryUnitKind.Bed ||
                             (!drainedBedIds.Contains(unit.Id) && !fullyDrainingRoomIds.Contains(unit.RoomId)));
            snapshots.Add(new(
                unit.ScopeId,
                unit.Id,
                unit.PropertyId,
                unit.RoomId,
                unit.BedId,
                unit.Kind,
                unit.Label,
                topologyActive,
                sellable,
                configuration?.Version ?? 1,
                unit.AvailabilityMutationVersion));
        }

        return snapshots;
    }

    private async Task AcquireTopologyLocksAsync(
        string tenantId,
        Guid propertyId,
        Guid? roomId,
        Guid? bedId,
        CancellationToken cancellationToken)
    {
        string scopeId = tenantId?.Trim() ?? string.Empty;
        if (scopeId.Length == 0 ||
            !string.Equals(scopeId, dbContext.CurrentScopeId, StringComparison.Ordinal) ||
            propertyId == Guid.Empty ||
            roomId == Guid.Empty ||
            bedId == Guid.Empty ||
            (bedId.HasValue && !roomId.HasValue))
        {
            throw new InvalidOperationException(
                "An Inventory topology lock requires valid scoped coordinates.");
        }

        if (!dbContext.Database.IsRelational())
        {
            return;
        }

        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "An Inventory topology lock requires an active database transaction.");
        }

        await EfTransactionKeyLock.AcquireAsync(
            dbContext,
            PropertyLockPrefix + scopeId + ':' + propertyId.ToString("N"),
            cancellationToken).ConfigureAwait(false);
        if (roomId.HasValue)
        {
            await EfTransactionKeyLock.AcquireAsync(
                dbContext,
                RoomLockPrefix + scopeId + ':' + roomId.Value.ToString("N"),
                cancellationToken).ConfigureAwait(false);
        }

        if (bedId.HasValue)
        {
            await EfTransactionKeyLock.AcquireAsync(
                dbContext,
                BedLockPrefix + scopeId + ':' + bedId.Value.ToString("N"),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> EnsurePropertyPlaceholderAsync(
        string scopeId,
        Guid propertyId,
        CancellationToken cancellationToken)
    {
        if (dbContext.PropertyTopology.Local.Any(item => item.Id == propertyId && item.ScopeId == scopeId) ||
            await dbContext.PropertyTopology.AnyAsync(item => item.Id == propertyId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        dbContext.PropertyTopology.Add(InventoryPropertyTopology.Create(propertyId, scopeId));
        return true;
    }

    private async Task<(InventoryRoomTopology Room, bool Created)> EnsureRoomPlaceholderAsync(
        string scopeId,
        Guid propertyId,
        Guid roomId,
        CancellationToken cancellationToken)
    {
        InventoryRoomTopology? room = dbContext.RoomTopology.Local.FirstOrDefault(
            item => item.Id == roomId && item.ScopeId == scopeId) ??
            await dbContext.RoomTopology.FirstOrDefaultAsync(item => item.Id == roomId, cancellationToken).ConfigureAwait(false);
        if (room is not null)
        {
            return (room, false);
        }

        room = InventoryRoomTopology.Create(roomId, scopeId, propertyId);
        dbContext.RoomTopology.Add(room);
        return (room, true);
    }

    private async Task<bool> ApplyInventoryUnitAsync(
        string scopeId,
        Guid propertyId,
        Guid roomId,
        Guid? bedId,
        InventoryUnitKind kind,
        string? label,
        bool isTopologyActive,
        long sourceVersion,
        CancellationToken cancellationToken)
    {
        Guid inventoryUnitId = bedId ?? roomId;
        InventoryUnit? unit = dbContext.InventoryUnits.Local.FirstOrDefault(
                item => item.Id == inventoryUnitId && item.ScopeId == scopeId) ??
            await dbContext.InventoryUnits.FirstOrDefaultAsync(
                item => item.Id == inventoryUnitId,
                cancellationToken).ConfigureAwait(false);
        bool created = unit is null;
        if (created)
        {
            unit = kind == InventoryUnitKind.Room
                ? InventoryUnit.CreateRoom(roomId, scopeId, propertyId)
                : InventoryUnit.CreateBed(inventoryUnitId, scopeId, propertyId, roomId);
            dbContext.InventoryUnits.Add(unit);
        }

        long previousSourceVersion = unit!.SourceVersion;
        long previousDetailsVersion = unit.DetailsVersion;
        unit.Apply(
            propertyId,
            roomId,
            bedId,
            kind,
            label,
            isTopologyActive,
            sourceVersion);
        return created ||
            unit.SourceVersion != previousSourceVersion ||
            unit.DetailsVersion != previousDetailsVersion;
    }
}
