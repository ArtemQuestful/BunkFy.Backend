namespace BunkFy.Modules.Workspaces.Admin.Contracts;

using Gma.Framework.Administration;

public static class WorkspacesAdminPermissions
{
    public static readonly AdminPermission AccessBootstrap =
        AdminPermission.Create("workspaces.access.bootstrap");
    public static readonly AdminPermission StaffAccessManage =
        AdminPermission.Create("workspaces.staff-access.manage");
    public static readonly AdminPermission IdentityAnchorsReconcile =
        AdminPermission.Create("workspaces.identity-anchors.reconcile");
    public static readonly AdminPermission
        IdentityAnchorsHistoricalNoProvisionReview =
        AdminPermission.Create(
            "workspaces.identity-anchors.historical-no-provision.review");
}
