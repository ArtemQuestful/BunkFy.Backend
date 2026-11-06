namespace Integration.Tests.Staff;

using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Persistence;
using BunkFy.Modules.Staff.Persistence.Repositories;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class StaffPropertyAuthorityTenantIntegrityMigrationIntegrationTests
{
    private const string PreviousMigration =
        "20260821151046_AddStaffAuthoritativeStateIntegrity";
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private static readonly Guid SharedPropertyId = Guid.Parse(
        "93000000-0000-0000-0000-000000000001");
    private static readonly Guid ForeignOnlyPropertyId = Guid.Parse(
        "93000000-0000-0000-0000-000000000002");

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Migration_preserves_scoped_authority_and_enforces_row_integrity()
    {
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder(
                "postgres:16-alpine")
            .WithDatabase("bunkfy_staff_property_authority_integrity_tests")
            .Build();
        await postgreSql.StartAsync();

        string connectionString = postgreSql.GetConnectionString();
        await SeedPreviousModelAsync(connectionString);

        await using StaffDbContext upgraded = CreateDbContext(
            connectionString,
            scopeId: string.Empty,
            scopeEnabled: false);
        IMigrator migrator = upgraded.Database.GetService<IMigrator>();
        await migrator.MigrateAsync();

        Assert.Equal(
            3,
            await upgraded.PropertyProjections
                .IgnoreQueryFilters()
                .CountAsync());
        await AssertTenantAuthorityAsync(
            connectionString,
            TenantA,
            expectedSharedPropertyActive: true,
            expectedForeignPropertyActive: false,
            expectedVisibleRows: 1);
        await AssertTenantAuthorityAsync(
            connectionString,
            TenantB,
            expectedSharedPropertyActive: false,
            expectedForeignPropertyActive: true,
            expectedVisibleRows: 2);

        await using (StaffDbContext tenantA = CreateDbContext(
            connectionString,
            TenantA,
            scopeEnabled: true))
        {
            StaffPropertyProjectionRepository repository = new(tenantA);
            await using var transaction = await tenantA.Database
                .BeginTransactionAsync();
            await repository.ApplyAsync(
                new StaffPropertyProjectionWriteModel(
                    TenantA,
                    SharedPropertyId,
                    "Tenant A updated",
                    PropertyStatus.Active,
                    3),
                CancellationToken.None);
            await tenantA.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        upgraded.ChangeTracker.Clear();
        StaffPropertyProjection tenantAProjection = await upgraded
            .PropertyProjections
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(property =>
                property.ScopeId == TenantA &&
                property.Id == SharedPropertyId);
        StaffPropertyProjection tenantBProjection = await upgraded
            .PropertyProjections
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(property =>
                property.ScopeId == TenantB &&
                property.Id == SharedPropertyId);
        Assert.Equal("Tenant A updated", tenantAProjection.Name);
        Assert.Equal(3, tenantAProjection.Version);
        Assert.Equal("Tenant B", tenantBProjection.Name);
        Assert.Equal(PropertyStatus.Retired, tenantBProjection.Status);
        Assert.Equal(2, tenantBProjection.Version);

        await AssertConstraintViolationAsync(
            upgraded,
            "CK_staff_property_projection_coordinates",
            $"""
            UPDATE staff.property_projection
            SET "Id" = {Guid.Empty}
            WHERE "ScopeId" = {TenantA} AND "Id" = {SharedPropertyId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_staff_property_projection_name",
            $"""
            UPDATE staff.property_projection
            SET "Name" = {" Tenant A "}
            WHERE "ScopeId" = {TenantA} AND "Id" = {SharedPropertyId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_staff_property_projection_state",
            $"""
            UPDATE staff.property_projection
            SET "Status" = {0}
            WHERE "ScopeId" = {TenantA} AND "Id" = {SharedPropertyId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_staff_property_projection_version",
            $"""
            UPDATE staff.property_projection
            SET "Version" = {0}
            WHERE "ScopeId" = {TenantA} AND "Id" = {SharedPropertyId};
            """);

        await migrator.MigrateAsync(PreviousMigration);
    }

    private static async Task SeedPreviousModelAsync(string connectionString)
    {
        await using (StaffDbContext migratorContext = CreateDbContext(
            connectionString,
            TenantA,
            scopeEnabled: true))
        {
            await migratorContext.Database.GetService<IMigrator>().MigrateAsync(
                PreviousMigration);
        }

        await SeedTenantAsync(
            connectionString,
            TenantA,
            new StaffPropertyProjection(
                TenantA,
                SharedPropertyId,
                "Tenant A",
                PropertyStatus.Active,
                2));
        await SeedTenantAsync(
            connectionString,
            TenantB,
            new StaffPropertyProjection(
                TenantB,
                SharedPropertyId,
                "Tenant B",
                PropertyStatus.Retired,
                2),
            new StaffPropertyProjection(
                TenantB,
                ForeignOnlyPropertyId,
                "Tenant B only",
                PropertyStatus.Active,
                1));
    }

    private static async Task SeedTenantAsync(
        string connectionString,
        string tenantId,
        params StaffPropertyProjection[] projections)
    {
        await using StaffDbContext context = CreateDbContext(
            connectionString,
            tenantId,
            scopeEnabled: true);
        context.PropertyProjections.AddRange(projections);
        await context.SaveChangesAsync();
    }

    private static async Task AssertTenantAuthorityAsync(
        string connectionString,
        string tenantId,
        bool expectedSharedPropertyActive,
        bool expectedForeignPropertyActive,
        int expectedVisibleRows)
    {
        await using StaffDbContext scoped = CreateDbContext(
            connectionString,
            tenantId,
            scopeEnabled: true);
        StaffPropertyProjectionRepository repository = new(scoped);

        Assert.Equal(
            expectedVisibleRows,
            await scoped.PropertyProjections.CountAsync());
        Assert.Equal(
            expectedSharedPropertyActive,
            await repository.IsActiveAsync(
                SharedPropertyId,
                CancellationToken.None));
        Assert.Equal(
            expectedForeignPropertyActive,
            await repository.IsActiveAsync(
                ForeignOnlyPropertyId,
                CancellationToken.None));
    }

    private static async Task AssertConstraintViolationAsync(
        StaffDbContext dbContext,
        string expectedConstraint,
        FormattableString command)
    {
        PostgresException failure = await Assert.ThrowsAsync<PostgresException>(
            () => dbContext.Database.ExecuteSqlInterpolatedAsync(command));
        Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
        Assert.Equal(expectedConstraint, failure.ConstraintName);
    }

    private static StaffDbContext CreateDbContext(
        string connectionString,
        string scopeId,
        bool scopeEnabled)
    {
        DbContextOptions<StaffDbContext> options =
            new DbContextOptionsBuilder<StaffDbContext>()
                .UseNpgsql(connectionString, provider => provider
                    .MigrationsAssembly(StaffMigrations.PostgreSqlAssembly)
                    .MigrationsHistoryTable(
                        StaffMigrations.HistoryTable,
                        StaffMigrations.Schema))
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
