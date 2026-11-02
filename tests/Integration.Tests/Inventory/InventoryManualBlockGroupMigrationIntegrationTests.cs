namespace Integration.Tests;

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

public sealed partial class InventoryManualBlockGroupMigrationIntegrationTests
{
    private const string PreviousMigration =
        "20260811135229_AddInventoryRetirementCancellation";
    private const string CurrentMigration =
        "20260812082324_AddInventoryManualBlockGroupConvergence";
    private const string Tenant = "tenant-a";
    private static readonly Guid PropertyId =
        Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherPropertyId =
        Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid GroupId =
        Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid UnitA =
        Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly Guid UnitB =
        Guid.Parse("30000000-0000-0000-0000-000000000002");
    private static readonly DateOnly Arrival = new(2026, 9, 1);
    private static readonly DateOnly Departure = new(2026, 9, 3);
    private static readonly DateTimeOffset CreatedAtUtc =
        new(2026, 8, 12, 8, 0, 0, TimeSpan.Zero);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Up_backfills_truthful_legacy_parent_and_invalidates_tenant_export_coordinate()
    {
        await using PostgreSqlContainer postgres = await StartAsync(
            "bunkfy_inventory_group_backfill");
        string connectionString = postgres.GetConnectionString();
        await MigrateAsync(connectionString, PreviousMigration);
        await SeedLegacyTopologyAndBlocksAsync(connectionString);
        await ExecuteAsync(connectionString, """
            INSERT INTO inventory.tenant_revisions (
                "ScopeId", "Revision", "LifecycleStatus")
            VALUES ('unrelated-tenant', 9, 1);
            """);
        await ExecuteAsync(connectionString, """
            INSERT INTO inventory.tenant_revisions (
                "ScopeId", "Revision", "LifecycleStatus")
            VALUES ('tenant-a', 7, 1);
            """);

        await MigrateAsync(connectionString, CurrentMigration);

        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            """
            SELECT "TargetKind", "SelectionDigest", "MembershipDigest",
                   "MembershipDigestVersion", "InitialBlockCount",
                   "ActiveBlockCount", "State", "Version", "CreatedAtUtc",
                   "UpdatedAtUtc", "ReleasedAtUtc", "CreatedByActorId",
                   "LastModifiedByActorId"
            FROM inventory.manual_block_groups
            WHERE "ScopeId" = 'tenant-a'
              AND "PropertyId" = @propertyId
              AND "Id" = @groupId;
            """,
            connection);
        command.Parameters.AddWithValue("propertyId", PropertyId);
        command.Parameters.AddWithValue("groupId", GroupId);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0, reader.GetInt32(0));
        Assert.True(await reader.IsDBNullAsync(1));
        string digest = reader.GetString(2);
        Assert.Equal(
            ComputeMembershipDigest(
                targetKind: 0,
                buildingLabel: null,
                floorLabel: null,
                roomId: null,
                inventoryUnitId: null,
                [UnitA, UnitB]),
            digest);
        Assert.Equal(1, reader.GetInt32(3));
        Assert.Equal(2, reader.GetInt32(4));
        Assert.Equal(1, reader.GetInt32(5));
        Assert.Equal(2, reader.GetInt32(6));
        Assert.Equal(1, reader.GetInt64(7));
        Assert.Equal(CreatedAtUtc, reader.GetFieldValue<DateTimeOffset>(8));
        Assert.Equal(CreatedAtUtc.AddHours(1), reader.GetFieldValue<DateTimeOffset>(9));
        Assert.True(await reader.IsDBNullAsync(10));
        Assert.True(await reader.IsDBNullAsync(11));
        Assert.True(await reader.IsDBNullAsync(12));
        await reader.DisposeAsync();

        Assert.Equal(8L, await ScalarAsync<long>(connectionString,
            "SELECT \"Revision\" FROM inventory.tenant_revisions WHERE \"ScopeId\" = 'tenant-a';"));
        Assert.Equal(9L, await ScalarAsync<long>(connectionString,
            "SELECT \"Revision\" FROM inventory.tenant_revisions WHERE \"ScopeId\" = 'unrelated-tenant';"));
        Assert.Equal(0L, await ScalarAsync<long>(connectionString,
            "SELECT count(*) FROM inventory.outbox_messages;"));
        Assert.Equal("tenant-a::::0:0:0:false:1", await ScalarAsync<string>(
            connectionString,
            $"""
            SELECT "ScopeId" || ':' || "Name" || ':' || "Code" || ':' ||
                   "TimeZoneId" || ':' || "Status"::text || ':' ||
                   "SourceVersion"::text || ':' || "DetailsVersion"::text ||
                   ':' || "IsKnown"::text || ':' ||
                   "AvailabilitySelectionVersion"::text
            FROM inventory.property_topology
            WHERE "Id" = '{PropertyId}';
            """));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Up_rejects_cross_property_legacy_membership_atomically()
    {
        await using PostgreSqlContainer postgres = await StartAsync(
            "bunkfy_inventory_group_owner_preflight");
        string connectionString = postgres.GetConnectionString();
        await MigrateAsync(connectionString, PreviousMigration);
        await SeedLegacyTopologyAndBlocksAsync(connectionString);
        await ExecuteAsync(connectionString, $"""
            UPDATE inventory.manual_blocks
            SET "PropertyId" = '{OtherPropertyId}'
            WHERE "ScopeId" = '{Tenant}' AND "Id" =
                '40000000-0000-0000-0000-000000000001';
            """);

        PostgresException rejected = await Assert.ThrowsAsync<PostgresException>(
            () => MigrateAsync(connectionString, CurrentMigration));

        Assert.Equal(PostgresErrorCodes.RaiseException, rejected.SqlState);
        Assert.Equal(
            "invalid Inventory manual-block unit property coordinates prevent upgrade",
            rejected.MessageText);
        Assert.Equal(0L, await ScalarAsync<long>(connectionString, $"""
            SELECT count(*)
            FROM inventory."{InventoryMigrations.HistoryTable}"
            WHERE "MigrationId" = '{CurrentMigration}';
            """));
        Assert.Equal(0L, await ScalarAsync<long>(connectionString, """
            SELECT count(*)
            FROM information_schema.tables
            WHERE table_schema = 'inventory'
              AND table_name = 'manual_block_groups';
            """));
        Assert.Equal(1L, await ScalarAsync<long>(connectionString, """
            SELECT count(*)
            FROM pg_catalog.pg_constraint constraint_row
            INNER JOIN pg_catalog.pg_class table_row
                ON table_row.oid = constraint_row.conrelid
            INNER JOIN pg_catalog.pg_namespace schema_row
                ON schema_row.oid = table_row.relnamespace
            WHERE schema_row.nspname = 'inventory'
              AND table_row.relname = 'manual_blocks'
              AND constraint_row.conname =
                  'FK_manual_blocks_inventory_units_ScopeId_InventoryUnitId';
            """));
        Assert.Equal(0L, await ScalarAsync<long>(connectionString, """
            SELECT count(*)
            FROM pg_catalog.pg_constraint constraint_row
            INNER JOIN pg_catalog.pg_class table_row
                ON table_row.oid = constraint_row.conrelid
            INNER JOIN pg_catalog.pg_namespace schema_row
                ON schema_row.oid = table_row.relnamespace
            WHERE schema_row.nspname = 'inventory'
              AND table_row.relname = 'manual_blocks'
              AND constraint_row.conname =
                  'FK_inventory_manual_blocks_inventory_unit';
            """));
        Assert.Equal(2L, await ScalarAsync<long>(connectionString,
            "SELECT count(*) FROM inventory.manual_blocks;"));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Composite_unit_owner_foreign_key_accepts_same_property_and_rejects_cross_property_writes()
    {
        await using PostgreSqlContainer postgres = await StartAsync(
            "bunkfy_inventory_group_owner_fk");
        string connectionString = postgres.GetConnectionString();
        await MigrateAsync(connectionString, PreviousMigration);
        await MigrateAsync(connectionString, CurrentMigration);
        await SeedUnitsAsync(connectionString, PropertyId, [UnitA]);
        await SeedUnitsAsync(connectionString, OtherPropertyId, [UnitB]);
        Guid validGroup =
            Guid.Parse("20000000-0000-0000-0000-000000000070");
        Guid validBlock =
            Guid.Parse("40000000-0000-0000-0000-000000000070");
        await ExecuteAsync(
            connectionString,
            "BEGIN;" + BuildNativeGroupSql(
                validGroup,
                targetKind: 1,
                buildingLabel: null,
                floorLabel: null,
                roomId: null,
                inventoryUnitId: null,
                [UnitA],
                [validBlock],
                CreatedAtUtc) +
            "COMMIT;");

        Assert.Equal(1L, await ScalarAsync<long>(connectionString, $"""
            SELECT count(*)
            FROM inventory.manual_blocks
            WHERE "ScopeId" = '{Tenant}'
              AND "PropertyId" = '{PropertyId}'
              AND "InventoryUnitId" = '{UnitA}';
            """));

        PostgresException insertRejected =
            await Assert.ThrowsAsync<PostgresException>(
                () => ExecuteAsync(connectionString, $"""
                    INSERT INTO inventory.manual_blocks (
                        "Id", "PropertyId", "InventoryUnitId", "Arrival",
                        "Departure", "Reason", "Status", "Version",
                        "CreatedAtUtc", "ReleasedAtUtc", "ScopeId",
                        "BlockGroupId")
                    VALUES (
                        '40000000-0000-0000-0000-000000000071',
                        '{PropertyId}', '{UnitB}', '{Arrival:yyyy-MM-dd}',
                        '{Departure:yyyy-MM-dd}', 'Maintenance', 1, 1,
                        '{CreatedAtUtc:O}', NULL, '{Tenant}', '{validGroup}');
                    """));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, insertRejected.SqlState);
        Assert.Equal(
            "FK_inventory_manual_blocks_inventory_unit",
            insertRejected.ConstraintName);

        PostgresException updateRejected =
            await Assert.ThrowsAsync<PostgresException>(
                () => ExecuteAsync(connectionString, $"""
                    UPDATE inventory.inventory_units
                    SET "PropertyId" = '{OtherPropertyId}'
                    WHERE "ScopeId" = '{Tenant}' AND "Id" = '{UnitA}';
                    """));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, updateRejected.SqlState);
        Assert.Equal(
            "FK_inventory_manual_blocks_inventory_unit",
            updateRejected.ConstraintName);
        Assert.Equal(PropertyId.ToString(), await ScalarAsync<string>(
            connectionString,
            $"""
            SELECT "PropertyId"::text
            FROM inventory.inventory_units
            WHERE "ScopeId" = '{Tenant}' AND "Id" = '{UnitA}';
            """));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Protocol_rejects_graph_tampering_and_management_receipt_mutation()
    {
        await using PostgreSqlContainer postgres = await StartAsync(
            "bunkfy_inventory_group_protocol");
        string connectionString = postgres.GetConnectionString();
        await MigrateAsync(connectionString, PreviousMigration);
        await SeedLegacyTopologyAndBlocksAsync(connectionString);
        await MigrateAsync(connectionString, CurrentMigration);

        PostgresException wrongCount = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(connectionString, $"""
                UPDATE inventory.manual_block_groups
                SET "InitialBlockCount" = 3
                WHERE "ScopeId" = '{Tenant}' AND "PropertyId" = '{PropertyId}'
                  AND "Id" = '{GroupId}';
                """));
        Assert.Equal(PostgresErrorCodes.RaiseException, wrongCount.SqlState);

        PostgresException childMutation = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(connectionString, $"""
                UPDATE inventory.manual_blocks
                SET "Reason" = 'forged'
                WHERE "ScopeId" = '{Tenant}' AND "Id" =
                    '40000000-0000-0000-0000-000000000001';
                """));
        Assert.Equal(PostgresErrorCodes.RaiseException, childMutation.SqlState);

        Guid operationId = Guid.Parse("50000000-0000-0000-0000-000000000001");
        await ExecuteAsync(connectionString, $"""
            INSERT INTO inventory.management_operations (
                "Id", "ScopeId", "ResourceKind", "ResourceId", "PropertyId",
                "Kind", "ExpectedVersion", "RequestFingerprint",
                "ResultSalesMode", "ResultBlockId", "ResultBlockGroupId",
                "ResultBlockStatus", "ResultAffectedBlockCount", "ResultVersion",
                "ResultTopologyChangeId", "CompletedAtUtc")
            VALUES (
                '{operationId}', '{Tenant}', 1,
                '60000000-0000-0000-0000-000000000001', '{PropertyId}',
                1, 1, '{new string('a', 64)}', 2,
                NULL, NULL, NULL, NULL, 1, NULL, '{CreatedAtUtc:O}');
            """);
        PostgresException receiptUpdate = await Assert.ThrowsAsync<PostgresException>(
            () => ExecuteAsync(connectionString, $"""
                UPDATE inventory.management_operations
                SET "RequestFingerprint" = '{new string('b', 64)}'
                WHERE "ScopeId" = '{Tenant}' AND "Id" = '{operationId}';
                """));
        Assert.Equal(PostgresErrorCodes.RaiseException, receiptUpdate.SqlState);
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Down_allows_untouched_legacy_evidence_and_refuses_native_history()
    {
        await using PostgreSqlContainer postgres = await StartAsync(
            "bunkfy_inventory_group_down");
        string connectionString = postgres.GetConnectionString();
        await MigrateAsync(connectionString, PreviousMigration);
        await SeedLegacyTopologyAndBlocksAsync(connectionString);
        await ExecuteAsync(connectionString, """
            INSERT INTO inventory.tenant_revisions (
                "ScopeId", "Revision", "LifecycleStatus")
            VALUES ('unrelated-tenant', 9, 1);
            """);
        string legacyEvidence = await ScalarAsync<string>(connectionString, """
            SELECT string_agg(
                "Id"::text || '|' || "PropertyId"::text || '|' ||
                "InventoryUnitId"::text || '|' || "Arrival"::text || '|' ||
                "Departure"::text || '|' || "Reason" || '|' ||
                "Status"::text || '|' || "Version"::text || '|' ||
                "CreatedAtUtc"::text || '|' ||
                COALESCE("ReleasedAtUtc"::text, '<null>') || '|' ||
                "ScopeId" || '|' || "BlockGroupId"::text,
                ';' ORDER BY "Id"::text COLLATE "C")
            FROM inventory.manual_blocks;
            """);
        await MigrateAsync(connectionString, CurrentMigration);

        await MigrateAsync(connectionString, PreviousMigration);
        Assert.Equal(2L, await ScalarAsync<long>(connectionString,
            "SELECT count(*) FROM inventory.manual_blocks;"));
        Assert.Equal(legacyEvidence, await ScalarAsync<string>(connectionString, """
            SELECT string_agg(
                "Id"::text || '|' || "PropertyId"::text || '|' ||
                "InventoryUnitId"::text || '|' || "Arrival"::text || '|' ||
                "Departure"::text || '|' || "Reason" || '|' ||
                "Status"::text || '|' || "Version"::text || '|' ||
                "CreatedAtUtc"::text || '|' ||
                COALESCE("ReleasedAtUtc"::text, '<null>') || '|' ||
                "ScopeId" || '|' || "BlockGroupId"::text,
                ';' ORDER BY "Id"::text COLLATE "C")
            FROM inventory.manual_blocks;
            """));
        Assert.Equal(2L, await ScalarAsync<long>(connectionString, """
            SELECT "Revision"
            FROM inventory.tenant_revisions
            WHERE "ScopeId" = 'tenant-a';
            """));
        Assert.Equal(9L, await ScalarAsync<long>(connectionString, """
            SELECT "Revision"
            FROM inventory.tenant_revisions
            WHERE "ScopeId" = 'unrelated-tenant';
            """));
        Assert.Equal(0L, await ScalarAsync<long>(connectionString, """
            SELECT count(*)
            FROM information_schema.tables
            WHERE table_schema = 'inventory'
              AND table_name = 'manual_block_groups';
            """));
        Assert.Equal(1L, await ScalarAsync<long>(connectionString, """
            SELECT count(*)
            FROM pg_catalog.pg_constraint constraint_row
            INNER JOIN pg_catalog.pg_class table_row
                ON table_row.oid = constraint_row.conrelid
            INNER JOIN pg_catalog.pg_namespace schema_row
                ON schema_row.oid = table_row.relnamespace
            WHERE schema_row.nspname = 'inventory'
              AND table_row.relname = 'manual_blocks'
              AND constraint_row.conname =
                  'FK_manual_blocks_inventory_units_ScopeId_InventoryUnitId';
            """));
        Assert.Equal(0L, await ScalarAsync<long>(connectionString, """
            SELECT count(*)
            FROM pg_catalog.pg_constraint constraint_row
            INNER JOIN pg_catalog.pg_class table_row
                ON table_row.oid = constraint_row.conrelid
            INNER JOIN pg_catalog.pg_namespace schema_row
                ON schema_row.oid = table_row.relnamespace
            WHERE schema_row.nspname = 'inventory'
              AND table_row.relname = 'manual_blocks'
              AND constraint_row.conname =
                  'FK_inventory_manual_blocks_inventory_unit';
            """));

        await MigrateAsync(connectionString, CurrentMigration);
        Guid nativeProperty =
            Guid.Parse("10000000-0000-0000-0000-000000000099");
        Guid nativeGroup =
            Guid.Parse("20000000-0000-0000-0000-000000000099");
        Guid nativeUnit =
            Guid.Parse("30000000-0000-0000-0000-000000000099");
        Guid nativeBlock =
            Guid.Parse("40000000-0000-0000-0000-000000000099");
        string nativePayload =
            "bunkfy-inventory-manual-block-group-membership/v1" +
            "|kind=1|building=0:|floor=0:|room=|unit=" +
            "|arrival=2026-09-01|departure=2026-09-03|members=1|" +
            nativeUnit.ToString("N");
        string nativeMembership = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(nativePayload)));
        await ExecuteAsync(connectionString, $"""
            BEGIN;
            INSERT INTO inventory.property_topology (
                "Id", "Name", "Code", "TimeZoneId", "Status",
                "SourceVersion", "DetailsVersion", "IsKnown",
                "AvailabilitySelectionVersion", "ScopeId")
            VALUES ('{nativeProperty}', 'Native property', 'NATIVE', 'UTC',
                    1, 1, 1, true, 1, '{Tenant}');
            INSERT INTO inventory.inventory_units (
                "Id", "PropertyId", "RoomId", "BedId", "Kind", "Label",
                "IsTopologyActive", "SourceVersion", "DetailsVersion",
                "IsKnown", "AvailabilityMutationVersion", "ScopeId")
            VALUES ('{nativeUnit}', '{nativeProperty}', '{nativeUnit}', NULL,
                    1, 'Native', true, 1, 1, true, 1, '{Tenant}');
            INSERT INTO inventory.manual_block_groups (
                "Id", "ScopeId", "PropertyId", "TargetKind",
                "Arrival", "Departure", "Reason", "SelectionDigest",
                "MembershipDigest", "MembershipDigestVersion",
                "InitialBlockCount", "ActiveBlockCount", "State", "Version",
                "CreatedAtUtc", "CreatedByActorId", "LastModifiedByActorId")
            VALUES ('{nativeGroup}', '{Tenant}', '{nativeProperty}', 1,
                    '2026-09-01', '2026-09-03', 'Native maintenance',
                    '{new string('a', 64)}', '{nativeMembership}', 1,
                    1, 1, 1, 1, '{CreatedAtUtc:O}', 'operator-a', 'operator-a');
            INSERT INTO inventory.manual_blocks (
                "Id", "PropertyId", "InventoryUnitId", "Arrival", "Departure",
                "Reason", "Status", "Version", "CreatedAtUtc", "ReleasedAtUtc",
                "ScopeId", "BlockGroupId")
            VALUES ('{nativeBlock}', '{nativeProperty}', '{nativeUnit}',
                    '2026-09-01', '2026-09-03', 'Native maintenance',
                    1, 1, '{CreatedAtUtc:O}', NULL, '{Tenant}', '{nativeGroup}');
            COMMIT;
            """);
        PostgresException refused = await Assert.ThrowsAsync<PostgresException>(
            () => MigrateAsync(connectionString, PreviousMigration));
        Assert.Equal(PostgresErrorCodes.RaiseException, refused.SqlState);
        Assert.Contains(
            "Cannot downgrade Inventory while manual block-group convergence history exists",
            refused.MessageText,
            StringComparison.Ordinal);
    }

    private static async Task<PostgreSqlContainer> StartAsync(string database)
    {
        PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase(database)
            .Build();
        await postgres.StartAsync();
        return postgres;
    }

    private static async Task MigrateAsync(
        string connectionString,
        string migration)
    {
        await using InventoryDbContext context = CreateDbContext(connectionString);
        await context.Database.GetService<IMigrator>().MigrateAsync(migration);
    }

    private static async Task SeedLegacyTopologyAndBlocksAsync(
        string connectionString)
    {
        await ExecuteAsync(connectionString, $"""
            INSERT INTO inventory.inventory_units (
                "Id", "PropertyId", "RoomId", "BedId", "Kind", "Label",
                "IsTopologyActive", "SourceVersion", "DetailsVersion",
                "IsKnown", "AvailabilityMutationVersion", "ScopeId")
            VALUES
                ('{UnitA}', '{PropertyId}', '{UnitA}', NULL, 1, 'A', true, 1, 1, true, 1, '{Tenant}'),
                ('{UnitB}', '{PropertyId}', '{UnitB}', NULL, 1, 'B', true, 1, 1, true, 1, '{Tenant}');

            INSERT INTO inventory.manual_blocks (
                "Id", "PropertyId", "InventoryUnitId", "Arrival", "Departure",
                "Reason", "Status", "Version", "CreatedAtUtc", "ReleasedAtUtc",
                "ScopeId", "BlockGroupId")
            VALUES
                ('40000000-0000-0000-0000-000000000001', '{PropertyId}', '{UnitA}',
                 '{Arrival:yyyy-MM-dd}', '{Departure:yyyy-MM-dd}', 'Maintenance',
                 1, 1, '{CreatedAtUtc:O}', NULL, '{Tenant}', '{GroupId}'),
                ('40000000-0000-0000-0000-000000000002', '{PropertyId}', '{UnitB}',
                 '{Arrival:yyyy-MM-dd}', '{Departure:yyyy-MM-dd}', 'Maintenance',
                 2, 2, '{CreatedAtUtc:O}', '{CreatedAtUtc.AddHours(1):O}',
                 '{Tenant}', '{GroupId}');
            """);
    }

    private static async Task ExecuteAsync(
        string connectionString,
        string sql)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(
        string connectionString,
        string sql)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);
        object? value = await command.ExecuteScalarAsync();
        return (T)Convert.ChangeType(value!, typeof(T),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static InventoryDbContext CreateDbContext(string connectionString)
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
            new TestScopeContext(),
            OpenWorkspaceTerminationFenceReader.Instance);
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => Tenant;
    }
}
