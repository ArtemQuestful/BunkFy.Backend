namespace BunkFy.Modules.Workspaces.Application.Ports;

using BunkFy.Modules.Workspaces.Domain;

public interface IWorkspaceStaffOnboardingRecoveryReceiptRepository
{
    Task<WorkspaceStaffOnboardingRecoveryReceipt?> FindByOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken);

    Task<WorkspaceStaffOnboardingRecoveryReceipt?> FindLatestAsync(
        Guid applicationId,
        CancellationToken cancellationToken);

    Task AddAsync(
        WorkspaceStaffOnboardingRecoveryReceipt receipt,
        CancellationToken cancellationToken);
}
