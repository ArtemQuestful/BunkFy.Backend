namespace BunkFy.Modules.Inventory.Contracts;

public sealed record ManualInventoryBlockGroupPreviewMemberDto(
    Guid InventoryUnitId,
    Guid RoomId,
    string RoomName,
    string? UnitLabel);
