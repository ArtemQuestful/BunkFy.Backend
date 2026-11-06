namespace BunkFy.Modules.Inventory.Persistence.Repositories;

using BunkFy.Modules.Inventory.Application.Ports;
using Gma.Framework.Naming;
using Microsoft.EntityFrameworkCore;

internal sealed class InventoryAllocationAmendmentDecisionRepository(InventoryDbContext dbContext)
    : IInventoryAllocationAmendmentDecisionRepository
{
    public async Task<InventoryAllocationAmendmentDecisionRecord?> GetAsync(
        Guid amendmentRequestId,
        CancellationToken cancellationToken)
    {
        string scopeId = this.RequireCurrentScope();
        if (amendmentRequestId == Guid.Empty)
        {
            return null;
        }

        InventoryAllocationAmendmentDecision? decision = await dbContext.AllocationAmendmentDecisions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.ScopeId == scopeId && item.Id == amendmentRequestId,
                cancellationToken)
            .ConfigureAwait(false);
        return decision?.ToRecord();
    }

    public Task AddAsync(
        InventoryAllocationAmendmentDecisionRecord decision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decision);
        cancellationToken.ThrowIfCancellationRequested();
        string scopeId = this.RequireCurrentScope();
        if (!TenantIds.TryNormalize(decision.ScopeId, out string? canonicalScopeId) ||
            !string.Equals(canonicalScopeId, scopeId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "An Inventory allocation amendment decision requires the active canonical scope.");
        }

        dbContext.AllocationAmendmentDecisions.Add(new InventoryAllocationAmendmentDecision(decision));
        return Task.CompletedTask;
    }

    private string RequireCurrentScope()
    {
        if (!dbContext.ScopeFilterEnabled ||
            !TenantIds.TryNormalize(
                dbContext.CurrentScopeId,
                out string? canonicalScopeId) ||
            !string.Equals(
                canonicalScopeId,
                dbContext.CurrentScopeId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "An Inventory allocation amendment decision requires an active canonical scope.");
        }

        return canonicalScopeId;
    }
}
