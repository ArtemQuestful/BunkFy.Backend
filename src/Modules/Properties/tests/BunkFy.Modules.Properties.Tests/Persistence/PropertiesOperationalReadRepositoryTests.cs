namespace BunkFy.Modules.Properties.Tests.Persistence;

using BunkFy.Modules.Properties.Application.Ports;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Domain.ValueObjects;
using BunkFy.Modules.Properties.Persistence;
using BunkFy.Modules.Properties.Persistence.Repositories;
using Gma.Framework.Pagination;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Xunit;

[Trait("Category", "Unit")]
public sealed class PropertiesOperationalReadRepositoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Directories_use_stable_bounded_lookahead()
    {
        await using PropertiesDbContext dbContext = CreateDbContext();
        Property property = CreateProperty("Hostel C", "hostel-c");
        dbContext.Properties.AddRange(
            property,
            CreateProperty("Hostel A", "hostel-a"),
            CreateProperty("Hostel B", "hostel-b"));

        Room[] rooms =
        [
            CreateRoom(property.Id, "103"),
            CreateRoom(property.Id, "101"),
            CreateRoom(property.Id, "102")
        ];
        Assert.True(rooms[1].AddBeds(
            [
                new BedAdditionDefinition(Guid.NewGuid(), "3", Guid.NewGuid()),
                new BedAdditionDefinition(Guid.NewGuid(), "1", Guid.NewGuid()),
                new BedAdditionDefinition(Guid.NewGuid(), "2", Guid.NewGuid())
            ],
            rooms[1].Version,
            Now).IsSuccess);
        dbContext.Rooms.AddRange(rooms);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();

        PropertiesReadRepository repository = CreateRepository(dbContext);
        PropertyReadPage propertyFirst = await repository.ListPropertiesAsync(
            new PageRequest(1, 2),
            CancellationToken.None);
        PropertyReadPage propertyLast = await repository.ListPropertiesAsync(
            new PageRequest(2, 2),
            CancellationToken.None);
        RoomListResponse roomFirst = await repository.ListRoomsAsync(
            property.Id,
            new PageRequest(1, 2),
            CancellationToken.None);
        RoomListResponse roomLast = await repository.ListRoomsAsync(
            property.Id,
            new PageRequest(2, 2),
            CancellationToken.None);
        BedListResponse bedFirst = await repository.ListBedsAsync(
            property.Id,
            rooms[1].Id,
            new PageRequest(1, 2),
            CancellationToken.None);
        BedListResponse bedLast = await repository.ListBedsAsync(
            property.Id,
            rooms[1].Id,
            new PageRequest(2, 2),
            CancellationToken.None);

        Assert.Equal(["hostel-a", "hostel-b"], propertyFirst.Properties.Select(item => item.Code));
        Assert.True(propertyFirst.HasMore);
        Assert.Equal(["hostel-c"], propertyLast.Properties.Select(item => item.Code));
        Assert.False(propertyLast.HasMore);
        Assert.Equal(["101", "102"], roomFirst.Rooms.Select(item => item.Name));
        Assert.True(roomFirst.HasMore);
        Assert.Equal(["103"], roomLast.Rooms.Select(item => item.Name));
        Assert.False(roomLast.HasMore);
        Assert.Equal(["1", "2"], bedFirst.Beds.Select(item => item.Label));
        Assert.True(bedFirst.HasMore);
        Assert.Equal(["3"], bedLast.Beds.Select(item => item.Label));
        Assert.False(bedLast.HasMore);
    }

    [Fact]
    public async Task Raw_property_reads_preserve_legacy_time_zone_without_classifying_it()
    {
        await using PropertiesDbContext dbContext = CreateDbContext();
        Property property = CreateProperty("Legacy hostel", "legacy-hostel");
        typeof(Property).GetProperty(nameof(Property.TimeZoneId))!
            .SetValue(
                property,
                PropertyTimeZoneId.RestorePersisted("UTC"));
        Assert.True(property.Retire(
            property.Version,
            Guid.NewGuid(),
            Now.AddMinutes(1),
            "user:owner").IsSuccess);
        dbContext.Properties.Add(property);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        PropertiesReadRepository repository = CreateRepository(dbContext);

        Property detail = Assert.IsType<Property>(
            await repository.GetPropertyAsync(
                property.Id,
                CancellationToken.None));
        PropertyListReadModel listed = Assert.Single(
            (await repository.ListPropertiesAsync(
                    new PageRequest(1, 20),
                    CancellationToken.None))
                .Properties);

        Assert.Equal("UTC", detail.TimeZoneId.Value);
        Assert.Equal(detail.TimeZoneId.Value, listed.TimeZoneId);
        Assert.Equal(PropertyState.Retired, listed.Status);
    }

    private static Property CreateProperty(string name, string code) =>
        Property.Create(
            Guid.NewGuid(),
            "tenant-a",
            name,
            code,
            "UTC",
            Guid.NewGuid(),
            Now).Value;

    private static Room CreateRoom(Guid propertyId, string name) =>
        Room.Create(
            Guid.NewGuid(),
            "tenant-a",
            propertyId,
            name,
            null,
            null,
            Guid.NewGuid(),
            Now).Value;

    private static PropertiesDbContext CreateDbContext()
    {
        DbContextOptions<PropertiesDbContext> options = new DbContextOptionsBuilder<PropertiesDbContext>()
            .UseInMemoryDatabase($"properties-operational-read-{Guid.NewGuid():N}")
            .Options;
        return new PropertiesDbContext(options, new TestScopeContext());
    }

    private static PropertiesReadRepository CreateRepository(
        PropertiesDbContext dbContext) => new(dbContext);

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }
}
