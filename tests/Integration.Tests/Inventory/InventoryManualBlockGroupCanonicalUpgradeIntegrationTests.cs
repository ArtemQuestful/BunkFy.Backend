namespace Integration.Tests;

using BunkFy.Host.Migrations;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Testcontainers.PostgreSql;
using Xunit;

public sealed partial class InventoryManualBlockGroupMigrationIntegrationTests
{
    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public Task Pending_convergence_preserves_later_canonical_integrity_and_retained_rows() =>
        AssertCanonicalUpgradeAsync(2);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public Task Interrupted_predecessor_upgrade_resumes_both_convergence_and_remaining_integrity() =>
        AssertCanonicalUpgradeAsync(1);

    private static async Task AssertCanonicalUpgradeAsync(int canonicalCount)
    {
        await using PostgreSqlContainer postgres = await StartAsync(
            $"bunkfy_inventory_canonical_upgrade_{canonicalCount}");
        string connectionString = postgres.GetConnectionString();
        await MigrateAsync(connectionString, PreviousMigration);
        await SeedLegacyTopologyAndBlocksAsync(connectionString);
        await ExecuteAsync(connectionString, """
            INSERT INTO inventory.tenant_revisions ("ScopeId", "Revision", "LifecycleStatus")
            VALUES ('tenant-a', 7, 1), ('unrelated-tenant', 9, 1);
            """);

        // Reproduce complete or interrupted predecessor progress with the
        // unchanged canonical operations before upgrading through the real host.
        await using var context = CreateDbContext(connectionString);
        IHistoryRepository history = context.Database.GetService<IHistoryRepository>();
        string[] laterCanonicalMigrations =
        [
            "20260821144203_AddInventoryAuthoritativeStateIntegrity",
            "20260821195120_AddInventoryAllocationAmendmentDecisionTenantIntegrity"
        ];
        await InventoryMigrationHistoryTestData.ApplyUnchangedMigrationsAsync(
            context, laterCanonicalMigrations.Take(canonicalCount).ToArray());

        string[] beforeHistory = (await history.GetAppliedMigrationsAsync())
            .Select(row => row.MigrationId + ":" + row.ProductVersion)
            .ToArray();
        string[] beforeIds = (await history.GetAppliedMigrationsAsync())
            .Select(row => row.MigrationId).ToArray();
        string[] pending = (await context.Database.GetPendingMigrationsAsync()).ToArray();
        string[] expectedPending = canonicalCount == 2
            ? [CurrentMigration]
            : [CurrentMigration, laterCanonicalMigrations[1]];
        Assert.Equal(expectedPending, pending);
        Assert.Equal(laterCanonicalMigrations[canonicalCount - 1],
            (await history.GetAppliedMigrationsAsync())[^1].MigrationId);
        const string retainedBlocksSql = """
            SELECT string_agg(row_to_json(block)::text, '|' ORDER BY block."Id")
            FROM inventory.manual_blocks AS block;
            """;
        string beforeBlocks = await ScalarAsync<string>(connectionString, retainedBlocksSql);

        IConfiguration configuration = new ConfigurationBuilder().Build();
        MigrationsHostOptions runtime = MigrationsHostOptions.FromConfiguration(configuration);
        MigrationsProductionAdmissionOptions prefixOnly =
            MigrationsProductionAdmissionOptions.FromConfiguration(configuration);
        BunkFyMigrationModule[] modules = [new("inventory", context)];
        InvalidOperationException defaultRefusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new BunkFyMigrationCoordinator(NullLogger.Instance,
                    runtime with { Mode = MigrationExecutionMode.Plan }, prefixOnly, isProduction: false)
                .RunAsync(modules, CancellationToken.None));
        Assert.Contains("exact prefix", defaultRefusal.Message, StringComparison.Ordinal);
        Assert.Equal(beforeBlocks, await ScalarAsync<string>(connectionString, retainedBlocksSql));
        Assert.Equal(beforeHistory, (await history.GetAppliedMigrationsAsync())
            .Select(row => row.MigrationId + ":" + row.ProductVersion).ToArray());

        MigrationsProductionAdmissionOptions transition = prefixOnly with
        {
            ExistingHistoryDisposition = MigrationsExistingHistoryDisposition.ApplyInventoryConvergenceV1
        };
        InvalidOperationException productionRefusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new BunkFyMigrationCoordinator(NullLogger.Instance,
                    runtime with { Mode = MigrationExecutionMode.Apply }, transition, isProduction: true)
                .RunAsync(modules, CancellationToken.None));
        Assert.Contains("ApprovalState", productionRefusal.Message, StringComparison.Ordinal);
        Assert.Equal(beforeHistory, (await history.GetAppliedMigrationsAsync())
            .Select(row => row.MigrationId + ":" + row.ProductVersion).ToArray());
        BunkFyMigrationCoordinator Coordinator(MigrationExecutionMode mode) => new(
            NullLogger.Instance, runtime with { Mode = mode }, transition, isProduction: false);
        BunkFyMigrationPlan plan = await Coordinator(MigrationExecutionMode.Plan)
            .RunAsync(modules, CancellationToken.None);
        Assert.Equal(pending.Length, plan.PendingMigrationCount);
        Assert.Equal(pending, plan.Modules[0].PendingMigrations);
        Assert.Equal("inventory-convergence-v1", plan.Modules[0].HistoryTransition);
        Assert.Equal(beforeBlocks, await ScalarAsync<string>(connectionString, retainedBlocksSql));

        BunkFyMigrationPlan applied = await Coordinator(MigrationExecutionMode.Apply)
            .RunAsync(modules, CancellationToken.None);
        Assert.Equal(0, applied.PendingMigrationCount);
        Assert.Equal(plan.TargetCatalogSha256, applied.TargetCatalogSha256);

        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        string[] afterExistingHistory = (await history.GetAppliedMigrationsAsync())
            .Where(row => beforeIds.Contains(row.MigrationId, StringComparer.Ordinal))
            .Select(row => row.MigrationId + ":" + row.ProductVersion)
            .ToArray();
        Assert.Equal(beforeHistory, afterExistingHistory);
        Assert.Equal(context.Database.GetMigrations(),
            (await history.GetAppliedMigrationsAsync()).Select(row => row.MigrationId));
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
        Assert.Equal(8L, await ScalarAsync<long>(connectionString,
            "SELECT \"Revision\" FROM inventory.tenant_revisions WHERE \"ScopeId\" = 'tenant-a';"));
        Assert.Equal(9L, await ScalarAsync<long>(connectionString,
            "SELECT \"Revision\" FROM inventory.tenant_revisions WHERE \"ScopeId\" = 'unrelated-tenant';"));

        BunkFyMigrationPlan replayed = await Coordinator(MigrationExecutionMode.Apply)
            .RunAsync(modules, CancellationToken.None);
        Assert.Equal(applied.CurrentStateSha256, replayed.CurrentStateSha256);
        Assert.Equal(0, replayed.PendingMigrationCount);
        Assert.Equal(beforeBlocks, await ScalarAsync<string>(connectionString, retainedBlocksSql));
        Assert.Equal(1L, await ScalarAsync<long>(connectionString,
            "SELECT count(*) FROM inventory.manual_block_groups;"));
    }
}
