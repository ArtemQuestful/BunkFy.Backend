namespace Integration.Tests.Properties;

using BunkFy.Modules.Properties.Persistence;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class PropertiesGovernanceRevisionTenantIntegrityMigrationIntegrationTests
{
    private const string SeedMigration =
        "20260807212415_AddBedMutationOperationReceipts";
    private const string PreviousMigration =
        "20260813213045_AddCanonicalPropertyTimeZoneOperations";
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private const string Digest =
        "0123456789abcdef0123456789abcdef" +
        "0123456789abcdef0123456789abcdef";
    private static readonly Guid PropertyA = Guid.Parse(
        "94000000-0000-0000-0000-000000000001");
    private static readonly Guid PropertyB = Guid.Parse(
        "94000000-0000-0000-0000-000000000002");
    private static readonly Guid RevisionA = Guid.Parse(
        "95000000-0000-0000-0000-000000000001");
    private static readonly Guid RevisionB = Guid.Parse(
        "95000000-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset OccurredAtUtc = new(
        2026,
        8,
        21,
        12,
        0,
        0,
        TimeSpan.Zero);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Migration_preserves_valid_evidence_and_enforces_tenant_integrity()
    {
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder(
                "postgres:16-alpine")
            .WithDatabase("bunkfy_properties_governance_revision_integrity_tests")
            .Build();
        await postgreSql.StartAsync();

        string connectionString = postgreSql.GetConnectionString();
        await SeedPreviousModelAsync(connectionString);

        await using PropertiesDbContext upgraded = CreateDbContext(
            connectionString,
            scopeId: string.Empty,
            scopeEnabled: false);
        IMigrator migrator = upgraded.Database.GetService<IMigrator>();
        await migrator.MigrateAsync();

        Assert.Equal(
            2,
            await upgraded.GovernanceRevisions
                .IgnoreQueryFilters()
                .CountAsync());
        await AssertTenantFilterAsync(connectionString, TenantA, RevisionA);
        await AssertTenantFilterAsync(connectionString, TenantB, RevisionB);

        await AssertConstraintViolationAsync(
            upgraded,
            "CK_property_governance_revision_coordinates",
            RevisionInsert(Guid.Empty));
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_property_governance_revision_text",
            RevisionInsert(
                Guid.Parse("95000000-0000-0000-0000-000000000011"),
                decisionReasonCode: " Allowed "));
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_property_governance_revision_evidence",
            RevisionInsert(
                Guid.Parse("95000000-0000-0000-0000-000000000012"),
                action: 2));
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_property_governance_revision_policy",
            RevisionInsert(
                Guid.Parse("95000000-0000-0000-0000-000000000013"),
                operatingCountryCode: "gb"));
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_property_governance_revision_occurred_at",
            RevisionInsert(
                Guid.Parse("95000000-0000-0000-0000-000000000014"),
                occurredAtUtc: DateTimeOffset.MinValue));

        await migrator.MigrateAsync(PreviousMigration);
        await migrator.MigrateAsync();
        Assert.Equal(
            2,
            await upgraded.GovernanceRevisions
                .IgnoreQueryFilters()
                .CountAsync());
    }

    private static async Task SeedPreviousModelAsync(string connectionString)
    {
        await using PropertiesDbContext context = CreateDbContext(
            connectionString,
            scopeId: string.Empty,
            scopeEnabled: false);
        IMigrator migrator = context.Database.GetService<IMigrator>();
        await migrator.MigrateAsync(SeedMigration);

        await SeedTenantAsync(
            context,
            TenantA,
            PropertyA,
            RevisionA,
            "Tenant A Hostel",
            "A");
        await SeedTenantAsync(
            context,
            TenantB,
            PropertyB,
            RevisionB,
            "Tenant B Hostel",
            "B");
        await migrator.MigrateAsync(PreviousMigration);
    }

    private static async Task SeedTenantAsync(
        PropertiesDbContext context,
        string tenantId,
        Guid propertyId,
        Guid revisionId,
        string propertyName,
        string propertyCode)
    {
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO properties.properties (
                "Id",
                "Name",
                "Code",
                "TimeZoneId",
                "Status",
                "CreatedAtUtc",
                "ScopeId")
            VALUES (
                {propertyId},
                {propertyName},
                {propertyCode},
                {"Europe/London"},
                {1},
                {OccurredAtUtc.AddDays(-10)},
                {tenantId});
            """);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO properties.property_governance_revisions (
                "Id",
                "ScopeId",
                "PropertyId",
                "PropertyVersion",
                "Action",
                "DecisionReasonCode",
                "CurrentOperatingCountryCode",
                "CurrentJurisdictionPolicyId",
                "CurrentJurisdictionPolicyVersion",
                "CurrentDataRegionId",
                "CurrentTransferProfileId",
                "CurrentRetentionPolicyId",
                "CurrentRetentionPolicyVersion",
                "CurrentPolicyContentSha256",
                "CurrentAcknowledgementSetSha256",
                "ActorId",
                "OccurredAtUtc")
            VALUES (
                {revisionId},
                {tenantId},
                {propertyId},
                {2},
                {1},
                {"Allowed"},
                {"GB"},
                {"uk-hostel-policy"},
                {3},
                {"eu-west"},
                {"standard-transfer"},
                {"hostel-retention"},
                {2},
                {Digest},
                {Digest},
                {"user:owner"},
                {OccurredAtUtc});
            """);
    }

    private static async Task AssertTenantFilterAsync(
        string connectionString,
        string tenantId,
        Guid expectedRevisionId)
    {
        await using PropertiesDbContext scoped = CreateDbContext(
            connectionString,
            tenantId,
            scopeEnabled: true);

        PropertyGovernanceRevision revision =
            await scoped.GovernanceRevisions.SingleAsync();

        Assert.Equal(tenantId, revision.ScopeId);
        Assert.Equal(expectedRevisionId, revision.Id);
    }

    private static async Task AssertConstraintViolationAsync(
        PropertiesDbContext dbContext,
        string expectedConstraint,
        FormattableString command)
    {
        PostgresException failure = await Assert.ThrowsAsync<PostgresException>(
            () => dbContext.Database.ExecuteSqlInterpolatedAsync(command));
        Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
        Assert.Equal(expectedConstraint, failure.ConstraintName);
    }

    private static FormattableString RevisionInsert(
        Guid revisionId,
        int action = 1,
        string decisionReasonCode = "Allowed",
        string operatingCountryCode = "GB",
        DateTimeOffset? occurredAtUtc = null) =>
        $"""
        INSERT INTO properties.property_governance_revisions (
            "Id",
            "ScopeId",
            "PropertyId",
            "PropertyVersion",
            "Action",
            "DecisionReasonCode",
            "CurrentOperatingCountryCode",
            "CurrentJurisdictionPolicyId",
            "CurrentJurisdictionPolicyVersion",
            "CurrentDataRegionId",
            "CurrentTransferProfileId",
            "CurrentRetentionPolicyId",
            "CurrentRetentionPolicyVersion",
            "CurrentPolicyContentSha256",
            "CurrentAcknowledgementSetSha256",
            "ActorId",
            "OccurredAtUtc")
        VALUES (
            {revisionId},
            {TenantA},
            {PropertyA},
            {3},
            {action},
            {decisionReasonCode},
            {operatingCountryCode},
            {"uk-hostel-policy"},
            {3},
            {"eu-west"},
            {"standard-transfer"},
            {"hostel-retention"},
            {2},
            {Digest},
            {Digest},
            {"user:owner"},
            {occurredAtUtc ?? OccurredAtUtc});
        """;

    private static PropertiesDbContext CreateDbContext(
        string connectionString,
        string scopeId,
        bool scopeEnabled)
    {
        DbContextOptions<PropertiesDbContext> options =
            new DbContextOptionsBuilder<PropertiesDbContext>()
                .UseNpgsql(connectionString, provider => provider
                    .MigrationsAssembly(PropertiesMigrations.PostgreSqlAssembly)
                    .MigrationsHistoryTable(
                        PropertiesMigrations.HistoryTable,
                        PropertiesMigrations.Schema))
                .Options;
        return new(
            options,
            new TestScopeContext(scopeId, scopeEnabled),
            new NoTerminationFenceReader());
    }

    private sealed class TestScopeContext(string scopeId, bool scopeEnabled)
        : IScopeContext
    {
        public bool IsEnabled { get; } = scopeEnabled;
        public string ScopeId { get; } = scopeId;
    }

    private sealed class NoTerminationFenceReader
        : IWorkspaceTerminationFenceReader
    {
        public Task<WorkspaceTerminationFenceSnapshot?> GetCurrentAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<WorkspaceTerminationFenceSnapshot?>(null);
        }
    }
}
