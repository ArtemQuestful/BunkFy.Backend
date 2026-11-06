namespace BunkFy.Modules.Workspaces.Application.Commands;

using BunkFy.Modules.Workspaces.Application.Models;
using Gma.Framework.Cqrs;

public sealed record ReconcileWorkspaceStaffIdentityAnchorSweepCandidateCommand(
    Guid ApplicationId)
    : ITransactionalCommand<WorkspaceStaffIdentityAnchorSweepCandidateResult>;
