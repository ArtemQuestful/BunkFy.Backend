namespace Integration.Tests;

using BunkFy.Modules.Retention.Persistence;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Scoping;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class RetentionScopeProjectionIntegrityMigrationIntegrationTests
{
    private const string PreviousMigration =
        "20260821170143_AddRetentionControlPlaneAuthoritativeStateIntegrity";
    private const string ScopeId =
        "10000000-0000-0000-0000-000000000001";
    private static readonly Guid TopologyFirstPropertyId = Guid.Parse(
        "81000000-0000-0000-0000-000000000001");
    private static readonly Guid PolicyFirstPropertyId = Guid.Parse(
        "81000000-0000-0000-0000-000000000002");
    private static readonly Guid CompletePropertyId = Guid.Parse(
        "81000000-0000-0000-0000-000000000003");

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Scope_projection_migration_preserves_valid_stream_shapes_and_rejects_malformed_writes()
    {
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder(
                "postgres:16-alpine")
            .WithDatabase("bunkfy_retention_scope_projection_integrity_tests")
            .Build();
        await postgreSql.StartAsync();

        Guid organizationId = Guid.Parse(
            "82000000-0000-0000-0000-000000000001");
        await using (RetentionDbContext previous = CreateDbContext(
            postgreSql.GetConnectionString()))
        {
            await previous.Database.GetService<IMigrator>().MigrateAsync(
                PreviousMigration);

            RetentionPropertyProjection topologyFirst = new(
                ScopeId,
                TopologyFirstPropertyId,
                isActive: true,
                topologySourceVersion: 2);
            RetentionPropertyProjection policyFirst = new(
                ScopeId,
                PolicyFirstPropertyId,
                isActive: false,
                topologySourceVersion: 0);
            policyFirst.ApplyPolicy(
                isProcessingEnabled: true,
                retentionPolicyVersion: 4,
                sourceVersion: 3);
            RetentionPropertyProjection complete = new(
                ScopeId,
                CompletePropertyId,
                isActive: true,
                topologySourceVersion: 5);
            complete.ApplyPolicy(
                isProcessingEnabled: false,
                retentionPolicyVersion: 7,
                sourceVersion: 6);

            previous.AddRange(
                new RetentionTenantProjection(
                    ScopeId,
                    organizationId,
                    isActive: true,
                    sourceVersion: 3),
                topologyFirst,
                policyFirst,
                complete);
            await previous.SaveChangesAsync();
        }

        await using RetentionDbContext upgraded = CreateDbContext(
            postgreSql.GetConnectionString());
        IMigrator migrator = upgraded.Database.GetService<IMigrator>();
        await migrator.MigrateAsync();

        RetentionTenantProjection tenant =
            await upgraded.TenantProjections.SingleAsync();
        Assert.Equal(organizationId, tenant.OrganizationId);
        Assert.True(tenant.IsActive);
        Assert.Equal(3, tenant.SourceVersion);

        RetentionPropertyProjection topology =
            await upgraded.PropertyProjections.SingleAsync(
                property => property.Id == TopologyFirstPropertyId);
        Assert.True(topology.IsKnown);
        Assert.True(topology.IsActive);
        Assert.Equal(0, topology.PolicySourceVersion);
        Assert.Null(topology.RetentionPolicyVersion);

        RetentionPropertyProjection policy =
            await upgraded.PropertyProjections.SingleAsync(
                property => property.Id == PolicyFirstPropertyId);
        Assert.True(policy.IsKnown);
        Assert.False(policy.IsActive);
        Assert.Equal(0, policy.TopologySourceVersion);
        Assert.True(policy.IsProcessingEnabled);
        Assert.Equal(4, policy.RetentionPolicyVersion);

        RetentionPropertyProjection completeProjection =
            await upgraded.PropertyProjections.SingleAsync(
                property => property.Id == CompletePropertyId);
        Assert.True(completeProjection.IsKnown);
        Assert.True(completeProjection.IsActive);
        Assert.False(completeProjection.IsProcessingEnabled);
        Assert.Equal(7, completeProjection.RetentionPolicyVersion);

        await AssertConstraintViolationAsync(
            upgraded,
            "CK_retention_tenant_projection_coordinates",
            $"""
            UPDATE retention.tenant_projection
            SET "OrganizationId" = {Guid.Empty}
            WHERE "ScopeId" = {ScopeId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_retention_property_projection_coordinates",
            $"""
            UPDATE retention.property_projection
            SET "Id" = {Guid.Empty}
            WHERE "Id" = {TopologyFirstPropertyId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_retention_property_projection_policy",
            $"""
            UPDATE retention.property_projection
            SET "PolicySourceVersion" = {-1L}
            WHERE "Id" = {TopologyFirstPropertyId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_retention_property_projection_topology",
            $"""
            UPDATE retention.property_projection
            SET "TopologySourceVersion" = {0L}, "IsKnown" = FALSE
            WHERE "Id" = {TopologyFirstPropertyId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_retention_property_projection_policy",
            $"""
            UPDATE retention.property_projection
            SET "RetentionPolicyVersion" = NULL
            WHERE "Id" = {PolicyFirstPropertyId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_retention_property_projection_known",
            $"""
            UPDATE retention.property_projection
            SET "IsKnown" = FALSE
            WHERE "Id" = {CompletePropertyId};
            """);

        await migrator.MigrateAsync(PreviousMigration);
    }

    private static async Task AssertConstraintViolationAsync(
        RetentionDbContext dbContext,
        string expectedConstraint,
        FormattableString command)
    {
        PostgresException failure = await Assert.ThrowsAsync<PostgresException>(
            () => dbContext.Database.ExecuteSqlInterpolatedAsync(command));
        Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
        Assert.Equal(expectedConstraint, failure.ConstraintName);
    }

    private static RetentionDbContext CreateDbContext(string connectionString)
    {
        DbContextOptions<RetentionDbContext> options =
            new DbContextOptionsBuilder<RetentionDbContext>()
                .UseNpgsql(connectionString, provider => provider
                    .MigrationsAssembly(RetentionMigrations.PostgreSqlAssembly)
                    .MigrationsHistoryTable(
                        RetentionMigrations.HistoryTable,
                        RetentionMigrations.Schema))
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
            RetentionScopeProjectionIntegrityMigrationIntegrationTests.ScopeId;
    }
}
