namespace Integration.Tests;

using BunkFy.Modules.Guests.Domain.Retention;
using BunkFy.Modules.Guests.Persistence;
using Gma.Framework.Scoping;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class GuestRetentionRetryAttemptIntegrityMigrationIntegrationTests
{
    private const string PreviousMigration =
        "20260821230328_AddGuestRetentionControlProofAuditIntegrity";
    private const string ScopeId = "tenant-a";
    private const string LifecycleConstraint =
        "CK_guest_retention_sweep_checkpoints_lifecycle";
    private static readonly DateTimeOffset Now = new(
        2026,
        8,
        22,
        1,
        0,
        0,
        TimeSpan.Zero);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Migration_preserves_checkpoint_states_and_rejects_malformed_retry_state()
    {
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder(
                "postgres:16-alpine")
            .WithDatabase("bunkfy_guest_retention_retry_integrity_tests")
            .Build();
        await postgreSql.StartAsync();

        Guid initialId = Guid.Parse(
            "94000000-0000-0000-0000-000000000390");
        Guid advancedId = Guid.Parse(
            "94000000-0000-0000-0000-000000000391");
        Guid retryPreparedId = Guid.Parse(
            "94000000-0000-0000-0000-000000000392");
        Guid lastExecutionId = Guid.Parse(
            "93000000-0000-0000-0000-000000000390");

        await using (GuestsDbContext previous = CreateDbContext(
            postgreSql.GetConnectionString()))
        {
            await previous.Database.GetService<IMigrator>().MigrateAsync(
                PreviousMigration);
            GuestRetentionSweepCheckpoint initial =
                GuestRetentionSweepCheckpoint.Create(
                    initialId,
                    ScopeId,
                    "guest-initial",
                    Now).Value;
            GuestRetentionSweepCheckpoint advanced =
                GuestRetentionSweepCheckpoint.Create(
                    advancedId,
                    ScopeId,
                    "guest-advanced",
                    Now).Value;
            Assert.True(advanced.Advance(
                expectedAfterProjectionOrdinal: 0,
                nextAfterProjectionOrdinal: 42,
                lastExecutionId,
                Now.AddMinutes(1)).IsSuccess);
            previous.RetentionSweepCheckpoints.AddRange(initial, advanced);
            await previous.SaveChangesAsync();
        }

        await using GuestsDbContext upgraded = CreateDbContext(
            postgreSql.GetConnectionString());
        await upgraded.Database.MigrateAsync();

        GuestRetentionSweepCheckpoint[] preserved = await upgraded
            .RetentionSweepCheckpoints
            .AsNoTracking()
            .OrderBy(checkpoint => checkpoint.Id)
            .ToArrayAsync();
        Assert.Collection(
            preserved,
            initial =>
            {
                Assert.Equal(initialId, initial.Id);
                Assert.Equal(0, initial.AfterProjectionOrdinal);
                Assert.Null(initial.LastExecutionId);
                Assert.Equal(1, initial.Version);
            },
            advanced =>
            {
                Assert.Equal(advancedId, advanced.Id);
                Assert.Equal(42, advanced.AfterProjectionOrdinal);
                Assert.Equal(lastExecutionId, advanced.LastExecutionId);
                Assert.Equal(2, advanced.Version);
            });

        await upgraded.Database.ExecuteSqlInterpolatedAsync(
            CheckpointInsert(
                retryPreparedId,
                "guest-retry-prepared",
                afterProjectionOrdinal: 17,
                lastExecutionId: null,
                Now.AddMinutes(2),
                version: 3));
        GuestRetentionSweepCheckpoint retryPrepared = await upgraded
            .RetentionSweepCheckpoints
            .AsNoTracking()
            .SingleAsync(checkpoint => checkpoint.Id == retryPreparedId);
        Assert.Equal(17, retryPrepared.AfterProjectionOrdinal);
        Assert.Null(retryPrepared.LastExecutionId);
        Assert.Equal(3, retryPrepared.Version);

        await AssertCheckViolationAsync(
            upgraded,
            CheckpointInsert(
                Guid.Parse("94000000-0000-0000-0000-000000000393"),
                "guest-invalid-retry-version",
                afterProjectionOrdinal: 17,
                lastExecutionId: null,
                Now.AddMinutes(2),
                version: 2));
        await AssertCheckViolationAsync(
            upgraded,
            CheckpointInsert(
                Guid.Parse("94000000-0000-0000-0000-000000000394"),
                "guest-invalid-initial-cursor",
                afterProjectionOrdinal: 17,
                lastExecutionId: null,
                Now.AddMinutes(2),
                version: 1));
        await AssertCheckViolationAsync(
            upgraded,
            CheckpointInsert(
                Guid.Parse("94000000-0000-0000-0000-000000000395"),
                "guest-empty-last-execution",
                afterProjectionOrdinal: 17,
                lastExecutionId: Guid.Empty,
                Now.AddMinutes(2),
                version: 2));

        await upgraded.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM guests.guest_retention_sweep_checkpoints
            WHERE "Id" = {retryPreparedId};
            """);
        await upgraded.Database.GetService<IMigrator>().MigrateAsync(
            PreviousMigration);
        Assert.Equal(2, await upgraded.RetentionSweepCheckpoints.CountAsync());
        await AssertCheckViolationAsync(
            upgraded,
            CheckpointInsert(
                retryPreparedId,
                "guest-old-schema-retry",
                afterProjectionOrdinal: 17,
                lastExecutionId: null,
                Now.AddMinutes(2),
                version: 3));
        await upgraded.Database.MigrateAsync();
    }

    private static FormattableString CheckpointInsert(
        Guid checkpointId,
        string dataClassKey,
        long afterProjectionOrdinal,
        Guid? lastExecutionId,
        DateTimeOffset updatedAtUtc,
        long version) => $"""
        INSERT INTO guests.guest_retention_sweep_checkpoints
            ("Id", "DataClassKey", "AfterProjectionOrdinal",
             "LastExecutionId", "UpdatedAtUtc", "Version", "ScopeId")
        VALUES
            ({checkpointId}, {dataClassKey}, {afterProjectionOrdinal},
             {lastExecutionId}, {updatedAtUtc}, {version}, {ScopeId});
        """;

    private static async Task AssertCheckViolationAsync(
        GuestsDbContext dbContext,
        FormattableString command)
    {
        PostgresException failure =
            await Assert.ThrowsAsync<PostgresException>(
                () => dbContext.Database.ExecuteSqlInterpolatedAsync(
                    command));
        Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
        Assert.Equal(LifecycleConstraint, failure.ConstraintName);
    }

    private static GuestsDbContext CreateDbContext(string connectionString)
    {
        DbContextOptions<GuestsDbContext> options =
            new DbContextOptionsBuilder<GuestsDbContext>()
                .UseNpgsql(connectionString, provider => provider
                    .MigrationsAssembly(GuestsMigrations.PostgreSqlAssembly)
                    .MigrationsHistoryTable(
                        GuestsMigrations.HistoryTable,
                        GuestsMigrations.Schema))
                .Options;
        return new(
            options,
            new TestScopeContext(),
            OpenWorkspaceTerminationFenceReader.Instance);
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId =>
            GuestRetentionRetryAttemptIntegrityMigrationIntegrationTests
                .ScopeId;
    }
}
