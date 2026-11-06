namespace BunkFy.Modules.Workspaces.Application.Ports;

using BunkFy.Modules.Workspaces.Domain;

public interface IWorkspaceStaffHistoricalNoProvisionReceiptRepository
{
    Task<WorkspaceStaffHistoricalNoProvisionReceipt?> FindByOperationIdAsync(
        Guid operationId,
        CancellationToken cancellationToken);

    Task<WorkspaceStaffHistoricalNoProvisionReceipt?> FindByApplicationIdAsync(
        Guid applicationId,
        CancellationToken cancellationToken);

    Task AddAsync(
        WorkspaceStaffHistoricalNoProvisionReceipt receipt,
        CancellationToken cancellationToken);
}
