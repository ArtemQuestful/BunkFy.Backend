namespace BunkFy.Modules.Workspaces.Persistence.Repositories;

using BunkFy.Modules.Workspaces.Application.Ports;
using Gma.Framework.Naming;
using Gma.Framework.Persistence.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

internal sealed class WorkspaceStaffOnboardingRetentionExecutionLock(
    WorkspacesDbContext dbContext)
    : IWorkspaceStaffOnboardingRetentionExecutionLock
{
    private const string ResourcePrefix =
        "bunkfy:workspaces:staff-onboarding-retention-execution:";

    public async Task AcquireAsync(
        string tenantId,
        Guid executionId,
        CancellationToken cancellationToken)
    {
        if (!TenantIds.TryNormalize(tenantId, out string? normalizedTenantId) ||
            !TenantIds.TryNormalize(
                dbContext.CurrentScopeId,
                out string? currentTenantId) ||
            !string.Equals(
                normalizedTenantId,
                currentTenantId,
                StringComparison.Ordinal) ||
            executionId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "A Workspaces retention execution lock requires the current tenant coordinate.");
        }

        if (!dbContext.Database.IsRelational())
        {
            return;
        }

        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "A Workspaces retention execution lock requires an active database transaction.");
        }

        await dbContext.AcquireOperationalMutationAdmissionAsync(cancellationToken)
            .ConfigureAwait(false);
        await EfTransactionKeyLock.AcquireAsync(
            dbContext,
            ResourcePrefix + normalizedTenantId + ':' + executionId.ToString("N"),
            EfTransactionKeyLockMode.Exclusive,
            cancellationToken).ConfigureAwait(false);
    }
}
