namespace BunkFy.Modules.Workspaces.Application.Commands;

using Gma.Framework.Cqrs;

public sealed record ReconcileWorkspaceStaffIdentityAnchorsCommand(
    string ExpectedSourceEvidenceSha256,
    string ExpectedAnchorStateSha256,
    WorkspaceStaffIdentityAnchorOwnerManifest OwnerManifest,
    string ExpectedOwnerManifestSha256,
    int BatchSize)
    : ICommand<WorkspaceStaffIdentityAnchorReconcileResult>;
