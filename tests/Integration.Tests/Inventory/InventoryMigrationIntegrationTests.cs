namespace Integration.Tests;

using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using BunkFy.Modules.Inventory.Persistence;
using Gma.Framework.Scoping;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class InventoryMigrationIntegrationTests
{
    private const string PreviousMigration = "20260715064134_AddRoomRetirements";
    private const string BeforeRetirementCancellationMigration =
        "20260809015632_AddInventoryRetirementManagementOperations";
    private const string BeforeAuthoritativeStateIntegrityMigration =
        "20260811135229_AddInventoryRetirementCancellation";
    private const string BeforeAmendmentDecisionTenantIntegrityMigration =
        "20260821144203_AddInventoryAuthoritativeStateIntegrity";

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Allocation_anonymisation_migration_preserves_allocations_and_backfills_operation_locks()
    {
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("bunkfy_inventory_anonymisation_migration_tests")
            .Build();
        await postgreSql.StartAsync();

        Guid allocationId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        Guid reservationId = Guid.Parse("20000000-0000-0000-0000-000000000001");
        Guid allocationRequestId = Guid.Parse("30000000-0000-0000-0000-000000000001");
        Guid propertyId = Guid.Parse("40000000-0000-0000-0000-000000000001");
        DateTimeOffset createdAtUtc = new(2026, 7, 26, 22, 30, 0, TimeSpan.Zero);
        const string scopeId = "tenant-a";

        await using (InventoryDbContext previous = CreateDbContext(postgreSql.GetConnectionString()))
        {
            await previous.Database.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            await previous.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO inventory.allocations (
                    "Id", "ReservationId", "AllocationRequestId", "PropertyId",
                    "Arrival", "Departure", "Status", "Rejection", "Version",
                    "ReleaseRequestId", "CreatedAtUtc", "ReleasedAtUtc", "ScopeId")
                VALUES (
                    {allocationId}, {reservationId}, {allocationRequestId}, {propertyId},
                    {new DateOnly(2026, 8, 1)}, {new DateOnly(2026, 8, 3)},
                    {(int)InventoryAllocationState.Active}, {(int)InventoryAllocationRejection.None}, {1L},
                    {null}, {createdAtUtc}, {null}, {scopeId});
                """);
        }

        await using (InventoryDbContext upgraded = CreateDbContext(postgreSql.GetConnectionString()))
        {
            await upgraded.Database.MigrateAsync();

            InventoryAllocation allocation = await upgraded.Allocations
                .AsNoTracking()
                .SingleAsync(item => item.Id == allocationId);

            Assert.Equal(reservationId, allocation.ReservationId);
            Assert.Equal(allocationRequestId, allocation.AllocationRequestId);
            Assert.Equal(propertyId, allocation.PropertyId);
            Assert.Equal(InventoryAllocationState.Active, allocation.Status);
            Assert.Equal(1, allocation.Version);
            Assert.False(allocation.IsAnonymised);
            Assert.Null(allocation.AnonymisedAtUtc);
        }

        await using NpgsqlConnection connection = new(postgreSql.GetConnectionString());
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            """
            SELECT COUNT(*)
            FROM inventory.allocation_operation_locks
            WHERE "Id" = @allocationId
              AND "AllocationId" = @allocationId
              AND "ScopeId" = @scopeId
              AND "Revision" = 1;
            """,
            connection);
        command.Parameters.AddWithValue("allocationId", allocationId);
        command.Parameters.AddWithValue("scopeId", scopeId);

        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Retirement_cancellation_migration_retains_history_and_enforces_active_attempts()
    {
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("bunkfy_inventory_retirement_cancellation_migration_tests")
            .Build();
        await postgreSql.StartAsync();

        const string scopeId = "tenant-a";
        Guid propertyId = Guid.Parse("40000000-0000-0000-0000-000000000010");
        Guid bedRoomId = Guid.Parse("50000000-0000-0000-0000-000000000010");
        Guid roomRetirementRoomId = Guid.Parse("50000000-0000-0000-0000-000000000011");
        Guid bedId = Guid.Parse("60000000-0000-0000-0000-000000000010");
        Guid historicalBedRetirementId = Guid.Parse("70000000-0000-0000-0000-000000000010");
        Guid historicalRoomRetirementId = Guid.Parse("80000000-0000-0000-0000-000000000010");
        DateTimeOffset createdAtUtc = new(2026, 8, 11, 13, 0, 0, TimeSpan.Zero);

        await using (InventoryDbContext previous = CreateDbContext(postgreSql.GetConnectionString()))
        {
            await previous.Database.GetService<IMigrator>()
                .MigrateAsync(BeforeRetirementCancellationMigration);
            await previous.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO inventory.bed_retirements (
                    "Id", "PropertyId", "RoomId", "BedId", "Reason", "RequestedBy",
                    "State", "RejectionReasonCode", "Version", "CreatedAtUtc",
                    "UpdatedAtUtc", "CompletedAtUtc", "ScopeId")
                VALUES (
                    {historicalBedRetirementId}, {propertyId}, {bedRoomId}, {bedId},
                    {"Replace bed"}, {"user:manager"},
                    {(int)InventoryRetirementProcessState.Draining}, {null}, {1L},
                    {createdAtUtc}, {null}, {null}, {scopeId});

                INSERT INTO inventory.room_retirements (
                    "Id", "PropertyId", "RoomId", "Reason", "RequestedBy", "State",
                    "RejectionReasonCode", "Version", "CreatedAtUtc", "UpdatedAtUtc",
                    "CompletedAtUtc", "ScopeId")
                VALUES (
                    {historicalRoomRetirementId}, {propertyId}, {roomRetirementRoomId},
                    {"Repurpose room"}, {"user:manager"},
                    {(int)InventoryRetirementProcessState.Draining}, {null}, {1L},
                    {createdAtUtc}, {null}, {null}, {scopeId});
                """);
        }

        await using (InventoryDbContext upgraded = CreateDbContext(postgreSql.GetConnectionString()))
        {
            await upgraded.Database.MigrateAsync();

            BedRetirementProcess historicalBed = await upgraded.BedRetirements
                .SingleAsync(process => process.Id == historicalBedRetirementId);
            RoomRetirementProcess historicalRoom = await upgraded.RoomRetirements
                .SingleAsync(process => process.Id == historicalRoomRetirementId);
            InventoryPropertyTopology selectionEpoch = await upgraded
                .PropertyTopology.SingleAsync(property =>
                    property.Id == propertyId);
            DateTimeOffset canceledAtUtc = createdAtUtc.AddHours(1);
            Assert.True(historicalBed.Cancel(
                historicalBed.Version,
                "Bed repaired",
                "user:operator",
                canceledAtUtc).IsSuccess);
            Assert.True(historicalRoom.Cancel(
                historicalRoom.Version,
                "Keep room in service",
                "user:operator",
                canceledAtUtc).IsSuccess);
            selectionEpoch.AdvanceAvailabilitySelection();
            upgraded.ChangeTracker.DetectChanges();
            Assert.True(upgraded.Entry(selectionEpoch)
                .Property(property => property.AvailabilitySelectionVersion)
                .IsModified);
            await upgraded.SaveChangesAsync();

            BedRetirementProcess activeBed = BedRetirementProcess.Create(
                Guid.NewGuid(),
                scopeId,
                propertyId,
                bedRoomId,
                bedId,
                "Replace bed after season",
                "user:manager",
                canceledAtUtc.AddMinutes(1)).Value;
            BedRetirementProcess anotherHistoricalBed = BedRetirementProcess.Create(
                Guid.NewGuid(),
                scopeId,
                propertyId,
                bedRoomId,
                bedId,
                "Temporary retirement",
                "user:manager",
                canceledAtUtc.AddMinutes(2)).Value;
            Assert.True(anotherHistoricalBed.Cancel(
                anotherHistoricalBed.Version,
                "No longer needed",
                "user:operator",
                canceledAtUtc.AddMinutes(3)).IsSuccess);
            RoomRetirementProcess activeRoom = RoomRetirementProcess.Create(
                Guid.NewGuid(),
                scopeId,
                propertyId,
                roomRetirementRoomId,
                "Repurpose after season",
                "user:manager",
                canceledAtUtc.AddMinutes(1)).Value;
            upgraded.AddRange(activeBed, anotherHistoricalBed, activeRoom);
            selectionEpoch.AdvanceAvailabilitySelection();
            upgraded.ChangeTracker.DetectChanges();
            Assert.True(upgraded.Entry(selectionEpoch)
                .Property(property => property.AvailabilitySelectionVersion)
                .IsModified);
            await upgraded.SaveChangesAsync();

            Assert.Equal(3, await upgraded.BedRetirements.CountAsync(process => process.BedId == bedId));
            Assert.Equal(
                2,
                await upgraded.RoomRetirements.CountAsync(
                    process => process.RoomId == roomRetirementRoomId));
        }

        await using (InventoryDbContext duplicateBed = CreateDbContext(postgreSql.GetConnectionString()))
        {
            duplicateBed.BedRetirements.Add(BedRetirementProcess.Create(
                Guid.NewGuid(),
                scopeId,
                propertyId,
                bedRoomId,
                bedId,
                "Competing active attempt",
                "user:manager",
                createdAtUtc.AddHours(2)).Value);
            DbUpdateException failure = await Assert.ThrowsAsync<DbUpdateException>(
                () => duplicateBed.SaveChangesAsync());
            PostgresException providerFailure = Assert.IsType<PostgresException>(failure.InnerException);
            Assert.Equal(PostgresErrorCodes.UniqueViolation, providerFailure.SqlState);
            Assert.Equal(
                "UX_bed_retirements_ScopeId_BedId_active",
                providerFailure.ConstraintName);
        }

        await using (InventoryDbContext duplicateRoom = CreateDbContext(postgreSql.GetConnectionString()))
        {
            duplicateRoom.RoomRetirements.Add(RoomRetirementProcess.Create(
                Guid.NewGuid(),
                scopeId,
                propertyId,
                roomRetirementRoomId,
                "Competing active attempt",
                "user:manager",
                createdAtUtc.AddHours(2)).Value);
            DbUpdateException failure = await Assert.ThrowsAsync<DbUpdateException>(
                () => duplicateRoom.SaveChangesAsync());
            PostgresException providerFailure = Assert.IsType<PostgresException>(failure.InnerException);
            Assert.Equal(PostgresErrorCodes.UniqueViolation, providerFailure.SqlState);
            Assert.Equal(
                "UX_room_retirements_ScopeId_RoomId_active",
                providerFailure.ConstraintName);
        }

        await using (InventoryDbContext ledger = CreateDbContext(postgreSql.GetConnectionString()))
        {
            Guid operationId = Guid.NewGuid();
            await ledger.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO inventory.management_operations (
                    "Id", "ScopeId", "ResourceKind", "ResourceId", "PropertyId",
                    "Kind", "ExpectedVersion", "RequestFingerprint", "ResultVersion",
                    "CompletedAtUtc", "ResultTopologyChangeId")
                VALUES (
                    {operationId}, {scopeId}, {6}, {historicalBedRetirementId}, {propertyId},
                    {10}, {1L}, {new string('a', 64)}, {2L}, {createdAtUtc.AddHours(1)},
                    {historicalBedRetirementId});
                """);

            PostgresException invalidShape = await Assert.ThrowsAsync<PostgresException>(
                () => ledger.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO inventory.management_operations (
                        "Id", "ScopeId", "ResourceKind", "ResourceId", "PropertyId",
                        "Kind", "ExpectedVersion", "RequestFingerprint", "ResultVersion",
                        "CompletedAtUtc", "ResultTopologyChangeId")
                    VALUES (
                        {Guid.NewGuid()}, {scopeId}, {7}, {historicalBedRetirementId}, {propertyId},
                        {10}, {1L}, {new string('b', 64)}, {2L}, {createdAtUtc.AddHours(1)},
                        {historicalBedRetirementId});
                    """));
            Assert.Equal(PostgresErrorCodes.CheckViolation, invalidShape.SqlState);
            Assert.Equal(
                "CK_inventory_management_operations_kind",
                invalidShape.ConstraintName);

            PostgresException unsafeDowngrade = await Assert.ThrowsAsync<PostgresException>(
                () => ledger.Database.GetService<IMigrator>()
                    .MigrateAsync(BeforeRetirementCancellationMigration));
            Assert.Equal(PostgresErrorCodes.RaiseException, unsafeDowngrade.SqlState);
            Assert.Contains(
                "Cannot downgrade Inventory while retirement cancellation history exists",
                unsafeDowngrade.MessageText,
                StringComparison.Ordinal);
        }
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Authoritative_state_integrity_migration_preserves_valid_rows_and_rejects_malformed_state()
    {
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("bunkfy_inventory_state_integrity_migration_tests")
            .Build();
        await postgreSql.StartAsync();

        const string scopeId = "tenant-a";
        Guid propertyId = Guid.Parse("40000000-0000-0000-0000-000000000020");
        Guid roomId = Guid.Parse("50000000-0000-0000-0000-000000000020");
        Guid retiringRoomId = Guid.Parse("50000000-0000-0000-0000-000000000021");
        Guid inventoryUnitId = Guid.Parse("60000000-0000-0000-0000-000000000020");
        Guid allocationId = Guid.Parse("10000000-0000-0000-0000-000000000020");
        Guid reservationId = Guid.Parse("20000000-0000-0000-0000-000000000020");
        Guid allocationRequestId = Guid.Parse("30000000-0000-0000-0000-000000000020");
        Guid amendmentId = Guid.Parse("70000000-0000-0000-0000-000000000020");
        Guid blockId = Guid.Parse("80000000-0000-0000-0000-000000000020");
        Guid blockGroupId = Guid.Parse("81000000-0000-0000-0000-000000000020");
        Guid bedRetirementId = Guid.Parse("90000000-0000-0000-0000-000000000020");
        Guid roomRetirementId = Guid.Parse("91000000-0000-0000-0000-000000000020");
        DateTimeOffset createdAtUtc = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset configuredAtUtc = createdAtUtc.AddMinutes(1);

        await using (InventoryDbContext previous = CreateDbContext(postgreSql.GetConnectionString()))
        {
            await previous.Database.GetService<IMigrator>()
                .MigrateAsync(BeforeAuthoritativeStateIntegrityMigration);
            await previous.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO inventory.inventory_units (
                    "Id", "PropertyId", "RoomId", "BedId", "Kind", "Label",
                    "IsTopologyActive", "SourceVersion", "DetailsVersion", "IsKnown",
                    "AvailabilityMutationVersion", "ScopeId")
                VALUES (
                    {inventoryUnitId}, {propertyId}, {roomId}, {inventoryUnitId},
                    {(int)InventoryUnitKind.Bed}, {"Bed 1"}, TRUE, {1L}, {1L}, TRUE,
                    {1L}, {scopeId});

                INSERT INTO inventory.room_configurations (
                    "Id", "PropertyId", "SalesMode", "Version", "CreatedAtUtc",
                    "UpdatedAtUtc", "ScopeId", "AvailabilityMutationVersion")
                VALUES (
                    {roomId}, {propertyId}, {(int)RoomSalesMode.BedLevel}, {2L},
                    {createdAtUtc}, {configuredAtUtc}, {scopeId}, {2L});

                INSERT INTO inventory.allocations (
                    "Id", "ReservationId", "AllocationRequestId", "PropertyId",
                    "Arrival", "Departure", "Status", "Rejection", "Version",
                    "ReleaseRequestId", "CreatedAtUtc", "ReleasedAtUtc", "ScopeId",
                    "IsAnonymised", "AnonymisedAtUtc")
                VALUES (
                    {allocationId}, {reservationId}, {allocationRequestId}, {propertyId},
                    {new DateOnly(2026, 9, 1)}, {new DateOnly(2026, 9, 3)},
                    {(int)InventoryAllocationState.Active},
                    {(int)InventoryAllocationRejection.None}, {1L}, NULL,
                    {createdAtUtc}, NULL, {scopeId}, FALSE, NULL);

                INSERT INTO inventory.allocation_units ("Id", "ScopeId", "AllocationId")
                VALUES ({inventoryUnitId}, {scopeId}, {allocationId});

                INSERT INTO inventory.allocation_amendment_decisions (
                    "Id", "ScopeId", "AllocationId", "ReservationId", "PropertyId",
                    "RequestFingerprint", "Confirmed", "RejectionReason",
                    "AllocationVersion", "DecidedAtUtc")
                VALUES (
                    {amendmentId}, {scopeId}, {allocationId}, {reservationId}, {propertyId},
                    {new string('a', 64)}, TRUE, NULL, {1L}, {configuredAtUtc});

                INSERT INTO inventory.manual_blocks (
                    "Id", "BlockGroupId", "PropertyId", "InventoryUnitId", "Arrival",
                    "Departure", "Reason", "Status", "Version", "CreatedAtUtc",
                    "ReleasedAtUtc", "ScopeId")
                VALUES (
                    {blockId}, {blockGroupId}, {propertyId}, {inventoryUnitId},
                    {new DateOnly(2026, 10, 1)}, {new DateOnly(2026, 10, 2)},
                    {"Deep clean"}, {(int)ManualInventoryBlockState.Active}, {1L},
                    {createdAtUtc}, NULL, {scopeId});

                INSERT INTO inventory.bed_retirements (
                    "Id", "PropertyId", "RoomId", "BedId", "Reason", "RequestedBy",
                    "State", "RejectionReasonCode", "Version", "CreatedAtUtc",
                    "UpdatedAtUtc", "CompletedAtUtc", "CancellationReason", "CanceledBy",
                    "CanceledAtUtc", "ScopeId")
                VALUES (
                    {bedRetirementId}, {propertyId}, {roomId}, {inventoryUnitId},
                    {"Replace bed"}, {"user:manager"},
                    {(int)InventoryRetirementProcessState.Draining}, NULL, {1L},
                    {createdAtUtc}, NULL, NULL, NULL, NULL, NULL, {scopeId});

                INSERT INTO inventory.room_retirements (
                    "Id", "PropertyId", "RoomId", "Reason", "RequestedBy", "State",
                    "RejectionReasonCode", "Version", "CreatedAtUtc", "UpdatedAtUtc",
                    "CompletedAtUtc", "CancellationReason", "CanceledBy", "CanceledAtUtc",
                    "ScopeId")
                VALUES (
                    {roomRetirementId}, {propertyId}, {retiringRoomId}, {"Repurpose room"},
                    {"user:manager"}, {(int)InventoryRetirementProcessState.Draining}, NULL,
                    {1L}, {createdAtUtc}, NULL, NULL, NULL, NULL, NULL, {scopeId});

                INSERT INTO inventory.allocation_operation_locks (
                    "Id", "AllocationId", "Revision", "ScopeId")
                VALUES ({allocationId}, {allocationId}, {1L}, {scopeId});
                """);
        }

        await using InventoryDbContext upgraded = CreateDbContext(postgreSql.GetConnectionString());
        // Verify the exact canonical checks before the pending convergence
        // triggers add their separate, earlier transition refusals.
        await InventoryMigrationHistoryTestData.ApplyUnchangedMigrationsAsync(
            upgraded,
            "20260821144203_AddInventoryAuthoritativeStateIntegrity",
            "20260821195120_AddInventoryAllocationAmendmentDecisionTenantIntegrity");

        Assert.Equal(1, await upgraded.Allocations.CountAsync(item => item.Id == allocationId));
        Assert.Equal(1, await upgraded.AllocationUnits.CountAsync(item => item.AllocationId == allocationId));
        Assert.Equal(1, await upgraded.AllocationAmendmentDecisions.CountAsync(item => item.Id == amendmentId));
        Assert.Equal(1, await upgraded.ManualBlocks.CountAsync(item => item.Id == blockId));
        Assert.Equal(1, await upgraded.RoomConfigurations.CountAsync(item => item.Id == roomId));
        Assert.Equal(1, await upgraded.BedRetirements.CountAsync(item => item.Id == bedRetirementId));
        Assert.Equal(1, await upgraded.RoomRetirements.CountAsync(item => item.Id == roomRetirementId));

        await AssertCheckViolationAsync(
            upgraded,
            "CK_allocations_coordinates",
            $"""
            UPDATE inventory.allocations SET "PropertyId" = {Guid.Empty}
            WHERE "Id" = {allocationId};
            """);
        await AssertCheckViolationAsync(
            upgraded,
            "CK_allocations_stay_and_version",
            $"""
            UPDATE inventory.allocations SET "Departure" = "Arrival"
            WHERE "Id" = {allocationId};
            """);
        await AssertCheckViolationAsync(
            upgraded,
            "CK_allocations_lifecycle",
            $"""
            UPDATE inventory.allocations
            SET "Rejection" = {(int)InventoryAllocationRejection.UnitNotFound}
            WHERE "Id" = {allocationId};
            """);
        await AssertCheckViolationAsync(
            upgraded,
            "CK_allocation_units_coordinates",
            $"""
            UPDATE inventory.allocation_units SET "Id" = {Guid.Empty}
            WHERE "ScopeId" = {scopeId} AND "AllocationId" = {allocationId};
            """);
        await AssertCheckViolationAsync(
            upgraded,
            "CK_allocation_amendment_decisions_fingerprint",
            $"""
            UPDATE inventory.allocation_amendment_decisions
            SET "RequestFingerprint" = {new string('A', 64)}
            WHERE "Id" = {amendmentId};
            """);
        await AssertCheckViolationAsync(
            upgraded,
            "CK_allocation_amendment_decisions_outcome",
            $"""
            UPDATE inventory.allocation_amendment_decisions
            SET "Confirmed" = FALSE, "AllocationVersion" = NULL
            WHERE "Id" = {amendmentId};
            """);
        await AssertCheckViolationAsync(
            upgraded,
            "CK_manual_blocks_coordinates",
            $"""
            UPDATE inventory.manual_blocks SET "BlockGroupId" = {Guid.Empty}
            WHERE "Id" = {blockId};
            """);
        await AssertCheckViolationAsync(
            upgraded,
            "CK_manual_blocks_content",
            $"""
            UPDATE inventory.manual_blocks SET "Reason" = {" "}
            WHERE "Id" = {blockId};
            """);
        await AssertCheckViolationAsync(
            upgraded,
            "CK_manual_blocks_lifecycle",
            $"""
            UPDATE inventory.manual_blocks SET "Status" = {(int)ManualInventoryBlockState.Released}
            WHERE "Id" = {blockId};
            """);
        await AssertCheckViolationAsync(
            upgraded,
            "CK_room_configurations_coordinates",
            $"""
            UPDATE inventory.room_configurations SET "PropertyId" = {Guid.Empty}
            WHERE "Id" = {roomId};
            """);
        await AssertCheckViolationAsync(
            upgraded,
            "CK_room_configurations_state",
            $"""
            UPDATE inventory.room_configurations SET "AvailabilityMutationVersion" = {1L}
            WHERE "Id" = {roomId};
            """);
        await AssertCheckViolationAsync(
            upgraded,
            "CK_bed_retirements_coordinates",
            $"""
            UPDATE inventory.bed_retirements SET "BedId" = {Guid.Empty}
            WHERE "Id" = {bedRetirementId};
            """);
        await AssertCheckViolationAsync(
            upgraded,
            "CK_bed_retirements_request",
            $"""
            UPDATE inventory.bed_retirements SET "Reason" = {" "}
            WHERE "Id" = {bedRetirementId};
            """);
        await AssertCheckViolationAsync(
            upgraded,
            "CK_bed_retirements_lifecycle",
            $"""
            UPDATE inventory.bed_retirements
            SET "State" = {(int)InventoryRetirementProcessState.Completed}
            WHERE "Id" = {bedRetirementId};
            """);
        await AssertCheckViolationAsync(
            upgraded,
            "CK_room_retirements_coordinates",
            $"""
            UPDATE inventory.room_retirements SET "RoomId" = {Guid.Empty}
            WHERE "Id" = {roomRetirementId};
            """);
        await AssertCheckViolationAsync(
            upgraded,
            "CK_room_retirements_request",
            $"""
            UPDATE inventory.room_retirements SET "RequestedBy" = {" "}
            WHERE "Id" = {roomRetirementId};
            """);
        await AssertCheckViolationAsync(
            upgraded,
            "CK_room_retirements_lifecycle",
            $"""
            UPDATE inventory.room_retirements
            SET "State" = {(int)InventoryRetirementProcessState.Completed}
            WHERE "Id" = {roomRetirementId};
            """);
        await AssertCheckViolationAsync(
            upgraded,
            "CK_allocation_operation_locks_coordinates",
            $"""
            UPDATE inventory.allocation_operation_locks
            SET "AllocationId" = {Guid.NewGuid()}
            WHERE "Id" = {allocationId};
            """);

        await upgraded.Database.MigrateAsync();
        Assert.Equal(1, await upgraded.ManualBlockGroups.CountAsync());
        Assert.Equal(1, await upgraded.ManualBlocks.CountAsync(item => item.Id == blockId));
        Assert.Empty(await upgraded.Database.GetPendingMigrationsAsync());
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Amendment_decision_tenant_integrity_migration_isolates_same_ids_and_guards_downgrade()
    {
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder(
                "postgres:16-alpine")
            .WithDatabase("bunkfy_inventory_amendment_tenant_tests")
            .Build();
        await postgreSql.StartAsync();

        const string tenantA = "tenant-a";
        const string tenantB = "tenant-b";
        Guid amendmentId =
            Guid.Parse("70000000-0000-0000-0000-000000000030");
        Guid allocationId =
            Guid.Parse("10000000-0000-0000-0000-000000000030");
        Guid reservationId =
            Guid.Parse("20000000-0000-0000-0000-000000000030");
        Guid propertyId =
            Guid.Parse("40000000-0000-0000-0000-000000000030");
        DateTimeOffset decidedAtUtc =
            new(2026, 8, 21, 19, 45, 0, TimeSpan.Zero);

        await using (InventoryDbContext previous = CreateDbContext(
            postgreSql.GetConnectionString(),
            tenantA))
        {
            await previous.Database.GetService<IMigrator>()
                .MigrateAsync(BeforeAmendmentDecisionTenantIntegrityMigration);
            await previous.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO inventory.allocation_amendment_decisions (
                    "Id", "ScopeId", "AllocationId", "ReservationId", "PropertyId",
                    "RequestFingerprint", "Confirmed", "RejectionReason",
                    "AllocationVersion", "DecidedAtUtc")
                VALUES (
                    {amendmentId}, {tenantA}, {allocationId}, {reservationId},
                    {propertyId}, {new string('a', 64)}, FALSE,
                    {(int)InventoryAllocationRejectionReason.AllocationConflict},
                    NULL, {decidedAtUtc});
                """);
        }

        await using (InventoryDbContext upgraded = CreateDbContext(
            postgreSql.GetConnectionString(),
            tenantA))
        {
            await upgraded.Database.MigrateAsync();
            await upgraded.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO inventory.allocation_amendment_decisions (
                    "Id", "ScopeId", "AllocationId", "ReservationId", "PropertyId",
                    "RequestFingerprint", "Confirmed", "RejectionReason",
                    "AllocationVersion", "DecidedAtUtc")
                VALUES (
                    {amendmentId}, {tenantB}, {allocationId}, {reservationId},
                    {propertyId}, {new string('b', 64)}, FALSE,
                    {(int)InventoryAllocationRejectionReason.AllocationConflict},
                    NULL, {decidedAtUtc.AddMinutes(1)});
                """);

            InventoryAllocationAmendmentDecision local = await upgraded
                .AllocationAmendmentDecisions
                .AsNoTracking()
                .SingleAsync(item => item.Id == amendmentId);
            Assert.Equal(tenantA, local.ScopeId);
            Assert.Equal(new string('a', 64), local.RequestFingerprint);
            Assert.Equal(
                2,
                await upgraded.AllocationAmendmentDecisions
                    .IgnoreQueryFilters()
                    .CountAsync(item => item.Id == amendmentId));

            await AssertCheckViolationAsync(
                upgraded,
                "CK_allocation_amendment_decisions_coordinates",
                $"""
                UPDATE inventory.allocation_amendment_decisions
                SET "ScopeId" = {"tenant a"}
                WHERE "ScopeId" = {tenantA} AND "Id" = {amendmentId};
                """);
            await AssertCheckViolationAsync(
                upgraded,
                "CK_allocation_amendment_decisions_decided_at",
                $"""
                UPDATE inventory.allocation_amendment_decisions
                SET "DecidedAtUtc" = TIMESTAMPTZ '0001-01-01 00:00:00+00'
                WHERE "ScopeId" = {tenantA} AND "Id" = {amendmentId};
                """);
        }

        await using (InventoryDbContext tenantBContext = CreateDbContext(
            postgreSql.GetConnectionString(),
            tenantB))
        {
            InventoryAllocationAmendmentDecision local = await tenantBContext
                .AllocationAmendmentDecisions
                .AsNoTracking()
                .SingleAsync(item => item.Id == amendmentId);
            Assert.Equal(tenantB, local.ScopeId);
            Assert.Equal(new string('b', 64), local.RequestFingerprint);

            PostgresException unsafeDowngrade = await Assert.ThrowsAsync<
                PostgresException>(() => tenantBContext.Database
                    .GetService<IMigrator>()
                    .MigrateAsync(
                        BeforeAmendmentDecisionTenantIntegrityMigration));
            Assert.Equal(PostgresErrorCodes.RaiseException, unsafeDowngrade.SqlState);
            Assert.Contains(
                "Cannot downgrade Inventory while amendment request ids are shared across tenants",
                unsafeDowngrade.MessageText,
                StringComparison.Ordinal);
        }

        await using (InventoryDbContext roundTrip = CreateDbContext(
            postgreSql.GetConnectionString(),
            tenantA))
        {
            await roundTrip.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM inventory.allocation_amendment_decisions
                WHERE "ScopeId" = {tenantB} AND "Id" = {amendmentId};
                """);
            await roundTrip.Database.GetService<IMigrator>()
                .MigrateAsync(BeforeAmendmentDecisionTenantIntegrityMigration);
            Assert.Equal(
                1,
                await roundTrip.AllocationAmendmentDecisions
                    .IgnoreQueryFilters()
                    .CountAsync(item => item.Id == amendmentId));

            await roundTrip.Database.MigrateAsync();
            Assert.Equal(
                tenantA,
                (await roundTrip.AllocationAmendmentDecisions
                    .AsNoTracking()
                    .SingleAsync(item => item.Id == amendmentId)).ScopeId);
        }
    }

    private static async Task AssertCheckViolationAsync(
        InventoryDbContext dbContext,
        string expectedConstraint,
        FormattableString command)
    {
        PostgresException failure = await Assert.ThrowsAsync<PostgresException>(
            () => dbContext.Database.ExecuteSqlInterpolatedAsync(command));
        Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
        Assert.Equal(expectedConstraint, failure.ConstraintName);
    }

    private static InventoryDbContext CreateDbContext(
        string connectionString,
        string scopeId = "tenant-a")
    {
        DbContextOptions<InventoryDbContext> options =
            new DbContextOptionsBuilder<InventoryDbContext>()
                .UseNpgsql(connectionString, provider => provider
                    .MigrationsAssembly(InventoryMigrations.PostgreSqlAssembly)
                    .MigrationsHistoryTable(
                        InventoryMigrations.HistoryTable,
                        InventoryMigrations.Schema))
                .Options;
        return new(
            options,
            new TestScopeContext(scopeId),
            OpenWorkspaceTerminationFenceReader.Instance);
    }

    private sealed class TestScopeContext(string scopeId) : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId { get; } = scopeId;
    }
}
