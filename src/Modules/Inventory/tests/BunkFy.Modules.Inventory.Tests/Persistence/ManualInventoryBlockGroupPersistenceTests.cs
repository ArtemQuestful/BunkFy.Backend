namespace BunkFy.Modules.Inventory.Tests;

using System.Reflection;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using BunkFy.Modules.Inventory.Persistence;
using BunkFy.Modules.Inventory.Persistence.Repositories;
using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ManualInventoryBlockGroupPersistenceTests
{
    private const string Digest =
        "0123456789abcdef0123456789abcdef" +
        "0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset Now =
        new(2026, 8, 12, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Model_uses_tenant_property_composite_identity_and_restrictive_links()
    {
        using InventoryDbContext dbContext = CreateContext();
        IModel model = dbContext.GetService<IDesignTimeModel>().Model;
        IEntityType group = model.FindEntityType(
            typeof(ManualInventoryBlockGroup))!;
        IEntityType block = model.FindEntityType(
            typeof(ManualInventoryBlock))!;
        IForeignKey selfLink = Assert.Single(
            group.GetForeignKeys(),
            candidate => candidate.PrincipalEntityType == group);
        IForeignKey memberLink = Assert.Single(
            block.GetForeignKeys(),
            candidate => candidate.PrincipalEntityType == group);

        Assert.Equal(
            ["ScopeId", "PropertyId", "Id"],
            group.FindPrimaryKey()!.Properties.Select(
                property => property.Name));
        Assert.NotEmpty(group.GetDeclaredQueryFilters());
        Assert.True(group.FindProperty(
            nameof(ManualInventoryBlockGroup.Version))!.IsConcurrencyToken);
        Assert.Equal(
            ["ScopeId", "PropertyId", "ReplacesGroupId"],
            selfLink.Properties.Select(property => property.Name));
        Assert.Equal(
            ["ScopeId", "PropertyId", "Id"],
            selfLink.PrincipalKey.Properties.Select(property => property.Name));
        Assert.Equal(DeleteBehavior.Restrict, selfLink.DeleteBehavior);
        Assert.Equal(
            ["ScopeId", "PropertyId", "BlockGroupId"],
            memberLink.Properties.Select(property => property.Name));
        Assert.Equal(
            ["ScopeId", "PropertyId", "Id"],
            memberLink.PrincipalKey.Properties.Select(property => property.Name));
        Assert.Equal(DeleteBehavior.Restrict, memberLink.DeleteBehavior);
        Assert.Contains(
            group.GetIndexes(),
            index => index.IsUnique &&
                index.GetFilter() == "\"ReplacesGroupId\" IS NOT NULL" &&
                index.Properties.Select(property => property.Name)
                    .SequenceEqual([
                        "ScopeId",
                        "PropertyId",
                        "ReplacesGroupId"
                    ]));
        Assert.Contains(
            block.GetIndexes(),
            index => index.IsUnique &&
                index.Properties.Select(property => property.Name)
                    .SequenceEqual([
                        "ScopeId",
                        "PropertyId",
                        "BlockGroupId",
                        "InventoryUnitId"
                    ]));
        Assert.DoesNotContain(
            group.GetIndexes(),
            index => index.IsUnique &&
                index.Properties.Select(property => property.Name)
                    .SequenceEqual(["Id"]));
        ICheckConstraint targetConstraint = Assert.Single(
            group.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_inventory_manual_block_groups_target");
        Assert.Equal(3, CountOccurrences(
            targetConstraint.Sql,
            "!~ '[[:cntrl:]]'"));
        Assert.Contains(
            group.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_inventory_manual_block_groups_counts");
        Assert.Contains(
            group.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_inventory_manual_block_groups_actors");
        Assert.Contains(
            group.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_inventory_manual_block_groups_digests");
    }

    [Fact]
    public async Task Batch_release_is_one_legal_versioned_transition()
    {
        await using InventoryDbContext dbContext = CreateContext();
        ManualInventoryBlockGroup group = CreateGroup(
            Guid.NewGuid(),
            Guid.NewGuid(),
            initialBlockCount: 3,
            createdAtUtc: Now);
        dbContext.ManualBlockGroups.Add(group);
        await dbContext.SaveChangesAsync();

        Assert.True(group.Release(
            expectedVersion: 1,
            releasedBlockCount: 3,
            Now.AddMinutes(1),
            "user:operator").IsSuccess);

        await dbContext.SaveChangesAsync();

        Assert.Equal(2, group.Version);
        Assert.Equal(0, group.ActiveBlockCount);
        Assert.Equal(ManualInventoryBlockGroupState.Released, group.State);
    }

    [Fact]
    public async Task Definitions_and_group_rows_are_append_only()
    {
        await using InventoryDbContext dbContext = CreateContext();
        ManualInventoryBlockGroup group = CreateGroup(
            Guid.NewGuid(),
            Guid.NewGuid(),
            initialBlockCount: 1,
            createdAtUtc: Now);
        dbContext.ManualBlockGroups.Add(group);
        await dbContext.SaveChangesAsync();
        dbContext.Entry(group).Property(
            nameof(ManualInventoryBlockGroup.Reason)).CurrentValue =
            "rewritten history";

        InvalidOperationException mutation = await Assert.ThrowsAsync<
            InvalidOperationException>(() => dbContext.SaveChangesAsync());

        Assert.Contains("definitions are immutable", mutation.Message);
        dbContext.ChangeTracker.Clear();
        group = await dbContext.ManualBlockGroups.SingleAsync();
        dbContext.ManualBlockGroups.Remove(group);

        InvalidOperationException deletion = await Assert.ThrowsAsync<
            InvalidOperationException>(() => dbContext.SaveChangesAsync());

        Assert.Contains("cannot be deleted", deletion.Message);
    }

    [Fact]
    public async Task Repository_derives_successor_and_pages_with_bound_cursor()
    {
        Guid propertyId = Guid.NewGuid();
        Guid predecessorId =
            Guid.Parse("10000000-0000-0000-0000-000000000001");
        Guid successorId =
            Guid.Parse("20000000-0000-0000-0000-000000000001");
        Guid newestId =
            Guid.Parse("30000000-0000-0000-0000-000000000001");
        await using InventoryDbContext dbContext = CreateContext();
        ManualInventoryBlockGroup predecessor = CreateGroup(
            predecessorId,
            propertyId,
            initialBlockCount: 1,
            createdAtUtc: Now);
        dbContext.ManualBlockGroups.Add(predecessor);
        await dbContext.SaveChangesAsync();
        ManualInventoryBlockGroup successor = CreateGroup(
            successorId,
            propertyId,
            initialBlockCount: 1,
            createdAtUtc: Now.AddMinutes(1),
            replacesGroupId: predecessorId);
        Assert.True(predecessor.ReplaceWith(
            predecessor.Version,
            successorId,
            releasedBlockCount: 1,
            Now.AddMinutes(1),
            "user:operator").IsSuccess);
        dbContext.ManualBlockGroups.Add(successor);
        dbContext.ManualBlockGroups.Add(CreateGroup(
            newestId,
            propertyId,
            initialBlockCount: 1,
            createdAtUtc: Now.AddMinutes(2)));
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        ManualInventoryBlockGroupRepository repository = new(dbContext);

        ManualInventoryBlockGroupDto predecessorDto = Assert.IsType<
            ManualInventoryBlockGroupDto>(await repository.GetDtoAsync(
                propertyId,
                predecessorId,
                CancellationToken.None));
        ManualInventoryBlockGroupListResponse first =
            await repository.ListAsync(
                propertyId,
                status: null,
                cursor: null,
                pageSize: 2,
                CancellationToken.None);
        ManualInventoryBlockGroupListResponse second =
            await repository.ListAsync(
                propertyId,
                status: null,
                Assert.IsType<string>(first.NextCursor),
                pageSize: 2,
                CancellationToken.None);

        Assert.Equal(successorId, predecessorDto.ReplacedByGroupId);
        Assert.Equal([newestId, successorId], first.BlockGroups.Select(
            group => group.BlockGroupId));
        Assert.Equal([predecessorId], second.BlockGroups.Select(
            group => group.BlockGroupId));
        Assert.Null(second.NextCursor);
        await Assert.ThrowsAsync<FormatException>(() => repository.ListAsync(
            Guid.NewGuid(),
            status: null,
            first.NextCursor,
            pageSize: 2,
            CancellationToken.None));
    }

    [Fact]
    public void PostgreSql_successor_lookup_translates_as_a_bounded_set_query()
    {
        DbContextOptions<InventoryDbContext> options =
            new DbContextOptionsBuilder<InventoryDbContext>()
                .UseNpgsql("Host=localhost;Database=inventory_query_shape")
                .Options;
        using InventoryDbContext dbContext = new(
            options,
            new TestScopeContext());
        ManualInventoryBlockGroupRepository repository = new(dbContext);
        MethodInfo successorReadModels = typeof(ManualInventoryBlockGroupRepository)
            .GetMethod(
                "SuccessorReadModels",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
        Guid propertyId = Guid.NewGuid();
        Guid[] boundedPredecessorIds = [Guid.NewGuid(), Guid.NewGuid()];

        IQueryable query = Assert.IsType<IQueryable>(
            successorReadModels.Invoke(
                repository,
                [propertyId, boundedPredecessorIds]),
            exactMatch: false);
        string sql = query.ToQueryString();

        Assert.Contains("\"ReplacesGroupId\"", sql, StringComparison.Ordinal);
        Assert.Contains("ANY", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void PostgreSql_retirement_evidence_is_selected_only_and_bounded()
    {
        DbContextOptions<InventoryDbContext> options =
            new DbContextOptionsBuilder<InventoryDbContext>()
                .UseNpgsql("Host=localhost;Database=inventory_query_shape")
                .Options;
        using InventoryDbContext dbContext = new(
            options,
            new TestScopeContext());
        InventoryReadRepository repository = new(dbContext);
        Guid propertyId = Guid.NewGuid();
        Guid[] selectedBedIds = [Guid.NewGuid(), Guid.NewGuid()];
        Guid[] selectedRoomIds = [Guid.NewGuid(), Guid.NewGuid()];
        InventoryRetirementProcessState[] activeStates =
        [
            InventoryRetirementProcessState.Draining,
            InventoryRetirementProcessState.FinalizationRequested,
            InventoryRetirementProcessState.FinalizedAwaitingTopology,
            InventoryRetirementProcessState.Rejected
        ];
        string bedSql = InvokeEvidenceQuery(
            repository,
            "ActiveBedRetirementEvidence",
            propertyId,
            selectedBedIds,
            activeStates).ToQueryString();
        string roomSql = InvokeEvidenceQuery(
            repository,
            "ActiveRoomRetirementEvidence",
            propertyId,
            selectedRoomIds,
            activeStates).ToQueryString();

        Assert.Contains("\"BedId\" = ANY", bedSql, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "\"RoomId\" = ANY",
            bedSql,
            StringComparison.Ordinal);
        Assert.Contains("LIMIT", bedSql, StringComparison.Ordinal);
        Assert.Contains("\"RoomId\" = ANY", roomSql, StringComparison.Ordinal);
        Assert.Contains("LIMIT", roomSql, StringComparison.Ordinal);
    }

    [Fact]
    public void Member_cursor_is_bound_to_property_group_and_filter()
    {
        Guid propertyId = Guid.NewGuid();
        Guid groupId = Guid.NewGuid();
        Guid blockId = Guid.NewGuid();
        string cursor = ManualInventoryBlockGroupCursor.EncodeMember(
            propertyId,
            groupId,
            ManualInventoryBlockStatus.Active,
            blockId);

        Assert.Equal(
            blockId,
            ManualInventoryBlockGroupCursor.DecodeMember(
                cursor,
                propertyId,
                groupId,
                ManualInventoryBlockStatus.Active));
        Assert.Throws<FormatException>(() =>
            ManualInventoryBlockGroupCursor.DecodeMember(
                cursor,
                propertyId,
                groupId,
                ManualInventoryBlockStatus.Released));
        Assert.Throws<FormatException>(() =>
            ManualInventoryBlockGroupCursor.DecodeMember(
                cursor,
                Guid.NewGuid(),
                groupId,
                ManualInventoryBlockStatus.Active));
        Assert.Throws<FormatException>(() =>
            ManualInventoryBlockGroupCursor.DecodeMember(
                cursor,
                propertyId,
                Guid.NewGuid(),
                ManualInventoryBlockStatus.Active));
    }

    [Fact]
    public void Group_cursor_is_bound_to_property_and_status_filter()
    {
        Guid propertyId = Guid.NewGuid();
        Guid groupId = Guid.NewGuid();
        string cursor = ManualInventoryBlockGroupCursor.EncodeGroup(
            propertyId,
            ManualInventoryBlockGroupStatus.Active,
            Now,
            groupId);

        Assert.Equal(
            (Now, groupId),
            ManualInventoryBlockGroupCursor.DecodeGroup(
                cursor,
                propertyId,
                ManualInventoryBlockGroupStatus.Active));
        Assert.Throws<FormatException>(() =>
            ManualInventoryBlockGroupCursor.DecodeGroup(
                cursor,
                propertyId,
                ManualInventoryBlockGroupStatus.Released));
        Assert.Throws<FormatException>(() =>
            ManualInventoryBlockGroupCursor.DecodeGroup(
                cursor,
                Guid.NewGuid(),
                ManualInventoryBlockGroupStatus.Active));
    }

    [Fact]
    public async Task Business_date_uses_property_zone_and_invalid_zone_fails_closed()
    {
        Guid westernPropertyId = Guid.NewGuid();
        Guid invalidPropertyId = Guid.NewGuid();
        await using InventoryDbContext dbContext = CreateContext();
        InventoryPropertyTopology western = InventoryPropertyTopology.Create(
            westernPropertyId,
            "tenant-a");
        western.Apply(
            "Western",
            "western",
            "America/Los_Angeles",
            PropertyStatus.Active,
            sourceVersion: 1);
        InventoryPropertyTopology invalid = InventoryPropertyTopology.Create(
            invalidPropertyId,
            "tenant-a");
        invalid.Apply(
            "Invalid",
            "invalid",
            "Not/A_Real_Zone",
            PropertyStatus.Active,
            sourceVersion: 1);
        dbContext.PropertyTopology.AddRange(western, invalid);
        await dbContext.SaveChangesAsync();
        InventoryBusinessDateProvider provider = new(dbContext);
        DateTimeOffset nowUtc =
            new(2026, 1, 1, 0, 30, 0, TimeSpan.Zero);

        DateOnly? date = await provider.GetAsync(
            westernPropertyId,
            nowUtc,
            CancellationToken.None);
        DateOnly? unavailable = await provider.GetAsync(
            invalidPropertyId,
            nowUtc,
            CancellationToken.None);

        Assert.Equal(new DateOnly(2025, 12, 31), date);
        Assert.Null(unavailable);
    }

    private static ManualInventoryBlockGroup CreateGroup(
        Guid id,
        Guid propertyId,
        int initialBlockCount,
        DateTimeOffset createdAtUtc,
        Guid? replacesGroupId = null) => ManualInventoryBlockGroup.Create(
            id,
            "tenant-a",
            propertyId,
            ManualInventoryBlockGroupTargetKind.Property,
            buildingLabel: null,
            floorLabel: null,
            roomId: null,
            inventoryUnitId: null,
            new DateOnly(2026, 8, 20),
            new DateOnly(2026, 8, 21),
            "maintenance",
            Digest,
            Digest,
            ManualInventoryBlockGroup.CurrentMembershipDigestVersion,
            initialBlockCount,
            replacesGroupId,
            createdAtUtc,
            "user:operator").Value;

    private static InventoryDbContext CreateContext()
    {
        DbContextOptions<InventoryDbContext> options =
            new DbContextOptionsBuilder<InventoryDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;
        return new InventoryDbContext(options, new TestScopeContext());
    }

    private static int CountOccurrences(string value, string fragment)
    {
        int count = 0;
        int offset = 0;
        while ((offset = value.IndexOf(
            fragment,
            offset,
            StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += fragment.Length;
        }

        return count;
    }

    private static IQueryable InvokeEvidenceQuery(
        InventoryReadRepository repository,
        string methodName,
        Guid propertyId,
        Guid[] selectedIds,
        InventoryRetirementProcessState[] activeStates)
    {
        MethodInfo queryMethod = typeof(InventoryReadRepository).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return Assert.IsType<IQueryable>(queryMethod.Invoke(
            repository,
            [propertyId, selectedIds, activeStates]),
            exactMatch: false);
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }
}
