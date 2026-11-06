namespace BunkFy.Modules.Workspaces.Persistence.Repositories;

using BunkFy.Modules.Workspaces.Application.Ports;
using Gma.Framework.Results;
using Microsoft.EntityFrameworkCore.Storage;

internal sealed class WorkspaceIdentityAnchorCutoverExecutionBoundary(
    WorkspacesDbContext dbContext)
    : IWorkspaceIdentityAnchorCutoverExecutionBoundary
{
    public async Task<Result<T>> ExecuteAsync<T>(
        Func<CancellationToken, Task<Result<T>>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (dbContext.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException(
                "The identity-anchor cutover boundary must own its transaction.");
        }

        IDbContextTransaction transaction = await dbContext
            .Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        Result<T>? operationResult = null;
        bool rollbackCompleted = false;
        try
        {
            operationResult = await operation(cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidOperationException(
                    "The identity-anchor cutover read boundary received no operation result.");
            await transaction.RollbackAsync(CancellationToken.None)
                .ConfigureAwait(false);
            rollbackCompleted = true;
        }
        catch
        {
            if (!rollbackCompleted)
            {
                await transaction.RollbackAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                rollbackCompleted = true;
            }

            throw;
        }
        finally
        {
            try
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
            catch when (rollbackCompleted && operationResult is not null)
            {
                // Rollback conclusively released the transaction-scoped tenant
                // lock. A later disposal failure cannot make Staff apply
                // transactional with this read boundary.
            }
        }

        return operationResult;
    }
}
