namespace Integration.Tests.Workspaces;

using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Persistence;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class
    WorkspaceStaffOnboardingRecoveryAuditMigrationIntegrationTests
{
    private const string PreviousMigration =
        "20260822023503_AddWorkspaceStaffOnboardingRetentionExecution";
    private const string CurrentMigration =
        "20260825181559_AddWorkspaceStaffOnboardingRecoveryAudit";
    private const string TenantId =
        "a1000000-0000-0000-0000-000000000001";
    private const string Digest =
        "0123456789abcdef0123456789abcdef" +
        "0123456789abcdef0123456789abcdef";
    private static readonly DateTimeOffset Now =
        new(2026, 8, 25, 18, 30, 0, TimeSpan.Zero);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Upgrade_is_exact_replayable_and_empty_downgrade_is_safe()
    {
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder(
                "postgres:16-alpine")
            .WithDatabase("bunkfy_workspace_recovery_audit_migration_empty")
            .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);

        await using WorkspacesDbContext context = CreateDbContext(
            postgreSql.GetConnectionString(),
            scopeId: string.Empty,
            scopeEnabled: false);
        IMigrator migrator = context.Database.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration).ConfigureAwait(false);
        Assert.False(await TableExistsAsync(context).ConfigureAwait(false));

        await migrator.MigrateAsync(CurrentMigration).ConfigureAwait(false);
        await migrator.MigrateAsync(CurrentMigration).ConfigureAwait(false);

        Assert.True(await TableExistsAsync(context).ConfigureAwait(false));
        Assert.True(await ColumnExistsAsync(
            context,
            "AcceptedMembershipId").ConfigureAwait(false));
        Assert.True(await ColumnExistsAsync(
            context,
            "AcceptedMembershipVersion").ConfigureAwait(false));
        Assert.Equal(
            ExpectedReceiptConstraints,
            await ConstraintNamesAsync(context).ConfigureAwait(false));
        Assert.Equal(
            ExpectedReceiptIndexes,
            await IndexNamesAsync(context).ConfigureAwait(false));
        Assert.Equal(
            ["TR_staff_onboarding_recovery_receipts_append_only"],
            await TriggerNamesAsync(context).ConfigureAwait(false));
        Assert.Contains(
            "ON DELETE CASCADE",
            await ForeignKeyDefinitionAsync(context).ConfigureAwait(false),
            StringComparison.Ordinal);
        Assert.Equal(
            1,
            (await context.Database.GetAppliedMigrationsAsync()
                .ConfigureAwait(false)).Count(migration =>
                    migration == CurrentMigration));
        string[] catalog = context.Database.GetMigrations().ToArray();
        int currentIndex = Array.IndexOf(catalog, CurrentMigration);
        Assert.InRange(currentIndex, 0, catalog.Length - 1);
        Assert.Equal(
            catalog[..(currentIndex + 1)],
            await context.Database.GetAppliedMigrationsAsync()
                .ConfigureAwait(false));
        Assert.Equal(
            catalog[(currentIndex + 1)..],
            await context.Database.GetPendingMigrationsAsync()
                .ConfigureAwait(false));

        await migrator.MigrateAsync(PreviousMigration).ConfigureAwait(false);

        Assert.False(await TableExistsAsync(context).ConfigureAwait(false));
        Assert.False(await ColumnExistsAsync(
            context,
            "AcceptedMembershipId").ConfigureAwait(false));
        Assert.False(await ColumnExistsAsync(
            context,
            "AcceptedMembershipVersion").ConfigureAwait(false));
        Assert.DoesNotContain(
            CurrentMigration,
            await context.Database.GetAppliedMigrationsAsync()
                .ConfigureAwait(false));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Receipt_and_binding_refuse_downgrade_without_data_loss()
    {
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder(
                "postgres:16-alpine")
            .WithDatabase("bunkfy_workspace_recovery_audit_migration_data")
            .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);
        string connectionString = postgreSql.GetConnectionString();

        await using (WorkspacesDbContext migratorContext = CreateDbContext(
            connectionString,
            scopeId: string.Empty,
            scopeEnabled: false))
        {
            await migratorContext.Database.MigrateAsync().ConfigureAwait(false);
        }

        Guid applicationId = Guid.Parse(
            "a2000000-0000-0000-0000-000000000001");
        Guid sourceId = Guid.Parse(
            "a3000000-0000-0000-0000-000000000001");
        Guid staffMemberId = Guid.Parse(
            "a4000000-0000-0000-0000-000000000001");
        Guid membershipId = Guid.Parse(
            "a5000000-0000-0000-0000-000000000001");
        Guid receiptId = Guid.Parse(
            "a6000000-0000-0000-0000-000000000001");

        await using (WorkspacesDbContext scoped = CreateDbContext(
            connectionString,
            TenantId,
            scopeEnabled: true))
        {
            WorkspaceStaffOnboarding application =
                WorkspaceStaffOnboarding.Create(
                    applicationId,
                    TenantId,
                    WorkspaceStaffOnboardingSource.Invitation,
                    sourceId,
                    "applicant-subject",
                    "applicant@example.test",
                    "Applicant",
                    legalName: null,
                    "applicant@example.test",
                    workPhone: null,
                    employeeNumber: null,
                    jobTitle: "Manager",
                    department: "Operations",
                    Now).Value;
            Assert.True(application.BindAcceptedMembership(
                membershipId,
                membershipVersion: 7,
                Now.AddMinutes(1)).IsSuccess);
            Assert.True(application.Fail(
                "Workspaces.TestInducedFailure",
                Now.AddMinutes(2)).IsSuccess);
            scoped.StaffOnboardingApplications.Add(application);
            await scoped.SaveChangesAsync().ConfigureAwait(false);

            WorkspaceStaffOnboardingRecoveryReceipt receipt =
                WorkspaceStaffOnboardingRecoveryReceipt.Create(new(
                    receiptId,
                    TenantId,
                    Guid.Parse(
                        "a7000000-0000-0000-0000-000000000001"),
                    applicationId,
                    sourceId,
                    staffMemberId,
                    WorkspaceStaffOnboardingRecoveryTriggerKind
                        .ManualOperator,
                    Guid.Parse(
                        "a8000000-0000-0000-0000-000000000001"),
                    "applicant-subject",
                    "plan-creator-subject",
                    "recovery-operator-subject",
                    "system:workspace-staff-onboarding",
                    Digest,
                    application.Version,
                    application.Version,
                    application.Status,
                    application.Version,
                    application.Status,
                    Guid.Parse(
                        "a9000000-0000-0000-0000-000000000001"),
                    CurrentProfileVersion: 3,
                    PropertyCount: 2,
                    TargetSetSha256: Digest,
                    OrganizationRevision: 5,
                    MembershipId: membershipId,
                    MembershipVersion: 7,
                    MembershipStatus: 1,
                    StaffTenantRevision: null,
                    StaffMemberVersion: null,
                    StaffStateProofSha256: null,
                    StaffPropertySetSha256: null,
                    AccessManagementRevision: null,
                    AccessFactsSha256: null,
                    WorkspaceRecoveryVerification.Equivalent,
                    WorkspaceRecoveryVerification.ReviewRequired,
                    WorkspaceRecoveryVerification.Unknown,
                    WorkspaceStaffOnboardingRecoveryOutcome.ReviewRequired,
                    WorkspaceStaffOnboardingRecoveryReason.StaffConflict,
                    AccessMutationPerformed: false,
                    OrganizationObservedAtUtc: Now.AddMinutes(3),
                    StaffObservedAtUtc: Now.AddMinutes(3),
                    AccessObservedAtUtc: null,
                    RecordedAtUtc: Now.AddMinutes(3))).Value;
            scoped.StaffOnboardingRecoveryReceipts.Add(receipt);
            await scoped.SaveChangesAsync().ConfigureAwait(false);

            await AssertDirectSqlRejectedAsync(
                scoped,
                receiptId,
                Guid.Parse("a6100000-0000-0000-0000-000000000001"),
                "'ExpectedApplicationVersion', 99",
                "CK_workspaces_staff_onboarding_recovery_receipts_versions")
                .ConfigureAwait(false);
            await AssertDirectSqlRejectedAsync(
                scoped,
                receiptId,
                Guid.Parse("a6200000-0000-0000-0000-000000000001"),
                "'Outcome', 3",
                "CK_workspaces_staff_onboarding_recovery_receipts_outcome")
                .ConfigureAwait(false);
            await AssertDirectSqlRejectedAsync(
                scoped,
                receiptId,
                Guid.Parse("a6300000-0000-0000-0000-000000000001"),
                "'StaffVerification', 3",
                "CK_workspaces_staff_onboarding_recovery_receipts_verification")
                .ConfigureAwait(false);
            await AssertDirectSqlRejectedAsync(
                scoped,
                receiptId,
                Guid.Parse("a6400000-0000-0000-0000-000000000001"),
                "'StaffObservedAtUtc', NULL",
                "CK_workspaces_staff_onboarding_recovery_receipts_presence")
                .ConfigureAwait(false);
            await AssertDirectSqlRejectedAsync(
                scoped,
                receiptId,
                Guid.Parse("a6500000-0000-0000-0000-000000000001"),
                "'StaffObservedAtUtc', '2026-08-25T18:34:00Z'",
                "CK_workspaces_staff_onboarding_recovery_receipts_chronology")
                .ConfigureAwait(false);

            PostgresException appendOnly = await Assert.ThrowsAsync<
                PostgresException>(() =>
                scoped.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE workspaces.staff_onboarding_recovery_receipts
                    SET "Reason" = {(int)WorkspaceStaffOnboardingRecoveryReason.AccessConflict}
                    WHERE "ScopeId" = {TenantId} AND "Id" = {receiptId};
                    """)).ConfigureAwait(false);
            Assert.Equal(PostgresErrorCodes.RaiseException, appendOnly.SqlState);
            Assert.Contains(
                "workspace receipts are append-only",
                appendOnly.MessageText,
                StringComparison.Ordinal);
        }

        await using WorkspacesDbContext downgrade = CreateDbContext(
            connectionString,
            scopeId: string.Empty,
            scopeEnabled: false);
        PostgresException refusal = await Assert.ThrowsAsync<
            PostgresException>(() => downgrade.Database.GetService<IMigrator>()
                .MigrateAsync(PreviousMigration)).ConfigureAwait(false);
        Assert.Equal(PostgresErrorCodes.RaiseException, refusal.SqlState);
        Assert.Contains(
            "onboarding recovery audit data exists; downgrade is refused",
            refusal.MessageText,
            StringComparison.Ordinal);
        Assert.Equal(
            1L,
            await ScalarAsync(
                downgrade,
                "SELECT count(*) FROM workspaces.staff_onboarding_recovery_receipts")
                .ConfigureAwait(false));
        Assert.Equal(
            1L,
            await ScalarAsync(
                downgrade,
                "SELECT count(*) FROM workspaces.staff_onboarding_applications " +
                "WHERE \"AcceptedMembershipId\" IS NOT NULL AND " +
                "\"AcceptedMembershipVersion\" = 7")
                .ConfigureAwait(false));
        Assert.Contains(
            CurrentMigration,
            await downgrade.Database.GetAppliedMigrationsAsync()
                .ConfigureAwait(false));
    }

    private static readonly string[] ExpectedReceiptConstraints =
    [
        "AK_staff_onboarding_recovery_receipts_ScopeId_Id",
        "CK_workspaces_staff_onboarding_recovery_receipts_chronology",
        "CK_workspaces_staff_onboarding_recovery_receipts_contract",
        "CK_workspaces_staff_onboarding_recovery_receipts_digests",
        "CK_workspaces_staff_onboarding_recovery_receipts_no_mutation",
        "CK_workspaces_staff_onboarding_recovery_receipts_outcome",
        "CK_workspaces_staff_onboarding_recovery_receipts_presence",
        "CK_workspaces_staff_onboarding_recovery_receipts_verification",
        "CK_workspaces_staff_onboarding_recovery_receipts_versions",
        "FK_staff_onboarding_recovery_receipts_staff_onboarding_applica~",
        "PK_staff_onboarding_recovery_receipts"
    ];

    private static readonly string[] ExpectedReceiptIndexes =
    [
        "AK_staff_onboarding_recovery_receipts_ScopeId_Id",
        "IX_staff_onboarding_recovery_receipts_ScopeId_ApplicationId_Re~",
        "IX_staff_onboarding_recovery_receipts_ScopeId_OperationId",
        "IX_staff_onboarding_recovery_receipts_ScopeId_RecoveryActorId_~",
        "PK_staff_onboarding_recovery_receipts"
    ];

    private static WorkspacesDbContext CreateDbContext(
        string connectionString,
        string scopeId,
        bool scopeEnabled)
    {
        DbContextOptions<WorkspacesDbContext> options =
            new DbContextOptionsBuilder<WorkspacesDbContext>()
                .UseNpgsql(connectionString, provider => provider
                    .MigrationsAssembly(
                        WorkspacesMigrations.PostgreSqlAssembly)
                    .MigrationsHistoryTable(
                        WorkspacesMigrations.HistoryTable,
                        WorkspacesMigrations.Schema))
                .Options;
        return new(options, new TestScopeContext(scopeId, scopeEnabled));
    }

    private static async Task AssertDirectSqlRejectedAsync(
        WorkspacesDbContext context,
        Guid sourceReceiptId,
        Guid attemptedReceiptId,
        string mutation,
        string expectedConstraint)
    {
        string command = $"""
            INSERT INTO workspaces.staff_onboarding_recovery_receipts
            SELECT (jsonb_populate_record(
                NULL::workspaces.staff_onboarding_recovery_receipts,
                to_jsonb(source) || jsonb_build_object(
                    'Id', '{attemptedReceiptId:D}',
                    {mutation})
            )).*
            FROM workspaces.staff_onboarding_recovery_receipts AS source
            WHERE source."Id" = '{sourceReceiptId:D}';
            """;
        PostgresException failure = await Assert.ThrowsAsync<
            PostgresException>(() => context.Database.ExecuteSqlRawAsync(
                command)).ConfigureAwait(false);
        Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
        Assert.Equal(expectedConstraint, failure.ConstraintName);
    }

    private static Task<bool> TableExistsAsync(WorkspacesDbContext context) =>
        ScalarAsync(
            context,
            "SELECT count(*) FROM information_schema.tables " +
            "WHERE table_schema = 'workspaces' AND " +
            "table_name = 'staff_onboarding_recovery_receipts'")
            .ContinueWith(
                task => task.Result == 1,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

    private static Task<bool> ColumnExistsAsync(
        WorkspacesDbContext context,
        string columnName) =>
        ScalarAsync(
            context,
            "SELECT count(*) FROM information_schema.columns " +
            "WHERE table_schema = 'workspaces' AND " +
            "table_name = 'staff_onboarding_applications' AND " +
            $"column_name = '{columnName}'")
            .ContinueWith(
                task => task.Result == 1,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

    private static Task<string[]> ConstraintNamesAsync(
        WorkspacesDbContext context) => ReadNamesAsync(
            context,
            "SELECT conname FROM pg_constraint WHERE conrelid = " +
            "'workspaces.staff_onboarding_recovery_receipts'::regclass " +
            "ORDER BY conname");

    private static Task<string[]> IndexNamesAsync(
        WorkspacesDbContext context) => ReadNamesAsync(
            context,
            "SELECT indexname FROM pg_indexes WHERE schemaname = " +
            "'workspaces' AND tablename = " +
            "'staff_onboarding_recovery_receipts' ORDER BY indexname");

    private static Task<string[]> TriggerNamesAsync(
        WorkspacesDbContext context) => ReadNamesAsync(
            context,
            "SELECT tgname FROM pg_trigger WHERE tgrelid = " +
            "'workspaces.staff_onboarding_recovery_receipts'::regclass " +
            "AND NOT tgisinternal ORDER BY tgname");

    private static async Task<string> ForeignKeyDefinitionAsync(
        WorkspacesDbContext context)
    {
        await context.Database.OpenConnectionAsync().ConfigureAwait(false);
        await using var command = context.Database.GetDbConnection()
            .CreateCommand();
        command.CommandText =
            "SELECT pg_get_constraintdef(oid) FROM pg_constraint " +
            "WHERE conname = " +
            "'FK_staff_onboarding_recovery_receipts_staff_onboarding_applica~'";
        return (string)(await command.ExecuteScalarAsync()
            .ConfigureAwait(false))!;
    }

    private static async Task<string[]> ReadNamesAsync(
        WorkspacesDbContext context,
        string sql)
    {
        await context.Database.OpenConnectionAsync().ConfigureAwait(false);
        await using var command = context.Database.GetDbConnection()
            .CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync()
            .ConfigureAwait(false);
        List<string> names = [];
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            names.Add(reader.GetString(0));
        }

        return names.ToArray();
    }

    private static async Task<long> ScalarAsync(
        WorkspacesDbContext context,
        string sql)
    {
        await context.Database.OpenConnectionAsync().ConfigureAwait(false);
        await using var command = context.Database.GetDbConnection()
            .CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class TestScopeContext(string scopeId, bool scopeEnabled)
        : IScopeContext
    {
        public bool IsEnabled { get; } = scopeEnabled;
        public string ScopeId { get; } = scopeId;
    }
}
