namespace Integration.Tests.DataRights;

using BunkFy.Modules.DataRights.Persistence;
using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class DataRightsPropertyProjectionTenantIntegrityMigrationIntegrationTests
{
    private const string PreviousMigration =
        "20260815171710_AllowTenantTerminationExportAuditScope";
    private const string TenantA =
        "10000000-0000-0000-0000-000000000001";
    private const string TenantB =
        "10000000-0000-0000-0000-000000000002";
    private static readonly Guid SharedPropertyId = Guid.Parse(
        "91000000-0000-0000-0000-000000000001");
    private static readonly Guid PolicyFirstPropertyId = Guid.Parse(
        "91000000-0000-0000-0000-000000000002");
    private static readonly Guid CompletePropertyId = Guid.Parse(
        "91000000-0000-0000-0000-000000000003");

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Migration_preserves_valid_stream_shapes_and_enforces_tenant_integrity()
    {
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder(
                "postgres:16-alpine")
            .WithDatabase("bunkfy_data_rights_property_projection_integrity_tests")
            .Build();
        await postgreSql.StartAsync();

        await SeedPreviousModelAsync(postgreSql.GetConnectionString());

        await using DataRightsDbContext upgraded = CreateDbContext(
            postgreSql.GetConnectionString(),
            scopeId: string.Empty,
            scopeEnabled: false);
        IMigrator migrator = upgraded.Database.GetService<IMigrator>();
        await migrator.MigrateAsync();

        List<DataRightsPropertyProjection> projections = await upgraded
            .PropertyProjections
            .Include(property => property.GovernancePolicy)
            .ThenInclude(policy => policy!.Acknowledgements)
            .OrderBy(property => property.ScopeId)
            .ThenBy(property => property.Id)
            .ToListAsync();
        Assert.Equal(4, projections.Count);
        Assert.Equal(
            2,
            projections.Count(property => property.Id == SharedPropertyId));

        DataRightsPropertyProjection policyFirst = Assert.Single(
            projections,
            property => property.Id == PolicyFirstPropertyId);
        Assert.True(policyFirst.IsKnown);
        Assert.Equal(PropertyStatus.Unknown, policyFirst.Status);
        Assert.Equal(0, policyFirst.TopologySourceVersion);
        Assert.Equal(3, policyFirst.PolicySourceVersion);
        Assert.Equal(
            PropertyProcessingStatus.Unconfigured,
            policyFirst.ProcessingStatus);

        DataRightsPropertyProjection complete = Assert.Single(
            projections,
            property => property.Id == CompletePropertyId);
        Assert.Equal("Europe/London", complete.TimeZoneId);
        Assert.Equal(5, complete.TopologySourceVersion);
        Assert.Equal(6, complete.PolicySourceVersion);
        Assert.Equal(new string('a', 64), complete.GovernancePolicy!.ContentSha256);
        Assert.Single(complete.GovernancePolicy.Acknowledgements);

        await AssertTenantFilterAsync(
            postgreSql.GetConnectionString(),
            TenantA,
            PropertyStatus.Active,
            "Europe/London");
        await AssertTenantFilterAsync(
            postgreSql.GetConnectionString(),
            TenantB,
            PropertyStatus.Retired,
            "America/Toronto");

        await AssertConstraintViolationAsync(
            upgraded,
            "CK_data_rights_property_projection_coordinates",
            $"""
            UPDATE "data-rights"."property_projection"
            SET "Id" = {Guid.Empty}
            WHERE "ScopeId" = {TenantA} AND "Id" = {SharedPropertyId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_data_rights_property_projection_topology",
            $"""
            UPDATE "data-rights"."property_projection"
            SET "TimeZoneId" = NULL
            WHERE "ScopeId" = {TenantA} AND "Id" = {SharedPropertyId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_data_rights_property_projection_topology_text",
            $"""
            UPDATE "data-rights"."property_projection"
            SET "Name" = {" Hostel "}
            WHERE "ScopeId" = {TenantA} AND "Id" = {SharedPropertyId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_data_rights_property_projection_known",
            $"""
            UPDATE "data-rights"."property_projection"
            SET "IsKnown" = FALSE
            WHERE "ScopeId" = {TenantA} AND "Id" = {CompletePropertyId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_data_rights_property_projection_policy_source",
            $"""
            UPDATE "data-rights"."property_projection"
            SET "PolicySourceVersion" = 0
            WHERE "ScopeId" = {TenantA} AND "Id" = {CompletePropertyId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_data_rights_property_projection_governance_policy",
            $"""
            UPDATE "data-rights"."property_projection"
            SET "OperatingCountryCode" = {"gb"}
            WHERE "ScopeId" = {TenantA} AND "Id" = {CompletePropertyId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_data_rights_property_projection_governance_policy",
            $"""
            UPDATE "data-rights"."property_projection"
            SET "PolicyContentSha256" = {new string('A', 64)}
            WHERE "ScopeId" = {TenantA} AND "Id" = {CompletePropertyId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_data_rights_property_policy_acknowledgements_contract",
            $"""
            UPDATE "data-rights"."property_policy_acknowledgements"
            SET "AcknowledgementVersion" = 0
            WHERE "ScopeId" = {TenantA} AND "PropertyId" = {CompletePropertyId};
            """);

        await migrator.MigrateAsync(PreviousMigration);
    }

    private static async Task SeedPreviousModelAsync(string connectionString)
    {
        await using DataRightsDbContext previous = CreateDbContext(
            connectionString,
            scopeId: string.Empty,
            scopeEnabled: false);
        await previous.Database.GetService<IMigrator>().MigrateAsync(
            PreviousMigration);

        DataRightsPropertyProjection policyFirst = new(
            TenantA,
            PolicyFirstPropertyId,
            null,
            null,
            PropertyStatus.Unknown,
            version: 0);
        policyFirst.ApplyPolicy(
            PropertyProcessingStatus.Unconfigured,
            governancePolicy: null,
            sourceVersion: 3);

        DataRightsPropertyProjection complete = new(
            TenantA,
            CompletePropertyId,
            "Complete Hostel",
            "Europe/London",
            PropertyStatus.Active,
            version: 5);
        complete.ApplyPolicy(
            PropertyProcessingStatus.Enabled,
            CreatePolicy(),
            sourceVersion: 6);

        previous.PropertyProjections.AddRange(
            new DataRightsPropertyProjection(
                TenantA,
                SharedPropertyId,
                "Tenant A Hostel",
                "Europe/London",
                PropertyStatus.Active,
                version: 2),
            new DataRightsPropertyProjection(
                TenantB,
                SharedPropertyId,
                "Tenant B Hostel",
                "America/Toronto",
                PropertyStatus.Retired,
                version: 4),
            policyFirst,
            complete);
        await previous.SaveChangesAsync();
    }

    private static PropertyGovernancePolicyBinding CreatePolicy()
    {
        DateTimeOffset effectiveAtUtc = new(
            2026,
            1,
            1,
            0,
            0,
            0,
            TimeSpan.Zero);
        return new(
            "GB",
            "policy.gb",
            2,
            "eu-west",
            "standard",
            "guest-retention",
            3,
            new string('a', 64),
            effectiveAtUtc,
            effectiveAtUtc.AddYears(1),
            effectiveAtUtc.AddDays(1),
            [new("privacy.notice", 1)]);
    }

    private static async Task AssertTenantFilterAsync(
        string connectionString,
        string scopeId,
        PropertyStatus expectedStatus,
        string expectedTimeZoneId)
    {
        await using DataRightsDbContext scoped = CreateDbContext(
            connectionString,
            scopeId,
            scopeEnabled: true);
        DataRightsPropertyProjection projection = await scoped.PropertyProjections
            .SingleAsync(property => property.Id == SharedPropertyId);
        Assert.Equal(scopeId, projection.ScopeId);
        Assert.Equal(expectedStatus, projection.Status);
        Assert.Equal(expectedTimeZoneId, projection.TimeZoneId);
    }

    private static async Task AssertConstraintViolationAsync(
        DataRightsDbContext dbContext,
        string expectedConstraint,
        FormattableString command)
    {
        PostgresException failure = await Assert.ThrowsAsync<PostgresException>(
            () => dbContext.Database.ExecuteSqlInterpolatedAsync(command));
        Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
        Assert.Equal(expectedConstraint, failure.ConstraintName);
    }

    private static DataRightsDbContext CreateDbContext(
        string connectionString,
        string scopeId,
        bool scopeEnabled)
    {
        DbContextOptions<DataRightsDbContext> options =
            new DbContextOptionsBuilder<DataRightsDbContext>()
                .UseNpgsql(connectionString, provider => provider
                    .MigrationsAssembly(DataRightsMigrations.PostgreSqlAssembly)
                    .MigrationsHistoryTable(
                        DataRightsMigrations.HistoryTable,
                        DataRightsMigrations.Schema))
                .Options;
        return new(options, new TestScopeContext(scopeId, scopeEnabled));
    }

    private sealed class TestScopeContext(string scopeId, bool scopeEnabled)
        : IScopeContext
    {
        public bool IsEnabled { get; } = scopeEnabled;
        public string ScopeId { get; } = scopeId;
    }
}
