namespace BunkFy.Modules.Workspaces.Application.Queries;

using BunkFy.Modules.Workspaces.Application.Models;
using Gma.Framework.Cqrs;

public sealed record GetWorkspaceStaffIdentityAnchorSweepStatusQuery
    : IQuery<WorkspaceStaffIdentityAnchorSweepStatus>;
