namespace BunkFy.Modules.Workspaces.Application.Ports;

using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Application.Models;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Tasks;

public interface IWorkspaceStaffIdentityAnchorSweepTransactionDispatcher
{
    Task<Result<WorkspaceStaffIdentityAnchorSweepPage>> PreparePageAsync(
        TaskExecutionContext context,
        PrepareWorkspaceStaffIdentityAnchorSweepPageCommand command,
        CancellationToken cancellationToken);

    Task<Result<WorkspaceStaffIdentityAnchorSweepCandidateResult>>
        ReconcileCandidateAsync(
            TaskExecutionContext context,
            ReconcileWorkspaceStaffIdentityAnchorSweepCandidateCommand command,
            CancellationToken cancellationToken);

    Task<Result<Unit>> AdvanceAsync(
        TaskExecutionContext context,
        AdvanceWorkspaceStaffIdentityAnchorSweepCommand command,
        CancellationToken cancellationToken);
}
