namespace BunkFy.Modules.Inventory.Application.Ports;

public sealed record InventoryBlockTargetResolution(
    IReadOnlyCollection<InventoryUnitSnapshot> Units,
    IReadOnlyCollection<InventoryBlockSelectionCoordinate> SelectionCoordinates,
    bool IsTruncated)
{
    public int AtLeastUnitCount => this.Units.Count;
}

public sealed record InventoryBlockSelectionCoordinate(
    Guid InventoryUnitId,
    long PropertySourceVersion,
    long PropertyDetailsVersion,
    long PropertyAvailabilitySelectionVersion,
    int PropertyStatus,
    Guid RoomId,
    string RoomName,
    long RoomSourceVersion,
    long RoomDetailsVersion,
    int RoomStatus,
    int RoomSalesMode,
    long RoomConfigurationVersion,
    long RoomAvailabilityMutationVersion,
    long UnitSourceVersion,
    long UnitDetailsVersion,
    bool UnitTopologyActive,
    long UnitAvailabilityMutationVersion,
    IReadOnlyCollection<InventoryBlockSelectionRetirementCoordinate> Retirements);

public sealed record InventoryBlockSelectionRetirementCoordinate(
    Guid TopologyChangeId,
    int Kind,
    long Version,
    int State);
