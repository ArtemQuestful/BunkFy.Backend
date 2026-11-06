namespace Integration.Tests.Workspaces;

using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Persistence;
using BunkFy.Modules.Workspaces.Persistence.Repositories;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class WorkspacesPropertyAuthorityTenantIntegrityMigrationIntegrationTests
{
    private const string PreviousMigration =
        "20260809155756_AddWorkspaceStaffOnboardingWithdrawal";
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private static readonly Guid SharedPropertyId = Guid.Parse(
        "91000000-0000-0000-0000-000000000001");
    private static readonly Guid ForeignOnlyPropertyId = Guid.Parse(
        "91000000-0000-0000-0000-000000000002");
    private static readonly Guid TenantAPlanId = Guid.Parse(
        "92000000-0000-0000-0000-000000000001");
    private static readonly Guid TenantBPlanId = Guid.Parse(
        "92000000-0000-0000-0000-000000000002");

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Migration_preserves_scoped_authority_and_enforces_row_integrity()
    {
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder(
                "postgres:16-alpine")
            .WithDatabase("bunkfy_workspaces_property_authority_integrity_tests")
            .Build();
        await postgreSql.StartAsync();

        string connectionString = postgreSql.GetConnectionString();
        await SeedPreviousModelAsync(connectionString);

        await using WorkspacesDbContext upgraded = CreateDbContext(
            connectionString,
            scopeId: string.Empty,
            scopeEnabled: false);
        IMigrator migrator = upgraded.Database.GetService<IMigrator>();
        await migrator.MigrateAsync();

        Assert.Equal(
            3,
            await upgraded.PropertyProjections.CountAsync());
        Assert.Equal(
            2,
            await upgraded.StaffAccessPlanProperties.CountAsync());

        await AssertTenantAuthorityAsync(
            connectionString,
            TenantA,
            TenantAPlanId,
            PropertyStatus.Active,
            expectedSharedPropertyActive: true);
        await AssertTenantAuthorityAsync(
            connectionString,
            TenantB,
            TenantBPlanId,
            PropertyStatus.Retired,
            expectedSharedPropertyActive: false);

        await using (WorkspacesDbContext tenantA = CreateDbContext(
            connectionString,
            TenantA,
            scopeEnabled: true))
        {
            WorkspacePropertyProjectionRepository repository = new(tenantA);
            Assert.False(await repository.AreAllActiveAsync(
                [SharedPropertyId, ForeignOnlyPropertyId],
                CancellationToken.None));

            await using var transaction = await tenantA.Database
                .BeginTransactionAsync();
            await repository.ApplyAsync(
                new WorkspacePropertyProjectionWriteModel(
                    TenantA,
                    SharedPropertyId,
                    "Tenant A updated",
                    PropertyStatus.Active,
                    3),
                CancellationToken.None);
            await tenantA.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        WorkspacePropertyProjection tenantAProjection = await upgraded
            .PropertyProjections.SingleAsync(property =>
                property.ScopeId == TenantA && property.Id == SharedPropertyId);
        WorkspacePropertyProjection tenantBProjection = await upgraded
            .PropertyProjections.SingleAsync(property =>
                property.ScopeId == TenantB && property.Id == SharedPropertyId);
        Assert.Equal("Tenant A updated", tenantAProjection.Name);
        Assert.Equal(3, tenantAProjection.Version);
        Assert.Null(tenantBProjection.Name);
        Assert.Equal(PropertyStatus.Retired, tenantBProjection.Status);
        Assert.Equal(2, tenantBProjection.Version);

        await AssertConstraintViolationAsync(
            upgraded,
            "CK_workspaces_property_projection_coordinates",
            $"""
            UPDATE workspaces.property_projection
            SET "Id" = {Guid.Empty}
            WHERE "ScopeId" = {TenantA} AND "Id" = {SharedPropertyId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_workspaces_property_projection_name",
            $"""
            UPDATE workspaces.property_projection
            SET "Name" = {" Tenant A "}
            WHERE "ScopeId" = {TenantA} AND "Id" = {SharedPropertyId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_workspaces_property_projection_state",
            $"""
            UPDATE workspaces.property_projection
            SET "Status" = {0}
            WHERE "ScopeId" = {TenantA} AND "Id" = {SharedPropertyId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_workspaces_staff_access_plan_properties_coordinates",
            $"""
            UPDATE workspaces.staff_access_plan_properties
            SET "PropertyId" = {Guid.Empty}
            WHERE "ScopeId" = {TenantA} AND "PlanId" = {TenantAPlanId};
            """);

        await migrator.MigrateAsync(PreviousMigration);
    }

    private static async Task SeedPreviousModelAsync(string connectionString)
    {
        await using (WorkspacesDbContext migratorContext = CreateDbContext(
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
            TenantAPlanId,
            new WorkspacePropertyProjection(
                TenantA,
                SharedPropertyId,
                "Tenant A",
                PropertyStatus.Active,
                2));
        await SeedTenantAsync(
            connectionString,
            TenantB,
            TenantBPlanId,
            new WorkspacePropertyProjection(
                TenantB,
                SharedPropertyId,
                null,
                PropertyStatus.Retired,
                2),
            new WorkspacePropertyProjection(
                TenantB,
                ForeignOnlyPropertyId,
                "Tenant B only",
                PropertyStatus.Active,
                1));
    }

    private static async Task SeedTenantAsync(
        string connectionString,
        string tenantId,
        Guid planId,
        params WorkspacePropertyProjection[] projections)
    {
        await using WorkspacesDbContext context = CreateDbContext(
            connectionString,
            tenantId,
            scopeEnabled: true);
        context.StaffAccessPlans.Add(CreatePlan(tenantId, planId));
        context.PropertyProjections.AddRange(projections);
        await context.SaveChangesAsync();
    }

    private static WorkspaceStaffAccessPlan CreatePlan(
        string tenantId,
        Guid planId) => WorkspaceStaffAccessPlan.Create(
            planId,
            tenantId,
            WorkspaceStaffOnboardingSource.EnrollmentLink,
            Guid.NewGuid(),
            "front-desk",
            [SharedPropertyId],
            $"owner:{tenantId}",
            new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero)).Value;

    private static async Task AssertTenantAuthorityAsync(
        string connectionString,
        string tenantId,
        Guid expectedPlanId,
        PropertyStatus expectedStatus,
        bool expectedSharedPropertyActive)
    {
        await using WorkspacesDbContext scoped = CreateDbContext(
            connectionString,
            tenantId,
            scopeEnabled: true);
        WorkspacePropertyProjection projection = await scoped.PropertyProjections
            .SingleAsync(property => property.Id == SharedPropertyId);
        WorkspaceStaffAccessPlanProperty assignment = await scoped
            .StaffAccessPlanProperties.SingleAsync();
        WorkspacePropertyProjectionRepository repository = new(scoped);

        Assert.Equal(tenantId, projection.ScopeId);
        Assert.Equal(expectedStatus, projection.Status);
        Assert.Equal(tenantId, assignment.ScopeId);
        Assert.Equal(expectedPlanId, assignment.PlanId);
        Assert.Equal(
            expectedSharedPropertyActive,
            await repository.AreAllActiveAsync(
                [SharedPropertyId],
                CancellationToken.None));
    }

    private static async Task AssertConstraintViolationAsync(
        WorkspacesDbContext dbContext,
        string expectedConstraint,
        FormattableString command)
    {
        PostgresException failure = await Assert.ThrowsAsync<PostgresException>(
            () => dbContext.Database.ExecuteSqlInterpolatedAsync(command));
        Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
        Assert.Equal(expectedConstraint, failure.ConstraintName);
    }

    private static WorkspacesDbContext CreateDbContext(
        string connectionString,
        string scopeId,
        bool scopeEnabled)
    {
        DbContextOptions<WorkspacesDbContext> options =
            new DbContextOptionsBuilder<WorkspacesDbContext>()
                .UseNpgsql(connectionString, provider => provider
                    .MigrationsAssembly(WorkspacesMigrations.PostgreSqlAssembly)
                    .MigrationsHistoryTable(
                        WorkspacesMigrations.HistoryTable,
                        WorkspacesMigrations.Schema))
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
