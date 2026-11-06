namespace BunkFy.Modules.Workspaces.Application;

using Gma.Framework.Results;

public static class WorkspaceStaffIdentityAnchorSweepErrors
{
    public static readonly Error InvalidTask = new(
        "Workspaces.IdentityAnchorSweepTaskInvalid",
        "The identity-anchor sweep task coordinates are invalid.");

    public static readonly Error StaffBatchInvalid = new(
        "Workspaces.IdentityAnchorSweepStaffBatchInvalid",
        "Staff returned an invalid identity-anchor outcome batch.");
}
