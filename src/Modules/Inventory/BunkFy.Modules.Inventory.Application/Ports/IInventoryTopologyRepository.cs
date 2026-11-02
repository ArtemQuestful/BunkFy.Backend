namespace BunkFy.Modules.Inventory.Application.Ports;

public interface IInventoryTopologyRepository
{
    Task<bool> ApplyPropertyAsync(InventoryPropertyTopologyWriteModel property, CancellationToken cancellationToken);
    Task<bool> ApplyRoomAsync(InventoryRoomTopologyWriteModel room, CancellationToken cancellationToken);
    Task<bool> ApplyBedAsync(InventoryBedTopologyWriteModel bed, CancellationToken cancellationToken);
    Task<InventoryRoomTopologySnapshot?> GetRoomAsync(Guid propertyId, Guid roomId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<InventoryUnitDefinitionSnapshot>> GetUnitDefinitionsAsync(
        Guid propertyId,
        Guid? roomId,
        Guid? inventoryUnitId,
        bool touchVersions,
        CancellationToken cancellationToken);
}
