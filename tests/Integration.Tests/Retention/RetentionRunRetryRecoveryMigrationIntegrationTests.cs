namespace Integration.Tests;

using BunkFy.Modules.Retention.Persistence;
using Gma.Framework.Scoping;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class RetentionRunRetryRecoveryMigrationIntegrationTests
{
    private const string PreviousMigration =
        "20260804105915_AddRetentionTenantDestructionLifecycle";

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Upgrade_preserves_in_progress_tenant_destruction_stages()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_retention_recovery_migration")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);

        await using RetentionDbContext dbContext = CreateDbContext(
            postgreSql.GetConnectionString());
        IMigrator migrator = dbContext.Database.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration).ConfigureAwait(false);
        DateTimeOffset now =
            new(2026, 8, 15, 16, 0, 0, TimeSpan.Zero);
        string requestSha256 = new('a', 64);
        string proofSha256 = new('b', 64);

        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO retention.tenant_destroy_operations (
                "OperationId", "ScopeId", "RequestSha256",
                "SelectedRevision", "ResultingRevision", "BatchSize",
                "Stage", "RemovedRecordCount", "CompletedBatchCount",
                "ProofVersion", "RemovalProofSha256", "StartedAtUtc",
                "UpdatedAtUtc", "ConcurrencyVersion")
            VALUES
                ({Guid.Parse("10000000-0000-0000-0000-000000000001")},
                 {"tenant-inbox"}, {requestSha256}, {0L}, {1L}, {500},
                 {1}, {0L}, {0}, {1}, {proofSha256}, {now}, {now}, {1}),
                ({Guid.Parse("10000000-0000-0000-0000-000000000002")},
                 {"tenant-schedules"}, {requestSha256}, {0L}, {1L}, {500},
                 {2}, {0L}, {0}, {1}, {proofSha256}, {now}, {now}, {1}),
                ({Guid.Parse("10000000-0000-0000-0000-000000000003")},
                 {"tenant-projection"}, {requestSha256}, {0L}, {1L}, {500},
                 {5}, {0L}, {0}, {1}, {proofSha256}, {now}, {now}, {1}),
                ({Guid.Parse("10000000-0000-0000-0000-000000000004")},
                 {"tenant-completed"}, {requestSha256}, {0L}, {1L}, {500},
                 {6}, {0L}, {0}, {1}, {proofSha256}, {now}, {now}, {1});
            """).ConfigureAwait(false);

        await migrator.MigrateAsync().ConfigureAwait(false);

        Assert.Equal(2, await ReadStageAsync(dbContext, "tenant-inbox")
            .ConfigureAwait(false));
        Assert.Equal(4, await ReadStageAsync(dbContext, "tenant-schedules")
            .ConfigureAwait(false));
        Assert.Equal(7, await ReadStageAsync(dbContext, "tenant-projection")
            .ConfigureAwait(false));
        Assert.Equal(8, await ReadStageAsync(dbContext, "tenant-completed")
            .ConfigureAwait(false));
    }

    private static Task<int> ReadStageAsync(
        RetentionDbContext dbContext,
        string tenantId) => dbContext.Database
        .SqlQuery<int>($"""
            SELECT "Stage" AS "Value"
            FROM retention.tenant_destroy_operations
            WHERE "ScopeId" = {tenantId}
            """)
        .SingleAsync();

    private static RetentionDbContext CreateDbContext(
        string connectionString) => new(
        new DbContextOptionsBuilder<RetentionDbContext>()
            .UseNpgsql(
                connectionString,
                options => options.MigrationsAssembly(
                    RetentionMigrations.PostgreSqlAssembly))
            .Options,
        new MigrationScopeContext(),
        OpenWorkspaceTerminationFenceReader.Instance);

    private sealed class MigrationScopeContext : IScopeContext
    {
        public bool IsEnabled => false;
        public string ScopeId => string.Empty;
    }
}
