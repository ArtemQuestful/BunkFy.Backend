namespace BunkFy.Modules.Inventory.Contracts;

public static class InventoryAdminPermissionCodes
{
    public const string Read = InventoryModuleMetadata.Name + ".read";
    public const string Configure = InventoryModuleMetadata.Name + ".configure";
    public const string BlocksManage = InventoryModuleMetadata.Name + ".blocks.manage";
    public const string BlockGroupsManage = InventoryModuleMetadata.Name + ".block-groups.manage";
    public const string Retire = InventoryModuleMetadata.Name + ".retire";
}
