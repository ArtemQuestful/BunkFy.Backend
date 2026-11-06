namespace BunkFy.Modules.Inventory.Admin.Contracts;

using BunkFy.Modules.Inventory.Contracts;
using Gma.Framework.Administration;

public static class InventoryAdminPermissions
{
    public static readonly AdminPermission Read = AdminPermission.Create(InventoryAdminPermissionCodes.Read);
    public static readonly AdminPermission Configure = AdminPermission.Create(InventoryAdminPermissionCodes.Configure);
    public static readonly AdminPermission BlocksManage = AdminPermission.Create(InventoryAdminPermissionCodes.BlocksManage);
    public static readonly AdminPermission BlockGroupsManage = AdminPermission.Create(InventoryAdminPermissionCodes.BlockGroupsManage);
    public static readonly AdminPermission Retire = AdminPermission.Create(InventoryAdminPermissionCodes.Retire);
}
