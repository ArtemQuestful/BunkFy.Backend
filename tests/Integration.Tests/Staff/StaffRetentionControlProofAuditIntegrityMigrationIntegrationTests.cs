namespace Integration.Tests;

using BunkFy.Modules.Staff.Domain.Aggregates;
using BunkFy.Modules.Staff.Domain.DataRights;
using BunkFy.Modules.Staff.Domain.Models;
using BunkFy.Modules.Staff.Domain.Retention;
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

public sealed class
    StaffRetentionControlProofAuditIntegrityMigrationIntegrationTests
{
    private const string BeforeRetentionControlProofAuditIntegrityMigration =
        "20260821191504_AddStaffPropertyAuthorityTenantIntegrity";
    private const string ScopeId = "tenant-a";
    private static readonly string DigestA = new('a', 64);
    private static readonly string DigestC = new('c', 64);
    private static readonly DateTimeOffset Now = new(
        2026,
        8,
        22,
        12,
        0,
        0,
        TimeSpan.Zero);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task
        Migration_preserves_reachable_control_and_proof_states_and_rejects_malformed_rows()
    {
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder(
                "postgres:16-alpine")
            .WithDatabase(
                "bunkfy_staff_retention_control_proof_audit_tests")
            .Build();
        await postgreSql.StartAsync();

        Guid currentExecutionId = Guid.Parse(
            "93000000-0000-0000-0000-000000000290");
        Guid runningExecutionId = Guid.Parse(
            "93000000-0000-0000-0000-000000000291");
        Guid failedExecutionId = Guid.Parse(
            "93000000-0000-0000-0000-000000000292");
        StaffRetentionExecution currentExecution =
            CreateTerminalExecution(
                currentExecutionId,
                "staff-employment");
        StaffRetentionExecution runningExecution =
            StaffRetentionExecution.Start(
                runningExecutionId,
                ScopeId,
                "staff-running",
                executionPolicyVersion: 1,
                attempt: 1,
                startingProjectionOrdinal: 42,
                Now,
                Now.AddMinutes(15)).Value;
        StaffRetentionExecution failedExecution =
            CreateFailedExecution(failedExecutionId);

        StaffRetentionSweepCheckpoint initialCheckpoint =
            StaffRetentionSweepCheckpoint.Create(
                Guid.Parse("94000000-0000-0000-0000-000000000290"),
                ScopeId,
                "staff-initial",
                executionPolicyVersion: 1,
                Now).Value;
        StaffRetentionSweepCheckpoint advancedCheckpoint =
            StaffRetentionSweepCheckpoint.Create(
                Guid.Parse("94000000-0000-0000-0000-000000000291"),
                ScopeId,
                currentExecution.DataClassKey,
                currentExecution.ExecutionPolicyVersion,
                Now).Value;
        Assert.True(advancedCheckpoint.Advance(
            expectedAfterProjectionOrdinal: 0,
            nextAfterProjectionOrdinal: 42,
            currentExecution.Id,
            Now.AddMinutes(2)).IsSuccess);
        StaffRetentionSweepCheckpoint retryCheckpoint =
            StaffRetentionSweepCheckpoint.Create(
                Guid.Parse("94000000-0000-0000-0000-000000000292"),
                ScopeId,
                failedExecution.DataClassKey,
                failedExecution.ExecutionPolicyVersion,
                Now).Value;
        Assert.True(retryCheckpoint.Advance(
            expectedAfterProjectionOrdinal: 0,
            nextAfterProjectionOrdinal: 42,
            failedExecution.Id,
            failedExecution.CompletedAtUtc!.Value).IsSuccess);
        Assert.True(retryCheckpoint.PrepareRetry(
            failedExecution,
            Now.AddMinutes(2)).IsSuccess);

        RetentionProof currentProof = CreateProof(
            Guid.Parse("11000000-0000-0000-0000-000000000290"),
            Guid.Parse("61000000-0000-0000-0000-000000000290"),
            currentExecution.Id,
            Guid.Parse("81000000-0000-0000-0000-000000000290"));
        RetentionProof supportProof = CreateProof(
            Guid.Parse("11000000-0000-0000-0000-000000000291"),
            Guid.Parse("61000000-0000-0000-0000-000000000291"),
            currentExecution.Id,
            Guid.Parse("81000000-0000-0000-0000-000000000291"));
        StaffMember memberWithoutTombstone = CreateDepartedMember(
            Guid.Parse("11000000-0000-0000-0000-000000000292"),
            "Staff without tombstone");

        await using (StaffDbContext previous = CreateDbContext(
            postgreSql.GetConnectionString()))
        {
            await previous.Database.GetService<IMigrator>().MigrateAsync(
                BeforeRetentionControlProofAuditIntegrityMigration);
            previous.RetentionExecutions.AddRange(
                currentExecution,
                runningExecution,
                failedExecution);
            previous.RetentionSweepCheckpoints.AddRange(
                initialCheckpoint,
                advancedCheckpoint,
                retryCheckpoint);
            previous.StaffMembers.AddRange(
                currentProof.Member,
                supportProof.Member,
                memberWithoutTombstone);
            previous.AnonymisationTombstones.AddRange(
                currentProof.Tombstone,
                supportProof.Tombstone);
            previous.RetentionAnonymisationReceipts.Add(
                currentProof.Receipt);
            await previous.SaveChangesAsync();
        }

        await using StaffDbContext upgraded = CreateDbContext(
            postgreSql.GetConnectionString());
        await upgraded.Database.MigrateAsync();

        Assert.Equal(3, await upgraded.RetentionExecutions.CountAsync());
        Assert.Equal(
            3,
            await upgraded.RetentionSweepCheckpoints.CountAsync());
        Assert.Single(await upgraded.RetentionAnonymisationReceipts
            .AsNoTracking()
            .ToArrayAsync());
        Assert.Equal(
            StaffRetentionExecutionState.Running,
            (await upgraded.RetentionExecutions
                .AsNoTracking()
                .SingleAsync(execution =>
                    execution.Id == runningExecutionId)).State);
        Assert.Null((await upgraded.RetentionSweepCheckpoints
            .AsNoTracking()
            .SingleAsync(checkpoint =>
                checkpoint.Id == initialCheckpoint.Id)).LastExecutionId);
        Assert.Equal(
            currentExecutionId,
            (await upgraded.RetentionSweepCheckpoints
                .AsNoTracking()
                .SingleAsync(checkpoint =>
                    checkpoint.Id == advancedCheckpoint.Id)).LastExecutionId);
        StaffRetentionSweepCheckpoint persistedRetry =
            await upgraded.RetentionSweepCheckpoints
                .AsNoTracking()
                .SingleAsync(checkpoint =>
                    checkpoint.Id == retryCheckpoint.Id);
        Assert.Null(persistedRetry.LastExecutionId);
        Assert.Equal(0, persistedRetry.AfterProjectionOrdinal);
        Assert.Equal(3, persistedRetry.Version);
        Assert.True(await upgraded.AnonymisationTombstones
            .AsNoTracking()
            .AnyAsync(tombstone =>
                tombstone.ScopeId == currentProof.Receipt.ScopeId &&
                tombstone.Id == currentProof.Receipt.StaffMemberId));

        await AssertCheckViolationAsync(
            upgraded,
            "CK_staff_retention_executions_coordinates",
            RunningExecutionInsert(
                Guid.Empty,
                "staff-coordinate",
                Now));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_staff_retention_executions_key",
            RunningExecutionInsert(
                Guid.Parse("93000000-0000-0000-0000-000000000293"),
                "Staff-Uppercase",
                Now));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_staff_retention_executions_timestamp",
            RunningExecutionInsert(
                Guid.Parse("93000000-0000-0000-0000-000000000294"),
                "staff-timestamp",
                default));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_staff_retention_executions_version",
            TerminalExecutionInsert(
                Guid.Parse("93000000-0000-0000-0000-000000000295"),
                "staff-version",
                "staff.staff-employment.completed",
                version: 1));

        await AssertCheckViolationAsync(
            upgraded,
            "CK_staff_retention_checkpoints_coordinates",
            CheckpointInsert(
                Guid.Empty,
                "staff-coordinate",
                afterProjectionOrdinal: 0,
                lastExecutionId: null,
                Now,
                version: 1));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_staff_retention_checkpoints_key",
            CheckpointInsert(
                Guid.Parse("94000000-0000-0000-0000-000000000293"),
                "Staff-Uppercase",
                afterProjectionOrdinal: 0,
                lastExecutionId: null,
                Now,
                version: 1));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_staff_retention_checkpoints_lifecycle",
            CheckpointInsert(
                Guid.Parse("94000000-0000-0000-0000-000000000294"),
                "staff-lifecycle",
                afterProjectionOrdinal: 1,
                lastExecutionId: null,
                Now,
                version: 2));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_staff_retention_checkpoints_timestamp",
            CheckpointInsert(
                Guid.Parse("94000000-0000-0000-0000-000000000295"),
                "staff-timestamp",
                afterProjectionOrdinal: 0,
                lastExecutionId: null,
                updatedAtUtc: default,
                version: 1));
        await AssertUniqueViolationAsync(
            upgraded,
            "UX_staff_retention_checkpoints_last_execution",
            CheckpointInsert(
                Guid.Parse("94000000-0000-0000-0000-000000000296"),
                "staff-duplicate-execution",
                afterProjectionOrdinal: 99,
                currentExecutionId,
                Now.AddMinutes(3),
                version: 2));

        await AssertCheckViolationAsync(
            upgraded,
            "CK_staff_retention_receipts_coordinates",
            ReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000293"),
                currentExecutionId,
                supportProof.Member.Id,
                eventId: Guid.Empty,
                StaffRetentionAnonymisationReceipt.SystemActorId,
                Now.AddDays(-400),
                Now.AddDays(-35),
                Now));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_staff_retention_receipts_actor",
            ReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000294"),
                currentExecutionId,
                supportProof.Member.Id,
                Guid.Parse("81000000-0000-0000-0000-000000000294"),
                "user:operator",
                Now.AddDays(-400),
                Now.AddDays(-35),
                Now));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_staff_retention_receipts_digests",
            ReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000295"),
                currentExecutionId,
                supportProof.Member.Id,
                Guid.Parse("81000000-0000-0000-0000-000000000295"),
                StaffRetentionAnonymisationReceipt.SystemActorId,
                Now.AddDays(-400),
                Now.AddDays(-35),
                Now,
                policyDigest: new string('A', 64)));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_staff_retention_receipts_timestamps",
            ReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000296"),
                currentExecutionId,
                supportProof.Member.Id,
                Guid.Parse("81000000-0000-0000-0000-000000000296"),
                StaffRetentionAnonymisationReceipt.SystemActorId,
                departedAtUtc: default,
                Now.AddDays(-35),
                Now));
        await AssertForeignKeyViolationAsync(
            upgraded,
            "FK_staff_retention_receipts_execution",
            ReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000297"),
                Guid.Parse("93000000-0000-0000-0000-000000000297"),
                supportProof.Member.Id,
                Guid.Parse("81000000-0000-0000-0000-000000000297"),
                StaffRetentionAnonymisationReceipt.SystemActorId,
                Now.AddDays(-400),
                Now.AddDays(-35),
                Now));
        await AssertForeignKeyViolationAsync(
            upgraded,
            "FK_staff_retention_receipts_tombstone",
            ReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000298"),
                currentExecutionId,
                memberWithoutTombstone.Id,
                Guid.Parse("81000000-0000-0000-0000-000000000298"),
                StaffRetentionAnonymisationReceipt.SystemActorId,
                Now.AddDays(-400),
                Now.AddDays(-35),
                Now));
        await AssertUniqueViolationAsync(
            upgraded,
            "UX_staff_retention_receipts_event",
            ReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000299"),
                currentExecutionId,
                supportProof.Member.Id,
                currentProof.Receipt.EventId,
                StaffRetentionAnonymisationReceipt.SystemActorId,
                Now.AddDays(-400),
                Now.AddDays(-35),
                Now));
        await AssertUniqueViolationAsync(
            upgraded,
            "UX_staff_retention_receipts_staff_member",
            ReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000300"),
                currentExecutionId,
                currentProof.Member.Id,
                Guid.Parse("81000000-0000-0000-0000-000000000300"),
                StaffRetentionAnonymisationReceipt.SystemActorId,
                Now.AddDays(-400),
                Now.AddDays(-35),
                Now));

        await upgraded.Database.GetService<IMigrator>().MigrateAsync(
            BeforeRetentionControlProofAuditIntegrityMigration);
        Assert.Equal(3, await upgraded.RetentionExecutions.CountAsync());
        Assert.Equal(
            3,
            await upgraded.RetentionSweepCheckpoints.CountAsync());
        Assert.Single(await upgraded.RetentionAnonymisationReceipts
            .AsNoTracking()
            .ToArrayAsync());
        await AssertCheckViolationAsync(
            upgraded,
            "CK_staff_retention_executions_state",
            TerminalExecutionInsert(
                Guid.Parse("93000000-0000-0000-0000-000000000301"),
                "staff-old-outcome",
                "staff/invalid/outcome",
                version: 2));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_staff_retention_receipts_actor",
            ReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000301"),
                currentExecutionId,
                supportProof.Member.Id,
                Guid.Parse("81000000-0000-0000-0000-000000000301"),
                "user:operator",
                Now.AddDays(-400),
                Now.AddDays(-35),
                Now));
        await upgraded.Database.MigrateAsync();
    }

    private static StaffRetentionExecution CreateTerminalExecution(
        Guid executionId,
        string dataClassKey)
    {
        StaffRetentionExecution execution =
            StaffRetentionExecution.Start(
                executionId,
                ScopeId,
                dataClassKey,
                executionPolicyVersion: 1,
                attempt: 1,
                startingProjectionOrdinal: 0,
                Now,
                Now.AddMinutes(15)).Value;
        Assert.True(execution.RecordAffected().IsSuccess);
        Assert.True(execution.Complete(
            StaffRetentionExecutionState.Completed,
            attempt: 1,
            scannedCount: 1,
            remainingCount: 0,
            "staff.staff-employment.completed",
            Now.AddMinutes(2),
            holdReviewDueAtUtc: null).IsSuccess);
        return execution;
    }

    private static StaffRetentionExecution CreateFailedExecution(
        Guid executionId)
    {
        StaffRetentionExecution execution =
            StaffRetentionExecution.Start(
                executionId,
                ScopeId,
                "staff-retry",
                executionPolicyVersion: 1,
                attempt: 1,
                startingProjectionOrdinal: 0,
                Now,
                Now.AddMinutes(15)).Value;
        Assert.True(execution.Complete(
            StaffRetentionExecutionState.Failed,
            attempt: 1,
            scannedCount: 0,
            remainingCount: 1,
            "staff.staff-employment.mutation-failed",
            Now.AddMinutes(1),
            holdReviewDueAtUtc: null).IsSuccess);
        return execution;
    }

    private static RetentionProof CreateProof(
        Guid staffMemberId,
        Guid receiptId,
        Guid executionId,
        Guid eventId)
    {
        DateTimeOffset departedAtUtc = Now.AddDays(-400);
        StaffMember member = CreateDepartedMember(
            staffMemberId,
            $"Retention staff {staffMemberId:N}",
            departedAtUtc);
        StaffMemberAnonymisationOutcome outcome = member.Anonymise(
            member.Version,
            StaffRetentionAnonymisationReceipt.SystemActorId,
            eventId,
            Now).Value;
        StaffRetentionAnonymisationReceipt receipt =
            StaffRetentionAnonymisationReceipt.Create(
                receiptId,
                ScopeId,
                executionId,
                staffMemberId,
                outcome,
                selectedOperationLockRevision: 8,
                resultingOperationLockRevision: 9,
                departedAtUtc,
                Now.AddDays(-35),
                DigestA).Value;
        StaffAnonymisationTombstone tombstone =
            StaffAnonymisationTombstone.CreateForRetention(receipt).Value;
        return new(member, receipt, tombstone);
    }

    private static StaffMember CreateDepartedMember(
        Guid staffMemberId,
        string displayName,
        DateTimeOffset? departedAtUtc = null)
    {
        DateTimeOffset departure = departedAtUtc ?? Now.AddDays(-400);
        StaffMember member = StaffMember.Create(
            staffMemberId,
            ScopeId,
            displayName,
            $"{displayName} legal",
            $"{staffMemberId:N}@example.test",
            "+44 20 1234 5678",
            $"EMP-{staffMemberId:N}",
            "Manager",
            "Operations",
            null,
            "user:creator",
            Guid.NewGuid(),
            departure.AddDays(-100)).Value;
        Assert.True(member.Depart(
            DateOnly.FromDateTime(departure.UtcDateTime),
            member.Version,
            "user:manager",
            "Employment ended",
            Guid.NewGuid(),
            [],
            departure).IsSuccess);
        member.ClearDomainEvents();
        return member;
    }

    private static FormattableString RunningExecutionInsert(
        Guid executionId,
        string dataClassKey,
        DateTimeOffset startedAtUtc) => $"""
        INSERT INTO staff.staff_retention_executions
            ("Id", "DataClassKey", "ExecutionPolicyVersion", "Attempt",
             "StartingProjectionOrdinal", "State", "StartedAtUtc",
             "DeadlineUtc", "CompletedAtUtc", "AffectedCount",
             "ScannedCount", "RemainingCount", "OutcomeCode",
             "HoldReviewDueAtUtc", "Version", "ScopeId")
        VALUES
            ({executionId}, {dataClassKey}, {1}, {1}, {0L},
             {(int)StaffRetentionExecutionState.Running}, {startedAtUtc},
             {Now.AddMinutes(15)}, NULL, {0}, NULL, NULL, NULL, NULL, {1L},
             {ScopeId});
        """;

    private static FormattableString TerminalExecutionInsert(
        Guid executionId,
        string dataClassKey,
        string outcomeCode,
        long version) => $"""
        INSERT INTO staff.staff_retention_executions
            ("Id", "DataClassKey", "ExecutionPolicyVersion", "Attempt",
             "StartingProjectionOrdinal", "State", "StartedAtUtc",
             "DeadlineUtc", "CompletedAtUtc", "AffectedCount",
             "ScannedCount", "RemainingCount", "OutcomeCode",
             "HoldReviewDueAtUtc", "Version", "ScopeId")
        VALUES
            ({executionId}, {dataClassKey}, {1}, {1}, {0L},
             {(int)StaffRetentionExecutionState.Completed}, {Now},
             {Now.AddMinutes(15)}, {Now.AddMinutes(2)}, {1}, {1}, {0},
             {outcomeCode}, NULL, {version}, {ScopeId});
        """;

    private static FormattableString CheckpointInsert(
        Guid checkpointId,
        string dataClassKey,
        long afterProjectionOrdinal,
        Guid? lastExecutionId,
        DateTimeOffset updatedAtUtc,
        long version) => $"""
        INSERT INTO staff.staff_retention_sweep_checkpoints
            ("Id", "DataClassKey", "ExecutionPolicyVersion",
             "AfterProjectionOrdinal", "LastExecutionId", "UpdatedAtUtc",
             "Version", "ScopeId")
        VALUES
            ({checkpointId}, {dataClassKey}, {1},
             {afterProjectionOrdinal}, {lastExecutionId}, {updatedAtUtc},
             {version}, {ScopeId});
        """;

    private static FormattableString ReceiptInsert(
        Guid receiptId,
        Guid executionId,
        Guid staffMemberId,
        Guid eventId,
        string actorId,
        DateTimeOffset departedAtUtc,
        DateTimeOffset retentionDeadlineUtc,
        DateTimeOffset completedAtUtc,
        string? policyDigest = null) => $"""
        INSERT INTO staff.staff_retention_anonymisation_receipts
            ("Id", "ContractVersion", "ExecutionId", "StaffMemberId",
             "SelectedStaffVersion", "ResultingStaffVersion",
             "SelectedOperationLockRevision",
             "ResultingOperationLockRevision", "DepartedAtUtc",
             "RetentionDeadlineUtc", "PolicyEvidenceSha256", "EventId",
             "ActorId", "CompletedAtUtc", "CanonicalSha256", "ScopeId")
        VALUES
            ({receiptId}, {1}, {executionId}, {staffMemberId}, {2L}, {3L},
             {8L}, {9L}, {departedAtUtc}, {retentionDeadlineUtc},
             {policyDigest ?? DigestA}, {eventId}, {actorId},
             {completedAtUtc}, {DigestC}, {ScopeId});
        """;

    private static async Task AssertCheckViolationAsync(
        StaffDbContext dbContext,
        string expectedConstraint,
        FormattableString command)
    {
        PostgresException failure =
            await Assert.ThrowsAsync<PostgresException>(
                () => dbContext.Database.ExecuteSqlInterpolatedAsync(
                    command));
        Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
        Assert.Equal(expectedConstraint, failure.ConstraintName);
    }

    private static async Task AssertForeignKeyViolationAsync(
        StaffDbContext dbContext,
        string expectedConstraint,
        FormattableString command)
    {
        PostgresException failure =
            await Assert.ThrowsAsync<PostgresException>(
                () => dbContext.Database.ExecuteSqlInterpolatedAsync(
                    command));
        Assert.Equal(
            PostgresErrorCodes.ForeignKeyViolation,
            failure.SqlState);
        Assert.Equal(expectedConstraint, failure.ConstraintName);
    }

    private static async Task AssertUniqueViolationAsync(
        StaffDbContext dbContext,
        string expectedConstraint,
        FormattableString command)
    {
        PostgresException failure =
            await Assert.ThrowsAsync<PostgresException>(
                () => dbContext.Database.ExecuteSqlInterpolatedAsync(
                    command));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, failure.SqlState);
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
            new NoTerminationFenceReader());
    }

    private sealed record RetentionProof(
        StaffMember Member,
        StaffRetentionAnonymisationReceipt Receipt,
        StaffAnonymisationTombstone Tombstone);

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId =>
            StaffRetentionControlProofAuditIntegrityMigrationIntegrationTests
                .ScopeId;
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
