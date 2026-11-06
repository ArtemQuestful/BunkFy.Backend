namespace BunkFy.Modules.Inventory.Tests.Persistence;

using System.Data.Common;
using BunkFy.Modules.Inventory.Application.Handlers;
using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Application.Queries;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using BunkFy.Modules.Inventory.Persistence;
using BunkFy.Modules.Inventory.Persistence.Repositories;
using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;
using Xunit.Abstractions;

[Trait("Category", "Unit")]
public sealed class InventoryRetirementContextReadTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 7, 0, 0, TimeSpan.Zero);
    private static readonly Guid PropertyId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid RoomId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid BedId = Guid.Parse("30000000-0000-0000-0000-000000000001");

    [Fact]
    public void PostgreSql_queries_are_exact_target_ordered_and_bounded_without_connecting()
    {
        using InventoryDbContext db = new(
            new DbContextOptionsBuilder<InventoryDbContext>()
                .UseNpgsql("Host=localhost;Database=query_shape_only;Username=unused").Options,
            new TestScope());
        InventoryRetirementContextReadRepository reads = new(db);
        string room = reads.RoomProcessQuery(PropertyId, RoomId).Take(1).ToQueryString();
        string bed = reads.BedProcessQuery(PropertyId, RoomId, BedId).Take(1).ToQueryString();
        foreach (string sql in new[] { room, bed })
        {
            output.WriteLine(sql);
            Assert.Contains("ORDER BY", sql);
            Assert.Contains("\"State\" IN (1, 2, 3, 5) DESC", sql);
            Assert.Contains("\"CreatedAtUtc\" DESC", sql);
            Assert.Contains("\"Id\" DESC", sql);
            Assert.Contains("LIMIT", sql);
            Assert.Contains("\"ScopeId\"", sql);
            Assert.Contains("\"PropertyId\"", sql);
            Assert.Contains("\"RoomId\"", sql);
        }
        Assert.Contains("\"BedId\"", bed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Verified_target_without_attempt_is_null_and_reads_never_track_or_write(bool bed)
    {
        await using InventoryDbContext db = CreateDb();
        SeedTarget(db);
        db.ChangeTracker.Clear();
        InventoryRetirementTargetSnapshot? result = await new InventoryRetirementContextReadRepository(db)
            .GetTargetAsync(PropertyId, RoomId, bed ? BedId : null, CancellationToken.None);
        Assert.NotNull(result);
        Assert.True(result.IsTopologyActive);
        Assert.Null(result.Process);
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Empty(await db.ManagementOperations.ToArrayAsync());
        Assert.Empty(await db.OutboxMessages.ToArrayAsync());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task Every_lifecycle_state_is_read_directly_for_exact_room_and_bed(int state)
    {
        await using InventoryDbContext db = CreateDb();
        SeedTarget(db);
        RoomRetirementProcess room = RoomProcess(Guid.NewGuid(), Now);
        BedRetirementProcess bed = BedRetirementProcess.Create(
            Guid.NewGuid(), "tenant-a", PropertyId, RoomId, BedId, "Retire bed", "qa-actor", Now).Value;
        SetState(room, state);
        SetState(bed, state);
        db.AddRange(room, bed);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        InventoryRetirementContextReadRepository reads = new(db);
        foreach (Guid? target in new Guid?[] { null, BedId })
        {
            InventoryRetirementTargetSnapshot? result = await reads.GetTargetAsync(
                PropertyId, RoomId, target, CancellationToken.None);
            Assert.NotNull(result?.Process);
            Assert.Equal((InventoryRetirementStatus)state, result.Process.Status);
            Assert.Equal("qa-actor", result.Process.RequestedBy);
            Assert.True(result.Process.Version > 0);
        }
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task Latest_initiated_terminal_uses_id_tie_break_but_active_process_wins_timestamp_skew()
    {
        await using InventoryDbContext db = CreateDb();
        SeedTarget(db);
        for (int index = 0; index < 120; index++)
        {
            RoomRetirementProcess old = RoomProcess(Guid.NewGuid(), Now.AddMinutes(-index - 1));
            SetState(old, 6);
            db.Add(old);
        }
        Guid low = Guid.Parse("40000000-0000-0000-0000-000000000001");
        Guid high = Guid.Parse("50000000-0000-0000-0000-000000000001");
        foreach (Guid id in new[] { low, high })
        {
            RoomRetirementProcess process = RoomProcess(id, Now);
            SetState(process, 6);
            db.Add(process);
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        InventoryRetirementContextReadRepository reads = new(db);
        Assert.Equal(high, (await reads.GetTargetAsync(PropertyId, RoomId, null, CancellationToken.None))?.Process?.TopologyChangeId);
        RoomRetirementProcess active = RoomProcess(Guid.NewGuid(), Now.AddYears(-1));
        db.Add(active);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(active.Id, (await reads.GetTargetAsync(PropertyId, RoomId, null, CancellationToken.None))?.Process?.TopologyChangeId);
    }

    [Fact]
    public async Task Retired_target_remains_inspectable_and_wrong_property_parent_and_scope_are_not_found()
    {
        await using InventoryDbContext db = CreateDb();
        SeedTarget(db);
        InventoryUnit bed = await db.InventoryUnits.SingleAsync(unit => unit.Id == BedId);
        bed.Apply(PropertyId, RoomId, BedId, InventoryUnitKind.Bed, "QA bed", false, 2);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        InventoryRetirementContextReadRepository reads = new(db);
        Assert.False((await reads.GetTargetAsync(PropertyId, RoomId, BedId, CancellationToken.None))!.IsTopologyActive);
        Assert.Null(await reads.GetTargetAsync(Guid.NewGuid(), RoomId, BedId, CancellationToken.None));
        Assert.Null(await reads.GetTargetAsync(PropertyId, Guid.NewGuid(), BedId, CancellationToken.None));
        Assert.Null(await reads.GetTargetAsync(PropertyId, RoomId, Guid.NewGuid(), CancellationToken.None));
        await using InventoryDbContext other = new(
            (DbContextOptions<InventoryDbContext>)db.GetService<IDbContextOptions>(), new TestScope("tenant-b"));
        Assert.Null(await new InventoryRetirementContextReadRepository(other)
            .GetTargetAsync(PropertyId, RoomId, BedId, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disabled_or_empty_scope_fails_before_any_read(bool enabled)
    {
        TestScope scope = new("", enabled);
        GetInventoryRetirementContextQueryHandler handler = new(new FailingContext(), new ImpactSource(), scope);
        Assert.True((await handler.HandleAsync(new(PropertyId, RoomId), CancellationToken.None)).IsFailure);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("transient")]
    [InlineData("missing")]
    public async Task Recognized_impact_unavailability_retains_lifecycle_and_disables_all_actions(string failure)
    {
        GetInventoryRetirementContextQueryHandler handler = new(
            new FixedContext(Summary(InventoryRetirementStatus.Draining)),
            new ImpactSource(failure), new TestScope());
        var result = await handler.HandleAsync(new(PropertyId, RoomId), CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value.Process);
        Assert.Null(result.Value.Impact);
        Assert.Equal(InventoryRetirementImpactStatus.Unavailable, result.Value.ImpactStatus);
        Assert.False(result.Value.CanRequest || result.Value.CanRetry || result.Value.CanCancel);
    }

    [Theory]
    [InlineData("programmer")]
    [InlineData("cancel")]
    [InlineData("permanent-db")]
    public async Task Programmer_nontransient_and_cancellation_errors_are_not_healthy_partial_reads(string failure)
    {
        GetInventoryRetirementContextQueryHandler handler = new(
            new FixedContext(Summary(InventoryRetirementStatus.Draining)),
            new ImpactSource(failure), new TestScope());
        await Assert.ThrowsAnyAsync<Exception>(() => handler.HandleAsync(new(PropertyId, RoomId), CancellationToken.None));
    }

    [Theory]
    [InlineData(0, true, false, false)]
    [InlineData(1, false, false, true)]
    [InlineData(2, false, false, false)]
    [InlineData(3, false, false, false)]
    [InlineData(4, false, false, false)]
    [InlineData(5, false, true, false)]
    [InlineData(6, true, false, false)]
    public async Task Advisory_actions_follow_existing_lifecycle_and_exact_bed_impact(
        int state, bool request, bool retry, bool cancel)
    {
        ImpactSource impact = new();
        GetInventoryRetirementContextQueryHandler handler = new(
            new FixedContext(state == 0 ? null : Summary((InventoryRetirementStatus)state)), impact, new TestScope());
        var result = await handler.HandleAsync(new(PropertyId, RoomId, BedId), CancellationToken.None);
        Assert.Equal((PropertyId, RoomId, BedId), impact.BedCoordinates);
        Assert.Equal(request, result.Value.CanRequest);
        Assert.Equal(retry, result.Value.CanRetry);
        Assert.Equal(cancel, result.Value.CanCancel);
        Assert.Equal(25, result.Value.Impact!.AffectedReservationIds.Count);
        Assert.True(result.Value.Impact.AffectedReservationIdsTruncated);
    }

    private static InventoryRetirementProcessSummaryDto Summary(InventoryRetirementStatus state) =>
        new(Guid.NewGuid(), state, 2, "Retire fixture", "qa-actor", null, null, null, Now, null, null, null);

    private static RoomRetirementProcess RoomProcess(Guid id, DateTimeOffset created) =>
        RoomRetirementProcess.Create(id, "tenant-a", PropertyId, RoomId, "Retire fixture", "qa-actor", created).Value;

    private static void SetState(object process, int state)
    {
        // Controlled persistence state coverage, not a real lifecycle/browser claim.
        process.GetType().GetProperty("State")!.SetValue(process, (InventoryRetirementProcessState)state);
    }

    private static InventoryDbContext CreateDb() => new(
        new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
        new TestScope());

    private static void SeedTarget(InventoryDbContext db)
    {
        InventoryPropertyTopology property = InventoryPropertyTopology.Create(PropertyId, "tenant-a");
        property.Apply("QA property", "qa", "Etc/UTC", PropertyStatus.Active, 1);
        InventoryRoomTopology room = InventoryRoomTopology.Create(RoomId, "tenant-a", PropertyId);
        room.Apply(PropertyId, "QA room", null, null, RoomStatus.Active, 1);
        InventoryUnit roomUnit = InventoryUnit.CreateRoom(RoomId, "tenant-a", PropertyId);
        roomUnit.Apply(PropertyId, RoomId, null, InventoryUnitKind.Room, "QA room", true, 1);
        InventoryUnit bed = InventoryUnit.CreateBed(BedId, "tenant-a", PropertyId, RoomId);
        bed.Apply(PropertyId, RoomId, BedId, InventoryUnitKind.Bed, "QA bed", true, 1);
        db.AddRange(property, room, roomUnit, bed);
        db.SaveChanges();
    }

    private sealed class TestScope(string id = "tenant-a", bool enabled = true) : IScopeContext
    {
        public bool IsEnabled => enabled;
        public string ScopeId => id;
    }

    private sealed class FailingContext : IInventoryRetirementContextReadRepository
    {
        public Task<InventoryRetirementTargetSnapshot?> GetTargetAsync(Guid propertyId, Guid roomId, Guid? bedId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Must not read.");
    }

    private sealed class FixedContext(InventoryRetirementProcessSummaryDto? process) : IInventoryRetirementContextReadRepository
    {
        public Task<InventoryRetirementTargetSnapshot?> GetTargetAsync(Guid propertyId, Guid roomId, Guid? bedId, CancellationToken cancellationToken) =>
            Task.FromResult<InventoryRetirementTargetSnapshot?>(new(true, process, false));
    }

    private sealed class DatabaseFailure(bool transient) : DbException
    {
        public override bool IsTransient => transient;
    }

    private sealed class ImpactSource(string? failure = null) : IInventoryAvailabilityRepository
    {
        public (Guid, Guid, Guid)? BedCoordinates { get; private set; }
        private static readonly Guid[] Reservations = Enumerable.Range(0, 25).Select(_ => Guid.NewGuid()).ToArray();

        private void Fail()
        {
            switch (failure)
            {
                case "timeout":
                    throw new TimeoutException();
                case "transient":
                    throw new DatabaseFailure(true);
                case "permanent-db":
                    throw new DatabaseFailure(false);
                case "programmer":
                    throw new InvalidOperationException();
                case "cancel":
                    throw new OperationCanceledException();
                default:
                    break;
            }
        }

        public Task<RoomInventoryImpactSnapshot?> GetRoomImpactAsync(Guid propertyId, Guid roomId, CancellationToken cancellationToken)
        {
            this.Fail();
            return Task.FromResult<RoomInventoryImpactSnapshot?>(failure == "missing" ? null : new(0, 0, 0, 0, Reservations, true));
        }

        public Task<BedRetirementImpactSnapshot?> GetBedRetirementImpactAsync(Guid propertyId, Guid roomId, Guid bedId,
            Guid? excludedAllocationId, IReadOnlyCollection<Guid> excludedBlockIds, CancellationToken cancellationToken)
        {
            this.BedCoordinates = (propertyId, roomId, bedId);
            this.Fail();
            return Task.FromResult<BedRetirementImpactSnapshot?>(failure == "missing" ? null : new(0, 0, Reservations, true));
        }

        public Task<InventoryAvailabilityContextSnapshot> GetContextAsync(Guid propertyId, IReadOnlyCollection<Guid> inventoryUnitIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<InventoryAvailabilityConflictSnapshot> GetConflictsAsync(Guid propertyId, IReadOnlyCollection<Guid> conflictUnitIds, DateOnly arrival, DateOnly departure, Guid? excludedAllocationId, IReadOnlyCollection<Guid> excludedBlockIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task TouchUnitsAsync(Guid propertyId, IReadOnlyCollection<Guid> inventoryUnitIds, CancellationToken cancellationToken) => throw new InvalidOperationException("Read must not write.");
    }
}
