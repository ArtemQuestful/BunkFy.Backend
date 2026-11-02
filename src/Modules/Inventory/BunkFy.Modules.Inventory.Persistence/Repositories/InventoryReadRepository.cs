namespace BunkFy.Modules.Inventory.Persistence.Repositories;

using Gma.Framework.Pagination;
using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using Microsoft.EntityFrameworkCore;
using BunkFy.Modules.Properties.Contracts;

internal sealed class InventoryReadRepository(InventoryDbContext dbContext) : IInventoryReadRepository
{
    public Task<bool> PropertyExistsAsync(Guid propertyId, CancellationToken cancellationToken) =>
        dbContext.PropertyTopology.AsNoTracking().AnyAsync(
            property => property.Id == propertyId && property.IsKnown,
            cancellationToken);

    public async Task<RoomInventoryDto?> GetRoomAsync(
        Guid propertyId,
        Guid roomId,
        CancellationToken cancellationToken)
    {
        InventoryRoomTopology? room = await dbContext.RoomTopology
            .AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.Id == roomId && item.PropertyId == propertyId && item.IsKnown,
                cancellationToken)
            .ConfigureAwait(false);
        if (room is null)
        {
            return null;
        }

        InventoryPropertyTopology? property = await dbContext.PropertyTopology
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == propertyId && item.IsKnown, cancellationToken)
            .ConfigureAwait(false);
        RoomInventoryConfiguration? configuration = await dbContext.RoomConfigurations
            .FirstOrDefaultAsync(item => item.Id == roomId, cancellationToken)
            .ConfigureAwait(false);
        List<InventoryUnit> units = await dbContext.InventoryUnits
            .AsNoTracking()
            .Where(unit => unit.RoomId == roomId && unit.IsKnown)
            .OrderBy(unit => unit.Kind)
            .ThenBy(unit => unit.Label)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        (HashSet<Guid> drainedBedIds, HashSet<Guid> drainingRoomIds, HashSet<Guid> fullyDrainingRoomIds) = await this
            .GetActiveDrainsAsync(propertyId, [roomId], cancellationToken)
            .ConfigureAwait(false);

        return MapRoom(
            room,
            configuration,
            units,
            property?.Status == PropertyStatus.Active,
            drainedBedIds,
            drainingRoomIds,
            fullyDrainingRoomIds);
    }

    public async Task<InventoryUnitSnapshot?> GetUnitAsync(
        Guid propertyId,
        Guid inventoryUnitId,
        CancellationToken cancellationToken)
    {
        InventoryUnit? unit = await dbContext.InventoryUnits
            .AsNoTracking()
            .FirstOrDefaultAsync(
                item => item.Id == inventoryUnitId && item.PropertyId == propertyId && item.IsKnown,
                cancellationToken)
            .ConfigureAwait(false);
        if (unit is null)
        {
            return null;
        }

        InventoryPropertyTopology? property = await dbContext.PropertyTopology
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == propertyId && item.IsKnown, cancellationToken)
            .ConfigureAwait(false);
        InventoryRoomTopology? room = await dbContext.RoomTopology
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == unit.RoomId && item.IsKnown, cancellationToken)
            .ConfigureAwait(false);
        RoomInventoryConfiguration? configuration = await dbContext.RoomConfigurations
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == unit.RoomId, cancellationToken)
            .ConfigureAwait(false);
        if (room is null)
        {
            return null;
        }

        (HashSet<Guid> drainedBedIds, HashSet<Guid> drainingRoomIds, HashSet<Guid> fullyDrainingRoomIds) = await this
            .GetActiveDrainsAsync(propertyId, [unit.RoomId], cancellationToken)
            .ConfigureAwait(false);

        InventoryUnitDto mapped = MapUnit(
            unit,
            configuration?.SalesMode ?? RoomSalesMode.Unconfigured,
            property?.Status == PropertyStatus.Active && room.Status == RoomStatus.Active,
            drainedBedIds,
            drainingRoomIds,
            fullyDrainingRoomIds);
        return new(mapped, mapped.IsSellable);
    }

    public async Task<IReadOnlyCollection<InventoryUnitSnapshot>> ResolveBlockTargetUnitsAsync(
        Guid propertyId,
        InventoryBlockTarget target,
        CancellationToken cancellationToken)
    {
        InventoryBlockTargetResolution resolution = await this
            .ResolveBlockTargetUnitsCoreAsync(
                propertyId,
                target,
                maximumUnitCount: null,
                sellableOnly: false,
                cancellationToken)
            .ConfigureAwait(false);
        return resolution.Units;
    }

    public Task<InventoryBlockTargetResolution>
        ResolveBlockTargetUnitsBoundedAsync(
            Guid propertyId,
            InventoryBlockTarget target,
            int maximumUnitCount,
            CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumUnitCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            maximumUnitCount,
            ManualInventoryBlockGroup.MaximumMemberCount + 1);
        return this.ResolveBlockTargetUnitsBoundedCoreAsync(
            propertyId,
            target,
            maximumUnitCount,
            cancellationToken);
    }

    private async Task<InventoryBlockTargetResolution>
        ResolveBlockTargetUnitsBoundedCoreAsync(
            Guid propertyId,
            InventoryBlockTarget target,
            int maximumUnitCount,
            CancellationToken cancellationToken)
    {
        string? buildingLabel = target.BuildingLabel?.Trim();
        string? floorLabel = target.FloorLabel?.Trim();
        InventoryRetirementProcessState[] activeStates =
        [
            InventoryRetirementProcessState.Draining,
            InventoryRetirementProcessState.FinalizationRequested,
            InventoryRetirementProcessState.FinalizedAwaitingTopology,
            InventoryRetirementProcessState.Rejected
        ];
        var selected = await dbContext.InventoryUnits
            .AsNoTracking()
            .Where(unit =>
                unit.PropertyId == propertyId &&
                unit.IsKnown &&
                unit.IsTopologyActive &&
                dbContext.PropertyTopology.Any(property =>
                    property.Id == propertyId &&
                    property.IsKnown &&
                    property.Status == PropertyStatus.Active) &&
                dbContext.RoomTopology.Any(room =>
                    room.Id == unit.RoomId &&
                    room.PropertyId == propertyId &&
                    room.IsKnown &&
                    room.Status == RoomStatus.Active &&
                    (target.Kind == InventoryBlockTargetKind.Property ||
                     (target.Kind == InventoryBlockTargetKind.Building &&
                      room.BuildingLabel == buildingLabel) ||
                     (target.Kind == InventoryBlockTargetKind.Floor &&
                      room.BuildingLabel == buildingLabel &&
                      room.FloorLabel == floorLabel) ||
                     (target.Kind == InventoryBlockTargetKind.Room &&
                      room.Id == target.RoomId) ||
                     (target.Kind == InventoryBlockTargetKind.Unit &&
                      unit.Id == target.InventoryUnitId))) &&
                ((unit.Kind == InventoryUnitKind.Room &&
                  dbContext.RoomConfigurations.Any(configuration =>
                      configuration.Id == unit.RoomId &&
                      configuration.SalesMode == RoomSalesMode.RoomLevel) &&
                  !dbContext.RoomRetirements.Any(process =>
                      process.PropertyId == propertyId &&
                      process.RoomId == unit.RoomId &&
                      activeStates.Contains(process.State)) &&
                  !dbContext.BedRetirements.Any(process =>
                      process.PropertyId == propertyId &&
                      process.RoomId == unit.RoomId &&
                      activeStates.Contains(process.State))) ||
                 (unit.Kind == InventoryUnitKind.Bed &&
                  dbContext.RoomConfigurations.Any(configuration =>
                      configuration.Id == unit.RoomId &&
                      configuration.SalesMode == RoomSalesMode.BedLevel) &&
                  !dbContext.RoomRetirements.Any(process =>
                      process.PropertyId == propertyId &&
                      process.RoomId == unit.RoomId &&
                      activeStates.Contains(process.State)) &&
                  !dbContext.BedRetirements.Any(process =>
                      process.PropertyId == propertyId &&
                      process.RoomId == unit.RoomId &&
                      process.BedId == unit.Id &&
                      activeStates.Contains(process.State)))))
            .OrderBy(unit => unit.Id)
            .Take(maximumUnitCount + 1)
            .Select(unit => new
            {
                unit.Id,
                unit.PropertyId,
                unit.RoomId,
                unit.BedId,
                unit.Kind,
                unit.Label
            })
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        bool isTruncated = selected.Length > maximumUnitCount;
        InventoryUnitSnapshot[] units = selected
            .Take(maximumUnitCount)
            .Select(unit => new InventoryUnitSnapshot(
                new InventoryUnitDto(
                    unit.Id,
                    unit.PropertyId,
                    unit.RoomId,
                    unit.BedId,
                    unit.Kind,
                    unit.Label,
                    IsSellable: true,
                    IsTopologyActive: true),
                IsSellable: true))
            .OrderBy(unit =>
                unit.Unit.InventoryUnitId.ToString("N"),
                StringComparer.Ordinal)
            .ToArray();
        if (units.Length == 0)
        {
            return new(units, [], isTruncated);
        }

        Guid[] selectedIds = units
            .Select(unit => unit.Unit.InventoryUnitId)
            .ToArray();
        var coordinateRows = await dbContext.InventoryUnits
            .AsNoTracking()
            .Where(unit =>
                unit.PropertyId == propertyId &&
                selectedIds.Contains(unit.Id))
            .Select(unit => new
            {
                Unit = unit,
                Property = dbContext.PropertyTopology.Single(property =>
                    property.Id == propertyId),
                Room = dbContext.RoomTopology.Single(room =>
                    room.Id == unit.RoomId),
                Configuration = dbContext.RoomConfigurations
                    .Where(configuration => configuration.Id == unit.RoomId)
                    .Select(configuration => new
                    {
                        configuration.SalesMode,
                        configuration.Version,
                        configuration.AvailabilityMutationVersion
                    })
                    .SingleOrDefault()
            })
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        coordinateRows = coordinateRows
            .OrderBy(
                row => row.Unit.Id.ToString("N"),
                StringComparer.Ordinal)
            .ToArray();
        Guid[] selectedRoomIds = coordinateRows
            .Select(row => row.Unit.RoomId)
            .Distinct()
            .ToArray();
        Guid[] selectedBedIds = coordinateRows
            .Where(row => row.Unit.Kind == InventoryUnitKind.Bed)
            .Select(row => row.Unit.Id)
            .ToArray();
        BedRetirementProcess[] bedRetirements = selectedBedIds.Length == 0
            ? []
            : await this.ActiveBedRetirementEvidence(
                    propertyId,
                    selectedBedIds,
                    activeStates)
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
        RoomRetirementProcess[] roomRetirements = selectedRoomIds.Length == 0
            ? []
            : await this.ActiveRoomRetirementEvidence(
                    propertyId,
                    selectedRoomIds,
                    activeStates)
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
        if (bedRetirements.Length > selectedBedIds.Length ||
            bedRetirements.Select(process => process.BedId).Distinct().Count() !=
                bedRetirements.Length ||
            roomRetirements.Length > selectedRoomIds.Length ||
            roomRetirements.Select(process => process.RoomId).Distinct().Count() !=
                roomRetirements.Length)
        {
            throw new InvalidDataException(
                "Inventory selection has duplicate active retirement evidence.");
        }
        InventoryBlockSelectionCoordinate[] coordinates = coordinateRows
            .Select(row => new InventoryBlockSelectionCoordinate(
                row.Unit.Id,
                row.Property.SourceVersion,
                row.Property.DetailsVersion,
                row.Property.AvailabilitySelectionVersion,
                (int)row.Property.Status,
                row.Room.Id,
                row.Room.Name,
                row.Room.SourceVersion,
                row.Room.DetailsVersion,
                (int)row.Room.Status,
                (int)(row.Configuration?.SalesMode ??
                    RoomSalesMode.Unconfigured),
                row.Configuration?.Version ?? 0,
                row.Configuration?.AvailabilityMutationVersion ?? 0,
                row.Unit.SourceVersion,
                row.Unit.DetailsVersion,
                row.Unit.IsTopologyActive,
                row.Unit.AvailabilityMutationVersion,
                bedRetirements
                    .Where(process => process.BedId == row.Unit.Id)
                    .Select(process =>
                        new InventoryBlockSelectionRetirementCoordinate(
                            process.Id,
                            Kind: 1,
                            process.Version,
                            (int)process.State))
                    .Concat(roomRetirements
                        .Where(process => process.RoomId == row.Unit.RoomId)
                        .Select(process =>
                            new InventoryBlockSelectionRetirementCoordinate(
                                process.Id,
                                Kind: 2,
                                process.Version,
                                (int)process.State)))
                    .OrderBy(process => process.Kind)
                    .ThenBy(
                        process => process.TopologyChangeId.ToString("N"),
                        StringComparer.Ordinal)
                    .ToArray()))
            .ToArray();
        return new(units, coordinates, isTruncated);
    }

    private IQueryable<BedRetirementProcess> ActiveBedRetirementEvidence(
        Guid propertyId,
        Guid[] selectedBedIds,
        InventoryRetirementProcessState[] activeStates) =>
        dbContext.BedRetirements
            .AsNoTracking()
            .Where(process =>
                process.PropertyId == propertyId &&
                selectedBedIds.Contains(process.BedId) &&
                activeStates.Contains(process.State))
            .OrderBy(process => process.BedId)
            .ThenBy(process => process.Id)
            .Take(selectedBedIds.Length + 1);

    private IQueryable<RoomRetirementProcess> ActiveRoomRetirementEvidence(
        Guid propertyId,
        Guid[] selectedRoomIds,
        InventoryRetirementProcessState[] activeStates) =>
        dbContext.RoomRetirements
            .AsNoTracking()
            .Where(process =>
                process.PropertyId == propertyId &&
                selectedRoomIds.Contains(process.RoomId) &&
                activeStates.Contains(process.State))
            .OrderBy(process => process.RoomId)
            .ThenBy(process => process.Id)
            .Take(selectedRoomIds.Length + 1);

    private async Task<InventoryBlockTargetResolution>
        ResolveBlockTargetUnitsCoreAsync(
            Guid propertyId,
            InventoryBlockTarget target,
            int? maximumUnitCount,
            bool sellableOnly,
            CancellationToken cancellationToken)
    {
        if (target.Kind == InventoryBlockTargetKind.Unit)
        {
            InventoryUnitSnapshot? unit = target.InventoryUnitId.HasValue
                ? await this.GetUnitAsync(propertyId, target.InventoryUnitId.Value, cancellationToken).ConfigureAwait(false)
                : null;
            InventoryUnitSnapshot[] unitResult = unit is null ||
                (sellableOnly && !unit.IsSellable)
                ? []
                : [unit];
            return new(unitResult, [], IsTruncated: false);
        }

        InventoryPropertyTopology? property = await dbContext.PropertyTopology
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == propertyId && item.IsKnown, cancellationToken)
            .ConfigureAwait(false);
        if (property is null)
        {
            return new([], [], IsTruncated: false);
        }

        IQueryable<InventoryRoomTopology> roomQuery = dbContext.RoomTopology
            .AsNoTracking()
            .Where(room => room.PropertyId == propertyId && room.IsKnown);
        string? buildingLabel = target.BuildingLabel?.Trim();
        string? floorLabel = target.FloorLabel?.Trim();
        roomQuery = target.Kind switch
        {
            InventoryBlockTargetKind.Property => roomQuery,
            InventoryBlockTargetKind.Building when buildingLabel is not null => roomQuery.Where(
                room => room.BuildingLabel == buildingLabel),
            InventoryBlockTargetKind.Floor when buildingLabel is null && floorLabel is not null => roomQuery.Where(
                room => room.BuildingLabel == null && room.FloorLabel == floorLabel),
            InventoryBlockTargetKind.Floor when floorLabel is not null => roomQuery.Where(
                room => room.BuildingLabel == buildingLabel && room.FloorLabel == floorLabel),
            InventoryBlockTargetKind.Room when target.RoomId.HasValue => roomQuery.Where(
                room => room.Id == target.RoomId.Value),
            _ => roomQuery.Where(_ => false)
        };

        InventoryRoomTopology[] rooms = await roomQuery
            .OrderBy(room => room.Id)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        Guid[] roomIds = rooms.Select(room => room.Id).ToArray();
        if (roomIds.Length == 0)
        {
            return new([], [], IsTruncated: false);
        }

        Dictionary<Guid, RoomInventoryConfiguration> configurations = await dbContext.RoomConfigurations
            .AsNoTracking()
            .Where(configuration => roomIds.Contains(configuration.Id))
            .ToDictionaryAsync(configuration => configuration.Id, cancellationToken)
            .ConfigureAwait(false);
        (HashSet<Guid> drainedBedIds, HashSet<Guid> drainingRoomIds, HashSet<Guid> fullyDrainingRoomIds) = await this
            .GetActiveDrainsAsync(propertyId, roomIds, cancellationToken)
            .ConfigureAwait(false);
        IQueryable<InventoryUnit> unitQuery = dbContext.InventoryUnits
            .AsNoTracking()
            .Where(unit => roomIds.Contains(unit.RoomId) && unit.IsKnown)
            .OrderBy(unit => unit.Id);
        if (sellableOnly)
        {
            Guid[] activeRoomIds = property.Status == PropertyStatus.Active
                ? rooms
                    .Where(room => room.Status == RoomStatus.Active)
                    .Select(room => room.Id)
                    .ToArray()
                : [];
            Guid[] roomLevelRoomIds = configurations.Values
                .Where(configuration =>
                    configuration.SalesMode == RoomSalesMode.RoomLevel)
                .Select(configuration => configuration.Id)
                .ToArray();
            Guid[] bedLevelRoomIds = configurations.Values
                .Where(configuration =>
                    configuration.SalesMode == RoomSalesMode.BedLevel)
                .Select(configuration => configuration.Id)
                .ToArray();
            Guid[] drainedBeds = drainedBedIds.ToArray();
            Guid[] drainingRooms = drainingRoomIds.ToArray();
            Guid[] fullyDrainingRooms = fullyDrainingRoomIds.ToArray();
            unitQuery = unitQuery.Where(unit =>
                unit.IsTopologyActive &&
                activeRoomIds.Contains(unit.RoomId) &&
                ((unit.Kind == InventoryUnitKind.Room &&
                  roomLevelRoomIds.Contains(unit.RoomId) &&
                  !drainingRooms.Contains(unit.RoomId)) ||
                 (unit.Kind == InventoryUnitKind.Bed &&
                  bedLevelRoomIds.Contains(unit.RoomId) &&
                  !drainedBeds.Contains(unit.Id) &&
                  !fullyDrainingRooms.Contains(unit.RoomId))));
        }

        if (maximumUnitCount.HasValue)
        {
            unitQuery = unitQuery.Take(maximumUnitCount.Value + 1);
        }

        InventoryUnit[] units = await unitQuery
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        bool isTruncated = maximumUnitCount.HasValue &&
            units.Length > maximumUnitCount.Value;
        if (isTruncated)
        {
            units = units[..maximumUnitCount!.Value];
        }

        Dictionary<Guid, InventoryRoomTopology> roomsById = rooms.ToDictionary(room => room.Id);

        InventoryUnitSnapshot[] mappedUnits = units
            .Select(unit =>
            {
                InventoryRoomTopology room = roomsById[unit.RoomId];
                RoomSalesMode salesMode = configurations.GetValueOrDefault(unit.RoomId)?.SalesMode ?? RoomSalesMode.Unconfigured;
                InventoryUnitDto mapped = MapUnit(
                    unit,
                    salesMode,
                    property.Status == PropertyStatus.Active && room.Status == RoomStatus.Active,
                    drainedBedIds,
                    drainingRoomIds,
                    fullyDrainingRoomIds);
                return new InventoryUnitSnapshot(mapped, mapped.IsSellable);
            })
            .Where(unit => !sellableOnly || unit.IsSellable)
            .ToArray();
        return new(mappedUnits, [], isTruncated);
    }

    public async Task<RoomInventoryListResponse> ListRoomsAsync(
        Guid propertyId,
        PageRequest pageRequest,
        CancellationToken cancellationToken)
    {
        InventoryPropertyTopology? property = await dbContext.PropertyTopology
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == propertyId && item.IsKnown, cancellationToken)
            .ConfigureAwait(false);
        if (property is null)
        {
            return new([], pageRequest.Page, pageRequest.PageSize, false);
        }

        List<InventoryRoomTopology> rooms = await dbContext.RoomTopology
            .AsNoTracking()
            .Where(room => room.PropertyId == propertyId && room.IsKnown)
            .OrderBy(room => room.Name)
            .ThenBy(room => room.Id)
            .Skip(pageRequest.SkipCount)
            .Take(pageRequest.PageSize + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        bool hasMore = rooms.Count > pageRequest.PageSize;
        if (hasMore)
        {
            rooms.RemoveAt(rooms.Count - 1);
        }

        Guid[] roomIds = rooms.Select(room => room.Id).ToArray();
        Dictionary<Guid, RoomInventoryConfiguration> configurations = await dbContext.RoomConfigurations
            .AsNoTracking()
            .Where(configuration => roomIds.Contains(configuration.Id))
            .ToDictionaryAsync(configuration => configuration.Id, cancellationToken)
            .ConfigureAwait(false);
        List<InventoryUnit> units = await dbContext.InventoryUnits
            .AsNoTracking()
            .Where(unit => roomIds.Contains(unit.RoomId) && unit.IsKnown)
            .OrderBy(unit => unit.Kind)
            .ThenBy(unit => unit.Label)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        ILookup<Guid, InventoryUnit> unitsByRoom = units.ToLookup(unit => unit.RoomId);
        (HashSet<Guid> drainedBedIds, HashSet<Guid> drainingRoomIds, HashSet<Guid> fullyDrainingRoomIds) = await this
            .GetActiveDrainsAsync(propertyId, roomIds, cancellationToken)
            .ConfigureAwait(false);

        RoomInventoryDto[] result = rooms
            .Select(room => MapRoom(
                room,
                configurations.GetValueOrDefault(room.Id),
                unitsByRoom[room.Id],
                property.Status == PropertyStatus.Active,
                drainedBedIds,
                drainingRoomIds,
                fullyDrainingRoomIds))
            .ToArray();
        return new(result, pageRequest.Page, pageRequest.PageSize, hasMore);
    }

    public async Task<InventoryAvailabilityResponse> GetAvailabilityAsync(
        Guid propertyId,
        DateOnly arrival,
        DateOnly departure,
        CancellationToken cancellationToken)
    {
        InventoryPropertyTopology? property = await dbContext.PropertyTopology
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == propertyId && item.IsKnown, cancellationToken)
            .ConfigureAwait(false);
        if (property is null)
        {
            return new(propertyId, arrival, departure, []);
        }

        List<InventoryRoomTopology> rooms = await dbContext.RoomTopology
            .AsNoTracking()
            .Where(room => room.PropertyId == propertyId && room.IsKnown)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Guid[] roomIds = rooms.Select(room => room.Id).ToArray();
        Dictionary<Guid, RoomInventoryConfiguration> configurations = await dbContext.RoomConfigurations
            .AsNoTracking()
            .Where(configuration => roomIds.Contains(configuration.Id))
            .ToDictionaryAsync(configuration => configuration.Id, cancellationToken)
            .ConfigureAwait(false);
        List<InventoryUnit> units = await dbContext.InventoryUnits
            .AsNoTracking()
            .Where(unit => unit.PropertyId == propertyId && unit.IsKnown)
            .OrderBy(unit => unit.RoomId)
            .ThenBy(unit => unit.Kind)
            .ThenBy(unit => unit.Label)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        Guid[] unitIds = units.Select(unit => unit.Id).ToArray();
        var activeBlocks = await dbContext.ManualBlocks
            .AsNoTracking()
            .Where(block => unitIds.Contains(block.InventoryUnitId) &&
                            block.Status == ManualInventoryBlockState.Active &&
                            block.Arrival < departure &&
                            arrival < block.Departure)
            .Select(block => new { block.InventoryUnitId, block.Id })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        ILookup<Guid, Guid> blocksByUnit = activeBlocks.ToLookup(block => block.InventoryUnitId, block => block.Id);
        var activeAllocations = await dbContext.AllocationUnits
            .AsNoTracking()
            .Where(unit => unitIds.Contains(unit.Id))
            .Join(
                dbContext.Allocations.AsNoTracking().Where(allocation =>
                    allocation.Status == InventoryAllocationState.Active &&
                    allocation.Arrival < departure &&
                    arrival < allocation.Departure),
                unit => unit.AllocationId,
                allocation => allocation.Id,
                (unit, allocation) => new { InventoryUnitId = unit.Id, AllocationId = allocation.Id })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        ILookup<Guid, Guid> allocationsByUnit = activeAllocations.ToLookup(
            allocation => allocation.InventoryUnitId,
            allocation => allocation.AllocationId);
        Dictionary<Guid, InventoryRoomTopology> roomsById = rooms.ToDictionary(room => room.Id);
        ILookup<Guid, InventoryUnit> unitsByRoom = units.ToLookup(unit => unit.RoomId);
        (HashSet<Guid> drainedBedIds, HashSet<Guid> drainingRoomIds, HashSet<Guid> fullyDrainingRoomIds) = await this
            .GetActiveDrainsAsync(propertyId, roomIds, cancellationToken)
            .ConfigureAwait(false);

        InventoryUnitAvailabilityDto[] availability = units
            .Select(unit =>
            {
                bool roomActive = roomsById.TryGetValue(unit.RoomId, out InventoryRoomTopology? room) &&
                                  property.Status == PropertyStatus.Active &&
                                  room.Status == RoomStatus.Active;
                RoomSalesMode mode = configurations.GetValueOrDefault(unit.RoomId)?.SalesMode ?? RoomSalesMode.Unconfigured;
                InventoryUnitDto mapped = MapUnit(
                    unit,
                    mode,
                    roomActive,
                    drainedBedIds,
                    drainingRoomIds,
                    fullyDrainingRoomIds);
                Guid[] conflictUnitIds = unit.Kind == InventoryUnitKind.Room
                    ? unitsByRoom[unit.RoomId].Select(item => item.Id).ToArray()
                    : unitsByRoom[unit.RoomId]
                        .Where(item => item.Id == unit.Id || item.Kind == InventoryUnitKind.Room)
                        .Select(item => item.Id)
                        .ToArray();
                Guid[] blockingIds = conflictUnitIds
                    .SelectMany(conflictUnitId => blocksByUnit[conflictUnitId])
                    .Distinct()
                    .ToArray();
                Guid[] allocationIds = conflictUnitIds
                    .SelectMany(conflictUnitId => allocationsByUnit[conflictUnitId])
                    .Distinct()
                    .ToArray();
                return new InventoryUnitAvailabilityDto(
                    mapped,
                    mapped.IsSellable && blockingIds.Length == 0 && allocationIds.Length == 0,
                    blockingIds,
                    allocationIds);
            })
            .Where(item => item.Unit.IsSellable)
            .ToArray();

        return new(propertyId, arrival, departure, availability);
    }

    private static RoomInventoryDto MapRoom(
        InventoryRoomTopology room,
        RoomInventoryConfiguration? configuration,
        IEnumerable<InventoryUnit> units,
        bool propertyActive,
        IReadOnlySet<Guid>? drainedBedIds = null,
        IReadOnlySet<Guid>? drainingRoomIds = null,
        IReadOnlySet<Guid>? fullyDrainingRoomIds = null)
    {
        RoomSalesMode salesMode = configuration?.SalesMode ?? RoomSalesMode.Unconfigured;
        bool roomActive = propertyActive && room.Status == RoomStatus.Active;
        InventoryUnitDto[] mappedUnits = units
            .Select(unit => MapUnit(
                unit,
                salesMode,
                roomActive,
                drainedBedIds,
                drainingRoomIds,
                fullyDrainingRoomIds))
            .ToArray();

        return new(
            room.PropertyId,
            room.Id,
            room.Name,
            room.BuildingLabel,
            room.FloorLabel,
            salesMode switch
            {
                RoomSalesMode.RoomLevel => InventorySalesMode.RoomLevel,
                RoomSalesMode.BedLevel => InventorySalesMode.BedLevel,
                _ => InventorySalesMode.Unconfigured
            },
            configuration?.Version ?? 1,
            mappedUnits);
    }

    private static InventoryUnitDto MapUnit(
        InventoryUnit unit,
        RoomSalesMode salesMode,
        bool roomActive,
        IReadOnlySet<Guid>? drainedBedIds = null,
        IReadOnlySet<Guid>? drainingRoomIds = null,
        IReadOnlySet<Guid>? fullyDrainingRoomIds = null)
    {
        bool topologyActive = roomActive && unit.IsTopologyActive;
        bool sellable = topologyActive &&
                        ((unit.Kind == InventoryUnitKind.Room && salesMode == RoomSalesMode.RoomLevel) ||
                         (unit.Kind == InventoryUnitKind.Bed && salesMode == RoomSalesMode.BedLevel)) &&
                        (unit.Kind != InventoryUnitKind.Room || drainingRoomIds?.Contains(unit.RoomId) != true) &&
                        (unit.Kind != InventoryUnitKind.Bed ||
                         (drainedBedIds?.Contains(unit.Id) != true &&
                          fullyDrainingRoomIds?.Contains(unit.RoomId) != true));
        return new(
            unit.Id,
            unit.PropertyId,
            unit.RoomId,
            unit.BedId,
            unit.Kind,
            unit.Label,
            sellable,
            topologyActive);
    }

    private async Task<(HashSet<Guid> BedIds, HashSet<Guid> RoomIds, HashSet<Guid> FullyDrainingRoomIds)> GetActiveDrainsAsync(
        Guid propertyId,
        IReadOnlyCollection<Guid> roomIds,
        CancellationToken cancellationToken)
    {
        BedRetirementProcess[] drains = await dbContext.BedRetirements
            .AsNoTracking()
            .Where(process =>
                process.PropertyId == propertyId &&
                roomIds.Contains(process.RoomId) &&
                (process.State == InventoryRetirementProcessState.Draining ||
                 process.State == InventoryRetirementProcessState.FinalizationRequested ||
                 process.State == InventoryRetirementProcessState.FinalizedAwaitingTopology ||
                 process.State == InventoryRetirementProcessState.Rejected))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (BedRetirementProcess local in dbContext.BedRetirements.Local.Where(process =>
                     process.PropertyId == propertyId &&
                     roomIds.Contains(process.RoomId) &&
                     BedRetirementProcess.IsDrainActive(process.State)))
        {
            if (drains.All(process => process.Id != local.Id))
            {
                drains = [.. drains, local];
            }
        }

        RoomRetirementProcess[] roomDrains = await dbContext.RoomRetirements
            .AsNoTracking()
            .Where(process =>
                process.PropertyId == propertyId &&
                roomIds.Contains(process.RoomId) &&
                (process.State == InventoryRetirementProcessState.Draining ||
                 process.State == InventoryRetirementProcessState.FinalizationRequested ||
                 process.State == InventoryRetirementProcessState.FinalizedAwaitingTopology ||
                 process.State == InventoryRetirementProcessState.Rejected))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (RoomRetirementProcess local in dbContext.RoomRetirements.Local.Where(process =>
                     process.PropertyId == propertyId &&
                     roomIds.Contains(process.RoomId) &&
                     RoomRetirementProcess.IsDrainActive(process.State)))
        {
            if (roomDrains.All(process => process.Id != local.Id))
            {
                roomDrains = [.. roomDrains, local];
            }
        }

        HashSet<Guid> fullyDrainingRoomIds = roomDrains.Select(process => process.RoomId).ToHashSet();

        return (
            drains.Select(process => process.BedId).ToHashSet(),
            drains.Select(process => process.RoomId).Concat(fullyDrainingRoomIds).ToHashSet(),
            fullyDrainingRoomIds);
    }
}
