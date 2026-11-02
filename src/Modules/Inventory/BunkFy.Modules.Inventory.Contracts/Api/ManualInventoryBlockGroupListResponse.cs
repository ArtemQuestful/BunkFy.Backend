namespace BunkFy.Modules.Inventory.Contracts;

public sealed record ManualInventoryBlockGroupListResponse(
    IReadOnlyCollection<ManualInventoryBlockGroupDto> BlockGroups,
    int PageSize,
    string? NextCursor);
