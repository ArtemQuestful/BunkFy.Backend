namespace BunkFy.Modules.Workspaces.Domain;

using Gma.Framework.Results;

public static class WorkspaceStaffIdentityAnchorSweepErrors
{
    public static readonly Error Invalid = new(
        "Workspaces.IdentityAnchorSweepInvalid",
        "The identity-anchor sweep checkpoint is invalid.");

    public static readonly Error CheckpointConflict = new(
        "Workspaces.IdentityAnchorSweepCheckpointConflict",
        "The identity-anchor sweep checkpoint changed concurrently or the replay does not match.");
}
