namespace BunkFy.Modules.Inventory.Domain.Aggregates;

using BunkFy.Modules.Inventory.Domain.Errors;
using Gma.Framework.Domain.Models;
using Gma.Framework.Results;

public sealed class ManualInventoryBlockGroup : ScopedAggregateRoot<Guid>
{
    public const int MaximumMemberCount = 500;
    public const int SelectionDigestLength = 64;
    public const int CurrentMembershipDigestVersion = 1;
    public const int ActorIdMaxLength = 200;
    public const int TargetLabelMaxLength = 128;

    private ManualInventoryBlockGroup() { }

    private ManualInventoryBlockGroup(
        Guid id,
        string scopeId,
        Guid propertyId,
        ManualInventoryBlockGroupTargetKind targetKind,
        string? buildingLabel,
        string? floorLabel,
        Guid? roomId,
        Guid? inventoryUnitId,
        DateOnly arrival,
        DateOnly departure,
        string reason,
        string? selectionDigest,
        string membershipDigest,
        int membershipDigestVersion,
        int initialBlockCount,
        Guid? replacesGroupId,
        DateTimeOffset nowUtc,
        string? actorId)
        : base(id, scopeId)
    {
        this.PropertyId = propertyId;
        this.TargetKind = targetKind;
        this.BuildingLabel = buildingLabel;
        this.FloorLabel = floorLabel;
        this.RoomId = roomId;
        this.InventoryUnitId = inventoryUnitId;
        this.Arrival = arrival;
        this.Departure = departure;
        this.Reason = reason;
        this.SelectionDigest = selectionDigest;
        this.MembershipDigest = membershipDigest;
        this.MembershipDigestVersion = membershipDigestVersion;
        this.InitialBlockCount = initialBlockCount;
        this.ActiveBlockCount = initialBlockCount;
        this.ReplacesGroupId = replacesGroupId;
        this.CreatedAtUtc = nowUtc;
        this.CreatedByActorId = actorId;
        this.LastModifiedByActorId = actorId;
    }

    public Guid PropertyId { get; private set; }
    public ManualInventoryBlockGroupTargetKind TargetKind { get; private set; }
    public string? BuildingLabel { get; private set; }
    public string? FloorLabel { get; private set; }
    public Guid? RoomId { get; private set; }
    public Guid? InventoryUnitId { get; private set; }
    public DateOnly Arrival { get; private set; }
    public DateOnly Departure { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public string? SelectionDigest { get; private set; }
    public string MembershipDigest { get; private set; } = string.Empty;
    public int MembershipDigestVersion { get; private set; } = CurrentMembershipDigestVersion;
    public int InitialBlockCount { get; private set; }
    public int ActiveBlockCount { get; private set; }
    public ManualInventoryBlockGroupState State { get; private set; } = ManualInventoryBlockGroupState.Active;
    public long Version { get; private set; } = 1;
    public Guid? ReplacesGroupId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? UpdatedAtUtc { get; private set; }
    public DateTimeOffset? ReleasedAtUtc { get; private set; }
    public string? CreatedByActorId { get; private set; }
    public string? LastModifiedByActorId { get; private set; }

    public static Result<ManualInventoryBlockGroup> Create(
        Guid id,
        string scopeId,
        Guid propertyId,
        ManualInventoryBlockGroupTargetKind targetKind,
        string? buildingLabel,
        string? floorLabel,
        Guid? roomId,
        Guid? inventoryUnitId,
        DateOnly arrival,
        DateOnly departure,
        string reason,
        string? selectionDigest,
        string membershipDigest,
        int membershipDigestVersion,
        int initialBlockCount,
        Guid? replacesGroupId,
        DateTimeOffset nowUtc,
        string? actorId = null)
    {
        string normalizedScopeId = scopeId?.Trim() ?? string.Empty;
        string normalizedReason = reason?.Trim() ?? string.Empty;
        string? normalizedSelectionDigest = NormalizeDigest(selectionDigest);
        string normalizedMembershipDigest = NormalizeDigest(membershipDigest) ?? string.Empty;
        string? normalizedBuilding = NormalizeOptional(buildingLabel);
        string? normalizedFloor = NormalizeOptional(floorLabel);
        string? normalizedActor = NormalizeOptional(actorId);

        if (id == Guid.Empty || propertyId == Guid.Empty ||
            replacesGroupId == Guid.Empty || replacesGroupId == id)
        {
            return Result.Failure<ManualInventoryBlockGroup>(
                InventoryDomainErrors.BlockGroupIdentityInvalid);
        }

        if (normalizedScopeId.Length == 0)
        {
            return Result.Failure<ManualInventoryBlockGroup>(
                InventoryDomainErrors.BlockGroupIdentityInvalid);
        }

        if (!IsValidTarget(
                targetKind,
                normalizedBuilding,
                normalizedFloor,
                roomId,
                inventoryUnitId))
        {
            return Result.Failure<ManualInventoryBlockGroup>(
                InventoryDomainErrors.BlockGroupTargetInvalid);
        }

        if (normalizedSelectionDigest is null)
        {
            return Result.Failure<ManualInventoryBlockGroup>(
                InventoryDomainErrors.BlockGroupSelectionInvalid);
        }

        if (arrival >= departure)
        {
            return Result.Failure<ManualInventoryBlockGroup>(
                InventoryDomainErrors.StayRangeInvalid);
        }

        if (normalizedReason.Length is 0 or > ManualInventoryBlock.ReasonMaxLength ||
            ContainsControlCharacter(normalizedReason))
        {
            return Result.Failure<ManualInventoryBlockGroup>(
                InventoryDomainErrors.BlockReasonInvalid);
        }

        if ((normalizedSelectionDigest is not null && !IsSha256(normalizedSelectionDigest)) ||
            !IsSha256(normalizedMembershipDigest) ||
            membershipDigestVersion != CurrentMembershipDigestVersion ||
            initialBlockCount is < 1 or > MaximumMemberCount)
        {
            return Result.Failure<ManualInventoryBlockGroup>(
                InventoryDomainErrors.BlockGroupSelectionInvalid);
        }

        if (!IsValidActorId(normalizedActor))
        {
            return Result.Failure<ManualInventoryBlockGroup>(
                InventoryDomainErrors.BlockGroupActorInvalid);
        }

        if (nowUtc == default)
        {
            return Result.Failure<ManualInventoryBlockGroup>(
                InventoryDomainErrors.BlockGroupTimestampInvalid);
        }

        return Result.Success(new ManualInventoryBlockGroup(
            id,
            normalizedScopeId,
            propertyId,
            targetKind,
            normalizedBuilding,
            normalizedFloor,
            roomId,
            inventoryUnitId,
            arrival,
            departure,
            normalizedReason,
            normalizedSelectionDigest,
            normalizedMembershipDigest,
            membershipDigestVersion,
            initialBlockCount,
            replacesGroupId,
            nowUtc,
            normalizedActor));
    }

    public Result RecordMemberRelease(DateTimeOffset nowUtc, string? actorId = null)
    {
        if (this.State is ManualInventoryBlockGroupState.Released or
            ManualInventoryBlockGroupState.Replaced || this.ActiveBlockCount <= 0)
        {
            return Result.Failure(InventoryDomainErrors.BlockGroupAlreadyTerminal);
        }

        string? normalizedActor = NormalizeOptional(actorId);
        if (!IsValidActorId(normalizedActor))
        {
            return Result.Failure(InventoryDomainErrors.BlockGroupActorInvalid);
        }

        if (!this.IsTimestampMonotonic(nowUtc))
        {
            return Result.Failure(InventoryDomainErrors.BlockGroupTimestampInvalid);
        }

        this.ActiveBlockCount--;
        this.State = this.ActiveBlockCount == 0
            ? ManualInventoryBlockGroupState.Released
            : ManualInventoryBlockGroupState.PartiallyReleased;
        this.Version++;
        this.UpdatedAtUtc = nowUtc;
        this.ReleasedAtUtc = this.ActiveBlockCount == 0 ? nowUtc : null;
        this.LastModifiedByActorId = normalizedActor;
        return Result.Success();
    }

    public Result Release(
        long expectedVersion,
        int releasedBlockCount,
        DateTimeOffset nowUtc,
        string? actorId = null) => this.Terminalize(
            expectedVersion,
            releasedBlockCount,
            successorGroupId: null,
            nowUtc,
            actorId);

    public Result ReplaceWith(
        long expectedVersion,
        Guid successorGroupId,
        int releasedBlockCount,
        DateTimeOffset nowUtc,
        string? actorId = null)
    {
        if (successorGroupId == Guid.Empty || successorGroupId == this.Id)
        {
            return Result.Failure(InventoryDomainErrors.BlockGroupIdentityInvalid);
        }

        return this.Terminalize(
            expectedVersion,
            releasedBlockCount,
            successorGroupId,
            nowUtc,
            actorId);
    }

    public bool HasSameDefinition(
        ManualInventoryBlockGroupTargetKind targetKind,
        string? buildingLabel,
        string? floorLabel,
        Guid? roomId,
        Guid? inventoryUnitId,
        DateOnly arrival,
        DateOnly departure,
        string reason,
        string membershipDigest,
        int activeBlockCount) =>
        (this.State is ManualInventoryBlockGroupState.Active or
            ManualInventoryBlockGroupState.PartiallyReleased) &&
        this.TargetKind == targetKind &&
        string.Equals(this.BuildingLabel, NormalizeOptional(buildingLabel), StringComparison.Ordinal) &&
        string.Equals(this.FloorLabel, NormalizeOptional(floorLabel), StringComparison.Ordinal) &&
        this.RoomId == roomId &&
        this.InventoryUnitId == inventoryUnitId &&
        this.Arrival == arrival &&
        this.Departure == departure &&
        string.Equals(this.Reason, reason?.Trim(), StringComparison.Ordinal) &&
        string.Equals(this.MembershipDigest, NormalizeDigest(membershipDigest), StringComparison.Ordinal) &&
        this.ActiveBlockCount == activeBlockCount;

    private Result Terminalize(
        long expectedVersion,
        int releasedBlockCount,
        Guid? successorGroupId,
        DateTimeOffset nowUtc,
        string? actorId)
    {
        if (expectedVersion != this.Version)
        {
            return Result.Failure(InventoryDomainErrors.VersionConflict);
        }

        if (this.State is ManualInventoryBlockGroupState.Released or
            ManualInventoryBlockGroupState.Replaced)
        {
            return Result.Failure(InventoryDomainErrors.BlockGroupAlreadyTerminal);
        }

        if (releasedBlockCount != this.ActiveBlockCount || releasedBlockCount <= 0)
        {
            return Result.Failure(InventoryDomainErrors.BlockGroupMemberCountInvalid);
        }

        string? normalizedActor = NormalizeOptional(actorId);
        if (!IsValidActorId(normalizedActor))
        {
            return Result.Failure(InventoryDomainErrors.BlockGroupActorInvalid);
        }

        if (!this.IsTimestampMonotonic(nowUtc))
        {
            return Result.Failure(InventoryDomainErrors.BlockGroupTimestampInvalid);
        }

        this.ActiveBlockCount = 0;
        this.State = successorGroupId.HasValue
            ? ManualInventoryBlockGroupState.Replaced
            : ManualInventoryBlockGroupState.Released;
        this.Version++;
        this.UpdatedAtUtc = nowUtc;
        this.ReleasedAtUtc = nowUtc;
        this.LastModifiedByActorId = normalizedActor;
        return Result.Success();
    }

    private static bool IsValidTarget(
        ManualInventoryBlockGroupTargetKind targetKind,
        string? buildingLabel,
        string? floorLabel,
        Guid? roomId,
        Guid? inventoryUnitId) =>
        IsValidTargetLabel(buildingLabel) &&
        IsValidTargetLabel(floorLabel) &&
        targetKind switch
        {
            ManualInventoryBlockGroupTargetKind.Property =>
                buildingLabel is null && floorLabel is null && roomId is null && inventoryUnitId is null,
            ManualInventoryBlockGroupTargetKind.Building =>
                buildingLabel is not null && floorLabel is null && roomId is null && inventoryUnitId is null,
            ManualInventoryBlockGroupTargetKind.Floor =>
                floorLabel is not null && roomId is null && inventoryUnitId is null,
            ManualInventoryBlockGroupTargetKind.Room =>
                buildingLabel is null && floorLabel is null &&
                roomId is { } value && value != Guid.Empty &&
                inventoryUnitId is null,
            ManualInventoryBlockGroupTargetKind.Unit =>
                buildingLabel is null && floorLabel is null && roomId is null &&
                inventoryUnitId is { } value && value != Guid.Empty,
            _ => false
        };

    private static bool IsValidTargetLabel(string? value) =>
        value is null ||
        (value.Length <= TargetLabelMaxLength &&
         !ContainsControlCharacter(value));

    private static string? NormalizeOptional(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        return normalized.Length == 0 ? null : normalized;
    }

    public static bool IsValidActorId(string? actorId)
    {
        string? normalizedActor = NormalizeOptional(actorId);
        return normalizedActor is not null &&
            normalizedActor.Length <= ActorIdMaxLength &&
            !ContainsControlCharacter(normalizedActor);
    }

    private static bool ContainsControlCharacter(string value) =>
        value.Any(char.IsControl);

    private static bool IsSha256(string value) =>
        value.Length == SelectionDigestLength &&
        value.All(character =>
            character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private bool IsTimestampMonotonic(DateTimeOffset nowUtc) =>
        nowUtc != default &&
        nowUtc >= this.CreatedAtUtc &&
        (!this.UpdatedAtUtc.HasValue || nowUtc >= this.UpdatedAtUtc.Value);

    private static string? NormalizeDigest(string? value)
    {
        string normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized.Length == 0 ? null : normalized;
    }
}
