namespace BunkFy.Modules.Workspaces.Application.Queries;

using Gma.Framework.Cqrs;

public sealed record GetWorkspaceStaffIdentityAnchorCutoverStatusQuery(
    WorkspaceStaffIdentityAnchorOwnerManifest? OwnerManifest)
    : IQuery<WorkspaceStaffIdentityAnchorCutoverStatus>;
