namespace BunkFy.Modules.Workspaces.Persistence.Repositories;

using BunkFy.Modules.Workspaces.Application.Ports;
using Gma.Framework.Naming;
using Gma.Framework.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

internal sealed class WorkspaceStaffOnboardingRecoveryOperationLock(
    WorkspacesDbContext dbContext)
    : IWorkspaceStaffOnboardingRecoveryOperationLock
{
    private const string ResourcePrefix =
        "bunkfy:workspaces:staff-onboarding-recovery:";

    public async Task AcquireAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException(
                "A recovery operation lock requires an operation identifier.",
                nameof(operationId));
        }

        await dbContext.AcquireOperationalMutationAdmissionAsync(
            cancellationToken).ConfigureAwait(false);
        if (!dbContext.Database.IsRelational())
        {
            return;
        }

        if (dbContext.Database.CurrentTransaction is null ||
            !TenantIds.TryNormalize(
                dbContext.CurrentScopeId,
                out string? tenantId))
        {
            throw new InvalidOperationException(
                "A recovery operation lock requires a tenant transaction.");
        }

        await EfTransactionKeyLock.AcquireAsync(
                dbContext,
                ResourcePrefix + tenantId + ':' + operationId.ToString("N"),
                EfTransactionKeyLockMode.Exclusive,
                cancellationToken)
            .ConfigureAwait(false);
    }
}
