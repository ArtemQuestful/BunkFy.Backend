namespace BunkFy.Modules.Workspaces.Persistence.Repositories;

using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Domain;
using Microsoft.EntityFrameworkCore;

internal sealed class WorkspaceStaffOnboardingRecoveryReceiptRepository(
    WorkspacesDbContext dbContext)
    : IWorkspaceStaffOnboardingRecoveryReceiptRepository
{
    public Task<WorkspaceStaffOnboardingRecoveryReceipt?> FindByOperationAsync(
        Guid operationId,
        CancellationToken cancellationToken) =>
        dbContext.StaffOnboardingRecoveryReceipts
            .AsNoTracking()
            .SingleOrDefaultAsync(
                receipt => receipt.OperationId == operationId,
                cancellationToken);

    public Task<WorkspaceStaffOnboardingRecoveryReceipt?> FindLatestAsync(
        Guid applicationId,
        CancellationToken cancellationToken) =>
        dbContext.StaffOnboardingRecoveryReceipts
            .AsNoTracking()
            .Where(receipt => receipt.ApplicationId == applicationId)
            .OrderByDescending(receipt => receipt.RecordedAtUtc)
            .ThenByDescending(receipt => receipt.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public Task AddAsync(
        WorkspaceStaffOnboardingRecoveryReceipt receipt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        dbContext.StaffOnboardingRecoveryReceipts.Add(receipt);
        return Task.CompletedTask;
    }
}
