namespace BunkFy.Modules.Workspaces.Application.Commands;

using BunkFy.Modules.Workspaces.Application.Models;
using Gma.Framework.Cqrs;

public sealed record PrepareWorkspaceStaffIdentityAnchorSweepPageCommand(
    Guid CheckpointId,
    Guid CycleId,
    Guid EmptyAdvanceId,
    Guid RunId,
    int BatchSize)
    : ITransactionalCommand<WorkspaceStaffIdentityAnchorSweepPage>;
