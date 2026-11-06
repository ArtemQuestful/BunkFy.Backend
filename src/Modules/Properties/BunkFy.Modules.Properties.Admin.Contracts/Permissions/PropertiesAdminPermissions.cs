namespace BunkFy.Modules.Properties.Admin.Contracts;

using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Administration;

public static class PropertiesAdminPermissions
{
    public static readonly AdminPermission Read = AdminPermission.Create(PropertiesAdminPermissionCodes.Read);
    public static readonly AdminPermission PropertiesManage = AdminPermission.Create(PropertiesAdminPermissionCodes.PropertiesManage);
    public static readonly AdminPermission TimeZonesManage = AdminPermission.Create(PropertiesAdminPermissionCodes.TimeZonesManage);
    public static readonly AdminPermission RoomsManage = AdminPermission.Create(PropertiesAdminPermissionCodes.RoomsManage);
    public static readonly AdminPermission BedsManage = AdminPermission.Create(PropertiesAdminPermissionCodes.BedsManage);
}
