namespace BunkFy.Modules.DataRights.Tests.Persistence;

using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Persistence;
using BunkFy.Modules.DataRights.Persistence.Repositories;
using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

[Trait("Category", "Unit")]
public sealed class DataRightsPropertyProjectionRepositoryTests
{
    [Fact]
    public async Task Topology_projection_preserves_time_zone_and_ignores_stale_updates()
    {
        Guid propertyId = Guid.NewGuid();
        InMemoryDatabaseRoot root = new();
        await using DataRightsDbContext dbContext = CreateDbContext(root);
        DataRightsPropertyProjectionRepository repository = new(dbContext);

        await repository.ApplyTopologyAsync(
            new(
                "tenant-a",
                propertyId,
                "Hostel",
                "Europe/London",
                PropertyStatus.Active,
                5),
            CancellationToken.None);
        await dbContext.SaveChangesAsync();
        await repository.ApplyTopologyAsync(
            new(
                "tenant-a",
                propertyId,
                "Stale",
                "UTC",
                PropertyStatus.Retired,
                4),
            CancellationToken.None);
        await dbContext.SaveChangesAsync();

        DataRightsPropertyPolicySnapshot snapshot = Assert.IsType<
            DataRightsPropertyPolicySnapshot>(
                await repository.GetPolicyAsync(propertyId, CancellationToken.None));
        Assert.True(snapshot.IsKnown);
        Assert.Equal(PropertyStatus.Active, snapshot.Status);
        Assert.Equal("Europe/London", snapshot.TimeZoneId);
        Assert.Equal(5, snapshot.TopologySourceVersion);
        Assert.Equal(0, snapshot.PolicySourceVersion);
    }

    [Fact]
    public async Task Retire_event_without_topology_payload_preserves_projected_time_zone()
    {
        Guid propertyId = Guid.NewGuid();
        InMemoryDatabaseRoot root = new();
        await using DataRightsDbContext dbContext = CreateDbContext(root);
        DataRightsPropertyProjectionRepository repository = new(dbContext);
        await repository.ApplyTopologyAsync(
            new(
                "tenant-a",
                propertyId,
                "Hostel",
                "Europe/London",
                PropertyStatus.Active,
                5),
            CancellationToken.None);
        await repository.ApplyTopologyAsync(
            new(
                "tenant-a",
                propertyId,
                string.Empty,
                null,
                PropertyStatus.Retired,
                6),
            CancellationToken.None);
        await dbContext.SaveChangesAsync();

        DataRightsPropertyPolicySnapshot snapshot = Assert.IsType<
            DataRightsPropertyPolicySnapshot>(
                await repository.GetPolicyAsync(propertyId, CancellationToken.None));
        Assert.Equal(PropertyStatus.Retired, snapshot.Status);
        Assert.Equal("Europe/London", snapshot.TimeZoneId);
        Assert.Equal(6, snapshot.TopologySourceVersion);
    }

    [Fact]
    public async Task Same_property_id_is_filtered_and_mutated_only_inside_the_active_tenant()
    {
        Guid propertyId = Guid.NewGuid();
        string databaseName = $"data-rights-property-scope-{Guid.NewGuid():N}";
        InMemoryDatabaseRoot root = new();
        await using (DataRightsDbContext seed = CreateDbContext(
            root,
            databaseName,
            scopeId: string.Empty,
            scopeEnabled: false))
        {
            seed.PropertyProjections.AddRange(
                new DataRightsPropertyProjection(
                    "tenant-b",
                    propertyId,
                    "Tenant B",
                    "America/Toronto",
                    PropertyStatus.Retired,
                    1),
                new DataRightsPropertyProjection(
                    "tenant-a",
                    propertyId,
                    "Tenant A",
                    "Europe/London",
                    PropertyStatus.Active,
                    1));
            await seed.SaveChangesAsync();
        }

        await using (DataRightsDbContext tenantA = CreateDbContext(
            root,
            databaseName,
            "tenant-a",
            scopeEnabled: true))
        {
            DataRightsPropertyProjectionRepository repository = new(tenantA);
            DataRightsPropertyPolicySnapshot snapshot = Assert.IsType<
                DataRightsPropertyPolicySnapshot>(
                    await repository.GetPolicyAsync(propertyId, CancellationToken.None));
            Assert.Equal(PropertyStatus.Active, snapshot.Status);
            Assert.Equal("Europe/London", snapshot.TimeZoneId);

            await repository.ApplyTopologyAsync(
                new(
                    "tenant-a",
                    propertyId,
                    "Tenant A updated",
                    "Europe/Paris",
                    PropertyStatus.Active,
                    2),
                CancellationToken.None);
            await tenantA.SaveChangesAsync();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                repository.ApplyTopologyAsync(
                    new(
                        "tenant-b",
                        propertyId,
                        "Foreign update",
                        "UTC",
                        PropertyStatus.Active,
                        3),
                    CancellationToken.None));
        }

        await using DataRightsDbContext verify = CreateDbContext(
            root,
            databaseName,
            scopeId: string.Empty,
            scopeEnabled: false);
        DataRightsPropertyProjection tenantARow = await verify.PropertyProjections
            .SingleAsync(property => property.ScopeId == "tenant-a");
        DataRightsPropertyProjection tenantBRow = await verify.PropertyProjections
            .SingleAsync(property => property.ScopeId == "tenant-b");
        Assert.Equal("Tenant A updated", tenantARow.Name);
        Assert.Equal("Europe/Paris", tenantARow.TimeZoneId);
        Assert.Equal(2, tenantARow.TopologySourceVersion);
        Assert.Equal("Tenant B", tenantBRow.Name);
        Assert.Equal("America/Toronto", tenantBRow.TimeZoneId);
        Assert.Equal(1, tenantBRow.TopologySourceVersion);

        DataRightsPropertyProjectionRepository unscopedRepository = new(verify);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            unscopedRepository.GetPolicyAsync(propertyId, CancellationToken.None));
    }

    private static DataRightsDbContext CreateDbContext(
        InMemoryDatabaseRoot root)
        => CreateDbContext(
            root,
            $"data-rights-property-{Guid.NewGuid():N}",
            "tenant-a",
            scopeEnabled: true);

    private static DataRightsDbContext CreateDbContext(
        InMemoryDatabaseRoot root,
        string databaseName,
        string scopeId,
        bool scopeEnabled)
    {
        DbContextOptions<DataRightsDbContext> options =
            new DbContextOptionsBuilder<DataRightsDbContext>()
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
