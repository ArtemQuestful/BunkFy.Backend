namespace BunkFy.Modules.Inventory.Application.Ports;

using BunkFy.Modules.Inventory.Contracts;

public interface IInventoryRetirementContextReadRepository
{
    Task<InventoryRetirementTargetSnapshot?> GetTargetAsync(
        Guid propertyId,
        Guid roomId,
        Guid? bedId,
        CancellationToken cancellationToken);
}

public sealed record InventoryRetirementTargetSnapshot(
    bool IsTopologyActive,
    InventoryRetirementProcessSummaryDto? Process,
    bool ParentRoomRetirementActive);
