namespace BunkFy.Modules.Workspaces.Application.Ports;

public interface IWorkspaceStaffOnboardingRetentionExecutionLock
{
    Task AcquireAsync(
        string tenantId,
        Guid executionId,
        CancellationToken cancellationToken);
}
