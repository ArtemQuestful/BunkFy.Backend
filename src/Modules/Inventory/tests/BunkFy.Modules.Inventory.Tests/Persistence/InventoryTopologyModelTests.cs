namespace BunkFy.Modules.Inventory.Tests;

using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using BunkFy.Modules.Inventory.Persistence;
using BunkFy.Modules.Inventory.Persistence.Repositories;
using BunkFy.Modules.Inventory.Persistence.TenantTermination;
using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

[Trait("Category", "Unit")]
public sealed class InventoryTopologyModelTests
{
    [Fact]
    public void Authoritative_inventory_tables_declare_database_invariants()
    {
        using InventoryDbContext dbContext = CreateDbContext();
        IModel designModel = dbContext.GetService<IDesignTimeModel>().Model;

        AssertConstraints(
            designModel.FindEntityType(typeof(InventoryAllocation))!,
            "CK_allocations_coordinates",
            "CK_allocations_stay_and_version",
            "CK_allocations_lifecycle",
            "CK_allocations_anonymisation_state");
        AssertConstraints(
            designModel.FindEntityType(typeof(Domain.Entities.InventoryAllocationUnit))!,
            "CK_allocation_units_coordinates");
        AssertConstraints(
            designModel.FindEntityType(typeof(InventoryAllocationAmendmentDecision))!,
            "CK_allocation_amendment_decisions_coordinates",
            "CK_allocation_amendment_decisions_decided_at",
            "CK_allocation_amendment_decisions_fingerprint",
            "CK_allocation_amendment_decisions_outcome");
        AssertConstraints(
            designModel.FindEntityType(typeof(ManualInventoryBlock))!,
            "CK_manual_blocks_coordinates",
            "CK_manual_blocks_content",
            "CK_manual_blocks_lifecycle");
        AssertConstraints(
            designModel.FindEntityType(typeof(RoomInventoryConfiguration))!,
            "CK_room_configurations_coordinates",
            "CK_room_configurations_state");
        AssertConstraints(
            designModel.FindEntityType(typeof(InventoryAllocationOperationLock))!,
            "CK_allocation_operation_locks_coordinates",
            "CK_allocation_operation_locks_revision");
        AssertConstraints(
            designModel.FindEntityType(typeof(BedRetirementProcess))!,
            "CK_bed_retirements_coordinates",
            "CK_bed_retirements_request",
            "CK_bed_retirements_lifecycle");
        AssertConstraints(
            designModel.FindEntityType(typeof(RoomRetirementProcess))!,
            "CK_room_retirements_coordinates",
            "CK_room_retirements_request",
            "CK_room_retirements_lifecycle");
    }

    [Fact]
    public void Management_operations_are_scoped_composite_receipts_with_constraints()
    {
        using InventoryDbContext dbContext = CreateDbContext();
        IModel designModel = dbContext.GetService<IDesignTimeModel>().Model;
        IEntityType operation = designModel.FindEntityType(
            typeof(InventoryManagementOperation))!;

        Assert.Equal(
            [
                nameof(InventoryManagementOperation.ScopeId),
                nameof(InventoryManagementOperation.ResourceKind),
                nameof(InventoryManagementOperation.ResourceId),
                nameof(InventoryManagementOperation.Id)
            ],
            operation.FindPrimaryKey()!.Properties.Select(
                property => property.Name));
        Assert.NotEmpty(operation.GetDeclaredQueryFilters());
        Assert.True(operation.FindProperty(
            nameof(InventoryManagementOperation.RequestFingerprint))!
            .IsFixedLength());
        Assert.Contains(
            operation.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_inventory_management_operations_coordinates");
        Assert.Contains(
            operation.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_inventory_management_operations_fingerprint");
        Assert.Contains(
            operation.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_inventory_management_operations_result");
    }

    [Fact]
    public async Task Management_operation_receipts_are_append_only()
    {
        await using InventoryDbContext dbContext = CreateDbContext();
        InventoryManagementOperation operation = new(
            new InventoryManagementOperationRecord(
                Guid.NewGuid(),
                "tenant-a",
                Guid.NewGuid(),
                InventoryManagementResourceKind.Room,
                Guid.NewGuid(),
                InventoryManagementMutationKind.RoomSalesModeConfiguration,
                1,
                new string('a', 64),
                InventorySalesMode.RoomLevel,
                null,
                null,
                null,
                null,
                2,
                DateTimeOffset.UtcNow));
        dbContext.ManagementOperations.Add(operation);
        await dbContext.SaveChangesAsync();
        dbContext.Entry(operation).Property(
            nameof(InventoryManagementOperation.ResultVersion))
            .CurrentValue = 3L;

        InvalidOperationException exception = await Assert.ThrowsAsync<
            InvalidOperationException>(() => dbContext.SaveChangesAsync());

        Assert.Contains("append-only", exception.Message);
    }

    [Fact]
    public void Tenant_revision_is_scope_filtered()
    {
        using InventoryDbContext dbContext = CreateDbContext();

        IEntityType revisionEntity = dbContext.Model.FindEntityType(
            typeof(InventoryTenantRevision))!;

        Assert.NotEmpty(revisionEntity.GetDeclaredQueryFilters());
    }

    [Fact]
    public void Tenant_destruction_progress_and_receipt_are_scope_unique_and_constrained()
    {
        using InventoryDbContext dbContext = CreateDbContext();
        IModel designModel = dbContext.GetService<IDesignTimeModel>().Model;
        IEntityType operation = designModel.FindEntityType(
            typeof(InventoryTenantDestroyOperation))!;
        IEntityType receipt = designModel.FindEntityType(
            typeof(InventoryTenantDestroyReceipt))!;

        Assert.Equal(
            [nameof(InventoryTenantDestroyOperation.OperationId)],
            operation.FindPrimaryKey()!.Properties.Select(
                property => property.Name));
        Assert.True(operation.FindProperty(
            nameof(InventoryTenantDestroyOperation.ConcurrencyVersion))!
            .IsConcurrencyToken);
        Assert.Contains(
            operation.GetIndexes(),
            index => index.IsUnique &&
                index.Properties.Select(property => property.Name)
                    .SequenceEqual([
                        nameof(InventoryTenantDestroyOperation.ScopeId)
                    ]));
        Assert.Contains(
            operation.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_inventory_tenant_destroy_operation_batch");
        Assert.Contains(
            operation.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_inventory_tenant_destroy_operation_progress");

        Assert.Equal(
            [nameof(InventoryTenantDestroyReceipt.OperationId)],
            receipt.FindPrimaryKey()!.Properties.Select(
                property => property.Name));
        Assert.Contains(
            receipt.GetIndexes(),
            index => index.IsUnique &&
                index.Properties.Select(property => property.Name)
                    .SequenceEqual([
                        nameof(InventoryTenantDestroyReceipt.ScopeId)
                    ]));
        Assert.Contains(
            receipt.GetCheckConstraints(),
            constraint => constraint.Name ==
                "CK_inventory_tenant_destroy_receipt_progress");
    }

    [Fact]
    public void Delayed_room_event_can_authoritatively_fill_a_bed_created_placeholder()
    {
        Guid propertyId = Guid.NewGuid();
        InventoryRoomTopology room = InventoryRoomTopology.Create(Guid.NewGuid(), "tenant-a", propertyId);

        room.Apply(propertyId, "101", "Main", "1", RoomStatus.Active, 1);

        Assert.Equal(1, room.SourceVersion);
        Assert.Equal(1, room.DetailsVersion);
        Assert.Equal("101", room.Name);
        Assert.True(room.IsKnown);
        Assert.Equal(RoomStatus.Active, room.Status);
    }

    [Fact]
    public void Stale_room_event_cannot_replace_newer_details_or_status()
    {
        Guid propertyId = Guid.NewGuid();
        InventoryRoomTopology room = InventoryRoomTopology.Create(Guid.NewGuid(), "tenant-a", propertyId);
        room.Apply(propertyId, "New", null, null, RoomStatus.Retired, 3);

        room.Apply(propertyId, "Old", null, null, RoomStatus.Active, 2);

        Assert.Equal("New", room.Name);
        Assert.Equal(RoomStatus.Retired, room.Status);
        Assert.Equal(3, room.SourceVersion);
        Assert.Equal(3, room.DetailsVersion);
    }

    [Fact]
    public void Room_configuration_version_is_a_concurrency_token()
    {
        using InventoryDbContext dbContext = CreateDbContext();

        IEntityType entity = dbContext.Model.FindEntityType(typeof(RoomInventoryConfiguration))!;

        Assert.True(entity.FindProperty(nameof(RoomInventoryConfiguration.Version))!.IsConcurrencyToken);
        Assert.True(entity.FindProperty(nameof(RoomInventoryConfiguration.AvailabilityMutationVersion))!.IsConcurrencyToken);
        Assert.Contains(
            entity.GetIndexes(),
            index => index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(RoomInventoryConfiguration.ScopeId), nameof(RoomInventoryConfiguration.PropertyId)]));
    }

    [Fact]
    public void Manual_block_has_version_concurrency_and_property_owned_unit_foreign_key()
    {
        using InventoryDbContext dbContext = CreateDbContext();

        IEntityType blockEntity = dbContext.Model.FindEntityType(typeof(ManualInventoryBlock))!;
        IEntityType unitEntity = dbContext.Model.FindEntityType(typeof(InventoryUnit))!;
        IForeignKey foreignKey = Assert.Single(
            blockEntity.GetForeignKeys(),
            candidate => candidate.PrincipalEntityType == unitEntity);

        Assert.True(blockEntity.FindProperty(nameof(ManualInventoryBlock.Version))!.IsConcurrencyToken);
        Assert.True(unitEntity.FindProperty(nameof(InventoryUnit.AvailabilityMutationVersion))!.IsConcurrencyToken);
        Assert.Equal(
            ["ScopeId", "PropertyId", "InventoryUnitId"],
            foreignKey.Properties.Select(property => property.Name));
        Assert.Equal(
            ["ScopeId", "PropertyId", "Id"],
            foreignKey.PrincipalKey.Properties.Select(property => property.Name));
        Assert.Equal(
            "FK_inventory_manual_blocks_inventory_unit",
            foreignKey.GetConstraintName());
        Assert.Contains(
            unitEntity.GetKeys(),
            key => !key.IsPrimaryKey() &&
                key.Properties.Select(property => property.Name)
                    .SequenceEqual(["ScopeId", "PropertyId", "Id"]));
        Assert.DoesNotContain(
            unitEntity.GetKeys(),
            key => !key.IsPrimaryKey() &&
                key.Properties.Select(property => property.Name)
                    .SequenceEqual(["ScopeId", "Id"]));
        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
    }

    [Fact]
    public void Property_projection_cursor_is_generated_and_unique()
    {
        using InventoryDbContext dbContext = CreateDbContext();

        IEntityType propertyEntity = dbContext.Model.FindEntityType(typeof(InventoryPropertyTopology))!;
        IProperty cursor = propertyEntity.FindProperty(nameof(InventoryPropertyTopology.ProjectionOrdinal))!;

        Assert.Equal(ValueGenerated.OnAdd, cursor.ValueGenerated);
        Assert.Contains(
            propertyEntity.GetIndexes(),
            index => index.IsUnique && index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(InventoryPropertyTopology.ProjectionOrdinal)]));
    }

    [Fact]
    public void Property_selection_version_is_durable_concurrency_and_advances_exactly_once()
    {
        using InventoryDbContext dbContext = CreateDbContext();
        IEntityType propertyEntity = dbContext.Model.FindEntityType(
            typeof(InventoryPropertyTopology))!;
        IProperty selectionVersion = propertyEntity.FindProperty(
            nameof(InventoryPropertyTopology.AvailabilitySelectionVersion))!;
        InventoryPropertyTopology property = InventoryPropertyTopology.Create(
            Guid.NewGuid(),
            "tenant-a");

        property.AdvanceAvailabilitySelection();

        Assert.True(selectionVersion.IsConcurrencyToken);
        Assert.Equal(2, property.AvailabilitySelectionVersion);
    }

    [Fact]
    public void Allocation_model_has_idempotency_indexes_scoped_units_and_version_concurrency()
    {
        using InventoryDbContext dbContext = CreateDbContext();

        IEntityType allocationEntity = dbContext.Model.FindEntityType(typeof(InventoryAllocation))!;
        IEntityType allocationUnitEntity = dbContext.Model.FindEntityType(typeof(Domain.Entities.InventoryAllocationUnit))!;
        IForeignKey parent = Assert.Single(
            allocationUnitEntity.GetForeignKeys(),
            candidate => candidate.PrincipalEntityType == allocationEntity);

        Assert.True(allocationEntity.FindProperty(nameof(InventoryAllocation.Version))!.IsConcurrencyToken);
        Assert.Contains(
            allocationEntity.GetIndexes(),
            index => index.IsUnique && index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(InventoryAllocation.ScopeId), nameof(InventoryAllocation.AllocationRequestId)]));
        Assert.Contains(
            allocationEntity.GetIndexes(),
            index => index.IsUnique && index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(InventoryAllocation.ScopeId), nameof(InventoryAllocation.ReservationId)]));
        Assert.Equal(["ScopeId", "AllocationId"], parent.Properties.Select(property => property.Name));
        Assert.Equal(["ScopeId", "Id"], parent.PrincipalKey.Properties.Select(property => property.Name));
        Assert.Equal(DeleteBehavior.Cascade, parent.DeleteBehavior);
    }

    [Fact]
    public void Allocation_amendment_decisions_have_durable_request_identity_and_history_index()
    {
        using InventoryDbContext dbContext = CreateDbContext();

        IEntityType decision = dbContext.Model.FindEntityType(typeof(InventoryAllocationAmendmentDecision))!;

        Assert.Equal([
            nameof(InventoryAllocationAmendmentDecision.ScopeId),
            nameof(InventoryAllocationAmendmentDecision.Id)
        ], decision.FindPrimaryKey()!
            .Properties.Select(property => property.Name));
        Assert.True(decision.FindProperty(nameof(InventoryAllocationAmendmentDecision.RequestFingerprint))!.IsFixedLength());
        Assert.Contains(
            decision.GetIndexes(),
            index => index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(InventoryAllocationAmendmentDecision.ScopeId),
                nameof(InventoryAllocationAmendmentDecision.AllocationId),
                nameof(InventoryAllocationAmendmentDecision.DecidedAtUtc)
            ]));
    }

    [Fact]
    public void Bed_retirement_process_has_concurrency_and_active_history_indexes()
    {
        using InventoryDbContext dbContext = CreateDbContext();

        IEntityType process = dbContext.Model.FindEntityType(typeof(BedRetirementProcess))!;

        Assert.True(process.FindProperty(nameof(BedRetirementProcess.Version))!.IsConcurrencyToken);
        Assert.Equal(
            BedRetirementProcess.ReasonMaxLength,
            process.FindProperty(nameof(BedRetirementProcess.CancellationReason))!.GetMaxLength());
        Assert.Equal(
            BedRetirementProcess.ActorIdMaxLength,
            process.FindProperty(nameof(BedRetirementProcess.CanceledBy))!.GetMaxLength());
        Assert.Contains(
            process.GetIndexes(),
            index => !index.IsUnique && index.Properties.Select(property => property.Name)
                .SequenceEqual([
                    nameof(BedRetirementProcess.ScopeId),
                    nameof(BedRetirementProcess.BedId),
                    nameof(BedRetirementProcess.State)
                ]));
        Assert.Contains(
            process.GetIndexes(),
            index => index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(BedRetirementProcess.ScopeId),
                nameof(BedRetirementProcess.PropertyId),
                nameof(BedRetirementProcess.RoomId),
                nameof(BedRetirementProcess.State)
            ]));
        IIndex activeTarget = Assert.Single(
            process.GetIndexes(),
            index => index.GetDatabaseName() ==
                "UX_bed_retirements_ScopeId_BedId_active");
        Assert.True(activeTarget.IsUnique);
        Assert.Equal(
            [
                nameof(BedRetirementProcess.ScopeId),
                nameof(BedRetirementProcess.BedId)
            ],
            activeTarget.Properties.Select(property => property.Name));
        Assert.Equal(
            "\"State\" IN (1, 2, 3, 5)",
            activeTarget.GetFilter());
    }

    [Fact]
    public void Room_retirement_process_has_concurrency_and_active_history_indexes()
    {
        using InventoryDbContext dbContext = CreateDbContext();

        IEntityType process = dbContext.Model.FindEntityType(typeof(RoomRetirementProcess))!;

        Assert.True(process.FindProperty(nameof(RoomRetirementProcess.Version))!.IsConcurrencyToken);
        Assert.Equal(
            RoomRetirementProcess.ReasonMaxLength,
            process.FindProperty(nameof(RoomRetirementProcess.CancellationReason))!.GetMaxLength());
        Assert.Equal(
            RoomRetirementProcess.ActorIdMaxLength,
            process.FindProperty(nameof(RoomRetirementProcess.CanceledBy))!.GetMaxLength());
        Assert.Contains(
            process.GetIndexes(),
            index => !index.IsUnique && index.Properties.Select(property => property.Name)
                .SequenceEqual([
                    nameof(RoomRetirementProcess.ScopeId),
                    nameof(RoomRetirementProcess.RoomId),
                    nameof(RoomRetirementProcess.State)
                ]));
        Assert.Contains(
            process.GetIndexes(),
            index => index.Properties.Select(property => property.Name).SequenceEqual([
                nameof(RoomRetirementProcess.ScopeId),
                nameof(RoomRetirementProcess.PropertyId),
                nameof(RoomRetirementProcess.State)
            ]));
        IIndex activeTarget = Assert.Single(
            process.GetIndexes(),
            index => index.GetDatabaseName() ==
                "UX_room_retirements_ScopeId_RoomId_active");
        Assert.True(activeTarget.IsUnique);
        Assert.Equal(
            [
                nameof(RoomRetirementProcess.ScopeId),
                nameof(RoomRetirementProcess.RoomId)
            ],
            activeTarget.Properties.Select(property => property.Name));
        Assert.Equal(
            "\"State\" IN (1, 2, 3, 5)",
            activeTarget.GetFilter());
    }

    [Fact]
    public async Task Bed_target_lookup_ignores_canceled_history_but_process_lookup_retains_it()
    {
        await using InventoryDbContext dbContext = CreateDbContext();
        Guid propertyId = Guid.NewGuid();
        Guid roomId = Guid.NewGuid();
        Guid bedId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        BedRetirementProcess historical = BedRetirementProcess.Create(
            Guid.NewGuid(),
            "tenant-a",
            propertyId,
            roomId,
            bedId,
            "Replace bed",
            "user:operator",
            now).Value;
        Assert.True(historical.Cancel(
            historical.Version,
            "Keep bed",
            "user:manager",
            now.AddMinutes(1)).IsSuccess);
        BedRetirementProcess active = BedRetirementProcess.Create(
            Guid.NewGuid(),
            "tenant-a",
            propertyId,
            roomId,
            bedId,
            "Replace bed later",
            "user:operator",
            now.AddMinutes(2)).Value;
        dbContext.BedRetirements.AddRange(historical, active);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        BedRetirementRepository repository = new(dbContext);

        BedRetirementProcess? byTarget = await repository.GetByBedAsync(
            propertyId,
            bedId,
            CancellationToken.None);
        BedRetirementProcess? byProcess = await repository.GetAsync(
            propertyId,
            historical.Id,
            CancellationToken.None);

        Assert.Equal(active.Id, byTarget?.Id);
        Assert.Equal(active.Id, await repository.GetTopologyChangeIdByBedAsync(
            propertyId,
            bedId,
            CancellationToken.None));
        Assert.Equal(historical.Id, byProcess?.Id);
        Assert.Equal(InventoryRetirementProcessState.Canceled, byProcess?.State);
    }

    [Fact]
    public async Task Room_target_lookup_ignores_canceled_history_but_process_lookup_retains_it()
    {
        await using InventoryDbContext dbContext = CreateDbContext();
        Guid propertyId = Guid.NewGuid();
        Guid roomId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        RoomRetirementProcess historical = RoomRetirementProcess.Create(
            Guid.NewGuid(),
            "tenant-a",
            propertyId,
            roomId,
            "Repurpose room",
            "user:operator",
            now).Value;
        Assert.True(historical.Cancel(
            historical.Version,
            "Keep room",
            "user:manager",
            now.AddMinutes(1)).IsSuccess);
        RoomRetirementProcess active = RoomRetirementProcess.Create(
            Guid.NewGuid(),
            "tenant-a",
            propertyId,
            roomId,
            "Repurpose room later",
            "user:operator",
            now.AddMinutes(2)).Value;
        dbContext.RoomRetirements.AddRange(historical, active);
        await dbContext.SaveChangesAsync();
        dbContext.ChangeTracker.Clear();
        RoomRetirementRepository repository = new(dbContext);

        RoomRetirementProcess? byTarget = await repository.GetByRoomAsync(
            propertyId,
            roomId,
            CancellationToken.None);
        RoomRetirementProcess? byProcess = await repository.GetAsync(
            propertyId,
            historical.Id,
            CancellationToken.None);

        Assert.Equal(active.Id, byTarget?.Id);
        Assert.Equal(active.Id, await repository.GetTopologyChangeIdByRoomAsync(
            propertyId,
            roomId,
            CancellationToken.None));
        Assert.Equal(historical.Id, byProcess?.Id);
        Assert.Equal(InventoryRetirementProcessState.Canceled, byProcess?.State);
    }

    private static InventoryDbContext CreateDbContext()
    {
        DbContextOptions<InventoryDbContext> options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase($"inventory-model-{Guid.NewGuid():N}")
            .Options;

        return new InventoryDbContext(options, new TestScopeContext());
    }

    private static void AssertConstraints(
        IEntityType entity,
        params string[] expectedNames)
    {
        string[] actualNames = entity.GetCheckConstraints()
            .Select(constraint => constraint.Name!)
            .ToArray();
        Assert.All(expectedNames, expected => Assert.Contains(expected, actualNames));
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }
}
