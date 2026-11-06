namespace BunkFy.Modules.Staff.Tests.Persistence;

using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Persistence;
using BunkFy.Modules.Staff.Persistence.Repositories;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

[Trait("Category", "Unit")]
public sealed class StaffPropertyProjectionRepositoryTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

    [Fact]
    public async Task Active_authority_and_updates_are_isolated_to_the_current_tenant()
    {
        Guid sharedPropertyId = Guid.NewGuid();
        Guid foreignOnlyPropertyId = Guid.NewGuid();
        string databaseName = $"staff-property-scope-{Guid.NewGuid():N}";
        InMemoryDatabaseRoot root = new();

        await using (StaffDbContext tenantB = CreateDbContext(
            root,
            databaseName,
            TenantB,
            scopeEnabled: true))
        {
            tenantB.PropertyProjections.AddRange(
                new StaffPropertyProjection(
                    TenantB,
                    sharedPropertyId,
                    "Tenant B",
                    PropertyStatus.Retired,
                    1),
                new StaffPropertyProjection(
                    TenantB,
                    foreignOnlyPropertyId,
                    "Tenant B only",
                    PropertyStatus.Active,
                    1));
            await tenantB.SaveChangesAsync();
        }

        await using (StaffDbContext tenantASeed = CreateDbContext(
            root,
            databaseName,
            TenantA,
            scopeEnabled: true))
        {
            tenantASeed.PropertyProjections.Add(new StaffPropertyProjection(
                TenantA,
                sharedPropertyId,
                "Tenant A",
                PropertyStatus.Active,
                1));
            await tenantASeed.SaveChangesAsync();
        }

        await using (StaffDbContext tenantA = CreateDbContext(
            root,
            databaseName,
            TenantA,
            scopeEnabled: true))
        {
            StaffPropertyProjectionRepository repository = new(tenantA);
            Assert.True(await repository.IsActiveAsync(
                sharedPropertyId,
                CancellationToken.None));
            Assert.False(await repository.IsActiveAsync(
                foreignOnlyPropertyId,
                CancellationToken.None));
            Assert.False(await repository.IsActiveAsync(
                Guid.Empty,
                CancellationToken.None));
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
                new StaffPropertyProjectionWriteModel(
                    TenantA,
                    sharedPropertyId,
                    "Tenant A updated",
                    PropertyStatus.Active,
                    2),
                CancellationToken.None);
            await tenantA.SaveChangesAsync();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                repository.ApplyAsync(
                    new StaffPropertyProjectionWriteModel(
                        TenantB,
                        sharedPropertyId,
                        "Foreign update",
                        PropertyStatus.Active,
                        3),
                    CancellationToken.None));
        }

        await using (StaffDbContext tenantAVerify = CreateDbContext(
            root,
            databaseName,
            TenantA,
            scopeEnabled: true))
        {
            StaffPropertyProjection tenantARow = await tenantAVerify
                .PropertyProjections.SingleAsync(property =>
                    property.Id == sharedPropertyId);
            Assert.Equal("Tenant A updated", tenantARow.Name);
            Assert.Equal(2, tenantARow.Version);
        }

        await using (StaffDbContext tenantBVerify = CreateDbContext(
            root,
            databaseName,
            TenantB,
            scopeEnabled: true))
        {
            StaffPropertyProjection tenantBRow = await tenantBVerify
                .PropertyProjections.SingleAsync(property =>
                    property.Id == sharedPropertyId);
            Assert.Equal("Tenant B", tenantBRow.Name);
            Assert.Equal(PropertyStatus.Retired, tenantBRow.Status);
            Assert.Equal(1, tenantBRow.Version);
        }

        await using StaffDbContext unscoped = CreateDbContext(
            root,
            databaseName,
            scopeId: string.Empty,
            scopeEnabled: false);
        StaffPropertyProjectionRepository unscopedRepository = new(unscoped);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            unscopedRepository.IsActiveAsync(
                sharedPropertyId,
                CancellationToken.None));
    }

    private static StaffDbContext CreateDbContext(
        InMemoryDatabaseRoot root,
        string databaseName,
        string scopeId,
        bool scopeEnabled)
    {
        DbContextOptions<StaffDbContext> options =
            new DbContextOptionsBuilder<StaffDbContext>()
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
