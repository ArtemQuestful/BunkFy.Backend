namespace BunkFy.Modules.Workspaces.Admin.Contracts;

public static class WorkspacesAdminOperationNames
{
    public const string AccessBootstrapStatus = "workspaces.access-bootstrap.status";
    public const string AccessBootstrapRun = "workspaces.access-bootstrap.run";
    public const string StaffAccessList = "workspaces.staff-access.list";
    public const string StaffAccessRetry = "workspaces.staff-access.retry";
    public const string IdentityAnchorsStatus =
        "workspaces.identity-anchors.status";
    public const string IdentityAnchorsReconcile =
        "workspaces.identity-anchors.reconcile";
    public const string IdentityAnchorsHistoricalNoProvisionReview =
        "workspaces.identity-anchors.historical-no-provision.review";
}
