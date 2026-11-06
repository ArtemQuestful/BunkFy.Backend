namespace BunkFy.Modules.Properties.Tests;

using System.Reflection;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Domain.ValueObjects;
using BunkFy.Modules.Properties.Persistence;
using BunkFy.Modules.Properties.Persistence.Repositories;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Xunit;

[Trait("Category", "Unit")]
public sealed class PropertyStationEligibilitySourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);
    private static readonly string[] SnapshotProperties =
        ["ConfiguredProcessingStatus", "Name", "PropertyId", "ScopeId", "Status", "TimeZoneId", "Version"];

    [Fact]
    public async Task Current_owner_facts_are_untracked_minimal_and_follow_retirement()
    {
        await using var db = Context();
        var property = Create();
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var reader = new PropertyStationEligibilitySource(db);
        var facts = Assert.IsType<PropertyStationEligibilitySnapshot>(await reader.FindAsync("tenant-a", property.Id));
        Assert.Equal(new("tenant-a", property.Id, PropertyStatus.Active, property.Version, PropertyProcessingStatus.Unconfigured, "Etc/UTC", "Synthetic property"), facts);
        Assert.Empty(db.ChangeTracker.Entries());
        db.Properties.Attach(property);
        Assert.True(property.Retire(property.Version, Guid.NewGuid(), Now).IsSuccess);
        await db.SaveChangesAsync();
        var retired = (await reader.FindAsync("tenant-a", property.Id))!;
        Assert.Equal(PropertyStatus.Retired, retired.Status);
        Assert.True(retired.Version > facts.Version);
        Assert.Equal(SnapshotProperties,
            typeof(PropertyStationEligibilitySnapshot).GetProperties().Select(p => p.Name).Order());
    }

    [Theory]
    [InlineData(PropertyProcessingState.Unconfigured, PropertyProcessingStatus.Unconfigured)]
    [InlineData(PropertyProcessingState.Enabled, PropertyProcessingStatus.Enabled)]
    [InlineData(PropertyProcessingState.Suspended, PropertyProcessingStatus.Suspended)]
    [InlineData((PropertyProcessingState)99, PropertyProcessingStatus.Unknown)]
    public async Task Configured_status_is_not_legal_or_effective_processing_admission(PropertyProcessingState state, PropertyProcessingStatus expected)
    {
        await using var db = Context();
        var property = Create();
        Set(property, nameof(property.ProcessingState), state);
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        Assert.Equal(expected, (await new PropertyStationEligibilitySource(db).FindAsync("tenant-a", property.Id))!.ConfiguredProcessingStatus);
    }

    [Fact]
    public async Task Time_zone_and_version_are_current_owner_facts_not_cached()
    {
        await using var db = Context();
        var property = Create();
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        var reader = new PropertyStationEligibilitySource(db);
        var before = (await reader.FindAsync("tenant-a", property.Id))!;
        Assert.True(property.SetTimeZone(PropertyTimeZoneId.Create("America/New_York").Value,
            property.Version, Guid.NewGuid(), Now.AddMinutes(1)).IsSuccess);
        await db.SaveChangesAsync();
        var after = (await reader.FindAsync("tenant-a", property.Id))!;
        Assert.Equal("America/New_York", after.TimeZoneId);
        Assert.True(after.Version > before.Version);
        Assert.Equal("Etc/UTC", before.TimeZoneId);
    }

    [Theory]
    [InlineData("tenant-b", true)]
    [InlineData("", true)]
    [InlineData("tenant-a", false)]
    public async Task Wrong_or_disabled_scope_cannot_read_the_same_property_guid(string requested, bool enabled)
    {
        var scope = new TestScope();
        await using var db = Context(scope);
        var property = Create();
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        scope.IsEnabled = enabled;
        Assert.Null(await new PropertyStationEligibilitySource(db).FindAsync(requested, property.Id));
    }

    [Fact]
    public async Task Missing_invalid_version_unknown_status_and_cancellation_are_safe()
    {
        await using var db = Context();
        var reader = new PropertyStationEligibilitySource(db);
        Assert.Null(await reader.FindAsync("tenant-a", Guid.NewGuid()));
        Assert.Null(await reader.FindAsync("tenant-a", Guid.Empty));
        var property = Create();
        Set(property, nameof(property.Status), (PropertyState)99);
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        Assert.Equal(PropertyStatus.Unknown, (await reader.FindAsync("tenant-a", property.Id))!.Status);
        Set(property, nameof(property.Version), 0L);
        await db.SaveChangesAsync();
        Assert.Null(await reader.FindAsync("tenant-a", property.Id));
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.FindAsync("tenant-a", property.Id, cancel.Token));
    }

    private static PropertiesDbContext Context(TestScope? scope = null) => new(
        new DbContextOptionsBuilder<PropertiesDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
        scope ?? new TestScope());
    private static Property Create() => Property.Create(Guid.NewGuid(), "tenant-a", "Synthetic property", "test", "Etc/UTC", Guid.NewGuid(), Now).Value;
    private static void Set(object target, string property, object? value) => target.GetType()
        .GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(target, value);
    private sealed class TestScope : IScopeContext
    {
        public bool IsEnabled { get; set; } = true;
        public string ScopeId => "tenant-a";
    }
}
