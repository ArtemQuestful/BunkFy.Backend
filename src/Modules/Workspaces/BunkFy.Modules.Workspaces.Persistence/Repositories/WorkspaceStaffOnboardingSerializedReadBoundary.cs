namespace BunkFy.Modules.Workspaces.Persistence.Repositories;

using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Application.Ports;
using Gma.Framework.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

internal sealed class WorkspaceStaffOnboardingSerializedReadBoundary(
    WorkspacesDbContext dbContext)
    : IWorkspaceStaffOnboardingSerializedReadBoundary
{
    public async Task<Result<T>> RunAsync<T>(
        Func<CancellationToken, Task<Result<T>>> read,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (!dbContext.Database.IsRelational())
        {
            return await this.RunAdmittedAsync(read, cancellationToken)
                .ConfigureAwait(false);
        }

        if (dbContext.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException(
                "The Staff-onboarding serialized read boundary cannot own a nested transaction.");
        }

        await using IDbContextTransaction transaction = await dbContext.Database
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await this.RunAdmittedAsync(read, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private async Task<Result<T>> RunAdmittedAsync<T>(
        Func<CancellationToken, Task<Result<T>>> read,
        CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.AcquireOperationalMutationAdmissionAsync(
                    cancellationToken).ConfigureAwait(false);
            return await read(cancellationToken).ConfigureAwait(false);
        }
        catch (WorkspaceOperationalMutationRejectedException)
        {
            return Result.Failure<T>(
                WorkspaceOperationalAdmissionErrors.ProcessingRestricted);
        }
    }
}
