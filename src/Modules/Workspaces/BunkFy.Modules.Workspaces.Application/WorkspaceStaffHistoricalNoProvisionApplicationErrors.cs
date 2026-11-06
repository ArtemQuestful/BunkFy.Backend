namespace BunkFy.Modules.Workspaces.Application;

using Gma.Framework.Results;

public static class WorkspaceStaffHistoricalNoProvisionApplicationErrors
{
    public static readonly Error TenantRequired = new(
        "Workspaces.StaffHistoricalNoProvisionTenantRequired",
        "A canonical tenant scope is required.");

    public static readonly Error RequestInvalid = new(
        "Workspaces.StaffHistoricalNoProvisionRequestInvalid",
        "The historical no-provision review request is invalid.");

    public static readonly Error Conflict = new(
        "Workspaces.StaffHistoricalNoProvisionConflict",
        "The historical no-provision evidence conflicts with current authority.");

    public static readonly Error ExternalEvidenceUnavailable = new(
        "Workspaces.StaffHistoricalNoProvisionEvidenceUnavailable",
        "Historical no-provision authority evidence is unavailable.");
}
