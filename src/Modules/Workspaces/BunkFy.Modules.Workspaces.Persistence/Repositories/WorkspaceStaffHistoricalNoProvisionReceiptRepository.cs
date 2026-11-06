namespace BunkFy.Modules.Workspaces.Persistence.Repositories;

using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Domain;
using Microsoft.EntityFrameworkCore;

internal sealed class WorkspaceStaffHistoricalNoProvisionReceiptRepository(
    WorkspacesDbContext dbContext)
    : IWorkspaceStaffHistoricalNoProvisionReceiptRepository
{
    public Task<WorkspaceStaffHistoricalNoProvisionReceipt?>
        FindByOperationIdAsync(
            Guid operationId,
            CancellationToken cancellationToken) =>
        dbContext.StaffHistoricalNoProvisionReceipts
            .SingleOrDefaultAsync(
                receipt => receipt.OperationId == operationId,
                cancellationToken);

    public Task<WorkspaceStaffHistoricalNoProvisionReceipt?>
        FindByApplicationIdAsync(
            Guid applicationId,
            CancellationToken cancellationToken) =>
        dbContext.StaffHistoricalNoProvisionReceipts
            .SingleOrDefaultAsync(
                receipt => receipt.ApplicationId == applicationId,
                cancellationToken);

    public Task AddAsync(
        WorkspaceStaffHistoricalNoProvisionReceipt receipt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        dbContext.StaffHistoricalNoProvisionReceipts.Add(receipt);
        return Task.CompletedTask;
    }
}
