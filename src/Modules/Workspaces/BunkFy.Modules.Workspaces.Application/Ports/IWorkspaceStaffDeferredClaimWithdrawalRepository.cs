namespace BunkFy.Modules.Workspaces.Application.Ports;

using BunkFy.Modules.Workspaces.Domain;

public interface IWorkspaceStaffDeferredClaimWithdrawalRepository
{
    Task<WorkspaceStaffDeferredClaimWithdrawal?> GetAsync(
        Guid claimId,
        CancellationToken cancellationToken);

    Task<bool> AnyBySourceAsync(
        Guid enrollmentLinkId,
        CancellationToken cancellationToken);

    Task AddAsync(
        WorkspaceStaffDeferredClaimWithdrawal withdrawal,
        CancellationToken cancellationToken);

    void Remove(WorkspaceStaffDeferredClaimWithdrawal withdrawal);

    Task<int> RemoveBySourceAsync(
        Guid enrollmentLinkId,
        CancellationToken cancellationToken);
}
