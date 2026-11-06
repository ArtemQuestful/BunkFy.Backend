namespace BunkFy.Modules.Workspaces.Application.Ports;

using BunkFy.Modules.Workspaces.Domain;

public interface IWorkspaceStaffOnboardingRetentionExecutionRepository
{
    Task<WorkspaceStaffOnboardingRetentionExecution?> GetAsync(
        Guid executionId,
        CancellationToken cancellationToken);

    Task AddAsync(
        WorkspaceStaffOnboardingRetentionExecution execution,
        CancellationToken cancellationToken);
}
