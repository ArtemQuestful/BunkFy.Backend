namespace BunkFy.Modules.Inventory.Contracts;

public sealed record ManualInventoryBlockGroupSelectionPreviewDto(
    Guid PropertyId,
    InventoryBlockTarget Target,
    DateOnly Arrival,
    DateOnly Departure,
    ManualInventoryBlockGroupPreviewStatus Status,
    int MaximumAffectedBlockCount,
    int? AffectedBlockCount,
    int AtLeastAffectedBlockCount,
    bool ExceedsMaximumAffectedBlockCount,
    string? SelectionDigest,
    string? MembershipDigest,
    int MembershipDigestVersion,
    bool HasManualBlockConflict,
    bool HasActiveAllocationConflict,
    IReadOnlyCollection<ManualInventoryBlockGroupPreviewMemberDto> Members,
    bool HasMoreMembers,
    Guid? BlockGroupId,
    long? BlockGroupVersion,
    bool IsNoOpReplacement);
