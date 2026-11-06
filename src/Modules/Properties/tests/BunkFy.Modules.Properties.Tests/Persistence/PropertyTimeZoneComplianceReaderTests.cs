namespace BunkFy.Modules.Properties.Tests;

using System.Reflection;
using BunkFy.Modules.Properties.Application.Ports;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Persistence;
using BunkFy.Modules.Properties.Persistence.Repositories;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Xunit;

[Trait("Category", "Unit")]
public sealed class PropertyTimeZoneComplianceReaderTests
{
    private const string TenantId = "tenant-a";
    private const string CatalogVersion = "TZDB: test-v1";
    private static readonly DateTimeOffset Now = new(
        2026,
        8,
        13,
        12,
        0,
        0,
        TimeSpan.Zero);

    [Fact]
    public async Task Monotonic_keyset_includes_an_insert_between_pages()
    {
        await using PropertiesDbContext dbContext = CreateDbContext();
        Property first = CreateProperty(
            Guid.Parse("f0000000-0000-0000-0000-000000000001"),
            "first");
        Property second = CreateProperty(
            Guid.Parse("f0000000-0000-0000-0000-000000000002"),
            "second");
        SetProjectionOrdinal(first, 1);
        SetProjectionOrdinal(second, 2);
        dbContext.Properties.AddRange(first, second);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        PropertyTimeZoneComplianceReader reader = new(
            dbContext,
            new TestScopeContext());

        PropertyTimeZoneComplianceReadPage firstPage =
            await reader.ReadPageAsync(
                cursor: null,
                pageSize: 1,
                CatalogVersion,
                CancellationToken.None);
        Assert.Equal(first.Id, Assert.Single(firstPage.Properties).PropertyId);
        Assert.True(firstPage.HasMore);
        Assert.NotNull(firstPage.NextCursor);

        Property inserted = CreateProperty(
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            "inserted");
        SetProjectionOrdinal(inserted, 3);
        dbContext.Properties.Add(inserted);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        PropertyTimeZoneComplianceReadPage remainder =
            await reader.ReadPageAsync(
                firstPage.NextCursor,
                pageSize: 10,
                CatalogVersion,
                CancellationToken.None);

        Assert.Equal(
            [second.Id, inserted.Id],
            remainder.Properties.Select(property => property.PropertyId));
        Assert.False(remainder.HasMore);
        Assert.Null(remainder.NextCursor);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(PropertiesContractLimits.PropertyTimeZonePageSizeMax + 1)]
    public async Task Page_size_is_strictly_bounded(int pageSize)
    {
        await using PropertiesDbContext dbContext = CreateDbContext();
        PropertyTimeZoneComplianceReader reader = new(
            dbContext,
            new TestScopeContext());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            reader.ReadPageAsync(
                cursor: null,
                pageSize,
                CatalogVersion,
                CancellationToken.None));
    }

    private static Property CreateProperty(Guid id, string code) =>
        Property.Create(
            id,
            TenantId,
            $"Property {code}",
            code,
            "Etc/UTC",
            Guid.NewGuid(),
            Now).Value;

    private static void SetProjectionOrdinal(
        Property property,
        long projectionOrdinal) =>
        typeof(Property)
            .GetProperty(
                nameof(Property.ProjectionOrdinal),
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic)!
            .SetValue(property, projectionOrdinal);

    private static PropertiesDbContext CreateDbContext()
    {
        DbContextOptions<PropertiesDbContext> options =
            new DbContextOptionsBuilder<PropertiesDbContext>()
                .UseInMemoryDatabase(
                    $"property-time-zone-compliance-{Guid.NewGuid():N}")
                .Options;
        return new(options, new TestScopeContext());
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => TenantId;
    }
}
