namespace BunkFy.Modules.Workspaces.Persistence.Repositories;

using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Domain;
using Microsoft.EntityFrameworkCore;

internal sealed class WorkspaceStaffOnboardingRetentionExecutionRepository(
    WorkspacesDbContext dbContext)
    : IWorkspaceStaffOnboardingRetentionExecutionRepository
{
    public Task<WorkspaceStaffOnboardingRetentionExecution?> GetAsync(
        Guid executionId,
        CancellationToken cancellationToken) =>
        dbContext.StaffOnboardingRetentionExecutions.SingleOrDefaultAsync(
            execution => execution.Id == executionId,
            cancellationToken);

    public Task AddAsync(
        WorkspaceStaffOnboardingRetentionExecution execution,
        CancellationToken cancellationToken)
    {
        dbContext.StaffOnboardingRetentionExecutions.Add(execution);
        return Task.CompletedTask;
    }
}
