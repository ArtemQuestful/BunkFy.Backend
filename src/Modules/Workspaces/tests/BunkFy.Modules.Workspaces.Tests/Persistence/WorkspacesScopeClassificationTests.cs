namespace BunkFy.Modules.Workspaces.Tests.Persistence;

using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Persistence;
using Gma.Framework.Domain;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspacesScopeClassificationTests
{
    [Fact]
    public void Every_workspace_scope_shaped_entity_is_explicitly_scoped()
    {
        using WorkspacesDbContext context = CreateDbContext();

        string[] unclassified = context.Model.GetEntityTypes()
            .Where(entity =>
                !entity.IsOwned() &&
                entity.ClrType.Namespace?.StartsWith(
                    "BunkFy.Modules.Workspaces.",
                    StringComparison.Ordinal) == true &&
                entity.FindProperty(nameof(IScopedEntity.ScopeId)) is not null &&
                !typeof(IScopedEntity).IsAssignableFrom(entity.ClrType))
            .Select(entity => entity.ClrType.FullName ?? entity.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(unclassified);
    }

    [Fact]
    public void Property_authority_rows_are_tenant_filtered()
    {
        using WorkspacesDbContext context = CreateDbContext();
        IEntityType projection = context.Model.FindEntityType(
            typeof(WorkspacePropertyProjection))!;
        IEntityType accessPlanProperty = context.Model.FindEntityType(
            typeof(WorkspaceStaffAccessPlanProperty))!;

        Assert.NotEmpty(projection.GetDeclaredQueryFilters());
        Assert.True(projection.FindProperty(
            nameof(WorkspacePropertyProjection.Version))!.IsConcurrencyToken);
        Assert.NotEmpty(accessPlanProperty.GetDeclaredQueryFilters());
        Assert.Contains(accessPlanProperty.GetForeignKeys(), foreignKey =>
            foreignKey.Properties.Select(property => property.Name)
                .SequenceEqual([
                    nameof(WorkspaceStaffAccessPlanProperty.ScopeId),
                    nameof(WorkspaceStaffAccessPlanProperty.PlanId)
                ]) &&
            foreignKey.PrincipalKey.Properties.Select(property => property.Name)
                .SequenceEqual([
                    nameof(WorkspaceStaffAccessPlan.ScopeId),
                    nameof(WorkspaceStaffAccessPlan.Id)
                ]));
    }

    private static WorkspacesDbContext CreateDbContext()
    {
        DbContextOptions<WorkspacesDbContext> options =
            new DbContextOptionsBuilder<WorkspacesDbContext>()
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
