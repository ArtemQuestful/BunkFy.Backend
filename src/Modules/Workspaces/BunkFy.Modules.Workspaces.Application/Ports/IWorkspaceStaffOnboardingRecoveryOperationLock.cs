namespace BunkFy.Modules.Workspaces.Application.Ports;

public interface IWorkspaceStaffOnboardingRecoveryOperationLock
{
    Task AcquireAsync(
        Guid operationId,
        CancellationToken cancellationToken);
}
