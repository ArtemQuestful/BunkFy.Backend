namespace BunkFy.Modules.Staff.Tests.Persistence;

using BunkFy.Modules.Staff.Persistence;
using Gma.Framework.Domain;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

[Trait("Category", "Unit")]
public sealed class StaffScopeClassificationTests
{
    [Fact]
    public void Every_staff_scope_shaped_entity_is_explicitly_scoped()
    {
        using StaffDbContext context = CreateDbContext();

        string[] unclassified = context.Model.GetEntityTypes()
            .Where(entity =>
                !entity.IsOwned() &&
                entity.ClrType.Namespace?.StartsWith(
                    "BunkFy.Modules.Staff.",
                    StringComparison.Ordinal) == true &&
                entity.FindProperty(nameof(IScopedEntity.ScopeId)) is not null &&
                !typeof(IScopedEntity).IsAssignableFrom(entity.ClrType))
            .Select(entity => entity.ClrType.FullName ?? entity.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(unclassified);
    }

    [Fact]
    public void Property_authority_projection_is_tenant_filtered()
    {
        using StaffDbContext context = CreateDbContext();
        IEntityType projection = context.Model.FindEntityType(
            typeof(StaffPropertyProjection))!;

        Assert.NotEmpty(projection.GetDeclaredQueryFilters());
        Assert.True(projection.FindProperty(
            nameof(StaffPropertyProjection.Version))!.IsConcurrencyToken);
    }

    private static StaffDbContext CreateDbContext()
    {
        DbContextOptions<StaffDbContext> options =
            new DbContextOptionsBuilder<StaffDbContext>()
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
