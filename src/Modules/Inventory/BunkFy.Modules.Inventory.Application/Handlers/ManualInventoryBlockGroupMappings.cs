namespace BunkFy.Modules.Inventory.Application.Handlers;

using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;

internal static class ManualInventoryBlockGroupMappings
{
    public static ManualInventoryBlockGroupStatus ToContractStatus(
        this ManualInventoryBlockGroupState state) => state switch
        {
            ManualInventoryBlockGroupState.Active => ManualInventoryBlockGroupStatus.Active,
            ManualInventoryBlockGroupState.PartiallyReleased => ManualInventoryBlockGroupStatus.PartiallyReleased,
            ManualInventoryBlockGroupState.Released => ManualInventoryBlockGroupStatus.Released,
            ManualInventoryBlockGroupState.Replaced => ManualInventoryBlockGroupStatus.Replaced,
            _ => ManualInventoryBlockGroupStatus.Unknown
        };

    public static InventoryBlockTarget ToContractTarget(
        this ManualInventoryBlockGroup group) => new(
            group.TargetKind switch
            {
                ManualInventoryBlockGroupTargetKind.Property => InventoryBlockTargetKind.Property,
                ManualInventoryBlockGroupTargetKind.Building => InventoryBlockTargetKind.Building,
                ManualInventoryBlockGroupTargetKind.Floor => InventoryBlockTargetKind.Floor,
                ManualInventoryBlockGroupTargetKind.Room => InventoryBlockTargetKind.Room,
                ManualInventoryBlockGroupTargetKind.Unit => InventoryBlockTargetKind.Unit,
                _ => InventoryBlockTargetKind.Unspecified
            },
            group.BuildingLabel,
            group.FloorLabel,
            group.RoomId,
            group.InventoryUnitId);

    public static ManualInventoryBlockGroupMutationReceiptDto ToReleaseReceipt(
        this ManualInventoryBlockGroup group,
        int releasedNowBlockCount,
        int alreadyReleasedBlockCount) => new(
            group.Id,
            group.PropertyId,
            releasedNowBlockCount,
            group.State.ToContractStatus(),
            group.Version,
            PreviousBlockGroupId: null,
            ReleasedBlockCount: releasedNowBlockCount,
            CreatedBlockCount: 0,
            TotalBlockCount: group.InitialBlockCount,
            ActiveBlockCount: group.ActiveBlockCount,
            AlreadyReleasedBlockCount: alreadyReleasedBlockCount,
            MembershipDigest: group.MembershipDigest);
}
