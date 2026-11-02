namespace BunkFy.Modules.Inventory.Persistence.Repositories;

using BunkFy.Modules.Inventory.Application.Ports;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;

internal sealed class InventoryAvailabilitySelectionFence(
    InventoryDbContext dbContext,
    IScopeContext scopeContext)
    : IInventoryAvailabilitySelectionFence
{
    public async Task AcquireAsync(
        Guid propertyId,
        CancellationToken cancellationToken)
    {
        if (propertyId == Guid.Empty)
        {
            throw new ArgumentException(
                "An Inventory availability-selection fence requires a property id.",
                nameof(propertyId));
        }

        string? scopeId = scopeContext.ScopeId;
        if (!scopeContext.IsEnabled || string.IsNullOrWhiteSpace(scopeId))
        {
            throw new InvalidOperationException(
                "An Inventory availability-selection fence requires an active tenant scope.");
        }

        if (!dbContext.Database.IsRelational())
        {
            return;
        }

        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                "An Inventory availability-selection fence requires an active transaction.");
        }

        if (dbContext.Database.IsNpgsql())
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                SELECT 1
                FROM inventory.property_topology
                WHERE "ScopeId" = {scopeId}
                  AND "Id" = {propertyId}
                FOR UPDATE
                """,
                cancellationToken).ConfigureAwait(false);
        }
        else if (dbContext.Database.IsSqlServer())
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                SELECT 1
                FROM [inventory].[property_topology]
                    WITH (UPDLOCK, HOLDLOCK, ROWLOCK)
                WHERE [ScopeId] = {scopeId}
                  AND [Id] = {propertyId}
                """,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new InvalidOperationException(
                "The configured relational provider does not support the " +
                "Inventory availability-selection fence.");
        }

        // The first accepted topology event may create the property projection
        // later in this transaction. There is no previously approved selection
        // to protect while the durable fence row is absent; a concurrent first
        // insert is still serialized by the property primary key and retry path.
    }

    public async Task AdvanceAsync(
        Guid propertyId,
        CancellationToken cancellationToken)
    {
        InventoryPropertyTopology property = await this.GetRequiredAsync(
            propertyId,
            cancellationToken).ConfigureAwait(false);
        property.AdvanceAvailabilitySelection();
    }

    private async Task<InventoryPropertyTopology> GetRequiredAsync(
        Guid propertyId,
        CancellationToken cancellationToken) =>
        dbContext.PropertyTopology.Local.FirstOrDefault(property =>
            property.Id == propertyId) ??
        await dbContext.PropertyTopology.FirstOrDefaultAsync(
                property => property.Id == propertyId,
                cancellationToken)
            .ConfigureAwait(false) ??
        throw new InvalidOperationException(
            "Cannot fence Inventory availability selection for a missing property projection.");
}
