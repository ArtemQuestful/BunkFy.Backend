namespace Integration.Tests.Support;

using Microsoft.EntityFrameworkCore;

internal static class ModuleTransactionIntegrationTestData
{
    public static async Task ExecuteAsync(
        DbContext dbContext,
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(operation);

        if (dbContext.Database.CurrentTransaction is not null)
        {
            await operation(cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await operation(cancellationToken).ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task<TResult> ExecuteAsync<TResult>(
        DbContext dbContext,
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(operation);

        if (dbContext.Database.CurrentTransaction is not null)
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }

        await using var transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        TResult result = await operation(cancellationToken).ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken)
            .ConfigureAwait(false);
        return result;
    }
}
