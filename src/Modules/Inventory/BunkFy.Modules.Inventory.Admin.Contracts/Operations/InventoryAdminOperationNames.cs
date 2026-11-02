namespace BunkFy.Modules.Inventory.Admin.Contracts;

public static class InventoryAdminOperationNames
{
    public const string RoomsList = "inventory.rooms.list";
    public const string RoomsConfigure = "inventory.rooms.configure";
    public const string AvailabilityRead = "inventory.availability.read";
    public const string BlocksList = "inventory.blocks.list";
    public const string BlocksCreate = "inventory.blocks.create";
    public const string BlocksRelease = "inventory.blocks.release";
    public const string BlockGroupsPreview = "inventory.block-groups.preview";
    public const string BlockGroupsList = "inventory.block-groups.list";
    public const string BlockGroupsGet = "inventory.block-groups.get";
    public const string BlockGroupMembersList = "inventory.block-groups.members.list";
    public const string BlockGroupsCreate = "inventory.block-groups.create";
    public const string BlockGroupsReplace = "inventory.block-groups.replace";
    public const string BlockGroupsRelease = "inventory.block-groups.release";
    public const string BlockGroupCreateOperationsGet = "inventory.block-group-create-operations.get";
    public const string BlockGroupOperationsGet = "inventory.block-group-operations.get";
    public const string BedRetirementsGet = "inventory.bed-retirements.get";
    public const string BedRetirementsRequest = "inventory.bed-retirements.request";
    public const string BedRetirementsRetry = "inventory.bed-retirements.retry";
    public const string BedRetirementsCancel = "inventory.bed-retirements.cancel";
    public const string RoomRetirementsGet = "inventory.room-retirements.get";
    public const string RoomRetirementsRequest = "inventory.room-retirements.request";
    public const string RoomRetirementsRetry = "inventory.room-retirements.retry";
    public const string RoomRetirementsCancel = "inventory.room-retirements.cancel";
}
