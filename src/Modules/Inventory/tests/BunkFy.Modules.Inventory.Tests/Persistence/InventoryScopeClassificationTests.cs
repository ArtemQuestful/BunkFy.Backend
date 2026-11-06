namespace BunkFy.Modules.Inventory.Tests.Persistence;

using BunkFy.Modules.Inventory.Persistence;
using Gma.Framework.Domain;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

[Trait("Category", "Unit")]
public sealed class InventoryScopeClassificationTests
{
    [Fact]
    public void Every_inventory_scope_shaped_entity_is_explicitly_scoped()
    {
        using InventoryDbContext context = CreateDbContext();

        string[] unclassified = context.Model.GetEntityTypes()
            .Where(entity =>
                !entity.IsOwned() &&
                entity.ClrType.Namespace?.StartsWith(
                    "BunkFy.Modules.Inventory.",
                    StringComparison.Ordinal) == true &&
                entity.FindProperty(nameof(IScopedEntity.ScopeId)) is not null &&
                !typeof(IScopedEntity).IsAssignableFrom(entity.ClrType))
            .Select(entity => entity.ClrType.FullName ?? entity.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(unclassified);
    }

    [Fact]
    public void Allocation_amendment_decision_is_tenant_filtered()
    {
        using InventoryDbContext context = CreateDbContext();
        IEntityType decision = context.Model.FindEntityType(
            typeof(InventoryAllocationAmendmentDecision))!;

        Assert.NotEmpty(decision.GetDeclaredQueryFilters());
    }

    private static InventoryDbContext CreateDbContext()
    {
        DbContextOptions<InventoryDbContext> options =
            new DbContextOptionsBuilder<InventoryDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;
        return new(options, new TestScopeContext());
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }
}
