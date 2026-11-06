namespace Integration.Tests.Workspaces;

using System.Security.Cryptography;
using System.Text;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Persistence;
using Gma.Framework.Scoping;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

public sealed partial class
    WorkspaceStaffIdentityProvisioningAnchorMigrationIntegrationTests
{
    private const string PreviousMigration =
        "20261102220000_AddWorkspaceStaffDeferredClaimWithdrawals";
    private const string CurrentMigration =
        "20261102220200_AddWorkspaceStaffIdentityProvisioningAnchors";
    private const string TenantId =
        "10000000-0000-0000-0000-000000000001";
    private const string OtherTenantId =
        "10000000-0000-0000-0000-000000000002";

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task
        Upgrade_fails_fast_for_writer_and_rolls_back_schema_and_history()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_workspaces_anchor_migration_lock_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);
        string connectionString = postgreSql.GetConnectionString();

        string upgradeScript;
        await using (WorkspacesDbContext previous = CreateDbContext(
                         connectionString,
                         TenantId))
        {
            await previous.Database.GetService<IMigrator>()
                .MigrateAsync(PreviousMigration)
                .ConfigureAwait(false);
            upgradeScript = previous.Database.GetService<IMigrator>()
                .GenerateScript(PreviousMigration, CurrentMigration);
        }

        await using NpgsqlConnection writer = new(connectionString);
        await writer.OpenAsync().ConfigureAwait(false);
        await using NpgsqlTransaction writerTransaction =
            await writer.BeginTransactionAsync().ConfigureAwait(false);
        await using (NpgsqlCommand lockCommand = new(
                         """
                         LOCK TABLE workspaces.staff_onboarding_applications
                         IN ROW EXCLUSIVE MODE
                         """,
                         writer,
                         writerTransaction))
        {
            await lockCommand.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await using (NpgsqlConnection blockedUpgrade = new(connectionString))
        {
            await blockedUpgrade.OpenAsync().ConfigureAwait(false);
            await using NpgsqlCommand upgradeCommand = new(
                upgradeScript,
                blockedUpgrade)
            {
                CommandTimeout = 5
            };
            Task<int> upgradeAttempt = upgradeCommand.ExecuteNonQueryAsync();
            await Task.Delay(500).ConfigureAwait(false);
            if (!upgradeAttempt.IsCompleted)
            {
                string activity = await ExecuteScalarAsync<string>(
                    connectionString,
                    $"""
                    SELECT COALESCE(wait_event_type, 'none') || ':' ||
                           COALESCE(wait_event, 'none') || ':' || query
                    FROM pg_stat_activity
                    WHERE pid = {blockedUpgrade.ProcessID}
                    """).ConfigureAwait(false);
                Assert.Fail($"Blocked migration activity: {activity}");
            }

            PostgresException blocked = Assert.IsType<PostgresException>(
                await Assert.ThrowsAnyAsync<Exception>(async () =>
                    await upgradeAttempt.ConfigureAwait(false))
                    .ConfigureAwait(false));
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, blocked.SqlState);
        }

        Assert.Equal(
            0,
            await ExecuteScalarAsync<int>(
                connectionString,
                $$"""
                SELECT COUNT(*)::integer
                FROM workspaces.__ef_migrations_history
                WHERE "MigrationId" = '{{CurrentMigration}}'
                """).ConfigureAwait(false));
        Assert.False(await ExecuteScalarAsync<bool>(
            connectionString,
            """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.columns
                WHERE table_schema = 'workspaces'
                  AND table_name = 'staff_onboarding_applications'
                  AND column_name = 'IdentityAnchorSweepOrdinal')
            """).ConfigureAwait(false));
        Assert.True(await ExecuteScalarAsync<bool>(
            connectionString,
            """
            SELECT EXISTS (
                SELECT 1
                FROM pg_constraint
                WHERE connamespace = 'workspaces'::regnamespace
                  AND conname =
                    'CK_workspaces_tenant_destroy_operation_progress')
            """).ConfigureAwait(false));

        await writerTransaction.CommitAsync().ConfigureAwait(false);

        await using WorkspacesDbContext drainedUpgrade = CreateDbContext(
            connectionString,
            TenantId);
        await drainedUpgrade.Database.GetService<IMigrator>()
            .MigrateAsync(CurrentMigration)
            .ConfigureAwait(false);
        Assert.Equal(
            1,
            await ExecuteScalarAsync<int>(
                connectionString,
                $$"""
                SELECT COUNT(*)::integer
                FROM workspaces.__ef_migrations_history
                WHERE "MigrationId" = '{{CurrentMigration}}'
                """).ConfigureAwait(false));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task
        Down_rejects_new_active_stages_and_preserves_canonical_completion()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_workspaces_anchor_down_stage_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);
        string connectionString = postgreSql.GetConnectionString();
        await using WorkspacesDbContext context = CreateDbContext(
            connectionString,
            TenantId);
        await context.Database.GetService<IMigrator>()
            .MigrateAsync(CurrentMigration)
            .ConfigureAwait(false);

        await ExecuteNonQueryAsync(
            connectionString,
            $$"""
            INSERT INTO workspaces.workspace_termination_fences (
                "Id", "ProcessId", "CaseId", "ApprovalRevision",
                "TerminationEpoch", "PolicyEvidenceSha256", "State",
                "CreatedBy", "CreatedAtUtc", "LastChangedBy",
                "LastChangedAtUtc", "Version", "ScopeId")
            VALUES (
                '61000000-0000-0000-0000-000000000001',
                '62000000-0000-0000-0000-000000000001',
                '63000000-0000-0000-0000-000000000001', 1,
                '64000000-0000-0000-0000-000000000001', repeat('c', 64), 4,
                'migration-test', '2026-08-11T21:00:00Z', 'migration-test',
                '2026-08-11T21:01:00Z', 3, '{{TenantId}}');
            INSERT INTO workspaces.tenant_destroy_operations (
                "OperationId", "ScopeId", "RequestSha256", "FenceId",
                "SelectedFenceVersion", "ResultingFenceVersion", "BatchSize",
                "Stage", "RemovedRecordCount", "CompletedBatchCount",
                "ProofVersion", "RemovalProofSha256", "StartedAtUtc",
                "UpdatedAtUtc", "ConcurrencyVersion")
            VALUES (
                '65000000-0000-0000-0000-000000000001', '{{TenantId}}',
                repeat('d', 64),
                '61000000-0000-0000-0000-000000000001', 1, 3, 500, 22, 0,
                0, 1, repeat('e', 64), '2026-08-11T21:02:00Z',
                '2026-08-11T21:02:00Z', 1)
            """).ConfigureAwait(false);

        Exception activeCheckpointStage =
            await Assert.ThrowsAnyAsync<Exception>(() =>
                context.Database.GetService<IMigrator>()
                    .MigrateAsync(PreviousMigration)).ConfigureAwait(false);
        Assert.Equal(
            "P0001",
            Assert.IsType<PostgresException>(
                activeCheckpointStage.GetBaseException()).SqlState);

        await ExecuteNonQueryAsync(
            connectionString,
            """
            UPDATE workspaces.tenant_destroy_operations
            SET "Stage" = 23
            WHERE "OperationId" =
                '65000000-0000-0000-0000-000000000001'
            """).ConfigureAwait(false);
        Exception activeReceiptStage =
            await Assert.ThrowsAnyAsync<Exception>(() =>
                context.Database.GetService<IMigrator>()
                    .MigrateAsync(PreviousMigration)).ConfigureAwait(false);
        Assert.Equal(
            "P0001",
            Assert.IsType<PostgresException>(
                activeReceiptStage.GetBaseException()).SqlState);

        await ExecuteNonQueryAsync(
            connectionString,
            """
            UPDATE workspaces.tenant_destroy_operations
            SET "Stage" = 21
            WHERE "OperationId" =
                '65000000-0000-0000-0000-000000000001'
            """).ConfigureAwait(false);

        await ExecuteNonQueryAsync(
            connectionString,
            $$"""
            INSERT INTO workspaces.inbox_messages (
                "Id", "Handler", "Subject", "EventType", "Version",
                "ScopeId", "Status", "Attempts", "OccurredAtUtc",
                "CreatedAtUtc", "ProcessingStartedAtUtc", "FailedAtUtc",
                "LastError")
            VALUES (
                '66000000-0000-0000-0000-000000000001',
                'identity-anchor-migration-test', 'subject:failed-inbox',
                'workspace-staff-onboarding-identity-anchor-resolved', 1,
                '{{TenantId}}', 4, 1, '2026-08-11T21:03:00Z',
                '2026-08-11T21:03:00Z', '2026-08-11T21:03:00Z',
                '2026-08-11T21:04:00Z', 'retryable migration test failure')
            """).ConfigureAwait(false);
        Exception failedInboxMessage =
            await Assert.ThrowsAnyAsync<Exception>(() =>
                context.Database.GetService<IMigrator>()
                    .MigrateAsync(PreviousMigration)).ConfigureAwait(false);
        Assert.Equal(
            "P0001",
            Assert.IsType<PostgresException>(
                failedInboxMessage.GetBaseException()).SqlState);
        await ExecuteNonQueryAsync(
            connectionString,
            """
            DELETE FROM workspaces.inbox_messages
            WHERE "Id" = '66000000-0000-0000-0000-000000000001'
            """).ConfigureAwait(false);

        await context.Database.GetService<IMigrator>()
            .MigrateAsync(PreviousMigration)
            .ConfigureAwait(false);

        Assert.Equal(
            21,
            await ExecuteScalarAsync<int>(
                connectionString,
                """
                SELECT "Stage"
                FROM workspaces.tenant_destroy_operations
                WHERE "OperationId" =
                    '65000000-0000-0000-0000-000000000001'
                """).ConfigureAwait(false));
        Assert.False(await ExecuteScalarAsync<bool>(
            connectionString,
            """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.columns
                WHERE table_schema = 'workspaces'
                  AND table_name = 'staff_onboarding_applications'
                  AND column_name = 'IdentityAnchorSweepOrdinal')
            """).ConfigureAwait(false));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task
        Upgrade_redacts_only_bound_legacy_rows_and_backfills_exact_protocol()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_workspaces_anchor_upgrade_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);
        string connectionString = postgreSql.GetConnectionString();

        await using (WorkspacesDbContext previous = CreateDbContext(
                         connectionString,
                         TenantId))
        {
            await previous.Database.GetService<IMigrator>()
                .MigrateAsync(PreviousMigration)
                .ConfigureAwait(false);
        }

        await ExecuteNonQueryAsync(
            connectionString,
            $$"""
            INSERT INTO workspaces.staff_onboarding_applications (
                "Id", "SourceKind", "SourceId", "ClaimId", "ClaimVersion",
                "SubjectId", "VerifiedAccountEmail", "DisplayName",
                "LegalName", "WorkEmail", "WorkPhone", "EmployeeNumber",
                "JobTitle", "Department", "Status", "StaffMemberId",
                "FailureCode", "Version", "CreatedAtUtc", "LastChangedAtUtc",
                "ScopeId")
            VALUES
                ('21000000-0000-0000-0000-000000000001', 1,
                 '22000000-0000-0000-0000-000000000001', NULL, NULL,
                 'subject:legacy-bound', 'bound@example.test',
                 'Bound Applicant', 'Bound Legal', 'bound-work@example.test',
                 '+1-555-0101', 'EMP-BOUND', 'Operator', 'Operations', 4,
                 '23000000-0000-0000-0000-000000000001', 'legacy-failure', 7,
                 '2026-08-11T19:00:00Z', '2026-08-11T19:01:00Z',
                 '{{TenantId}}'),
                ('21000000-0000-0000-0000-000000000002', 1,
                 '22000000-0000-0000-0000-000000000002', NULL, NULL,
                 'subject:legacy-completed', NULL, NULL, NULL, NULL, NULL,
                 NULL, NULL, NULL, 5,
                 '23000000-0000-0000-0000-000000000002', NULL, 9,
                 '2026-08-11T19:00:00Z', '2026-08-11T19:02:00Z',
                 '{{TenantId}}'),
                ('21000000-0000-0000-0000-000000000003', 2,
                 '22000000-0000-0000-0000-000000000003', NULL, NULL,
                 'subject:legacy-unbound', 'unbound@example.test',
                 'Unbound Applicant', NULL, NULL, NULL, NULL, NULL, NULL, 1,
                 NULL, NULL, 3, '2026-08-11T19:00:00Z',
                 '2026-08-11T19:03:00Z', '{{TenantId}}');

            INSERT INTO workspaces.staff_access_processes (
                "Id", "StaffMemberId", "SubjectId", "TargetState",
                "TargetStaffVersion", "EffectiveOn", "RequestedBy", "State",
                "FailureCode", "Version", "CreatedAtUtc", "LastChangedAtUtc",
                "CompletedAtUtc", "ScopeId")
            VALUES
                ('24000000-0000-0000-0000-000000000001',
                 '23000000-0000-0000-0000-000000000001', 'subject:active', 1,
                 2, '2026-08-11', 'migration-test', 1, NULL, 1,
                 '2026-08-11T19:00:00Z', '2026-08-11T19:00:00Z', NULL,
                 '{{TenantId}}'),
                ('24000000-0000-0000-0000-000000000002',
                 '23000000-0000-0000-0000-000000000002',
                 'subject:suspended', 2, 2, '2026-08-11', 'migration-test', 1,
                 NULL, 1, '2026-08-11T19:00:00Z',
                 '2026-08-11T19:00:00Z', NULL, '{{TenantId}}'),
                ('24000000-0000-0000-0000-000000000003',
                 '23000000-0000-0000-0000-000000000003', 'subject:departed', 3,
                 2, '2026-08-11', 'migration-test', 1, NULL, 1,
                 '2026-08-11T19:00:00Z', '2026-08-11T19:00:00Z', NULL,
                 '{{TenantId}}');

            INSERT INTO workspaces.workspace_termination_fences (
                "Id", "ProcessId", "CaseId", "ApprovalRevision",
                "TerminationEpoch", "PolicyEvidenceSha256", "State",
                "CreatedBy", "CreatedAtUtc", "LastChangedBy",
                "LastChangedAtUtc", "Version", "ScopeId")
            VALUES (
                '25000000-0000-0000-0000-000000000001',
                '26000000-0000-0000-0000-000000000001',
                '27000000-0000-0000-0000-000000000001', 1,
                '28000000-0000-0000-0000-000000000001', repeat('c', 64), 4,
                'migration-test', '2026-08-11T18:00:00Z', 'migration-test',
                '2026-08-11T18:01:00Z', 3, '{{TenantId}}');

            INSERT INTO workspaces.tenant_destroy_operations (
                "OperationId", "ScopeId", "RequestSha256", "FenceId",
                "SelectedFenceVersion", "ResultingFenceVersion", "BatchSize",
                "Stage", "RemovedRecordCount", "CompletedBatchCount",
                "ProofVersion", "RemovalProofSha256", "StartedAtUtc",
                "UpdatedAtUtc", "ConcurrencyVersion")
            VALUES (
                '29000000-0000-0000-0000-000000000001', '{{TenantId}}',
                repeat('d', 64),
                '25000000-0000-0000-0000-000000000001', 1, 3, 500, 20, 0,
                0, 1, repeat('e', 64), '2026-08-11T18:02:00Z',
                '2026-08-11T18:03:00Z', 1);
            """).ConfigureAwait(false);

        await using (WorkspacesDbContext upgrade = CreateDbContext(
                         connectionString,
                         TenantId))
        {
            await upgrade.Database.GetService<IMigrator>()
                .MigrateAsync(CurrentMigration)
                .ConfigureAwait(false);
        }

        Assert.Equal(
            20,
            await ExecuteScalarAsync<int>(
                connectionString,
                """
                SELECT "Stage"
                FROM workspaces.tenant_destroy_operations
                WHERE "OperationId" =
                    '29000000-0000-0000-0000-000000000001'
                """).ConfigureAwait(false));
        Assert.True(await ExecuteScalarAsync<bool>(
            connectionString,
            """
            SELECT "Version" = 8
               AND "LastChangedAtUtc" > '2026-08-11T19:01:00Z'
               AND "VerifiedAccountEmail" IS NULL
               AND "DisplayName" IS NULL
               AND "LegalName" IS NULL
               AND "WorkEmail" IS NULL
               AND "WorkPhone" IS NULL
               AND "EmployeeNumber" IS NULL
               AND "JobTitle" IS NULL
               AND "Department" IS NULL
            FROM workspaces.staff_onboarding_applications
            WHERE "Id" = '21000000-0000-0000-0000-000000000001'
            """).ConfigureAwait(false));
        Assert.True(await ExecuteScalarAsync<bool>(
            connectionString,
            """
            SELECT "Version" = 9
               AND "LastChangedAtUtc" = '2026-08-11T19:02:00Z'
            FROM workspaces.staff_onboarding_applications
            WHERE "Id" = '21000000-0000-0000-0000-000000000002'
            """).ConfigureAwait(false));
        Assert.True(await ExecuteScalarAsync<bool>(
            connectionString,
            """
            SELECT "Version" = 3
               AND "LastChangedAtUtc" = '2026-08-11T19:03:00Z'
               AND "VerifiedAccountEmail" = 'unbound@example.test'
               AND "DisplayName" = 'Unbound Applicant'
            FROM workspaces.staff_onboarding_applications
            WHERE "Id" = '21000000-0000-0000-0000-000000000003'
            """).ConfigureAwait(false));
        Assert.True(await ExecuteScalarAsync<bool>(
            connectionString,
            """
            SELECT COUNT(*) = 3
               AND COUNT(DISTINCT "IdentityAnchorSweepOrdinal") = 3
               AND MIN("IdentityAnchorSweepOrdinal") > 0
               AND bool_and("IdentityAnchorExpectedResolutionEventId" IS NULL)
               AND bool_and("IdentityAnchorContinuationEventId" IS NULL)
               AND bool_and("IdentityAnchorResolutionEventId" IS NULL)
               AND bool_and("IdentityAnchorResolutionStaffMemberId" IS NULL)
            FROM workspaces.staff_onboarding_applications
            """).ConfigureAwait(false));
        Assert.True(await ExecuteScalarAsync<bool>(
            connectionString,
            """
            SELECT bool_and(
                ("TargetState" = 1 AND "RestorationDisposition" = 2) OR
                ("TargetState" IN (2, 3) AND "RestorationDisposition" = 1))
            FROM workspaces.staff_access_processes
            """).ConfigureAwait(false));

        long newcomerOrdinal = await ExecuteScalarAsync<long>(
            connectionString,
            $$"""
            INSERT INTO workspaces.staff_onboarding_applications (
                "Id", "SourceKind", "SourceId", "SubjectId",
                "VerifiedAccountEmail", "DisplayName", "Status", "Version",
                "CreatedAtUtc", "LastChangedAtUtc", "ScopeId")
            VALUES (
                '2a000000-0000-0000-0000-000000000001', 2,
                '2b000000-0000-0000-0000-000000000001', 'subject:newcomer',
                'newcomer@example.test', 'New Applicant', 1, 1,
                '2026-08-11T21:00:00Z', '2026-08-11T21:00:00Z',
                '{{TenantId}}')
            RETURNING "IdentityAnchorSweepOrdinal"
            """).ConfigureAwait(false);
        Assert.True(newcomerOrdinal > 0);

        long rewrittenForgedOrdinal = await ExecuteScalarAsync<long>(
            connectionString,
            $$"""
            INSERT INTO workspaces.staff_onboarding_applications (
                "Id", "SourceKind", "SourceId", "SubjectId",
                "VerifiedAccountEmail", "DisplayName", "Status",
                "Version", "CreatedAtUtc", "LastChangedAtUtc", "ScopeId",
                "IdentityAnchorSweepOrdinal")
            OVERRIDING SYSTEM VALUE
            VALUES (
                '2a000000-0000-0000-0000-000000000002', 2,
                '2b000000-0000-0000-0000-000000000002',
                'subject:forged-ordinal', 'forged@example.test',
                'Forged Applicant', 1, 1, '2026-08-11T21:00:00Z',
                '2026-08-11T21:00:00Z', '{{TenantId}}', 9000000)
            RETURNING "IdentityAnchorSweepOrdinal"
            """).ConfigureAwait(false);
        Assert.NotEqual(9000000, rewrittenForgedOrdinal);
        Assert.True(rewrittenForgedOrdinal > newcomerOrdinal);

        await using (NpgsqlConnection ordinalSession = new(connectionString))
        {
            await ordinalSession.OpenAsync().ConfigureAwait(false);
            await using NpgsqlCommand allocated = new(
                $$"""
                INSERT INTO workspaces.staff_onboarding_applications (
                    "Id", "SourceKind", "SourceId", "SubjectId",
                    "VerifiedAccountEmail", "DisplayName", "Status", "Version",
                    "CreatedAtUtc", "LastChangedAtUtc", "ScopeId")
                VALUES (
                    '2a000000-0000-0000-0000-000000000003', 2,
                    '2b000000-0000-0000-0000-000000000003',
                    'subject:allocated-before-replay', 'allocated@example.test',
                    'Allocated Applicant', 1, 1, '2026-08-11T21:00:00Z',
                    '2026-08-11T21:00:00Z', '{{TenantId}}')
                RETURNING "IdentityAnchorSweepOrdinal"
                """,
                ordinalSession);
            long allocatedOrdinal = Assert.IsType<long>(
                await allocated.ExecuteScalarAsync().ConfigureAwait(false));
            await using NpgsqlCommand replay = new(
                $$"""
                INSERT INTO workspaces.staff_onboarding_applications (
                    "Id", "SourceKind", "SourceId", "SubjectId",
                    "VerifiedAccountEmail", "DisplayName", "Status", "Version",
                    "CreatedAtUtc", "LastChangedAtUtc", "ScopeId",
                    "IdentityAnchorSweepOrdinal")
                OVERRIDING SYSTEM VALUE
                VALUES (
                    '2a000000-0000-0000-0000-000000000004', 2,
                    '2b000000-0000-0000-0000-000000000004',
                    'subject:cross-tenant-currval-replay',
                    'replay@example.test', 'Replay Applicant', 1, 1,
                    '2026-08-11T21:00:00Z', '2026-08-11T21:00:00Z',
                    '{{OtherTenantId}}', {{allocatedOrdinal}})
                RETURNING "IdentityAnchorSweepOrdinal"
                """,
                ordinalSession);
            long replayedCurrval = Assert.IsType<long>(
                await replay.ExecuteScalarAsync().ConfigureAwait(false));
            Assert.True(replayedCurrval > allocatedOrdinal);

            long rolledBackOrdinal;
            await using (NpgsqlTransaction rolledBackAllocation =
                         await ordinalSession.BeginTransactionAsync()
                             .ConfigureAwait(false))
            {
                await using NpgsqlCommand allocateThenRollback = new(
                    $$"""
                    INSERT INTO workspaces.staff_onboarding_applications (
                        "Id", "SourceKind", "SourceId", "SubjectId",
                        "VerifiedAccountEmail", "DisplayName", "Status",
                        "Version", "CreatedAtUtc", "LastChangedAtUtc",
                        "ScopeId")
                    VALUES (
                        '2a000000-0000-0000-0000-000000000005', 1,
                        '2b000000-0000-0000-0000-000000000005',
                        'subject:rolled-back-ordinal',
                        'rolled-back@example.test', 'Rolled Back', 1, 1,
                        '2026-08-11T21:00:00Z', '2026-08-11T21:00:00Z',
                        '{{TenantId}}')
                    RETURNING "IdentityAnchorSweepOrdinal"
                    """,
                    ordinalSession,
                    rolledBackAllocation);
                rolledBackOrdinal = Assert.IsType<long>(
                    await allocateThenRollback.ExecuteScalarAsync()
                        .ConfigureAwait(false));
                await rolledBackAllocation.RollbackAsync()
                    .ConfigureAwait(false);
            }

            long committedHighWater = await ExecuteScalarAsync<long>(
                connectionString,
                $$"""
                INSERT INTO workspaces.staff_onboarding_applications (
                    "Id", "SourceKind", "SourceId", "SubjectId",
                    "VerifiedAccountEmail", "DisplayName", "Status",
                    "Version", "CreatedAtUtc", "LastChangedAtUtc", "ScopeId")
                VALUES (
                    '2a000000-0000-0000-0000-000000000006', 1,
                    '2b000000-0000-0000-0000-000000000006',
                    'subject:later-committed-ordinal',
                    'later@example.test', 'Later Committed', 1, 1,
                    '2026-08-11T21:00:00Z', '2026-08-11T21:00:00Z',
                    '{{TenantId}}')
                RETURNING "IdentityAnchorSweepOrdinal"
                """).ConfigureAwait(false);
            Assert.True(committedHighWater > rolledBackOrdinal);

            await using NpgsqlCommand rolledBackCurrvalReplay = new(
                $$"""
                INSERT INTO workspaces.staff_onboarding_applications (
                    "Id", "SourceKind", "SourceId", "SubjectId",
                    "VerifiedAccountEmail", "DisplayName", "Status",
                    "Version", "CreatedAtUtc", "LastChangedAtUtc", "ScopeId",
                    "IdentityAnchorSweepOrdinal")
                OVERRIDING SYSTEM VALUE
                VALUES (
                    '2a000000-0000-0000-0000-000000000007', 2,
                    '2b000000-0000-0000-0000-000000000007',
                    'subject:rolled-back-currval-replay',
                    'rollback-replay@example.test', 'Rollback Replay', 1, 1,
                    '2026-08-11T21:00:00Z', '2026-08-11T21:00:00Z',
                    '{{OtherTenantId}}', {{rolledBackOrdinal}})
                RETURNING "IdentityAnchorSweepOrdinal"
                """,
                ordinalSession);
            long actualOrdinal = Assert.IsType<long>(
                await rolledBackCurrvalReplay.ExecuteScalarAsync()
                    .ConfigureAwait(false));
            Assert.True(
                actualOrdinal > committedHighWater,
                $"Rolled-back currval {rolledBackOrdinal} was persisted " +
                $"behind committed high-water {committedHighWater}.");
        }

        PostgresException mutatedOrdinal =
            await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteNonQueryAsync(
                    connectionString,
                    """
                    UPDATE workspaces.staff_onboarding_applications
                    SET "IdentityAnchorSweepOrdinal" =
                        "IdentityAnchorSweepOrdinal" + 1
                    WHERE "Id" =
                        '2a000000-0000-0000-0000-000000000001'
                    """)).ConfigureAwait(false);
        Assert.True(mutatedOrdinal.SqlState is "P0001" or "428C9");
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task
        Raw_onboarding_anchor_protocol_rejects_partial_changed_and_mismatched_coordinates()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_workspaces_anchor_raw_protocol_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);
        string connectionString = postgreSql.GetConnectionString();
        await using (WorkspacesDbContext context = CreateDbContext(
                         connectionString,
                         TenantId))
        {
            await context.Database.GetService<IMigrator>()
                .MigrateAsync(CurrentMigration)
                .ConfigureAwait(false);
        }

        long ordinaryUnboundOrdinal = await ExecuteScalarAsync<long>(
            connectionString,
            $$"""
            INSERT INTO workspaces.staff_onboarding_applications (
                "Id", "SourceKind", "SourceId", "SubjectId",
                "VerifiedAccountEmail", "DisplayName", "Status", "Version",
                "CreatedAtUtc", "LastChangedAtUtc", "ScopeId")
            VALUES (
                '30000000-0000-0000-0000-000000000001', 1,
                '30000000-0000-0000-0000-000000000011',
                'subject:ordinary-unbound-insert', 'ordinary@example.test',
                'Ordinary Unbound', 1, 1, '2026-08-11T20:59:00Z',
                '2026-08-11T20:59:00Z', '{{TenantId}}')
            RETURNING "IdentityAnchorSweepOrdinal"
            """).ConfigureAwait(false);
        Assert.True(ordinaryUnboundOrdinal > 0);

        PostgresException boundContinuationInsert =
            await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteNonQueryAsync(
                    connectionString,
                    $$"""
                    INSERT INTO workspaces.staff_onboarding_applications (
                        "Id", "SourceKind", "SourceId", "SubjectId", "Status",
                        "StaffMemberId",
                        "IdentityAnchorExpectedResolutionEventId",
                        "IdentityAnchorContinuationEventId", "Version",
                        "CreatedAtUtc", "LastChangedAtUtc", "ScopeId")
                    VALUES (
                        '30000000-0000-0000-0000-000000000002', 2,
                        '30000000-0000-0000-0000-000000000012',
                        'subject:forged-bound-continuation', 4,
                        '30000000-0000-0000-0000-000000000022',
                        '30000000-0000-0000-0000-000000000032',
                        '30000000-0000-0000-0000-000000000042', 2,
                        '2026-08-11T20:59:00Z', '2026-08-11T21:00:00Z',
                        '{{TenantId}}')
                    """)).ConfigureAwait(false);
        Assert.Equal("P0001", boundContinuationInsert.SqlState);

        PostgresException terminalResolutionInsert =
            await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteNonQueryAsync(
                    connectionString,
                    $$"""
                    INSERT INTO workspaces.staff_onboarding_applications (
                        "Id", "SourceKind", "SourceId", "SubjectId", "Status",
                        "StaffMemberId",
                        "IdentityAnchorExpectedResolutionEventId",
                        "IdentityAnchorResolutionEventId",
                        "IdentityAnchorResolutionStaffMemberId",
                        "IdentityAnchorResolutionApplicationVersion",
                        "IdentityAnchorResolutionDisposition",
                        "IdentityAnchorResolutionIntentAtUtc", "Version",
                        "CreatedAtUtc", "LastChangedAtUtc", "ScopeId")
                    VALUES (
                        '30000000-0000-0000-0000-000000000003', 1,
                        '30000000-0000-0000-0000-000000000013',
                        'subject:forged-terminal-resolution', 5,
                        '30000000-0000-0000-0000-000000000023',
                        '30000000-0000-0000-0000-000000000033',
                        '30000000-0000-0000-0000-000000000033',
                        '30000000-0000-0000-0000-000000000023', 3, 1,
                        '2026-08-11T21:00:00Z', 3,
                        '2026-08-11T20:59:00Z', '2026-08-11T21:00:00Z',
                        '{{TenantId}}')
                    """)).ConfigureAwait(false);
        Assert.Equal("P0001", terminalResolutionInsert.SqlState);
        Assert.True(await ExecuteScalarAsync<bool>(
            connectionString,
            """
            SELECT COUNT(*) = 0
            FROM workspaces.staff_onboarding_applications
            WHERE "Id" IN (
                '30000000-0000-0000-0000-000000000002',
                '30000000-0000-0000-0000-000000000003')
            """).ConfigureAwait(false));

        await ExecuteNonQueryAsync(
            connectionString,
            $$"""
            INSERT INTO workspaces.staff_onboarding_applications (
                "Id", "SourceKind", "SourceId", "SubjectId",
                "VerifiedAccountEmail", "DisplayName", "Status", "Version",
                "CreatedAtUtc", "LastChangedAtUtc", "ScopeId")
            VALUES (
                '31000000-0000-0000-0000-000000000001', 1,
                '32000000-0000-0000-0000-000000000001',
                'subject:anchor-protocol', 'anchor@example.test',
                'Anchor Applicant', 1, 1, '2026-08-11T21:00:00Z',
                '2026-08-11T21:00:00Z', '{{TenantId}}')
            """).ConfigureAwait(false);

        await ExecuteNonQueryAsync(
            connectionString,
            $$"""
            INSERT INTO workspaces.staff_onboarding_applications (
                "Id", "SourceKind", "SourceId", "SubjectId",
                "VerifiedAccountEmail", "DisplayName", "Status", "Version",
                "CreatedAtUtc", "LastChangedAtUtc", "ScopeId")
            VALUES (
                '31000000-0000-0000-0000-000000000002', 1,
                '32000000-0000-0000-0000-000000000002',
                'subject:direct-terminal-anchor', 'terminal@example.test',
                'Direct Terminal Applicant', 1, 1,
                '2026-08-11T21:00:00Z', '2026-08-11T21:00:00Z',
                '{{TenantId}}')
            """).ConfigureAwait(false);

        PostgresException partialExpected =
            await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteNonQueryAsync(
                    connectionString,
                    """
                    UPDATE workspaces.staff_onboarding_applications
                    SET "IdentityAnchorExpectedResolutionEventId" =
                            '33000000-0000-0000-0000-000000000001',
                        "Version" = 2,
                        "LastChangedAtUtc" = '2026-08-11T21:01:00Z'
                    WHERE "Id" =
                        '31000000-0000-0000-0000-000000000001'
                    """)).ConfigureAwait(false);
        Assert.True(partialExpected.SqlState is "P0001" or "23514");

        await ExecuteNonQueryAsync(
            connectionString,
            """
            UPDATE workspaces.staff_onboarding_applications
            SET "StaffMemberId" =
                    '34000000-0000-0000-0000-000000000001',
                "IdentityAnchorExpectedResolutionEventId" =
                    '33000000-0000-0000-0000-000000000001',
                "IdentityAnchorContinuationEventId" =
                    '35000000-0000-0000-0000-000000000001',
                "VerifiedAccountEmail" = NULL,
                "DisplayName" = NULL,
                "Status" = 4,
                "Version" = 2,
                "LastChangedAtUtc" = '2026-08-11T21:01:00Z'
            WHERE "Id" = '31000000-0000-0000-0000-000000000001'
            """).ConfigureAwait(false);

        PostgresException changedExpected =
            await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteNonQueryAsync(
                    connectionString,
                    """
                    UPDATE workspaces.staff_onboarding_applications
                    SET "IdentityAnchorExpectedResolutionEventId" =
                            '33000000-0000-0000-0000-000000000002',
                        "Version" = 3,
                        "LastChangedAtUtc" = '2026-08-11T21:02:00Z'
                    WHERE "Id" =
                        '31000000-0000-0000-0000-000000000001'
                    """)).ConfigureAwait(false);
        Assert.Equal("P0001", changedExpected.SqlState);

        PostgresException clearedContinuation =
            await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteNonQueryAsync(
                    connectionString,
                    """
                    UPDATE workspaces.staff_onboarding_applications
                    SET "IdentityAnchorContinuationEventId" = NULL,
                        "Version" = 3,
                        "LastChangedAtUtc" = '2026-08-11T21:02:00Z'
                    WHERE "Id" =
                        '31000000-0000-0000-0000-000000000001'
                    """)).ConfigureAwait(false);
        Assert.Equal("P0001", clearedContinuation.SqlState);

        PostgresException partialResolution =
            await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteNonQueryAsync(
                    connectionString,
                    """
                    UPDATE workspaces.staff_onboarding_applications
                    SET "IdentityAnchorResolutionEventId" =
                            '33000000-0000-0000-0000-000000000001',
                        "Status" = 5,
                        "Version" = 3,
                        "LastChangedAtUtc" = '2026-08-11T21:02:00Z'
                    WHERE "Id" =
                        '31000000-0000-0000-0000-000000000001'
                    """)).ConfigureAwait(false);
        Assert.Equal("P0001", partialResolution.SqlState);

        PostgresException mismatchedDisposition =
            await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteNonQueryAsync(
                    connectionString,
                    """
                    UPDATE workspaces.staff_onboarding_applications
                    SET "IdentityAnchorResolutionEventId" =
                            '33000000-0000-0000-0000-000000000001',
                        "IdentityAnchorResolutionStaffMemberId" =
                            '34000000-0000-0000-0000-000000000001',
                        "IdentityAnchorResolutionApplicationVersion" = 3,
                        "IdentityAnchorResolutionDisposition" = 2,
                        "IdentityAnchorResolutionIntentAtUtc" =
                            '2026-08-11T21:02:00Z',
                        "Status" = 5,
                        "Version" = 3,
                        "LastChangedAtUtc" = '2026-08-11T21:02:00Z'
                    WHERE "Id" =
                        '31000000-0000-0000-0000-000000000001'
                    """)).ConfigureAwait(false);
        Assert.Equal(PostgresErrorCodes.CheckViolation,
            mismatchedDisposition.SqlState);

        PostgresException observationWithFirstResolution =
            await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteNonQueryAsync(
                    connectionString,
                    """
                    UPDATE workspaces.staff_onboarding_applications
                    SET "IdentityAnchorResolutionEventId" =
                            '33000000-0000-0000-0000-000000000001',
                        "IdentityAnchorResolutionStaffMemberId" =
                            '34000000-0000-0000-0000-000000000001',
                        "IdentityAnchorResolutionApplicationVersion" = 3,
                        "IdentityAnchorResolutionDisposition" = 1,
                        "IdentityAnchorResolutionIntentAtUtc" =
                            '2026-08-11T21:02:00Z',
                        "IdentityAnchorResolutionObservedAtUtc" =
                            '2026-08-11T21:02:00Z',
                        "Status" = 5,
                        "Version" = 3,
                        "LastChangedAtUtc" = '2026-08-11T21:02:00Z'
                    WHERE "Id" =
                        '31000000-0000-0000-0000-000000000001'
                    """)).ConfigureAwait(false);
        Assert.Equal("P0001", observationWithFirstResolution.SqlState);

        await using (NpgsqlConnection sameTransaction =
                     new(connectionString))
        {
            await sameTransaction.OpenAsync().ConfigureAwait(false);
            await using NpgsqlTransaction sameTransactionResolution =
                await sameTransaction.BeginTransactionAsync()
                    .ConfigureAwait(false);
            await ExecuteNonQueryAsync(
                    sameTransaction,
                    sameTransactionResolution,
                    """
                    UPDATE workspaces.staff_onboarding_applications
                    SET "IdentityAnchorResolutionEventId" =
                            '33000000-0000-0000-0000-000000000001',
                        "IdentityAnchorResolutionStaffMemberId" =
                            '34000000-0000-0000-0000-000000000001',
                        "IdentityAnchorResolutionApplicationVersion" = 3,
                        "IdentityAnchorResolutionDisposition" = 1,
                        "IdentityAnchorResolutionIntentAtUtc" =
                            '2026-08-11T21:02:00Z',
                        "Status" = 5,
                        "Version" = 3,
                        "LastChangedAtUtc" = '2026-08-11T21:02:00Z'
                    WHERE "Id" =
                        '31000000-0000-0000-0000-000000000001'
                    """)
                .ConfigureAwait(false);

            PostgresException observationBeforeIntentCommit =
                await Assert.ThrowsAsync<PostgresException>(() =>
                    ExecuteNonQueryAsync(
                        sameTransaction,
                        sameTransactionResolution,
                        """
                        UPDATE workspaces.staff_onboarding_applications
                        SET "IdentityAnchorResolutionObservedAtUtc" =
                                '2026-08-11T21:03:00Z',
                            "Version" = 4,
                            "LastChangedAtUtc" = '2026-08-11T21:03:00Z'
                        WHERE "Id" =
                            '31000000-0000-0000-0000-000000000001'
                        """));
            Assert.Equal("P0001", observationBeforeIntentCommit.SqlState);
            await sameTransactionResolution.RollbackAsync()
                .ConfigureAwait(false);
        }

        await ExecuteNonQueryAsync(
            connectionString,
            """
            UPDATE workspaces.staff_onboarding_applications
            SET "IdentityAnchorResolutionEventId" =
                    '33000000-0000-0000-0000-000000000001',
                "IdentityAnchorResolutionStaffMemberId" =
                    '34000000-0000-0000-0000-000000000001',
                "IdentityAnchorResolutionApplicationVersion" = 3,
                "IdentityAnchorResolutionDisposition" = 1,
                "IdentityAnchorResolutionIntentAtUtc" =
                    '2026-08-11T21:02:00Z',
                "Status" = 5,
                "Version" = 3,
                "LastChangedAtUtc" = '2026-08-11T21:02:00Z'
            WHERE "Id" = '31000000-0000-0000-0000-000000000001'
            """).ConfigureAwait(false);
        Assert.True(await ExecuteScalarAsync<bool>(
            connectionString,
            """
            SELECT "IdentityAnchorResolutionEventId" =
                       '33000000-0000-0000-0000-000000000001'
                   AND "IdentityAnchorResolutionObservedAtUtc" IS NULL
                   AND "Version" = 3
            FROM workspaces.staff_onboarding_applications
            WHERE "Id" = '31000000-0000-0000-0000-000000000001'
            """).ConfigureAwait(false));

        PostgresException earlyObservation =
            await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteNonQueryAsync(
                    connectionString,
                    """
                    UPDATE workspaces.staff_onboarding_applications
                    SET "IdentityAnchorResolutionObservedAtUtc" =
                            '2026-08-11T21:01:59Z',
                        "Version" = 4,
                        "LastChangedAtUtc" = '2026-08-11T21:01:59Z'
                    WHERE "Id" =
                        '31000000-0000-0000-0000-000000000001'
                    """)).ConfigureAwait(false);
        Assert.True(earlyObservation.SqlState is "P0001" or "23514");

        await ExecuteNonQueryAsync(
            connectionString,
            """
            UPDATE workspaces.staff_onboarding_applications
            SET "IdentityAnchorResolutionObservedAtUtc" =
                    '2026-08-11T21:03:00Z',
                "Version" = 4,
                "LastChangedAtUtc" = '2026-08-11T21:03:00Z'
            WHERE "Id" = '31000000-0000-0000-0000-000000000001'
            """).ConfigureAwait(false);

        PostgresException changedObservation =
            await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteNonQueryAsync(
                    connectionString,
                    """
                    UPDATE workspaces.staff_onboarding_applications
                    SET "IdentityAnchorResolutionObservedAtUtc" =
                            '2026-08-11T21:04:00Z',
                        "Version" = 5,
                        "LastChangedAtUtc" = '2026-08-11T21:04:00Z'
                    WHERE "Id" =
                        '31000000-0000-0000-0000-000000000001'
                    """)).ConfigureAwait(false);
        Assert.Equal("P0001", changedObservation.SqlState);

        await ExecuteNonQueryAsync(
            connectionString,
            """
            UPDATE workspaces.staff_onboarding_applications
            SET "StaffMemberId" =
                    '34000000-0000-0000-0000-000000000002',
                "IdentityAnchorExpectedResolutionEventId" =
                    '33000000-0000-0000-0000-000000000002',
                "IdentityAnchorResolutionEventId" =
                    '33000000-0000-0000-0000-000000000002',
                "IdentityAnchorResolutionStaffMemberId" =
                    '34000000-0000-0000-0000-000000000002',
                "IdentityAnchorResolutionApplicationVersion" = 2,
                "IdentityAnchorResolutionDisposition" = 1,
                "IdentityAnchorResolutionIntentAtUtc" =
                    '2026-08-11T21:01:00Z',
                "VerifiedAccountEmail" = NULL,
                "DisplayName" = NULL,
                "Status" = 5,
                "Version" = 2,
                "LastChangedAtUtc" = '2026-08-11T21:01:00Z'
            WHERE "Id" = '31000000-0000-0000-0000-000000000002'
            """).ConfigureAwait(false);
        Assert.True(await ExecuteScalarAsync<bool>(
            connectionString,
            """
            SELECT "IdentityAnchorContinuationEventId" IS NULL
                   AND "IdentityAnchorResolutionEventId" =
                       '33000000-0000-0000-0000-000000000002'
                   AND "Status" = 5
                   AND "Version" = 2
            FROM workspaces.staff_onboarding_applications
            WHERE "Id" = '31000000-0000-0000-0000-000000000002'
            """).ConfigureAwait(false));

        PostgresException continuationAfterTerminalResolution =
            await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteNonQueryAsync(
                    connectionString,
                    """
                    UPDATE workspaces.staff_onboarding_applications
                    SET "IdentityAnchorContinuationEventId" =
                            '35000000-0000-0000-0000-000000000002',
                        "Version" = 3,
                        "LastChangedAtUtc" = '2026-08-11T21:02:00Z'
                    WHERE "Id" =
                        '31000000-0000-0000-0000-000000000002'
                    """)).ConfigureAwait(false);
        Assert.Equal("P0001", continuationAfterTerminalResolution.SqlState);
        Assert.True(await ExecuteScalarAsync<bool>(
            connectionString,
            """
            SELECT "IdentityAnchorContinuationEventId" IS NULL
                   AND "IdentityAnchorResolutionEventId" =
                       '33000000-0000-0000-0000-000000000002'
                   AND "Version" = 2
            FROM workspaces.staff_onboarding_applications
            WHERE "Id" = '31000000-0000-0000-0000-000000000002'
            """).ConfigureAwait(false));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task
        Raw_access_and_checkpoint_protocols_reject_ambiguous_or_nonmonotonic_state()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_workspaces_anchor_control_state_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);
        string connectionString = postgreSql.GetConnectionString();
        await using (WorkspacesDbContext context = CreateDbContext(
                         connectionString,
                         TenantId))
        {
            await context.Database.GetService<IMigrator>()
                .MigrateAsync(CurrentMigration)
                .ConfigureAwait(false);
        }

        await ExecuteNonQueryAsync(
            connectionString,
            $$"""
            INSERT INTO workspaces.staff_access_processes (
                "Id", "StaffMemberId", "SubjectId", "TargetState",
                "RestorationDisposition", "TargetStaffVersion", "EffectiveOn",
                "RequestedBy", "State", "Version", "CreatedAtUtc",
                "LastChangedAtUtc", "ScopeId")
            VALUES (
                '41000000-0000-0000-0000-000000000001',
                '42000000-0000-0000-0000-000000000001',
                'subject:suppressed', 1, 3, 2, '2026-08-11',
                'migration-test', 1, 1, '2026-08-11T21:00:00Z',
                '2026-08-11T21:00:00Z', '{{TenantId}}')
            """).ConfigureAwait(false);
        PostgresException snapshotAfterSuppressed =
            await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteNonQueryAsync(
                    connectionString,
                    """
                    INSERT INTO workspaces.staff_access_profile_snapshots (
                        "ProfileId", "ProcessId", "AssignmentScope")
                    VALUES (
                        '43000000-0000-0000-0000-000000000001',
                        '41000000-0000-0000-0000-000000000001',
                        'tenant:10000000000000000000000000000001')
                    """)).ConfigureAwait(false);
        Assert.Equal("P0001", snapshotAfterSuppressed.SqlState);

        await ExecuteNonQueryAsync(
            connectionString,
            $$"""
            INSERT INTO workspaces.staff_access_processes (
                "Id", "StaffMemberId", "SubjectId", "TargetState",
                "RestorationDisposition", "TargetStaffVersion", "EffectiveOn",
                "RequestedBy", "State", "Version", "CreatedAtUtc",
                "LastChangedAtUtc", "ScopeId")
            VALUES (
                '41000000-0000-0000-0000-000000000002',
                '42000000-0000-0000-0000-000000000002',
                'subject:restore-snapshot', 1, 2, 2, '2026-08-11',
                'migration-test', 1, 1, '2026-08-11T21:00:00Z',
                '2026-08-11T21:00:00Z', '{{TenantId}}');
            INSERT INTO workspaces.staff_access_profile_snapshots (
                "ProfileId", "ProcessId", "AssignmentScope")
            VALUES (
                '43000000-0000-0000-0000-000000000002',
                '41000000-0000-0000-0000-000000000002',
                'tenant:10000000000000000000000000000001');
            """).ConfigureAwait(false);
        PostgresException suppressAfterSnapshot =
            await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteNonQueryAsync(
                    connectionString,
                    """
                    UPDATE workspaces.staff_access_processes
                    SET "RestorationDisposition" = 3
                    WHERE "Id" =
                        '41000000-0000-0000-0000-000000000002'
                    """)).ConfigureAwait(false);
        Assert.Equal("P0001", suppressAfterSnapshot.SqlState);

        PostgresException mismatchedAccessDisposition =
            await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteNonQueryAsync(
                    connectionString,
                    $$"""
                    INSERT INTO workspaces.staff_access_processes (
                        "Id", "StaffMemberId", "SubjectId", "TargetState",
                        "RestorationDisposition", "TargetStaffVersion",
                        "EffectiveOn", "RequestedBy", "State", "Version",
                        "CreatedAtUtc", "LastChangedAtUtc", "ScopeId")
                    VALUES (
                        '41000000-0000-0000-0000-000000000003',
                        '42000000-0000-0000-0000-000000000003',
                        'subject:mismatched-disposition', 2, 2, 2,
                        '2026-08-11', 'migration-test', 1, 1,
                        '2026-08-11T21:00:00Z', '2026-08-11T21:00:00Z',
                        '{{TenantId}}')
                    """)).ConfigureAwait(false);
        Assert.Equal(PostgresErrorCodes.CheckViolation,
            mismatchedAccessDisposition.SqlState);

        await ExecuteNonQueryAsync(
            connectionString,
            $$"""
            INSERT INTO workspaces.staff_onboarding_applications (
                "Id", "SourceKind", "SourceId", "SubjectId",
                "VerifiedAccountEmail", "DisplayName", "Status", "Version",
                "CreatedAtUtc", "LastChangedAtUtc", "ScopeId")
            VALUES
                ('4b000000-0000-0000-0000-000000000001', 1,
                 '4c000000-0000-0000-0000-000000000001',
                 'subject:checkpoint-first', 'checkpoint-first@example.test',
                 'Checkpoint First', 1, 1, '2026-08-11T21:00:00Z',
                 '2026-08-11T21:00:00Z', '{{TenantId}}'),
                ('4b000000-0000-0000-0000-000000000002', 2,
                 '4c000000-0000-0000-0000-000000000002',
                 'subject:checkpoint-second',
                 'checkpoint-second@example.test', 'Checkpoint Second', 1, 1,
                 '2026-08-11T21:00:00Z', '2026-08-11T21:00:00Z',
                 '{{TenantId}}')
            """).ConfigureAwait(false);
        long firstOrdinal = await ExecuteScalarAsync<long>(
            connectionString,
            $$"""
            SELECT min("IdentityAnchorSweepOrdinal")
            FROM workspaces.staff_onboarding_applications
            WHERE "ScopeId" = '{{TenantId}}'
            """).ConfigureAwait(false);
        long cycleUpperOrdinal = await ExecuteScalarAsync<long>(
            connectionString,
            $$"""
            SELECT max("IdentityAnchorSweepOrdinal")
            FROM workspaces.staff_onboarding_applications
            WHERE "ScopeId" = '{{TenantId}}'
            """).ConfigureAwait(false);

        await ExecuteNonQueryAsync(
            connectionString,
            $$"""
            INSERT INTO
                workspaces.workspace_staff_identity_anchor_sweep_checkpoints (
                "Id", "ProtocolVersion", "CycleId", "CycleUpperOrdinal",
                "AfterOrdinal", "CycleStartedAtUtc", "CycleScannedCount",
                "CycleNoAnchorCount", "CycleRemovedCount",
                "CycleObservedCount", "CycleAlreadyObservedCount",
                "CycleDeferredCount", "CycleConflictCount",
                "CyclePassOneCommittedCount",
                "CycleResolutionRecordConfirmedCount",
                "LastCompletedCycleId", "LastCompletedUpperOrdinal",
                "LastCompletedAtUtc", "LastCompletedScannedCount",
                "LastCompletedNoAnchorCount", "LastCompletedRemovedCount",
                "LastCompletedObservedCount",
                "LastCompletedAlreadyObservedCount",
                "LastCompletedDeferredCount", "LastCompletedConflictCount",
                "LastCompletedPassOneCommittedCount",
                "LastCompletedResolutionRecordConfirmedCount",
                "LastAdvanceId", "LastAdvanceSha256", "LastRunId",
                "UpdatedAtUtc", "Version", "ScopeId")
            VALUES (
                '44000000-0000-0000-0000-000000000001', 1, NULL, NULL,
                NULL, NULL, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                NULL, NULL, NULL, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                NULL, NULL, NULL, '2026-08-11T21:00:00Z', 1,
                '{{TenantId}}')
            """).ConfigureAwait(false);

        PostgresException skippedVersion =
            await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteNonQueryAsync(
                    connectionString,
                    """
                    UPDATE
                        workspaces.workspace_staff_identity_anchor_sweep_checkpoints
                    SET "Version" = 3,
                        "UpdatedAtUtc" = '2026-08-11T21:01:00Z'
                    WHERE "Id" =
                        '44000000-0000-0000-0000-000000000001'
                    """)).ConfigureAwait(false);
        Assert.Equal("P0001", skippedVersion.SqlState);

        await ExecuteNonQueryAsync(
            connectionString,
            $$"""
            UPDATE workspaces.workspace_staff_identity_anchor_sweep_checkpoints
            SET "CycleId" =
                    '45000000-0000-0000-0000-000000000001',
                "CycleUpperOrdinal" = {{cycleUpperOrdinal}},
                "CycleStartedAtUtc" = '2026-08-11T21:01:00Z',
                "LastRunId" =
                    '46000000-0000-0000-0000-000000000001',
                "UpdatedAtUtc" = '2026-08-11T21:01:00Z',
                "Version" = 2
            WHERE "Id" = '44000000-0000-0000-0000-000000000001'
            """).ConfigureAwait(false);

        PostgresException malformedDigest =
            await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteNonQueryAsync(
                    connectionString,
                    $$"""
                    UPDATE
                        workspaces.workspace_staff_identity_anchor_sweep_checkpoints
                    SET "AfterOrdinal" = {{firstOrdinal}},
                        "CycleScannedCount" = 1,
                        "CycleNoAnchorCount" = 1,
                        "LastAdvanceId" =
                            '47000000-0000-0000-0000-000000000001',
                        "LastAdvanceSha256" = repeat('A', 64),
                        "LastRunId" =
                            '46000000-0000-0000-0000-000000000002',
                        "UpdatedAtUtc" = '2026-08-11T21:02:00Z',
                        "Version" = 3
                    WHERE "Id" =
                        '44000000-0000-0000-0000-000000000001'
                    """)).ConfigureAwait(false);
        Assert.Equal("P0001", malformedDigest.SqlState);

        PostgresException forgedAdvanceDigest =
            await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteNonQueryAsync(
                    connectionString,
                    $$"""
                    UPDATE
                        workspaces.workspace_staff_identity_anchor_sweep_checkpoints
                    SET "AfterOrdinal" = {{firstOrdinal}},
                        "CycleScannedCount" = 1,
                        "CycleNoAnchorCount" = 1,
                        "LastAdvanceId" =
                            '47000000-0000-0000-0000-000000000001',
                        "LastAdvanceSha256" = repeat('a', 64),
                        "LastRunId" =
                            '46000000-0000-0000-0000-000000000002',
                        "UpdatedAtUtc" = '2026-08-11T21:02:00Z',
                        "Version" = 3
                    WHERE "Id" =
                        '44000000-0000-0000-0000-000000000001'
                    """)).ConfigureAwait(false);
        Assert.Equal("P0001", forgedAdvanceDigest.SqlState);

        string correctAdvanceDigest = Sha256(
            "2|45000000-0000-0000-0000-000000000001|null|" +
            $"{firstOrdinal}|0|" +
            "46000000-0000-0000-0000-000000000002|1|1|0|0|0|0|0|0|0");
        await ExecuteNonQueryAsync(
            connectionString,
            $$"""
            UPDATE workspaces.workspace_staff_identity_anchor_sweep_checkpoints
            SET "AfterOrdinal" = {{firstOrdinal}},
                "CycleScannedCount" = 1,
                "CycleNoAnchorCount" = 1,
                "LastAdvanceId" =
                    '47000000-0000-0000-0000-000000000001',
                "LastAdvanceSha256" = '{{correctAdvanceDigest}}',
                "LastRunId" =
                    '46000000-0000-0000-0000-000000000002',
                "UpdatedAtUtc" = '2026-08-11T21:02:00Z',
                "Version" = 3
            WHERE "Id" = '44000000-0000-0000-0000-000000000001'
            """).ConfigureAwait(false);

        string correctCompletionDigest = Sha256(
            "3|45000000-0000-0000-0000-000000000001|" +
            $"{firstOrdinal}|{cycleUpperOrdinal}|1|" +
            "46000000-0000-0000-0000-000000000003|1|1|0|0|0|0|0|0|0");
        PostgresException inflatedCompletion =
            await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteNonQueryAsync(
                    connectionString,
                    $$"""
                    UPDATE
                        workspaces.workspace_staff_identity_anchor_sweep_checkpoints
                    SET "CycleId" = NULL,
                        "CycleUpperOrdinal" = NULL,
                        "AfterOrdinal" = NULL,
                        "CycleStartedAtUtc" = NULL,
                        "CycleScannedCount" = 0,
                        "CycleNoAnchorCount" = 0,
                        "LastCompletedCycleId" =
                            '45000000-0000-0000-0000-000000000001',
                        "LastCompletedUpperOrdinal" = {{cycleUpperOrdinal}},
                        "LastCompletedAtUtc" = '2026-08-11T21:03:00Z',
                        "LastCompletedScannedCount" = 3,
                        "LastCompletedNoAnchorCount" = 3,
                        "LastAdvanceId" =
                            '47000000-0000-0000-0000-000000000002',
                        "LastAdvanceSha256" =
                            '{{correctCompletionDigest}}',
                        "LastRunId" =
                            '46000000-0000-0000-0000-000000000003',
                        "UpdatedAtUtc" = '2026-08-11T21:03:00Z',
                        "Version" = 4
                    WHERE "Id" =
                        '44000000-0000-0000-0000-000000000001'
                    """)).ConfigureAwait(false);
        Assert.Equal("P0001", inflatedCompletion.SqlState);

        await ExecuteNonQueryAsync(
            connectionString,
            $$"""
            UPDATE workspaces.workspace_staff_identity_anchor_sweep_checkpoints
            SET "CycleId" = NULL,
                "CycleUpperOrdinal" = NULL,
                "AfterOrdinal" = NULL,
                "CycleStartedAtUtc" = NULL,
                "CycleScannedCount" = 0,
                "CycleNoAnchorCount" = 0,
                "LastCompletedCycleId" =
                    '45000000-0000-0000-0000-000000000001',
                "LastCompletedUpperOrdinal" = {{cycleUpperOrdinal}},
                "LastCompletedAtUtc" = '2026-08-11T21:03:00Z',
                "LastCompletedScannedCount" = 2,
                "LastCompletedNoAnchorCount" = 2,
                "LastAdvanceId" =
                    '47000000-0000-0000-0000-000000000002',
                "LastAdvanceSha256" = '{{correctCompletionDigest}}',
                "LastRunId" =
                    '46000000-0000-0000-0000-000000000003',
                "UpdatedAtUtc" = '2026-08-11T21:03:00Z',
                "Version" = 4
            WHERE "Id" = '44000000-0000-0000-0000-000000000001'
            """).ConfigureAwait(false);

        await ExecuteNonQueryAsync(
            connectionString,
            $$"""
            INSERT INTO
                workspaces.workspace_staff_identity_anchor_sweep_checkpoints (
                "Id", "ProtocolVersion", "CycleScannedCount",
                "CycleNoAnchorCount", "CycleRemovedCount",
                "CycleObservedCount", "CycleAlreadyObservedCount",
                "CycleDeferredCount", "CycleConflictCount",
                "CyclePassOneCommittedCount",
                "CycleResolutionRecordConfirmedCount",
                "LastCompletedScannedCount", "LastCompletedNoAnchorCount",
                "LastCompletedRemovedCount", "LastCompletedObservedCount",
                "LastCompletedAlreadyObservedCount",
                "LastCompletedDeferredCount", "LastCompletedConflictCount",
                "LastCompletedPassOneCommittedCount",
                "LastCompletedResolutionRecordConfirmedCount",
                "UpdatedAtUtc", "Version", "ScopeId")
            VALUES (
                '44000000-0000-0000-0000-000000000002', 1,
                0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0,
                '2026-08-11T21:00:00Z', 1, '{{OtherTenantId}}')
            """).ConfigureAwait(false);
        PostgresException forgedEmptyDigest =
            await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteNonQueryAsync(
                    connectionString,
                    """
                    UPDATE
                        workspaces.workspace_staff_identity_anchor_sweep_checkpoints
                    SET "LastCompletedCycleId" =
                            '48000000-0000-0000-0000-000000000001',
                        "LastCompletedAtUtc" = '2026-08-11T21:01:00Z',
                        "LastAdvanceId" =
                            '49000000-0000-0000-0000-000000000001',
                        "LastAdvanceSha256" = repeat('b', 64),
                        "LastRunId" =
                            '4a000000-0000-0000-0000-000000000001',
                        "UpdatedAtUtc" = '2026-08-11T21:01:00Z',
                        "Version" = 2
                    WHERE "Id" =
                        '44000000-0000-0000-0000-000000000002'
                    """)).ConfigureAwait(false);
        Assert.Equal("P0001", forgedEmptyDigest.SqlState);

        string correctEmptyDigest = Sha256(
            "empty|48000000-0000-0000-0000-000000000001|" +
            "4a000000-0000-0000-0000-000000000001");
        await ExecuteNonQueryAsync(
            connectionString,
            $$"""
            UPDATE workspaces.workspace_staff_identity_anchor_sweep_checkpoints
            SET "LastCompletedCycleId" =
                    '48000000-0000-0000-0000-000000000001',
                "LastCompletedAtUtc" = '2026-08-11T21:01:00Z',
                "LastAdvanceId" =
                    '49000000-0000-0000-0000-000000000001',
                "LastAdvanceSha256" = '{{correctEmptyDigest}}',
                "LastRunId" =
                    '4a000000-0000-0000-0000-000000000001',
                "UpdatedAtUtc" = '2026-08-11T21:01:00Z',
                "Version" = 2
            WHERE "Id" = '44000000-0000-0000-0000-000000000002'
            """).ConfigureAwait(false);

        PostgresException unadmittedCheckpointDelete =
            await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteNonQueryAsync(
                    connectionString,
                    """
                    DELETE FROM
                        workspaces.workspace_staff_identity_anchor_sweep_checkpoints
                    WHERE "Id" =
                        '44000000-0000-0000-0000-000000000001'
                    """)).ConfigureAwait(false);
        Assert.Equal("P0001", unadmittedCheckpointDelete.SqlState);
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task
        Checkpoint_high_water_and_empty_completion_are_bound_to_current_tenant_backlog()
    {
        const string emptyTenantId =
            "10000000-0000-0000-0000-000000000003";
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_workspaces_anchor_checkpoint_truth_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);
        string connectionString = postgreSql.GetConnectionString();
        await using (WorkspacesDbContext context = CreateDbContext(
                         connectionString,
                         TenantId))
        {
            await context.Database.GetService<IMigrator>()
                .MigrateAsync(CurrentMigration)
                .ConfigureAwait(false);
        }

        await ExecuteNonQueryAsync(
            connectionString,
            $$"""
            INSERT INTO workspaces.staff_onboarding_applications (
                "Id", "SourceKind", "SourceId", "SubjectId",
                "VerifiedAccountEmail", "DisplayName", "Status", "Version",
                "CreatedAtUtc", "LastChangedAtUtc", "ScopeId")
            VALUES
                ('81000000-0000-0000-0000-000000000001', 1,
                 '82000000-0000-0000-0000-000000000001',
                 'subject:high-water-first', 'first@example.test',
                 'High Water First', 1, 1, '2026-08-11T23:00:00Z',
                 '2026-08-11T23:00:00Z', '{{TenantId}}'),
                ('81000000-0000-0000-0000-000000000002', 2,
                 '82000000-0000-0000-0000-000000000002',
                 'subject:high-water-second', 'second@example.test',
                 'High Water Second', 1, 1, '2026-08-11T23:00:00Z',
                 '2026-08-11T23:00:00Z', '{{TenantId}}'),
                ('81000000-0000-0000-0000-000000000003', 1,
                 '82000000-0000-0000-0000-000000000003',
                 'subject:backlog-present', 'backlog@example.test',
                 'Backlog Present', 1, 1, '2026-08-11T23:00:00Z',
                 '2026-08-11T23:00:00Z', '{{OtherTenantId}}')
            """).ConfigureAwait(false);

        long tenantMaximum = await ExecuteScalarAsync<long>(
            connectionString,
            $$"""
            SELECT max("IdentityAnchorSweepOrdinal")
            FROM workspaces.staff_onboarding_applications
            WHERE "ScopeId" = '{{TenantId}}'
            """).ConfigureAwait(false);
        long otherTenantMaximum = await ExecuteScalarAsync<long>(
            connectionString,
            $$"""
            SELECT max("IdentityAnchorSweepOrdinal")
            FROM workspaces.staff_onboarding_applications
            WHERE "ScopeId" = '{{OtherTenantId}}'
            """).ConfigureAwait(false);

        PostgresException forgedInitialUpper =
            await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteNonQueryAsync(
                    connectionString,
                    $$"""
                    INSERT INTO workspaces.
                        workspace_staff_identity_anchor_sweep_checkpoints (
                        "Id", "ProtocolVersion", "CycleId",
                        "CycleUpperOrdinal", "AfterOrdinal",
                        "CycleStartedAtUtc", "CycleScannedCount",
                        "CycleNoAnchorCount", "CycleRemovedCount",
                        "CycleObservedCount", "CycleAlreadyObservedCount",
                        "CycleDeferredCount", "CycleConflictCount",
                        "CyclePassOneCommittedCount",
                        "CycleResolutionRecordConfirmedCount",
                        "LastCompletedScannedCount",
                        "LastCompletedNoAnchorCount",
                        "LastCompletedRemovedCount",
                        "LastCompletedObservedCount",
                        "LastCompletedAlreadyObservedCount",
                        "LastCompletedDeferredCount",
                        "LastCompletedConflictCount",
                        "LastCompletedPassOneCommittedCount",
                        "LastCompletedResolutionRecordConfirmedCount",
                        "LastRunId", "UpdatedAtUtc", "Version", "ScopeId")
                    VALUES (
                        '83000000-0000-0000-0000-000000000001', 1,
                        '84000000-0000-0000-0000-000000000001',
                        {{tenantMaximum + 1}}, NULL,
                        '2026-08-11T23:01:00Z', 0, 0, 0, 0, 0, 0, 0, 0, 0,
                        0, 0, 0, 0, 0, 0, 0, 0, 0,
                        '85000000-0000-0000-0000-000000000001',
                        '2026-08-11T23:01:00Z', 2, '{{TenantId}}')
                    """)).ConfigureAwait(false);
        Assert.Equal("P0001", forgedInitialUpper.SqlState);

        await ExecuteNonQueryAsync(
            connectionString,
            $$"""
            INSERT INTO workspaces.
                workspace_staff_identity_anchor_sweep_checkpoints (
                "Id", "ProtocolVersion", "CycleId", "CycleUpperOrdinal",
                "AfterOrdinal", "CycleStartedAtUtc", "CycleScannedCount",
                "CycleNoAnchorCount", "CycleRemovedCount",
                "CycleObservedCount", "CycleAlreadyObservedCount",
                "CycleDeferredCount", "CycleConflictCount",
                "CyclePassOneCommittedCount",
                "CycleResolutionRecordConfirmedCount",
                "LastCompletedScannedCount", "LastCompletedNoAnchorCount",
                "LastCompletedRemovedCount", "LastCompletedObservedCount",
                "LastCompletedAlreadyObservedCount",
                "LastCompletedDeferredCount", "LastCompletedConflictCount",
                "LastCompletedPassOneCommittedCount",
                "LastCompletedResolutionRecordConfirmedCount",
                "LastRunId", "UpdatedAtUtc", "Version", "ScopeId")
            VALUES (
                '83000000-0000-0000-0000-000000000001', 1,
                '84000000-0000-0000-0000-000000000001', {{tenantMaximum}},
                NULL, '2026-08-11T23:01:00Z', 0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0,
                '85000000-0000-0000-0000-000000000001',
                '2026-08-11T23:01:00Z', 2, '{{TenantId}}')
            """).ConfigureAwait(false);
        Assert.Equal(
            tenantMaximum,
            await ExecuteScalarAsync<long>(
                connectionString,
                $$"""
                SELECT "CycleUpperOrdinal"
                FROM workspaces.
                    workspace_staff_identity_anchor_sweep_checkpoints
                WHERE "ScopeId" = '{{TenantId}}'
                """).ConfigureAwait(false));

        await ExecuteNonQueryAsync(
            connectionString,
            $$"""
            INSERT INTO workspaces.
                workspace_staff_identity_anchor_sweep_checkpoints (
                "Id", "ProtocolVersion", "CycleScannedCount",
                "CycleNoAnchorCount", "CycleRemovedCount",
                "CycleObservedCount", "CycleAlreadyObservedCount",
                "CycleDeferredCount", "CycleConflictCount",
                "CyclePassOneCommittedCount",
                "CycleResolutionRecordConfirmedCount",
                "LastCompletedScannedCount", "LastCompletedNoAnchorCount",
                "LastCompletedRemovedCount", "LastCompletedObservedCount",
                "LastCompletedAlreadyObservedCount",
                "LastCompletedDeferredCount", "LastCompletedConflictCount",
                "LastCompletedPassOneCommittedCount",
                "LastCompletedResolutionRecordConfirmedCount",
                "UpdatedAtUtc", "Version", "ScopeId")
            VALUES
                ('83000000-0000-0000-0000-000000000002', 1,
                 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                 '2026-08-11T23:00:00Z', 1, '{{OtherTenantId}}'),
                ('83000000-0000-0000-0000-000000000003', 1,
                 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                 '2026-08-11T23:00:00Z', 1, '{{emptyTenantId}}')
            """).ConfigureAwait(false);

        const string blockedEmptyCycleId =
            "86000000-0000-0000-0000-000000000001";
        const string blockedEmptyRunId =
            "87000000-0000-0000-0000-000000000001";
        string blockedEmptyDigest = Sha256(
            $"empty|{blockedEmptyCycleId}|{blockedEmptyRunId}");
        PostgresException emptyWithBacklog =
            await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteNonQueryAsync(
                    connectionString,
                    $$"""
                    UPDATE workspaces.
                        workspace_staff_identity_anchor_sweep_checkpoints
                    SET "LastCompletedCycleId" = '{{blockedEmptyCycleId}}',
                        "LastCompletedAtUtc" = '2026-08-11T23:02:00Z',
                        "LastAdvanceId" =
                            '88000000-0000-0000-0000-000000000001',
                        "LastAdvanceSha256" = '{{blockedEmptyDigest}}',
                        "LastRunId" = '{{blockedEmptyRunId}}',
                        "UpdatedAtUtc" = '2026-08-11T23:02:00Z',
                        "Version" = 2
                    WHERE "Id" =
                        '83000000-0000-0000-0000-000000000002'
                    """)).ConfigureAwait(false);
        Assert.Equal("P0001", emptyWithBacklog.SqlState);

        PostgresException forgedBeginUpper =
            await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteNonQueryAsync(
                    connectionString,
                    $$"""
                    UPDATE workspaces.
                        workspace_staff_identity_anchor_sweep_checkpoints
                    SET "CycleId" =
                            '86000000-0000-0000-0000-000000000002',
                        "CycleUpperOrdinal" = {{otherTenantMaximum + 1}},
                        "CycleStartedAtUtc" = '2026-08-11T23:02:00Z',
                        "LastRunId" =
                            '87000000-0000-0000-0000-000000000002',
                        "UpdatedAtUtc" = '2026-08-11T23:02:00Z',
                        "Version" = 2
                    WHERE "Id" =
                        '83000000-0000-0000-0000-000000000002'
                    """)).ConfigureAwait(false);
        Assert.Equal("P0001", forgedBeginUpper.SqlState);

        await ExecuteNonQueryAsync(
            connectionString,
            $$"""
            UPDATE workspaces.
                workspace_staff_identity_anchor_sweep_checkpoints
            SET "CycleId" = '86000000-0000-0000-0000-000000000002',
                "CycleUpperOrdinal" = {{otherTenantMaximum}},
                "CycleStartedAtUtc" = '2026-08-11T23:02:00Z',
                "LastRunId" = '87000000-0000-0000-0000-000000000002',
                "UpdatedAtUtc" = '2026-08-11T23:02:00Z',
                "Version" = 2
            WHERE "Id" = '83000000-0000-0000-0000-000000000002'
            """).ConfigureAwait(false);

        const string trueEmptyCycleId =
            "86000000-0000-0000-0000-000000000003";
        const string trueEmptyRunId =
            "87000000-0000-0000-0000-000000000003";
        string trueEmptyDigest = Sha256(
            $"empty|{trueEmptyCycleId}|{trueEmptyRunId}");
        await ExecuteNonQueryAsync(
            connectionString,
            $$"""
            UPDATE workspaces.
                workspace_staff_identity_anchor_sweep_checkpoints
            SET "LastCompletedCycleId" = '{{trueEmptyCycleId}}',
                "LastCompletedAtUtc" = '2026-08-11T23:02:00Z',
                "LastAdvanceId" =
                    '88000000-0000-0000-0000-000000000003',
                "LastAdvanceSha256" = '{{trueEmptyDigest}}',
                "LastRunId" = '{{trueEmptyRunId}}',
                "UpdatedAtUtc" = '2026-08-11T23:02:00Z',
                "Version" = 2
            WHERE "Id" = '83000000-0000-0000-0000-000000000003'
            """).ConfigureAwait(false);
        Assert.Equal(
            trueEmptyDigest,
            await ExecuteScalarAsync<string>(
                connectionString,
                $$"""
                SELECT "LastAdvanceSha256"
                FROM workspaces.
                    workspace_staff_identity_anchor_sweep_checkpoints
                WHERE "ScopeId" = '{{emptyTenantId}}'
                """).ConfigureAwait(false));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task
        Destruction_deletes_require_exact_operation_scope_and_attempted_stage()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_workspaces_anchor_destroy_guard_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);
        string connectionString = postgreSql.GetConnectionString();
        DateTimeOffset createdAtUtc = new(
            2026,
            8,
            11,
            21,
            0,
            0,
            TimeSpan.Zero);
        Guid receiptId =
            Guid.Parse("51000000-0000-0000-0000-000000000002");

        await using (WorkspacesDbContext context = CreateDbContext(
                         connectionString,
                         TenantId))
        {
            await context.Database.GetService<IMigrator>()
                .MigrateAsync(CurrentMigration)
                .ConfigureAwait(false);
            WorkspaceStaffOnboarding ordinary =
                WorkspaceStaffOnboarding.Create(
                    Guid.Parse("51000000-0000-0000-0000-000000000001"),
                    TenantId,
                    WorkspaceStaffOnboardingSource.Invitation,
                    Guid.Parse("52000000-0000-0000-0000-000000000001"),
                    "subject:destroy-ordinary",
                    "ordinary@example.test",
                    "Ordinary Applicant",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    createdAtUtc).Value;
            WorkspaceStaffOnboarding historical =
                WorkspaceStaffOnboarding.Create(
                    Guid.Parse("51000000-0000-0000-0000-000000000002"),
                    TenantId,
                    WorkspaceStaffOnboardingSource.EnrollmentLink,
                    Guid.Parse("52000000-0000-0000-0000-000000000002"),
                    "subject:destroy-historical",
                    "historical@example.test",
                    "Historical Applicant",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    createdAtUtc).Value;
            context.StaffOnboardingApplications.AddRange(ordinary, historical);
            await context.SaveChangesAsync().ConfigureAwait(false);
            Assert.True(historical.ReviewHistoricalNoProvision(
                receiptId,
                createdAtUtc.AddMinutes(1)).IsSuccess);
            context.StaffHistoricalNoProvisionReceipts.Add(
                WorkspaceStaffHistoricalNoProvisionReceipt.Create(
                    receiptId,
                    TenantId,
                    Guid.Parse("53000000-0000-0000-0000-000000000001"),
                    historical.Id,
                    historical.SourceKind,
                    historical.SourceId,
                    1,
                    WorkspaceStaffOnboardingState.Submitted,
                    historical.Version,
                    historical.Status,
                    7,
                    2,
                    WorkspaceStaffHistoricalNoProvisionAuthorityStatus
                        .EnrollmentLinkDisabled,
                    new string('a', 64),
                    Guid.Parse("54000000-0000-0000-0000-000000000001"),
                    new string('b', 64),
                    "destroy-reviewer",
                    createdAtUtc.AddMinutes(1)).Value);
            await context.SaveChangesAsync().ConfigureAwait(false);
        }

        await using (WorkspacesDbContext sentinel = CreateDbContext(
                         connectionString,
                         OtherTenantId))
        {
            sentinel.StaffOnboardingApplications.Add(
                WorkspaceStaffOnboarding.Create(
                    Guid.Parse("51000000-0000-0000-0000-000000000003"),
                    OtherTenantId,
                    WorkspaceStaffOnboardingSource.Invitation,
                    Guid.Parse("52000000-0000-0000-0000-000000000003"),
                    "subject:cross-tenant-sentinel",
                    "sentinel@example.test",
                    "Sentinel Applicant",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    createdAtUtc).Value);
            await sentinel.SaveChangesAsync().ConfigureAwait(false);
        }

        await ExecuteNonQueryAsync(
            connectionString,
            $$"""
            INSERT INTO
                workspaces.workspace_staff_identity_anchor_sweep_checkpoints (
                "Id", "ProtocolVersion", "CycleScannedCount",
                "CycleNoAnchorCount", "CycleRemovedCount",
                "CycleObservedCount", "CycleAlreadyObservedCount",
                "CycleDeferredCount", "CycleConflictCount",
                "CyclePassOneCommittedCount",
                "CycleResolutionRecordConfirmedCount",
                "LastCompletedScannedCount", "LastCompletedNoAnchorCount",
                "LastCompletedRemovedCount", "LastCompletedObservedCount",
                "LastCompletedAlreadyObservedCount",
                "LastCompletedDeferredCount", "LastCompletedConflictCount",
                "LastCompletedPassOneCommittedCount",
                "LastCompletedResolutionRecordConfirmedCount",
                "UpdatedAtUtc", "Version", "ScopeId")
            VALUES (
                '55000000-0000-0000-0000-000000000001', 1,
                0, 0, 0, 0, 0, 0, 0, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0,
                '2026-08-11T21:02:00Z', 1, '{{TenantId}}');

            INSERT INTO workspaces.workspace_termination_fences (
                "Id", "ProcessId", "CaseId", "ApprovalRevision",
                "TerminationEpoch", "PolicyEvidenceSha256", "State",
                "CreatedBy", "CreatedAtUtc", "LastChangedBy",
                "LastChangedAtUtc", "Version", "ScopeId")
            VALUES (
                '56000000-0000-0000-0000-000000000001',
                '57000000-0000-0000-0000-000000000001',
                '58000000-0000-0000-0000-000000000001', 1,
                '59000000-0000-0000-0000-000000000001', repeat('c', 64), 2,
                'migration-test', '2026-08-11T21:03:00Z', 'migration-test',
                '2026-08-11T21:04:00Z', 2, '{{TenantId}}');

            INSERT INTO workspaces.tenant_destroy_operations (
                "OperationId", "ScopeId", "RequestSha256", "FenceId",
                "SelectedFenceVersion", "ResultingFenceVersion", "BatchSize",
                "Stage", "RemovedRecordCount", "CompletedBatchCount",
                "ProofVersion", "RemovalProofSha256", "StartedAtUtc",
                "UpdatedAtUtc", "ConcurrencyVersion")
            VALUES (
                '5a000000-0000-0000-0000-000000000001', '{{TenantId}}',
                repeat('d', 64),
                '56000000-0000-0000-0000-000000000001', 2, 4, 500, 13, 0,
                0, 1, repeat('e', 64), '2026-08-11T21:05:00Z',
                '2026-08-11T21:05:00Z', 1);
            """).ConfigureAwait(false);

        PostgresException noApplicationAdmission =
            await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteNonQueryAsync(
                    connectionString,
                    $$"""
                    DELETE FROM workspaces.staff_onboarding_applications
                    WHERE "ScopeId" = '{{TenantId}}'
                    """)).ConfigureAwait(false);
        Assert.Equal("P0001", noApplicationAdmission.SqlState);

        PostgresException missingAttemptedStage =
            await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteDestroyDeleteAsync(
                    connectionString,
                    "5a000000-0000-0000-0000-000000000001",
                    attemptedStage: null,
                    $$"""
                    DELETE FROM workspaces.staff_onboarding_applications
                    WHERE "ScopeId" = '{{TenantId}}'
                    """)).ConfigureAwait(false);
        Assert.Equal("P0001", missingAttemptedStage.SqlState);

        PostgresException wrongApplicationStage =
            await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteDestroyDeleteAsync(
                    connectionString,
                    "5a000000-0000-0000-0000-000000000001",
                    attemptedStage: 21,
                    $$"""
                    DELETE FROM workspaces.staff_onboarding_applications
                    WHERE "ScopeId" = '{{TenantId}}'
                    """)).ConfigureAwait(false);
        Assert.Equal("P0001", wrongApplicationStage.SqlState);

        Assert.Equal(
            2,
            await ExecuteDestroyDeleteAsync(
                connectionString,
                "5a000000-0000-0000-0000-000000000001",
                attemptedStage: 13,
                $$"""
                DELETE FROM workspaces.staff_onboarding_applications
                WHERE "ScopeId" = '{{TenantId}}'
                """).ConfigureAwait(false));
        Assert.Equal(
            1,
            await ExecuteScalarAsync<int>(
                connectionString,
                $$"""
                SELECT COUNT(*)::integer
                FROM workspaces.staff_onboarding_applications
                WHERE "ScopeId" = '{{OtherTenantId}}'
                """).ConfigureAwait(false));

        await ExecuteNonQueryAsync(
            connectionString,
            """
            UPDATE workspaces.tenant_destroy_operations
            SET "Stage" = 22
            WHERE "OperationId" =
                '5a000000-0000-0000-0000-000000000001'
            """).ConfigureAwait(false);
        Assert.Equal(
            1,
            await ExecuteDestroyDeleteAsync(
                connectionString,
                "5a000000-0000-0000-0000-000000000001",
                attemptedStage: 22,
                """
                DELETE FROM
                    workspaces.workspace_staff_identity_anchor_sweep_checkpoints
                WHERE "Id" =
                    '55000000-0000-0000-0000-000000000001'
                """).ConfigureAwait(false));

        await ExecuteNonQueryAsync(
            connectionString,
            """
            UPDATE workspaces.tenant_destroy_operations
            SET "Stage" = 23
            WHERE "OperationId" =
                '5a000000-0000-0000-0000-000000000001'
            """).ConfigureAwait(false);
        PostgresException wrongReceiptOperation =
            await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteDestroyDeleteAsync(
                    connectionString,
                    "5a000000-0000-0000-0000-000000000099",
                    attemptedStage: 23,
                    """
                    DELETE FROM
                        workspaces.staff_historical_no_provision_receipts
                    WHERE "Id" =
                        '51000000-0000-0000-0000-000000000002'
                    """)).ConfigureAwait(false);
        Assert.Equal("P0001", wrongReceiptOperation.SqlState);
        Assert.Equal(
            1,
            await ExecuteDestroyDeleteAsync(
                connectionString,
                "5a000000-0000-0000-0000-000000000001",
                attemptedStage: 23,
                """
                DELETE FROM
                    workspaces.staff_historical_no_provision_receipts
                WHERE "Id" =
                    '51000000-0000-0000-0000-000000000002'
                """).ConfigureAwait(false));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task
        Canonical_receipt_round_trips_microsecond_time_and_utf16_reviewer()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_workspaces_anchor_receipt_migration_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);

        DateTimeOffset createdAtUtc = new(
            2026,
            8,
            11,
            20,
            30,
            0,
            TimeSpan.Zero);
        DateTimeOffset reviewedAtUtc = createdAtUtc
            .AddMinutes(5)
            .AddTicks(9);
        Guid applicationId =
            Guid.Parse("11000000-0000-0000-0000-000000000001");
        Guid sourceId =
            Guid.Parse("12000000-0000-0000-0000-000000000001");
        Guid receiptId =
            Guid.Parse("13000000-0000-0000-0000-000000000001");
        Guid operationId =
            Guid.Parse("14000000-0000-0000-0000-000000000001");
        Guid manifestId =
            Guid.Parse("15000000-0000-0000-0000-000000000001");
        const string reviewerId = "reviewer-🧭";

        await using WorkspacesDbContext context = CreateDbContext(
            postgreSql.GetConnectionString(),
            TenantId);
        await context.Database.GetService<IMigrator>()
            .MigrateAsync(CurrentMigration)
            .ConfigureAwait(false);

        WorkspaceStaffOnboarding application =
            WorkspaceStaffOnboarding.Create(
                applicationId,
                TenantId,
                WorkspaceStaffOnboardingSource.Invitation,
                sourceId,
                "subject:historical-no-provision",
                "historical@example.test",
                "Historical Applicant",
                "Historical Legal Name",
                "work@example.test",
                "+1-555-0100",
                "EMP-001",
                "Operator",
                "Operations",
                createdAtUtc).Value;
        context.StaffOnboardingApplications.Add(application);
        await context.SaveChangesAsync().ConfigureAwait(false);

        Assert.True(application.ReviewHistoricalNoProvision(
            receiptId,
            reviewedAtUtc).IsSuccess);
        WorkspaceStaffHistoricalNoProvisionReceipt receipt =
            WorkspaceStaffHistoricalNoProvisionReceipt.Create(
                receiptId,
                TenantId,
                operationId,
                applicationId,
                WorkspaceStaffOnboardingSource.Invitation,
                sourceId,
                expectedApplicationVersion: 1,
                WorkspaceStaffOnboardingState.Submitted,
                application.Version,
                application.Status,
                organizationsScopeRevision: 17,
                organizationsSourceVersion: 4,
                WorkspaceStaffHistoricalNoProvisionAuthorityStatus
                    .InvitationRevoked,
                new string('a', 64),
                manifestId,
                new string('b', 64),
                reviewerId,
                reviewedAtUtc).Value;
        context.StaffHistoricalNoProvisionReceipts.Add(receipt);
        await context.SaveChangesAsync().ConfigureAwait(false);

        context.ChangeTracker.Clear();
        WorkspaceStaffHistoricalNoProvisionReceipt persisted =
            await context.StaffHistoricalNoProvisionReceipts
                .SingleAsync(item => item.Id == receiptId)
                .ConfigureAwait(false);
        WorkspaceStaffOnboarding persistedApplication =
            await context.StaffOnboardingApplications
                .SingleAsync(item => item.Id == applicationId)
                .ConfigureAwait(false);

        Assert.True(persisted.HasValidCanonicalProof());
        Assert.True(persisted.MatchesResult(persistedApplication));
        Assert.Equal(reviewerId, persisted.ReviewerId);
        Assert.Equal(
            CanonicalizePostgreSqlTimestamp(reviewedAtUtc),
            persisted.ReviewedAtUtc);
        Assert.Equal(
            WorkspaceStaffHistoricalNoProvisionReceipt
                .CreateSubjectPseudonym(receiptId),
            persistedApplication.SubjectId);

        PostgresException divergence =
            await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteNonQueryAsync(
                    postgreSql.GetConnectionString(),
                    """
                    UPDATE workspaces.staff_onboarding_applications
                    SET "SubjectId" = 'subject:resurrected'
                    WHERE "Id" =
                        '11000000-0000-0000-0000-000000000001'
                    """)).ConfigureAwait(false);
        Assert.Equal("P0001", divergence.SqlState);

        PostgresException receiptUpdate =
            await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteNonQueryAsync(
                    postgreSql.GetConnectionString(),
                    """
                    UPDATE workspaces.staff_historical_no_provision_receipts
                    SET "CanonicalSha256" = repeat('c', 64)
                    WHERE "Id" =
                        '13000000-0000-0000-0000-000000000001'
                    """)).ConfigureAwait(false);
        Assert.Equal("P0001", receiptUpdate.SqlState);

        PostgresException receiptDelete =
            await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteNonQueryAsync(
                    postgreSql.GetConnectionString(),
                    """
                    DELETE FROM
                        workspaces.staff_historical_no_provision_receipts
                    WHERE "Id" =
                        '13000000-0000-0000-0000-000000000001'
                    """)).ConfigureAwait(false);
        Assert.Equal("P0001", receiptDelete.SqlState);

        context.ChangeTracker.Clear();
        Guid corruptApplicationId =
            Guid.Parse("16000000-0000-0000-0000-000000000001");
        Guid corruptSourceId =
            Guid.Parse("17000000-0000-0000-0000-000000000001");
        Guid corruptReceiptId =
            Guid.Parse("18000000-0000-0000-0000-000000000001");
        DateTimeOffset corruptReviewedAtUtc =
            createdAtUtc.AddMinutes(10);
        WorkspaceStaffOnboarding corruptApplication =
            WorkspaceStaffOnboarding.Create(
                corruptApplicationId,
                TenantId,
                WorkspaceStaffOnboardingSource.EnrollmentLink,
                corruptSourceId,
                "subject:corrupt-canonical-candidate",
                "corrupt@example.test",
                "Corrupt Candidate",
                null,
                null,
                null,
                null,
                null,
                null,
                createdAtUtc).Value;
        context.StaffOnboardingApplications.Add(corruptApplication);
        await context.SaveChangesAsync().ConfigureAwait(false);
        Assert.True(corruptApplication.ReviewHistoricalNoProvision(
            corruptReceiptId,
            corruptReviewedAtUtc).IsSuccess);
        await context.SaveChangesAsync().ConfigureAwait(false);

        PostgresException corruptCanonical =
            await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteNonQueryAsync(
                    postgreSql.GetConnectionString(),
                    $$"""
                    INSERT INTO
                        workspaces.staff_historical_no_provision_receipts (
                        "Id", "ContractVersion", "OperationId",
                        "ApplicationId", "SourceKind", "SourceId",
                        "ExpectedApplicationVersion",
                        "ExpectedApplicationStatus", "ResultApplicationVersion",
                        "ResultApplicationStatus", "OrganizationsScopeRevision",
                        "OrganizationsSourceVersion", "OrganizationsSourceStatus",
                        "StaffEvidenceSha256", "ExternalEvidenceManifestId",
                        "ExternalEvidenceSha256", "ReviewerId", "ReviewedAtUtc",
                        "CanonicalSha256", "ScopeId")
                    VALUES (
                        '18000000-0000-0000-0000-000000000001', 1,
                        '19000000-0000-0000-0000-000000000001',
                        '16000000-0000-0000-0000-000000000001', 2,
                        '17000000-0000-0000-0000-000000000001', 1, 1, 2, 8,
                        19, 2, 7, repeat('a', 64),
                        '1a000000-0000-0000-0000-000000000001',
                        repeat('b', 64), 'reviewer-corrupt',
                        '2026-08-11T20:40:00Z', repeat('c', 64),
                        '{{TenantId}}')
                    """)).ConfigureAwait(false);
        Assert.Equal("P0001", corruptCanonical.SqlState);
        Assert.Equal(
            0,
            await ExecuteScalarAsync<int>(
                postgreSql.GetConnectionString(),
                """
                SELECT COUNT(*)::integer
                FROM workspaces.staff_historical_no_provision_receipts
                WHERE "Id" =
                    '18000000-0000-0000-0000-000000000001'
                """).ConfigureAwait(false));
        Assert.Equal(
            1,
            await ExecuteScalarAsync<int>(
                postgreSql.GetConnectionString(),
                """
                SELECT COUNT(*)::integer
                FROM workspaces.staff_onboarding_applications
                WHERE "Id" =
                    '16000000-0000-0000-0000-000000000001'
                """).ConfigureAwait(false));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task
        Receipt_and_application_races_serialize_to_one_consistent_committed_side()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_workspaces_anchor_receipt_race_tests")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);
        string connectionString = postgreSql.GetConnectionString();
        DateTimeOffset createdAtUtc = new(
            2026,
            8,
            11,
            22,
            0,
            0,
            TimeSpan.Zero);
        Guid firstApplicationId =
            Guid.Parse("71000000-0000-0000-0000-000000000001");
        Guid firstReceiptId =
            Guid.Parse("72000000-0000-0000-0000-000000000001");
        Guid secondApplicationId =
            Guid.Parse("71000000-0000-0000-0000-000000000002");
        Guid secondReceiptId =
            Guid.Parse("72000000-0000-0000-0000-000000000002");
        WorkspaceStaffHistoricalNoProvisionReceipt firstReceipt;
        WorkspaceStaffHistoricalNoProvisionReceipt secondReceipt;

        await using (WorkspacesDbContext context = CreateDbContext(
                         connectionString,
                         TenantId))
        {
            await context.Database.GetService<IMigrator>()
                .MigrateAsync(CurrentMigration)
                .ConfigureAwait(false);
            WorkspaceStaffOnboarding first =
                WorkspaceStaffOnboarding.Create(
                    firstApplicationId,
                    TenantId,
                    WorkspaceStaffOnboardingSource.Invitation,
                    Guid.Parse("73000000-0000-0000-0000-000000000001"),
                    "subject:receipt-race-first",
                    "first@example.test",
                    "First Race",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    createdAtUtc).Value;
            WorkspaceStaffOnboarding second =
                WorkspaceStaffOnboarding.Create(
                    secondApplicationId,
                    TenantId,
                    WorkspaceStaffOnboardingSource.EnrollmentLink,
                    Guid.Parse("73000000-0000-0000-0000-000000000002"),
                    "subject:receipt-race-second",
                    "second@example.test",
                    "Second Race",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    createdAtUtc).Value;
            context.StaffOnboardingApplications.AddRange(first, second);
            await context.SaveChangesAsync().ConfigureAwait(false);
            Assert.True(first.ReviewHistoricalNoProvision(
                firstReceiptId,
                createdAtUtc.AddMinutes(1)).IsSuccess);
            Assert.True(second.ReviewHistoricalNoProvision(
                secondReceiptId,
                createdAtUtc.AddMinutes(2)).IsSuccess);
            await context.SaveChangesAsync().ConfigureAwait(false);

            firstReceipt =
                WorkspaceStaffHistoricalNoProvisionReceipt.Create(
                    firstReceiptId,
                    TenantId,
                    Guid.Parse("74000000-0000-0000-0000-000000000001"),
                    first.Id,
                    first.SourceKind,
                    first.SourceId,
                    1,
                    WorkspaceStaffOnboardingState.Submitted,
                    first.Version,
                    first.Status,
                    31,
                    4,
                    WorkspaceStaffHistoricalNoProvisionAuthorityStatus
                        .InvitationRevoked,
                    new string('a', 64),
                    Guid.Parse("75000000-0000-0000-0000-000000000001"),
                    new string('b', 64),
                    "race-reviewer-first",
                    createdAtUtc.AddMinutes(1)).Value;
            secondReceipt =
                WorkspaceStaffHistoricalNoProvisionReceipt.Create(
                    secondReceiptId,
                    TenantId,
                    Guid.Parse("74000000-0000-0000-0000-000000000002"),
                    second.Id,
                    second.SourceKind,
                    second.SourceId,
                    1,
                    WorkspaceStaffOnboardingState.Submitted,
                    second.Version,
                    second.Status,
                    32,
                    5,
                    WorkspaceStaffHistoricalNoProvisionAuthorityStatus
                        .EnrollmentLinkDisabled,
                    new string('c', 64),
                    Guid.Parse("75000000-0000-0000-0000-000000000002"),
                    new string('d', 64),
                    "race-reviewer-second",
                    createdAtUtc.AddMinutes(2)).Value;
        }

        await using (NpgsqlConnection applicationWriter =
                         new(connectionString))
        await using (NpgsqlConnection receiptWriter = new(connectionString))
        {
            await applicationWriter.OpenAsync().ConfigureAwait(false);
            await receiptWriter.OpenAsync().ConfigureAwait(false);
            await using NpgsqlTransaction applicationTransaction =
                await applicationWriter.BeginTransactionAsync()
                    .ConfigureAwait(false);
            await using NpgsqlTransaction receiptTransaction =
                await receiptWriter.BeginTransactionAsync()
                    .ConfigureAwait(false);
            await ExecuteNonQueryAsync(
                applicationWriter,
                applicationTransaction,
                """
                UPDATE workspaces.staff_onboarding_applications
                SET "SubjectId" = 'subject:divergent-first'
                WHERE "Id" =
                    '71000000-0000-0000-0000-000000000001'
                """).ConfigureAwait(false);
            await InsertHistoricalReceiptAsync(
                receiptWriter,
                receiptTransaction,
                firstReceipt).ConfigureAwait(false);

            Task receiptCommit = receiptTransaction.CommitAsync();
            Assert.True(await WaitForBackendLockAsync(
                connectionString,
                receiptWriter.ProcessID,
                receiptCommit).ConfigureAwait(false));
            await applicationTransaction.CommitAsync().ConfigureAwait(false);
            PostgresException rejectedReceipt =
                await Assert.ThrowsAsync<PostgresException>(async () =>
                    await receiptCommit.ConfigureAwait(false))
                    .ConfigureAwait(false);
            Assert.Equal("P0001", rejectedReceipt.SqlState);
        }

        await using (NpgsqlConnection receiptFirst = new(connectionString))
        await using (NpgsqlConnection applicationSecond =
                         new(connectionString))
        {
            await receiptFirst.OpenAsync().ConfigureAwait(false);
            await applicationSecond.OpenAsync().ConfigureAwait(false);
            await using NpgsqlTransaction receiptTransaction =
                await receiptFirst.BeginTransactionAsync()
                    .ConfigureAwait(false);
            await using NpgsqlTransaction applicationTransaction =
                await applicationSecond.BeginTransactionAsync()
                    .ConfigureAwait(false);
            await InsertHistoricalReceiptAsync(
                receiptFirst,
                receiptTransaction,
                secondReceipt).ConfigureAwait(false);
            await ExecuteNonQueryAsync(
                receiptFirst,
                receiptTransaction,
                """
                SET CONSTRAINTS
                    workspaces.
                    "TR_staff_historical_no_provision_receipt_integrity"
                    IMMEDIATE
                """).ConfigureAwait(false);

            Task<int> applicationUpdate = ExecuteNonQueryAsync(
                applicationSecond,
                applicationTransaction,
                """
                UPDATE workspaces.staff_onboarding_applications
                SET "SubjectId" = 'subject:divergent-second'
                WHERE "Id" =
                    '71000000-0000-0000-0000-000000000002'
                """);
            Assert.True(await WaitForBackendLockAsync(
                connectionString,
                applicationSecond.ProcessID,
                applicationUpdate).ConfigureAwait(false));

            await receiptTransaction.CommitAsync().ConfigureAwait(false);
            Assert.Equal(
                1,
                await applicationUpdate.ConfigureAwait(false));
            PostgresException rejectedApplication =
                await Assert.ThrowsAsync<PostgresException>(() =>
                    applicationTransaction.CommitAsync()).ConfigureAwait(false);
            Assert.Equal("P0001", rejectedApplication.SqlState);
        }

        Assert.Equal(
            1,
            await ExecuteScalarAsync<int>(
                connectionString,
                """
                SELECT COUNT(*)::integer
                FROM workspaces.staff_historical_no_provision_receipts
                WHERE "Id" IN (
                    '72000000-0000-0000-0000-000000000001',
                    '72000000-0000-0000-0000-000000000002')
                """).ConfigureAwait(false));
        Assert.Equal(
            "subject:divergent-first",
            await ExecuteScalarAsync<string>(
                connectionString,
                """
                SELECT "SubjectId"
                FROM workspaces.staff_onboarding_applications
                WHERE "Id" =
                    '71000000-0000-0000-0000-000000000001'
                """).ConfigureAwait(false));
        Assert.Equal(
            WorkspaceStaffHistoricalNoProvisionReceipt
                .CreateSubjectPseudonym(secondReceiptId),
            await ExecuteScalarAsync<string>(
                connectionString,
                """
                SELECT "SubjectId"
                FROM workspaces.staff_onboarding_applications
                WHERE "Id" =
                    '71000000-0000-0000-0000-000000000002'
                """).ConfigureAwait(false));
    }

    private static WorkspacesDbContext CreateDbContext(
        string connectionString,
        string scopeId)
    {
        DbContextOptions<WorkspacesDbContext> options =
            new DbContextOptionsBuilder<WorkspacesDbContext>()
                .UseNpgsql(
                    connectionString,
                    provider => provider
                        .MigrationsAssembly(
                            WorkspacesMigrations.PostgreSqlAssembly)
                        .MigrationsHistoryTable(
                            WorkspacesMigrations.HistoryTable,
                            WorkspacesMigrations.Schema))
                .Options;
        return new(options, new TestScopeContext(scopeId));
    }

    private static DateTimeOffset CanonicalizePostgreSqlTimestamp(
        DateTimeOffset value)
    {
        DateTimeOffset utc = value.ToUniversalTime();
        const long ticksPerMicrosecond =
            TimeSpan.TicksPerMillisecond / 1000;
        return new(
            utc.Ticks - (utc.Ticks % ticksPerMicrosecond),
            TimeSpan.Zero);
    }

    private static async Task<int> ExecuteNonQueryAsync(
        string connectionString,
        string sql)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using NpgsqlCommand command = new(sql, connection);
        return await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<int> ExecuteNonQueryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql)
    {
        await using NpgsqlCommand command = new(sql, connection, transaction);
        return await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<T> ExecuteScalarAsync<T>(
        string connectionString,
        string sql)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using NpgsqlCommand command = new(sql, connection);
        object? value = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return Assert.IsType<T>(value);
    }

    private static async Task<int> ExecuteDestroyDeleteAsync(
        string connectionString,
        string operationId,
        int? attemptedStage,
        string deleteSql)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync().ConfigureAwait(false);
        await using (NpgsqlCommand operationSetting = new(
                         """
                         SELECT set_config(
                             'bunkfy.workspaces_tenant_destroy_operation_id',
                             @operationId,
                             true)
                         """,
                         connection,
                         transaction))
        {
            operationSetting.Parameters.AddWithValue(
                "operationId",
                operationId);
            _ = await operationSetting.ExecuteScalarAsync()
                .ConfigureAwait(false);
        }

        if (attemptedStage.HasValue)
        {
            await using NpgsqlCommand stageSetting = new(
                """
                SELECT set_config(
                    'bunkfy.workspaces_tenant_destroy_attempted_stage',
                    @attemptedStage,
                    true)
                """,
                connection,
                transaction);
            stageSetting.Parameters.AddWithValue(
                "attemptedStage",
                attemptedStage.Value.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            _ = await stageSetting.ExecuteScalarAsync().ConfigureAwait(false);
        }

        await using NpgsqlCommand delete = new(
            deleteSql,
            connection,
            transaction);
        int affected = await delete.ExecuteNonQueryAsync().ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
        return affected;
    }

    private static async Task InsertHistoricalReceiptAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorkspaceStaffHistoricalNoProvisionReceipt receipt)
    {
        await using NpgsqlCommand command = new(
            """
            INSERT INTO workspaces.staff_historical_no_provision_receipts (
                "Id", "ContractVersion", "OperationId", "ApplicationId",
                "SourceKind", "SourceId", "ExpectedApplicationVersion",
                "ExpectedApplicationStatus", "ResultApplicationVersion",
                "ResultApplicationStatus", "OrganizationsScopeRevision",
                "OrganizationsSourceVersion", "OrganizationsSourceStatus",
                "StaffEvidenceSha256", "ExternalEvidenceManifestId",
                "ExternalEvidenceSha256", "ReviewerId", "ReviewedAtUtc",
                "CanonicalSha256", "ScopeId")
            VALUES (
                @id, @contractVersion, @operationId, @applicationId,
                @sourceKind, @sourceId, @expectedApplicationVersion,
                @expectedApplicationStatus, @resultApplicationVersion,
                @resultApplicationStatus, @organizationsScopeRevision,
                @organizationsSourceVersion, @organizationsSourceStatus,
                @staffEvidenceSha256, @externalEvidenceManifestId,
                @externalEvidenceSha256, @reviewerId, @reviewedAtUtc,
                @canonicalSha256, @scopeId)
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("id", receipt.Id);
        command.Parameters.AddWithValue(
            "contractVersion",
            receipt.ContractVersion);
        command.Parameters.AddWithValue("operationId", receipt.OperationId);
        command.Parameters.AddWithValue(
            "applicationId",
            receipt.ApplicationId);
        command.Parameters.AddWithValue("sourceKind", (int)receipt.SourceKind);
        command.Parameters.AddWithValue("sourceId", receipt.SourceId);
        command.Parameters.AddWithValue(
            "expectedApplicationVersion",
            receipt.ExpectedApplicationVersion);
        command.Parameters.AddWithValue(
            "expectedApplicationStatus",
            (int)receipt.ExpectedApplicationStatus);
        command.Parameters.AddWithValue(
            "resultApplicationVersion",
            receipt.ResultApplicationVersion);
        command.Parameters.AddWithValue(
            "resultApplicationStatus",
            (int)receipt.ResultApplicationStatus);
        command.Parameters.AddWithValue(
            "organizationsScopeRevision",
            receipt.OrganizationsScopeRevision);
        command.Parameters.AddWithValue(
            "organizationsSourceVersion",
            receipt.OrganizationsSourceVersion);
        command.Parameters.AddWithValue(
            "organizationsSourceStatus",
            (int)receipt.OrganizationsSourceStatus);
        command.Parameters.AddWithValue(
            "staffEvidenceSha256",
            receipt.StaffEvidenceSha256);
        command.Parameters.AddWithValue(
            "externalEvidenceManifestId",
            receipt.ExternalEvidenceManifestId);
        command.Parameters.AddWithValue(
            "externalEvidenceSha256",
            receipt.ExternalEvidenceSha256);
        command.Parameters.AddWithValue("reviewerId", receipt.ReviewerId);
        command.Parameters.AddWithValue("reviewedAtUtc", receipt.ReviewedAtUtc);
        command.Parameters.AddWithValue(
            "canonicalSha256",
            receipt.CanonicalSha256);
        command.Parameters.AddWithValue("scopeId", receipt.ScopeId);
        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<bool> WaitForBackendLockAsync(
        string connectionString,
        int backendPid,
        Task operation)
    {
        await using NpgsqlConnection observer = new(connectionString);
        await observer.OpenAsync().ConfigureAwait(false);
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (operation.IsCompleted)
            {
                return false;
            }

            await using NpgsqlCommand command = new(
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_stat_activity
                    WHERE pid = @backendPid
                      AND wait_event_type = 'Lock')
                """,
                observer);
            command.Parameters.AddWithValue("backendPid", backendPid);
            if (Assert.IsType<bool>(
                    await command.ExecuteScalarAsync().ConfigureAwait(false)))
            {
                return true;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        return false;
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private sealed class TestScopeContext(string scopeId) : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId { get; } = scopeId;
    }
}
