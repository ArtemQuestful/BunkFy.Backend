namespace BunkFy.Modules.Workspaces.Tests.Persistence;

using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Persistence;
using BunkFy.Modules.Workspaces.Persistence.Repositories;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspacePropertyProjectionRepositoryTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

    [Fact]
    public async Task Active_authority_and_updates_are_isolated_to_the_current_tenant()
    {
        Guid sharedPropertyId = Guid.NewGuid();
        Guid foreignOnlyPropertyId = Guid.NewGuid();
        string databaseName = $"workspaces-property-scope-{Guid.NewGuid():N}";
        InMemoryDatabaseRoot root = new();

        await using (WorkspacesDbContext seed = CreateDbContext(
            root,
            databaseName,
            scopeId: string.Empty,
            scopeEnabled: false))
        {
            seed.PropertyProjections.AddRange(
                new WorkspacePropertyProjection(
                    TenantB,
                    sharedPropertyId,
                    "Tenant B",
                    PropertyStatus.Retired,
                    1),
                new WorkspacePropertyProjection(
                    TenantA,
                    sharedPropertyId,
                    "Tenant A",
                    PropertyStatus.Active,
                    1),
                new WorkspacePropertyProjection(
                    TenantB,
                    foreignOnlyPropertyId,
                    "Tenant B only",
                    PropertyStatus.Active,
                    1));
            await seed.SaveChangesAsync();
        }

        await using (WorkspacesDbContext tenantA = CreateDbContext(
            root,
            databaseName,
            TenantA,
            scopeEnabled: true))
        {
            WorkspacePropertyProjectionRepository repository = new(tenantA);
            Assert.True(await repository.AreAllActiveAsync(
                [sharedPropertyId],
                CancellationToken.None));
            Assert.False(await repository.AreAllActiveAsync(
                [sharedPropertyId, foreignOnlyPropertyId],
                CancellationToken.None));
            Assert.False(await repository.AreAllActiveAsync(
                [Guid.Empty],
                CancellationToken.None));
            Assert.Single(await tenantA.PropertyProjections.ToArrayAsync());

            await repository.ApplyAsync(
                new WorkspacePropertyProjectionWriteModel(
                    TenantA,
                    sharedPropertyId,
                    "Tenant A updated",
                    PropertyStatus.Active,
                    2),
                CancellationToken.None);
            await tenantA.SaveChangesAsync();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                repository.ApplyAsync(
                    new WorkspacePropertyProjectionWriteModel(
                        TenantB,
                        sharedPropertyId,
                        "Foreign update",
                        PropertyStatus.Active,
                        3),
                    CancellationToken.None));
        }

        await using WorkspacesDbContext verify = CreateDbContext(
            root,
            databaseName,
            scopeId: string.Empty,
            scopeEnabled: false);
        WorkspacePropertyProjection tenantARow = await verify.PropertyProjections
            .SingleAsync(property =>
                property.ScopeId == TenantA && property.Id == sharedPropertyId);
        WorkspacePropertyProjection tenantBRow = await verify.PropertyProjections
            .SingleAsync(property =>
                property.ScopeId == TenantB && property.Id == sharedPropertyId);
        Assert.Equal("Tenant A updated", tenantARow.Name);
        Assert.Equal(2, tenantARow.Version);
        Assert.Equal("Tenant B", tenantBRow.Name);
        Assert.Equal(PropertyStatus.Retired, tenantBRow.Status);
        Assert.Equal(1, tenantBRow.Version);

        WorkspacePropertyProjectionRepository unscopedRepository = new(verify);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            unscopedRepository.AreAllActiveAsync(
                [sharedPropertyId],
                CancellationToken.None));
    }

    private static WorkspacesDbContext CreateDbContext(
        InMemoryDatabaseRoot root,
        string databaseName,
        string scopeId,
        bool scopeEnabled)
    {
        DbContextOptions<WorkspacesDbContext> options =
            new DbContextOptionsBuilder<WorkspacesDbContext>()
                .UseInMemoryDatabase(databaseName, root)
                .Options;
        return new(options, new TestScopeContext(scopeId, scopeEnabled));
    }

    private sealed class TestScopeContext(string scopeId, bool scopeEnabled)
        : IScopeContext
    {
        public bool IsEnabled { get; } = scopeEnabled;
        public string ScopeId { get; } = scopeId;
    }
}
