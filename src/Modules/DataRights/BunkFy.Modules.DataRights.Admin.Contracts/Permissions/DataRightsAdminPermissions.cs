namespace BunkFy.Modules.DataRights.Admin.Contracts;

using BunkFy.Modules.DataRights.Contracts;
using Gma.Framework.Administration;

public static class DataRightsAdminPermissions
{
    public static readonly AdminPermission Manage = AdminPermission.Create(DataRightsAdminPermissionCodes.Manage);
    public static readonly AdminPermission TenantTerminationRead =
        AdminPermission.Create(
            DataRightsAdminPermissionCodes.TenantTerminationRead);
    public static readonly AdminPermission TenantTerminationRequest =
        AdminPermission.Create(
            DataRightsAdminPermissionCodes.TenantTerminationRequest);
    public static readonly AdminPermission TenantTerminationApprove =
        AdminPermission.Create(
            DataRightsAdminPermissionCodes.TenantTerminationApprove);
    public static readonly AdminPermission TenantTerminationExecute =
        AdminPermission.Create(
            DataRightsAdminPermissionCodes.TenantTerminationExecute);
    public static readonly AdminPermission TenantTerminationExportDownload =
        AdminPermission.Create(
            DataRightsAdminPermissionCodes.TenantTerminationExportDownload);
    public static readonly AdminPermission TenantTerminationExportConfirm =
        AdminPermission.Create(
            DataRightsAdminPermissionCodes.TenantTerminationExportConfirm);
    public static readonly AdminPermission TenantTerminationRetry =
        AdminPermission.Create(
            DataRightsAdminPermissionCodes.TenantTerminationRetry);
    public static readonly AdminPermission TenantTerminationCancel =
        AdminPermission.Create(
            DataRightsAdminPermissionCodes.TenantTerminationCancel);
    public static readonly AdminPermission TenantTerminationRecover =
        AdminPermission.Create(
            DataRightsAdminPermissionCodes.TenantTerminationRecover);
}
