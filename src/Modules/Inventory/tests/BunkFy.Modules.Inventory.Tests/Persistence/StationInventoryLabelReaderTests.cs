namespace BunkFy.Modules.Inventory.Tests.Persistence;

using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Contracts.Stations;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using BunkFy.Modules.Inventory.Persistence;
using BunkFy.Modules.Inventory.Persistence.Repositories;
using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>Actual reader over isolated InMemory owner records; not PostgreSQL translation or constraint evidence.</summary>
[Trait("Category", "Unit")]
public sealed class StationInventoryLabelReaderTests
{
    private const string Tenant = "station-labels-a";
    private const string OtherTenant = "station-labels-b";
    private static readonly Guid Property = Id(1);
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Current_room_and_bed_identity_uses_independent_owner_version_coordinates(bool bed)
    {
        await using var db = CreateDb();
        var seeded = Seed(db, bed);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await new StationInventoryLabelReader(db).ReadAsync(Property, [seeded.Unit.Id]);

        Assert.Equal(StationInventoryLabelsState.Current, result.State);
        var label = Assert.Single(result.Items);
        Assert.Equal(Property, label.PropertyId);
        Assert.Equal(seeded.Unit.Id, label.InventoryUnitId);
        Assert.Equal(bed ? InventoryUnitKind.Bed : InventoryUnitKind.Room, label.Kind);
        Assert.Equal(seeded.Room.Id, label.RoomId);
        Assert.Equal("Garden dorm", label.RoomName);
        Assert.Equal(bed ? seeded.Bed!.Id : null, label.BedId);
        Assert.Equal(bed ? "Upper bunk" : null, label.BedLabel);
        Assert.Equal(2, label.ConfigurationVersion);
        Assert.Equal(4, label.UnitVersion);
        Assert.Equal(bed ? 11 : 7, label.UnitSourceVersion);
        Assert.Equal(7, label.RoomSourceVersion);
        Assert.Equal(bed ? 11 : null, label.BedSourceVersion);
        Assert.NotEqual(seeded.Configuration.AvailabilityMutationVersion, label.ConfigurationVersion);
        Assert.NotEqual(seeded.Configuration.AvailabilityMutationVersion, label.UnitVersion);
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Empty(await db.ManagementOperations.ToArrayAsync());
        Assert.Empty(await db.OutboxMessages.ToArrayAsync());
        Assert.Equal(4, (await db.InventoryUnits.AsNoTracking().SingleAsync()).AvailabilityMutationVersion);
        Assert.Equal(2, (await db.RoomConfigurations.AsNoTracking().SingleAsync()).Version);
    }

    [Fact]
    public async Task Maximum_batch_returns_every_requested_unit_in_stable_order_without_unrequested_neighbours()
    {
        await using var db = CreateDb();
        var seeded = Enumerable.Range(1, 26).Select(i => Seed(db, bed: true, index: i)).ToArray();
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Guid[] requested = seeded.Take(25).Select(x => x.Unit.Id).Reverse().ToArray();

        var result = await new StationInventoryLabelReader(db).ReadAsync(Property, requested);

        Assert.Equal(StationInventoryLabelsState.Current, result.State);
        Assert.Equal(25, result.Items.Count);
        Assert.Equal(requested.Order(), result.Items.Select(x => x.InventoryUnitId));
        Assert.DoesNotContain(result.Items, x => x.InventoryUnitId == seeded[25].Unit.Id);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData("empty-property")]
    [InlineData("empty-list")]
    [InlineData("too-many")]
    [InlineData("empty-unit")]
    [InlineData("duplicate")]
    public async Task Invalid_coordinates_or_duplicate_ids_fail_before_database_access(string scenario)
    {
        var db = CreateDb();
        await db.DisposeAsync();
        Guid property = scenario == "empty-property" ? Guid.Empty : Property;
        Guid[] ids = scenario switch
        {
            "empty-list" => [],
            "too-many" => Enumerable.Range(1, 26).Select(Id).ToArray(),
            "empty-unit" => [Guid.Empty],
            "duplicate" => [Id(100), Id(100)],
            "empty-property" => [Id(100)],
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

        var result = await new StationInventoryLabelReader(db).ReadAsync(property, ids);

        AssertFailure(result, StationInventoryLabelsState.Invalid);
        // A query against this disposed context would yield Unavailable, not the required early Invalid.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_requested_unit_discards_existing_labels_instead_of_returning_a_partial_batch(bool allMissing)
    {
        await using var db = CreateDb();
        var seeded = Seed(db, bed: true);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Guid[] ids = allMissing ? [Id(999)] : [seeded.Unit.Id, Id(999)];

        AssertFailure(await new StationInventoryLabelReader(db).ReadAsync(Property, ids), StationInventoryLabelsState.Incomplete);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData("unit")]
    [InlineData("room")]
    [InlineData("bed")]
    [InlineData("configuration")]
    public async Task Every_join_is_bound_to_the_exact_property(string component)
    {
        await using var db = CreateDb();
        var seeded = Seed(db, bed: true);
        Guid otherProperty = Id(900);
        switch (component)
        {
            case "unit":
                db.Entry(seeded.Unit).Property(x => x.PropertyId).CurrentValue = otherProperty;
                break;
            case "room":
                db.Entry(seeded.Room).Property(x => x.PropertyId).CurrentValue = otherProperty;
                break;
            case "bed":
                db.Entry(seeded.Bed!).Property(x => x.PropertyId).CurrentValue = otherProperty;
                break;
            case "configuration":
                db.Entry(seeded.Configuration).Property(x => x.PropertyId).CurrentValue = otherProperty;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(component));
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        AssertFailure(await new StationInventoryLabelReader(db).ReadAsync(Property, [seeded.Unit.Id]), StationInventoryLabelsState.Incomplete);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task Requested_wrong_property_does_not_reveal_an_existing_unit()
    {
        await using var db = CreateDb();
        var seeded = Seed(db, bed: true);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        AssertFailure(await new StationInventoryLabelReader(db).ReadAsync(Id(900), [seeded.Unit.Id]), StationInventoryLabelsState.Incomplete);
    }

    [Fact]
    public async Task Other_tenant_units_are_not_visible_even_with_the_same_property_coordinate()
    {
        var options = Options();
        await using var current = new InventoryDbContext(options, new Scope(Tenant));
        await using var other = new InventoryDbContext(options, new Scope(OtherTenant));
        var own = Seed(current, bed: true);
        var foreign = Seed(other, bed: true, index: 2, tenant: OtherTenant, roomName: "Other tenant room", bedLabel: "Other tenant bed");
        await current.SaveChangesAsync();
        await other.SaveChangesAsync();
        current.ChangeTracker.Clear();
        other.ChangeTracker.Clear();

        var actual = await new StationInventoryLabelReader(current).ReadAsync(Property, [own.Unit.Id]);
        var denied = await new StationInventoryLabelReader(current).ReadAsync(Property, [foreign.Unit.Id]);
        var control = await new StationInventoryLabelReader(other).ReadAsync(Property, [foreign.Unit.Id]);

        Assert.Equal(StationInventoryLabelsState.Current, actual.State);
        Assert.Equal(StationInventoryLabelsState.Current, control.State);
        Assert.Equal("Garden dorm", Assert.Single(actual.Items).RoomName);
        Assert.Equal("Upper bunk", Assert.Single(actual.Items).BedLabel);
        Assert.Equal("Other tenant room", Assert.Single(control.Items).RoomName);
        Assert.Equal("Other tenant bed", Assert.Single(control.Items).BedLabel);
        Assert.NotEqual(own.Unit.Id, foreign.Unit.Id);
        AssertFailure(denied, StationInventoryLabelsState.Incomplete);
        Assert.Empty(current.ChangeTracker.Entries());
        Assert.Empty(other.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData("room")]
    [InlineData("bed")]
    [InlineData("configuration")]
    public async Task Other_tenant_topology_or_configuration_cannot_complete_a_current_tenant_unit(string component)
    {
        var options = Options();
        await using var current = new InventoryDbContext(options, new Scope(Tenant));
        await using var other = new InventoryDbContext(options, new Scope(OtherTenant));
        var own = Seed(current, bed: true);
        var foreign = Seed(other, bed: true, tenant: OtherTenant);
        // IDs are globally unique: only the component omitted from the current tenant may use this matching ID.
        other.ChangeTracker.Clear();
        other.Add(Component(foreign, component));
        current.Entry(Component(own, component)).State = EntityState.Detached;
        await current.SaveChangesAsync();
        await other.SaveChangesAsync();
        current.ChangeTracker.Clear();

        AssertFailure(await new StationInventoryLabelReader(current).ReadAsync(Property, [own.Unit.Id]), StationInventoryLabelsState.Incomplete);
    }

    [Theory]
    [InlineData("room")]
    [InlineData("bed")]
    [InlineData("configuration")]
    public async Task Missing_owner_component_never_falls_back_to_the_inventory_unit_label(string component)
    {
        await using var db = CreateDb();
        var seeded = Seed(db, bed: true);
        db.Entry(Component(seeded, component)).State = EntityState.Detached;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        AssertFailure(await new StationInventoryLabelReader(db).ReadAsync(Property, [seeded.Unit.Id]), StationInventoryLabelsState.Incomplete);
    }

    [Theory]
    [InlineData("unit-unknown")]
    [InlineData("unit-inactive")]
    [InlineData("unit-source-zero")]
    [InlineData("unit-availability-zero")]
    [InlineData("unit-label-empty")]
    [InlineData("unit-details-stale")]
    [InlineData("unit-kind-unknown")]
    [InlineData("room-unknown")]
    [InlineData("room-retired")]
    [InlineData("room-source-zero")]
    [InlineData("room-name-empty")]
    [InlineData("room-details-stale")]
    [InlineData("bed-unknown")]
    [InlineData("bed-retired")]
    [InlineData("bed-source-zero")]
    [InlineData("bed-label-empty")]
    [InlineData("bed-details-stale")]
    [InlineData("configuration-version-zero")]
    public async Task Inactive_unknown_or_incomplete_projection_rejects_the_whole_batch(string scenario)
    {
        await using var db = CreateDb();
        var valid = Seed(db, bed: true, index: 1);
        var broken = Seed(db, bed: true, index: 2);
        // These EF values intentionally represent incomplete/corrupt persisted projections; no database-constraint claim.
        switch (scenario)
        {
            case "unit-unknown":
                db.Entry(broken.Unit).Property(x => x.IsKnown).CurrentValue = false;
                break;
            case "unit-inactive":
                db.Entry(broken.Unit).Property(x => x.IsTopologyActive).CurrentValue = false;
                break;
            case "unit-source-zero":
                db.Entry(broken.Unit).Property(x => x.SourceVersion).CurrentValue = 0;
                db.Entry(broken.Unit).Property(x => x.DetailsVersion).CurrentValue = 0;
                break;
            case "unit-availability-zero":
                db.Entry(broken.Unit).Property(x => x.AvailabilityMutationVersion).CurrentValue = 0;
                break;
            case "unit-label-empty":
                db.Entry(broken.Unit).Property(x => x.Label).CurrentValue = " ";
                break;
            case "unit-details-stale":
                broken.Unit.Apply(Property, broken.Room.Id, broken.Bed!.Id, InventoryUnitKind.Bed, null, true, 12);
                break;
            case "unit-kind-unknown":
                db.Entry(broken.Unit).Property(x => x.Kind).CurrentValue = InventoryUnitKind.Unknown;
                break;
            case "room-unknown":
                db.Entry(broken.Room).Property(x => x.IsKnown).CurrentValue = false;
                break;
            case "room-retired":
                db.Entry(broken.Room).Property(x => x.Status).CurrentValue = RoomStatus.Retired;
                break;
            case "room-source-zero":
                db.Entry(broken.Room).Property(x => x.SourceVersion).CurrentValue = 0;
                db.Entry(broken.Room).Property(x => x.DetailsVersion).CurrentValue = 0;
                break;
            case "room-name-empty":
                db.Entry(broken.Room).Property(x => x.Name).CurrentValue = " ";
                break;
            case "room-details-stale":
                broken.Room.Apply(Property, null, null, null, RoomStatus.Active, 8);
                break;
            case "bed-unknown":
                db.Entry(broken.Bed!).Property(x => x.IsKnown).CurrentValue = false;
                break;
            case "bed-retired":
                db.Entry(broken.Bed!).Property(x => x.Status).CurrentValue = BedStatus.Retired;
                break;
            case "bed-source-zero":
                db.Entry(broken.Bed!).Property(x => x.SourceVersion).CurrentValue = 0;
                db.Entry(broken.Bed!).Property(x => x.DetailsVersion).CurrentValue = 0;
                break;
            case "bed-label-empty":
                db.Entry(broken.Bed!).Property(x => x.Label).CurrentValue = " ";
                break;
            case "bed-details-stale":
                broken.Bed!.Apply(Property, broken.Room.Id, null, BedStatus.Active, 12);
                break;
            case "configuration-version-zero":
                db.Entry(broken.Configuration).Property(x => x.Version).CurrentValue = 0;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        AssertFailure(await new StationInventoryLabelReader(db).ReadAsync(Property, [valid.Unit.Id, broken.Unit.Id]), StationInventoryLabelsState.Incomplete);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData(false, RoomSalesMode.BedLevel)]
    [InlineData(false, RoomSalesMode.Unconfigured)]
    [InlineData(true, RoomSalesMode.RoomLevel)]
    [InlineData(true, RoomSalesMode.Unconfigured)]
    public async Task Selling_mode_must_match_the_exact_physical_unit_kind(bool bed, RoomSalesMode mode)
    {
        await using var db = CreateDb();
        var seeded = Seed(db, bed);
        db.Entry(seeded.Configuration).Property(x => x.SalesMode).CurrentValue = mode;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        AssertFailure(await new StationInventoryLabelReader(db).ReadAsync(Property, [seeded.Unit.Id]), StationInventoryLabelsState.Incomplete);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Paired_unit_and_topology_must_share_source_version_even_when_each_has_complete_unchanged_details(
        bool bed, bool advanceUnit)
    {
        await using var db = CreateDb();
        var seeded = Seed(db, bed);
        if (advanceUnit)
        {
            seeded.Unit.Apply(Property, seeded.Room.Id, seeded.Unit.BedId, seeded.Unit.Kind, seeded.Unit.Label, true,
                seeded.Unit.SourceVersion + 1);
        }
        else if (bed)
        {
            var topology = seeded.Bed!;
            topology.Apply(Property, seeded.Room.Id, topology.Label, BedStatus.Active, topology.SourceVersion + 1);
        }
        else
        {
            seeded.Room.Apply(Property, seeded.Room.Name, null, null, RoomStatus.Active, seeded.Room.SourceVersion + 1);
        }
        Assert.Equal(seeded.Unit.SourceVersion, seeded.Unit.DetailsVersion);
        Assert.Equal(seeded.Room.SourceVersion, seeded.Room.DetailsVersion);
        if (bed)
        {
            var topology = seeded.Bed!;
            Assert.Equal(topology.SourceVersion, topology.DetailsVersion);
        }
        Assert.NotEqual(seeded.Unit.SourceVersion, bed ? seeded.Bed!.SourceVersion : seeded.Room.SourceVersion);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        AssertFailure(await new StationInventoryLabelReader(db).ReadAsync(Property, [seeded.Unit.Id]), StationInventoryLabelsState.Incomplete);
    }

    [Theory]
    [InlineData("room-unit-id")]
    [InlineData("room-with-bed-id")]
    [InlineData("room-label")]
    [InlineData("bed-id-null")]
    [InlineData("bed-id-wrong")]
    [InlineData("bed-parent-room")]
    [InlineData("bed-label")]
    public async Task Exact_identity_and_owner_label_mismatches_never_produce_plausible_room_copy(string scenario)
    {
        await using var db = CreateDb();
        bool bed = scenario.StartsWith("bed-", StringComparison.Ordinal);
        var seeded = Seed(db, bed);
        switch (scenario)
        {
            case "room-unit-id":
                var other = Seed(db, bed: false, index: 2);
                db.Entry(seeded.Unit).Property(x => x.RoomId).CurrentValue = other.Room.Id;
                break;
            case "room-with-bed-id":
                db.Entry(seeded.Unit).Property(x => x.BedId).CurrentValue = Id(999);
                break;
            case "room-label":
                db.Entry(seeded.Unit).Property(x => x.Label).CurrentValue = "Another room";
                break;
            case "bed-id-null":
                db.Entry(seeded.Unit).Property(x => x.BedId).CurrentValue = null;
                break;
            case "bed-id-wrong":
                db.Entry(seeded.Unit).Property(x => x.BedId).CurrentValue = Id(999);
                break;
            case "bed-parent-room":
                db.Entry(seeded.Bed!).Property(x => x.RoomId).CurrentValue = Id(999);
                break;
            case "bed-label":
                db.Entry(seeded.Unit).Property(x => x.Label).CurrentValue = "Another bunk";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        AssertFailure(await new StationInventoryLabelReader(db).ReadAsync(Property, [seeded.Unit.Id]), StationInventoryLabelsState.Incomplete);
    }

    [Fact]
    public async Task Database_unavailability_is_typed_and_contains_no_invented_labels()
    {
        var db = CreateDb();
        await db.DisposeAsync();

        AssertFailure(await new StationInventoryLabelReader(db).ReadAsync(Property, [Id(100)]), StationInventoryLabelsState.Unavailable);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_instead_of_becoming_an_empty_or_unavailable_result()
    {
        await using var db = CreateDb();
        var seeded = Seed(db, bed: true);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new StationInventoryLabelReader(db)
            .ReadAsync(Property, [seeded.Unit.Id], cancellation.Token));
        Assert.Empty(db.ChangeTracker.Entries());
    }

    private static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:D12}");
    private static DbContextOptions<InventoryDbContext> Options() => new DbContextOptionsBuilder<InventoryDbContext>()
        .UseInMemoryDatabase($"station-label-reader-{Guid.NewGuid():N}").Options;
    private static InventoryDbContext CreateDb() => new(Options(), new Scope(Tenant));
    private static object Component(Seeded seeded, string component) => component switch
    {
        "room" => seeded.Room,
        "bed" => seeded.Bed!,
        "configuration" => seeded.Configuration,
        _ => throw new ArgumentOutOfRangeException(nameof(component))
    };
    private static void AssertFailure(StationInventoryLabels result, StationInventoryLabelsState expected)
    {
        Assert.Equal(expected, result.State);
        Assert.Empty(result.Items);
    }
    private static Seeded Seed(InventoryDbContext db, bool bed, int index = 1, string tenant = Tenant,
        string roomName = "Garden dorm", string bedLabel = "Upper bunk")
    {
        Guid roomId = Id(100 + index), bedId = Id(200 + index);
        var room = InventoryRoomTopology.Create(roomId, tenant, Property);
        room.Apply(Property, roomName, "Garden house", "First floor", RoomStatus.Active, 7);
        InventoryBedTopology? bedTopology = null;
        InventoryUnit unit;
        if (bed)
        {
            bedTopology = InventoryBedTopology.Create(bedId, tenant, Property, roomId);
            bedTopology.Apply(Property, roomId, bedLabel, BedStatus.Active, 11);
            unit = InventoryUnit.CreateBed(bedId, tenant, Property, roomId);
            unit.Apply(Property, roomId, bedId, InventoryUnitKind.Bed, bedLabel, true, 11);
            db.BedTopology.Add(bedTopology);
        }
        else
        {
            unit = InventoryUnit.CreateRoom(roomId, tenant, Property);
            unit.Apply(Property, roomId, null, InventoryUnitKind.Room, roomName, true, 7);
        }
        var configuration = RoomInventoryConfiguration.Create(roomId, tenant, Property, Now).Value;
        Assert.True(configuration.Configure(bed ? RoomSalesMode.BedLevel : RoomSalesMode.RoomLevel, 1, Guid.NewGuid(), Now).IsSuccess);
        for (int i = 0; i < 3; i++)
        {
            unit.TouchAvailability();
            configuration.TouchAvailability();
        }
        configuration.ClearDomainEvents();
        db.AddRange(room, unit, configuration);
        return new(unit, room, bedTopology, configuration);
    }

    private sealed record Seeded(InventoryUnit Unit, InventoryRoomTopology Room, InventoryBedTopology? Bed,
        RoomInventoryConfiguration Configuration);
    private sealed class Scope(string scopeId) : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => scopeId;
    }
}
