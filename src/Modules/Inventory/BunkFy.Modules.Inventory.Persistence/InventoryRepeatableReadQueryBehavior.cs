namespace BunkFy.Modules.Inventory.Persistence;

using System.Data;
using BunkFy.Modules.Inventory.Application.Queries;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

internal sealed class InventoryRepeatableReadQueryBehavior<TQuery, TResponse>(
    InventoryDbContext dbContext)
    : IQueryPipelineBehavior<TQuery, TResponse>
    where TQuery : IQuery<TResponse>
{
    public async Task<Result<TResponse>> HandleAsync(
        TQuery query,
        QueryNext<TResponse> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(next);

        if (query is not PreviewManualInventoryBlockGroupQuery ||
            !dbContext.Database.IsRelational() ||
            dbContext.Database.CurrentTransaction is not null)
        {
            return await next().ConfigureAwait(false);
        }

        await using IDbContextTransaction transaction = await dbContext.Database
            .BeginTransactionAsync(
                IsolationLevel.RepeatableRead,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            Result<TResponse> result = await next().ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Preserve the query failure; rollback is best-effort after provider errors.
            }

            throw;
        }
    }
}
