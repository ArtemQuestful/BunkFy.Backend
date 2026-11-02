namespace BunkFy.Modules.Inventory.Contracts;

public sealed record ManualInventoryBlockGroupDto(
    Guid BlockGroupId,
    Guid PropertyId,
    InventoryBlockTarget Target,
    DateOnly Arrival,
    DateOnly Departure,
    string Reason,
    string? SelectionDigest,
    string MembershipDigest,
    int MembershipDigestVersion,
    int InitialBlockCount,
    int ActiveBlockCount,
    ManualInventoryBlockGroupStatus Status,
    long Version,
    Guid? ReplacesGroupId,
    Guid? ReplacedByGroupId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? UpdatedAtUtc,
    DateTimeOffset? ReleasedAtUtc,
    string? CreatedByActorId,
    string? LastModifiedByActorId);
