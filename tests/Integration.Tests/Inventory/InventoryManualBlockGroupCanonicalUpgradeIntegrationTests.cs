namespace Integration.Tests;

using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.PostgreSql;
using Xunit;

public sealed partial class InventoryManualBlockGroupMigrationIntegrationTests
{
    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Pending_convergence_preserves_later_canonical_integrity_and_retained_rows()
    {
        await using PostgreSqlContainer postgres = await StartAsync(
            "bunkfy_inventory_canonical_upgrade");
        string connectionString = postgres.GetConnectionString();
        await MigrateAsync(connectionString, PreviousMigration);
        await SeedLegacyTopologyAndBlocksAsync(connectionString);

        // Current dev already applied these unchanged migrations. Reproduce their
        // actual operations before applying the older pending convergence migration.
        await using var context = CreateDbContext(connectionString);
        IHistoryRepository history = context.Database.GetService<IHistoryRepository>();
        string[] laterCanonicalMigrations =
        [
            "20260821144203_AddInventoryAuthoritativeStateIntegrity",
            "20260821195120_AddInventoryAllocationAmendmentDecisionTenantIntegrity"
        ];
        await InventoryMigrationHistoryTestData.ApplyUnchangedMigrationsAsync(
            context, laterCanonicalMigrations);

        string[] beforeHistory = (await history.GetAppliedMigrationsAsync())
            .Select(row => row.MigrationId + ":" + row.ProductVersion)
            .ToArray();
        string[] pending = (await context.Database.GetPendingMigrationsAsync()).ToArray();
        Assert.Equal([CurrentMigration], pending);
        Assert.Equal(laterCanonicalMigrations[^1],
            (await history.GetAppliedMigrationsAsync())[^1].MigrationId);
        const string retainedBlocksSql = """
            SELECT string_agg(row_to_json(block)::text, '|' ORDER BY block."Id")
            FROM inventory.manual_blocks AS block;
            """;
        string beforeBlocks = await ScalarAsync<string>(connectionString, retainedBlocksSql);

        await context.Database.MigrateAsync();

        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        string[] afterExistingHistory = (await history.GetAppliedMigrationsAsync())
            .Where(row => row.MigrationId != CurrentMigration)
            .Select(row => row.MigrationId + ":" + row.ProductVersion)
            .ToArray();
        Assert.Equal(beforeHistory, afterExistingHistory);
        Assert.Equal(beforeBlocks, await ScalarAsync<string>(connectionString, retainedBlocksSql));
        Assert.Equal(1L, await ScalarAsync<long>(connectionString,
            "SELECT count(*) FROM inventory.manual_block_groups;"));
        Assert.Equal(2L, await ScalarAsync<long>(connectionString, """
            SELECT count(*) FROM pg_constraint
            WHERE connamespace = 'inventory'::regnamespace
              AND conname IN (
                  'CK_manual_blocks_lifecycle',
                  'CK_allocation_amendment_decisions_decided_at');
            """));
        Assert.Equal(0L, await ScalarAsync<long>(connectionString,
            "SELECT count(*) FROM inventory.outbox_messages;"));

        await context.Database.MigrateAsync();
        Assert.Equal(beforeBlocks, await ScalarAsync<string>(connectionString, retainedBlocksSql));
        Assert.Equal(1L, await ScalarAsync<long>(connectionString,
            "SELECT count(*) FROM inventory.manual_block_groups;"));
    }
}
