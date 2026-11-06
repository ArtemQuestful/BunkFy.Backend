namespace BunkFy.Modules.Workspaces.Domain;

using Gma.Framework.Results;

public static class WorkspaceStaffHistoricalNoProvisionErrors
{
    public static readonly Error ReceiptInvalid = new(
        "Workspaces.StaffHistoricalNoProvisionReceiptInvalid",
        "The historical no-provision receipt is invalid.");
}
