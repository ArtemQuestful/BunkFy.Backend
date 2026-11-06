namespace BunkFy.Modules.Inventory.Contracts.Stations;

public enum StationInventoryLabelsState { Current = 0, Incomplete = 1, Invalid = 2, Unavailable = 3 }
/// <summary>Version namespaces remain separate: unit availability, selling configuration, and each topology's source/details.</summary>
public sealed record StationInventoryLabel(Guid PropertyId, Guid InventoryUnitId, InventoryUnitKind Kind,
    Guid RoomId, string RoomName, Guid? BedId, string? BedLabel, long ConfigurationVersion,
    long UnitVersion, long UnitSourceVersion, long RoomSourceVersion, long? BedSourceVersion);
public sealed record StationInventoryLabels(StationInventoryLabelsState State, IReadOnlyList<StationInventoryLabel> Items);
public interface IStationInventoryLabelReader
{
    Task<StationInventoryLabels> ReadAsync(Guid propertyId, IReadOnlyCollection<Guid> inventoryUnitIds,
        CancellationToken cancellationToken = default);
}
