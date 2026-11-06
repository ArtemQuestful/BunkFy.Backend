namespace BunkFy.Modules.Inventory.Contracts;

public sealed record ManualInventoryBlockGroupMemberListResponse(
    IReadOnlyCollection<ManualInventoryBlockDto> Blocks,
    string? NextCursor,
    int PageSize);
