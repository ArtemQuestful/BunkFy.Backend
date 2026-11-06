namespace BunkFy.Modules.Workspaces.Persistence.Repositories;

using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Domain;
using Microsoft.EntityFrameworkCore;

internal sealed class WorkspaceStaffDeferredClaimWithdrawalRepository(
    WorkspacesDbContext dbContext)
    : IWorkspaceStaffDeferredClaimWithdrawalRepository
{
    public Task<WorkspaceStaffDeferredClaimWithdrawal?> GetAsync(
        Guid claimId,
        CancellationToken cancellationToken) =>
        dbContext.StaffDeferredClaimWithdrawals.SingleOrDefaultAsync(
            withdrawal => withdrawal.Id == claimId,
            cancellationToken);

    public Task<bool> AnyBySourceAsync(
        Guid enrollmentLinkId,
        CancellationToken cancellationToken) =>
        dbContext.StaffDeferredClaimWithdrawals.AnyAsync(
            withdrawal => withdrawal.EnrollmentLinkId == enrollmentLinkId,
            cancellationToken);

    public async Task AddAsync(
        WorkspaceStaffDeferredClaimWithdrawal withdrawal,
        CancellationToken cancellationToken) =>
        await dbContext.StaffDeferredClaimWithdrawals
            .AddAsync(withdrawal, cancellationToken)
            .ConfigureAwait(false);

    public void Remove(WorkspaceStaffDeferredClaimWithdrawal withdrawal) =>
        dbContext.StaffDeferredClaimWithdrawals.Remove(withdrawal);

    public async Task<int> RemoveBySourceAsync(
        Guid enrollmentLinkId,
        CancellationToken cancellationToken)
    {
        WorkspaceStaffDeferredClaimWithdrawal[] tracked = dbContext
            .ChangeTracker
            .Entries<WorkspaceStaffDeferredClaimWithdrawal>()
            .Where(entry =>
                entry.State != EntityState.Detached &&
                entry.Entity.EnrollmentLinkId == enrollmentLinkId)
            .Select(entry => entry.Entity)
            .ToArray();
        Guid[] trackedClaimIds = tracked
            .Select(withdrawal => withdrawal.Id)
            .ToArray();
        IQueryable<WorkspaceStaffDeferredClaimWithdrawal> remaining =
            dbContext.StaffDeferredClaimWithdrawals.Where(withdrawal =>
                withdrawal.EnrollmentLinkId == enrollmentLinkId);
        if (trackedClaimIds.Length > 0)
        {
            remaining = remaining.Where(withdrawal =>
                !trackedClaimIds.Contains(withdrawal.Id));
        }

        int removed = await remaining.ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        dbContext.StaffDeferredClaimWithdrawals.RemoveRange(tracked);
        return removed + tracked.Length;
    }
}
