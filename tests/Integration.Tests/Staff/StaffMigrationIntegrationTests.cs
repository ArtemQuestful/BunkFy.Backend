namespace Integration.Tests;

using BunkFy.Modules.Staff.Domain.Aggregates;
using BunkFy.Modules.Staff.Persistence;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Scoping;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class StaffMigrationIntegrationTests
{
    private const string BeforeAuthoritativeStateIntegrityMigration =
        "20260811045655_AddStaffOnboardingProvisioningOperations";

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Authoritative_state_integrity_migration_preserves_valid_rows_and_rejects_malformed_state()
    {
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder(
                "postgres:16-alpine")
            .WithDatabase("bunkfy_staff_state_integrity_migration_tests")
            .Build();
        await postgreSql.StartAsync();

        const string scopeId = "tenant-a";
        Guid activeMemberId = Guid.Parse(
            "10000000-0000-0000-0000-000000000030");
        Guid anonymisedMemberId = Guid.Parse(
            "10000000-0000-0000-0000-000000000031");
        Guid assignmentId = Guid.Parse(
            "20000000-0000-0000-0000-000000000030");
        Guid propertyId = Guid.Parse(
            "30000000-0000-0000-0000-000000000030");
        Guid secondPropertyId = Guid.Parse(
            "30000000-0000-0000-0000-000000000031");
        DateTimeOffset createdAtUtc = new(
            2026,
            8,
            21,
            12,
            0,
            0,
            TimeSpan.Zero);
        DateTimeOffset assignedAtUtc = createdAtUtc.AddMinutes(5);
        DateTimeOffset departedAtUtc = createdAtUtc.AddHours(1);
        DateTimeOffset anonymisedAtUtc = createdAtUtc.AddHours(2);
        DateOnly effectiveOn = new(2026, 8, 21);

        await using (StaffDbContext previous = CreateDbContext(
            postgreSql.GetConnectionString()))
        {
            await previous.Database.GetService<IMigrator>().MigrateAsync(
                BeforeAuthoritativeStateIntegrityMigration);
            await previous.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO staff.staff_members (
                    "Id", "AnonymisedAtUtc", "AuthSubjectId", "CreatedAtUtc",
                    "CreatedBy", "DepartedAtUtc", "Department",
                    "DepartureEffectiveOn", "DisplayName", "DisplayNameSearch",
                    "EmployeeNumber", "EmployeeNumberSearch", "JobTitle",
                    "LastChangedAtUtc", "LastChangedBy", "LegalName",
                    "LegalNameSearch", "ScopeId", "Status", "SuspendedAtUtc",
                    "Version", "WorkEmail", "WorkEmailSearch", "WorkPhone",
                    "WorkPhoneSearch")
                VALUES (
                    {activeMemberId}, NULL, {"subject-active"}, {createdAtUtc},
                    {"user:owner"}, NULL, {"Operations"}, NULL,
                    {"Ada Operator"}, {"ADA OPERATOR"}, {"EMP-30"}, {"EMP-30"},
                    {"Manager"}, {assignedAtUtc}, {"user:owner"}, {"Ada Lovelace"},
                    {"ADA LOVELACE"}, {scopeId}, {(int)StaffMemberState.Active},
                    NULL, {2L}, {"ada@example.test"}, {"ADA@EXAMPLE.TEST"},
                    {"+44 20 1000"}, {"+44 20 1000"});

                INSERT INTO staff.staff_members (
                    "Id", "AnonymisedAtUtc", "AuthSubjectId", "CreatedAtUtc",
                    "CreatedBy", "DepartedAtUtc", "Department",
                    "DepartureEffectiveOn", "DisplayName", "DisplayNameSearch",
                    "EmployeeNumber", "EmployeeNumberSearch", "JobTitle",
                    "LastChangedAtUtc", "LastChangedBy", "LegalName",
                    "LegalNameSearch", "ScopeId", "Status", "SuspendedAtUtc",
                    "Version", "WorkEmail", "WorkEmailSearch", "WorkPhone",
                    "WorkPhoneSearch")
                VALUES (
                    {anonymisedMemberId}, {anonymisedAtUtc}, NULL, {createdAtUtc},
                    {"user:owner"}, {departedAtUtc}, NULL, {effectiveOn},
                    {StaffMember.AnonymisedDisplayName},
                    {StaffMember.AnonymisedDisplayName.ToUpperInvariant()},
                    NULL, NULL, NULL, {anonymisedAtUtc}, {"user:privacy"},
                    NULL, NULL, {scopeId}, {(int)StaffMemberState.Anonymised},
                    NULL, {4L}, NULL, NULL, NULL, NULL);

                INSERT INTO staff.property_assignments (
                    "ScopeId", "StaffMemberId", "Id", "AssignedAtUtc",
                    "AssignedAtVersion", "AssignedBy", "EffectiveFrom",
                    "EffectiveTo", "IsCurrent", "IsPrimary", "PropertyId",
                    "PropertyJobTitle", "UnassignedAtUtc",
                    "UnassignedAtVersion", "UnassignedBy",
                    "UnassignmentReason")
                VALUES (
                    {scopeId}, {activeMemberId}, {assignmentId}, {assignedAtUtc},
                    {2L}, {"user:owner"}, {effectiveOn}, NULL, TRUE, TRUE,
                    {propertyId}, {"Duty manager"}, NULL, NULL, NULL, NULL);
                """);
        }

        await using StaffDbContext upgraded = CreateDbContext(
            postgreSql.GetConnectionString());
        await upgraded.Database.MigrateAsync();

        Assert.Equal(2, await upgraded.StaffMembers.AsNoTracking().CountAsync());
        Assert.Equal(
            1,
            await upgraded.PropertyAssignments.AsNoTracking().CountAsync());

        await AssertConstraintViolationAsync(
            upgraded,
            PostgresErrorCodes.CheckViolation,
            "CK_staff_members_coordinates",
            $"""
            UPDATE staff.staff_members SET "Id" = {Guid.Empty}
            WHERE "Id" = {anonymisedMemberId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            PostgresErrorCodes.CheckViolation,
            "CK_staff_members_search_shape",
            $"""
            UPDATE staff.staff_members SET "LegalNameSearch" = NULL
            WHERE "Id" = {activeMemberId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            PostgresErrorCodes.CheckViolation,
            "CK_staff_members_optional_text",
            $"""
            UPDATE staff.staff_members SET "JobTitle" = {" "}
            WHERE "Id" = {activeMemberId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            PostgresErrorCodes.CheckViolation,
            "CK_staff_members_timestamps",
            $"""
            UPDATE staff.staff_members
            SET "LastChangedAtUtc" = {createdAtUtc.AddMinutes(-1)}
            WHERE "Id" = {activeMemberId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            PostgresErrorCodes.CheckViolation,
            "CK_staff_members_anonymised_profile",
            $"""
            UPDATE staff.staff_members
            SET "WorkEmail" = {"retained@example.test"},
                "WorkEmailSearch" = {"RETAINED@EXAMPLE.TEST"}
            WHERE "Id" = {anonymisedMemberId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            PostgresErrorCodes.CheckViolation,
            "CK_staff_assignments_coordinates",
            $"""
            UPDATE staff.property_assignments SET "PropertyId" = {Guid.Empty}
            WHERE "Id" = {assignmentId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            PostgresErrorCodes.CheckViolation,
            "CK_staff_assignments_actor_shape",
            $"""
            UPDATE staff.property_assignments SET "AssignedBy" = {" "}
            WHERE "Id" = {assignmentId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            PostgresErrorCodes.CheckViolation,
            "CK_staff_assignments_versions",
            $"""
            UPDATE staff.property_assignments
            SET "IsCurrent" = FALSE, "IsPrimary" = FALSE,
                "EffectiveTo" = {effectiveOn}, "UnassignedBy" = {"user:owner"},
                "UnassignmentReason" = {"Ended"},
                "UnassignedAtUtc" = {assignedAtUtc.AddMinutes(1)},
                "UnassignedAtVersion" = {2L}
            WHERE "Id" = {assignmentId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            PostgresErrorCodes.CheckViolation,
            "CK_staff_assignments_dates",
            $"""
            UPDATE staff.property_assignments
            SET "EffectiveFrom" = {DateOnly.MinValue}
            WHERE "Id" = {assignmentId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            PostgresErrorCodes.CheckViolation,
            "CK_staff_assignments_timestamps",
            $"""
            UPDATE staff.property_assignments
            SET "IsCurrent" = FALSE, "IsPrimary" = FALSE,
                "EffectiveTo" = {effectiveOn}, "UnassignedBy" = {"user:owner"},
                "UnassignmentReason" = {"Ended"},
                "UnassignedAtUtc" = {assignedAtUtc.AddMinutes(-1)},
                "UnassignedAtVersion" = {3L}
            WHERE "Id" = {assignmentId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            PostgresErrorCodes.UniqueViolation,
            "UX_staff_assignments_current_property",
            $"""
            INSERT INTO staff.property_assignments (
                "ScopeId", "StaffMemberId", "Id", "AssignedAtUtc",
                "AssignedAtVersion", "AssignedBy", "EffectiveFrom",
                "EffectiveTo", "IsCurrent", "IsPrimary", "PropertyId",
                "PropertyJobTitle", "UnassignedAtUtc",
                "UnassignedAtVersion", "UnassignedBy",
                "UnassignmentReason")
            VALUES (
                {scopeId}, {activeMemberId}, {Guid.NewGuid()}, {assignedAtUtc},
                {2L}, {"user:owner"}, {effectiveOn}, NULL, TRUE, FALSE,
                {propertyId}, NULL, NULL, NULL, NULL, NULL);
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            PostgresErrorCodes.UniqueViolation,
            "UX_staff_assignments_current_primary",
            $"""
            INSERT INTO staff.property_assignments (
                "ScopeId", "StaffMemberId", "Id", "AssignedAtUtc",
                "AssignedAtVersion", "AssignedBy", "EffectiveFrom",
                "EffectiveTo", "IsCurrent", "IsPrimary", "PropertyId",
                "PropertyJobTitle", "UnassignedAtUtc",
                "UnassignedAtVersion", "UnassignedBy",
                "UnassignmentReason")
            VALUES (
                {scopeId}, {activeMemberId}, {Guid.NewGuid()}, {assignedAtUtc},
                {2L}, {"user:owner"}, {effectiveOn}, NULL, TRUE, TRUE,
                {secondPropertyId}, NULL, NULL, NULL, NULL, NULL);
            """);
    }

    private static async Task AssertConstraintViolationAsync(
        StaffDbContext dbContext,
        string expectedSqlState,
        string expectedConstraint,
        FormattableString command)
    {
        PostgresException failure = await Assert.ThrowsAsync<PostgresException>(
            () => dbContext.Database.ExecuteSqlInterpolatedAsync(command));
        Assert.Equal(expectedSqlState, failure.SqlState);
        Assert.Equal(expectedConstraint, failure.ConstraintName);
    }

    private static StaffDbContext CreateDbContext(string connectionString)
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
            new TestScopeContext(),
            OpenWorkspaceTerminationFenceReader.Instance);
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }
}
