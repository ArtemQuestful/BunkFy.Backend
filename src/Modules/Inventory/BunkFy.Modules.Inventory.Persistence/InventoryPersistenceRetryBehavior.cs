namespace BunkFy.Modules.Inventory.Persistence;

using BunkFy.Modules.Inventory.Application;
using BunkFy.Modules.Inventory.Application.Commands;
using Gma.Framework.Cqrs;
using Gma.Framework.Persistence.EntityFrameworkCore;
using Gma.Framework.Results;
using Microsoft.EntityFrameworkCore;

internal sealed class InventoryPersistenceRetryBehavior<TCommand, TResponse>
    : ICommandPipelineBehavior<TCommand, TResponse>
    where TCommand : ICommand<TResponse>
{
    private readonly InventoryDbContext dbContext;
    private readonly Func<DbUpdateException, bool>
        isUniqueConstraintViolation;

    public InventoryPersistenceRetryBehavior(InventoryDbContext dbContext)
        : this(
            dbContext,
            EfDatabaseExceptionClassifier.IsUniqueConstraintViolation)
    {
    }

    internal InventoryPersistenceRetryBehavior(
        InventoryDbContext dbContext,
        Func<DbUpdateException, bool> isUniqueConstraintViolation)
    {
        this.dbContext = dbContext ??
            throw new ArgumentNullException(nameof(dbContext));
        this.isUniqueConstraintViolation = isUniqueConstraintViolation ??
            throw new ArgumentNullException(nameof(isUniqueConstraintViolation));
    }

    public async Task<Result<TResponse>> HandleAsync(
        TCommand command,
        CommandNext<TResponse> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(next);

        if (command is not CreateManualInventoryBlockCommand and
            not ReleaseManualInventoryBlockCommand and
            not CreateManualInventoryBlockGroupCommand and
            not ReplaceManualInventoryBlockGroupCommand and
            not ReleaseManualInventoryBlockGroupCommand)
        {
            return await next().ConfigureAwait(false);
        }

        try
        {
            return await next().ConfigureAwait(false);
        }
        catch (OptimisticConcurrencyException)
        {
            this.dbContext.ChangeTracker.Clear();
            return await this.RetryOnceAsync(next).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            this.dbContext.ChangeTracker.Clear();
            return await this.RetryOnceAsync(next).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (
            this.isUniqueConstraintViolation(exception))
        {
            this.dbContext.ChangeTracker.Clear();
            return await this.RetryOnceAsync(next).ConfigureAwait(false);
        }
    }

    private async Task<Result<TResponse>> RetryOnceAsync(
        CommandNext<TResponse> next)
    {
        try
        {
            return await next().ConfigureAwait(false);
        }
        catch (OptimisticConcurrencyException)
        {
            return this.VersionConflict();
        }
        catch (DbUpdateConcurrencyException)
        {
            return this.VersionConflict();
        }
        catch (DbUpdateException exception) when (
            this.isUniqueConstraintViolation(exception))
        {
            return this.VersionConflict();
        }
    }

    private Result<TResponse> VersionConflict()
    {
        this.dbContext.ChangeTracker.Clear();
        return Result.Failure<TResponse>(
            InventoryApplicationErrors.VersionConflict);
    }
}
