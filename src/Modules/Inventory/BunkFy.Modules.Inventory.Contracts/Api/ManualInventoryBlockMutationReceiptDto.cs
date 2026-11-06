namespace BunkFy.Modules.Inventory.Contracts;

public sealed record ManualInventoryBlockMutationReceiptDto(
    Guid BlockId,
    Guid BlockGroupId,
    Guid PropertyId,
    ManualInventoryBlockStatus Status,
    long Version);

public sealed record ManualInventoryBlockGroupMutationReceiptDto(
    Guid BlockGroupId,
    Guid PropertyId,
    int AffectedBlockCount,
    ManualInventoryBlockGroupStatus? Status = null,
    long? Version = null,
    Guid? PreviousBlockGroupId = null,
    int? ReleasedBlockCount = null,
    int? CreatedBlockCount = null,
    int? TotalBlockCount = null,
    int? ActiveBlockCount = null,
    int? AlreadyReleasedBlockCount = null,
    string? MembershipDigest = null)
{
    public Guid ResultBlockGroupId => this.BlockGroupId;
    public int? ReleasedNowBlockCount => this.ReleasedBlockCount;
    public int? CreatedNowBlockCount => this.CreatedBlockCount;
}
