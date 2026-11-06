namespace BunkFy.Modules.Workspaces.Application.Ports;

using BunkFy.Modules.Workspaces.Application.Models;
using Gma.Framework.Results;

public interface IWorkspaceStaffIdentityAnchorSweepRepository
{
    Task<Result<WorkspaceStaffIdentityAnchorSweepPage>> PreparePageAsync(
        string scopeId,
        Guid checkpointId,
        Guid cycleId,
        Guid emptyAdvanceId,
        Guid runId,
        int batchSize,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);

    Task<Result> AdvanceAsync(
        WorkspaceStaffIdentityAnchorSweepAdvance advance,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);

    Task<WorkspaceStaffIdentityAnchorSweepStatus> GetStatusAsync(
        string scopeId,
        CancellationToken cancellationToken);

    IAsyncEnumerable<string> StreamScheduleScopeIdsAsync(
        CancellationToken cancellationToken);
}
