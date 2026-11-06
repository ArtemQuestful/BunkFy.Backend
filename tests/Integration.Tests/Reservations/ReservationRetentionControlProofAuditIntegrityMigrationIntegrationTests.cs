namespace Integration.Tests;

using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Domain.DataRights;
using BunkFy.Modules.Reservations.Domain.Models;
using BunkFy.Modules.Reservations.Domain.Retention;
using BunkFy.Modules.Reservations.Persistence;
using Gma.Framework.Scoping;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class
    ReservationRetentionControlProofAuditIntegrityMigrationIntegrationTests
{
    private const string BeforeRetentionControlProofAuditIntegrityMigration =
        "20260812011925_AddReservationStayAmendmentConvergence";
    private const string ScopeId = "tenant-a";
    private static readonly string DigestA = new('a', 64);
    private static readonly string DigestC = new('c', 64);
    private static readonly DateTimeOffset Now = new(
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
    public async Task
        Migration_preserves_reachable_control_and_proof_states_and_rejects_malformed_rows()
    {
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder(
                "postgres:16-alpine")
            .WithDatabase(
                "bunkfy_reservation_retention_control_proof_audit_tests")
            .Build();
        await postgreSql.StartAsync();

        Guid currentExecutionId = Guid.Parse(
            "93000000-0000-0000-0000-000000000190");
        Guid runningExecutionId = Guid.Parse(
            "93000000-0000-0000-0000-000000000191");
        Guid failedExecutionId = Guid.Parse(
            "93000000-0000-0000-0000-000000000192");
        ReservationRetentionExecution currentExecution =
            CreateTerminalExecution(
                currentExecutionId,
                "reservation-operational");
        ReservationRetentionExecution runningExecution =
            ReservationRetentionExecution.Start(
                runningExecutionId,
                ScopeId,
                "reservation-running",
                executionPolicyVersion: 1,
                attempt: 1,
                startingProjectionOrdinal: 42,
                Now,
                Now.AddMinutes(15)).Value;
        ReservationRetentionExecution failedExecution =
            CreateFailedExecution(failedExecutionId);

        ReservationRetentionSweepCheckpoint initialCheckpoint =
            ReservationRetentionSweepCheckpoint.Create(
                Guid.Parse("94000000-0000-0000-0000-000000000190"),
                ScopeId,
                "reservation-initial",
                executionPolicyVersion: 1,
                Now).Value;
        ReservationRetentionSweepCheckpoint advancedCheckpoint =
            ReservationRetentionSweepCheckpoint.Create(
                Guid.Parse("94000000-0000-0000-0000-000000000191"),
                ScopeId,
                currentExecution.DataClassKey,
                currentExecution.ExecutionPolicyVersion,
                Now).Value;
        Assert.True(advancedCheckpoint.Advance(
            expectedAfterProjectionOrdinal: 0,
            nextAfterProjectionOrdinal: 42,
            currentExecution.Id,
            Now.AddMinutes(2)).IsSuccess);
        ReservationRetentionSweepCheckpoint retryCheckpoint =
            ReservationRetentionSweepCheckpoint.Create(
                Guid.Parse("94000000-0000-0000-0000-000000000192"),
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
            Guid.Parse("11000000-0000-0000-0000-000000000190"),
            Guid.Parse("31000000-0000-0000-0000-000000000190"),
            Guid.Parse("61000000-0000-0000-0000-000000000190"),
            currentExecution.Id,
            Guid.Parse("81000000-0000-0000-0000-000000000190"));
        RetentionProof supportProof = CreateProof(
            Guid.Parse("11000000-0000-0000-0000-000000000191"),
            Guid.Parse("31000000-0000-0000-0000-000000000191"),
            Guid.Parse("61000000-0000-0000-0000-000000000191"),
            currentExecution.Id,
            Guid.Parse("81000000-0000-0000-0000-000000000191"));
        Reservation reservationWithoutTombstone = CreateTerminalReservation(
            Guid.Parse("11000000-0000-0000-0000-000000000192"),
            Guid.Parse("31000000-0000-0000-0000-000000000192"));

        await using (ReservationsDbContext previous = CreateDbContext(
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
            previous.Reservations.AddRange(
                currentProof.Reservation,
                supportProof.Reservation,
                reservationWithoutTombstone);
            previous.AnonymisationTombstones.AddRange(
                currentProof.Tombstone,
                supportProof.Tombstone);
            previous.RetentionAnonymisationReceipts.Add(
                currentProof.Receipt);
            await previous.SaveChangesAsync();
        }

        await using ReservationsDbContext upgraded = CreateDbContext(
            postgreSql.GetConnectionString());
        await upgraded.Database.MigrateAsync();

        Assert.Equal(3, await upgraded.RetentionExecutions.CountAsync());
        Assert.Equal(3, await upgraded.RetentionSweepCheckpoints.CountAsync());
        Assert.Single(await upgraded.RetentionAnonymisationReceipts
            .AsNoTracking()
            .ToArrayAsync());
        Assert.Equal(
            ReservationRetentionExecutionState.Running,
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
        ReservationRetentionSweepCheckpoint persistedRetry =
            await upgraded.RetentionSweepCheckpoints
                .AsNoTracking()
                .SingleAsync(checkpoint =>
                    checkpoint.Id == retryCheckpoint.Id);
        Assert.Null(persistedRetry.LastExecutionId);
        Assert.Equal(50, persistedRetry.AfterProjectionOrdinal);
        Assert.Equal(3, persistedRetry.Version);
        Assert.True(await upgraded.AnonymisationTombstones
            .AsNoTracking()
            .AnyAsync(tombstone =>
                tombstone.ScopeId == currentProof.Receipt.ScopeId &&
                tombstone.Id == currentProof.Receipt.ReservationId));

        await AssertCheckViolationAsync(
            upgraded,
            "CK_reservation_retention_executions_coordinates",
            RunningExecutionInsert(
                Guid.Empty,
                "reservation-coordinate",
                Now));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_reservation_retention_executions_key",
            RunningExecutionInsert(
                Guid.Parse("93000000-0000-0000-0000-000000000193"),
                "Reservation-Uppercase",
                Now));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_reservation_retention_executions_timestamp",
            RunningExecutionInsert(
                Guid.Parse("93000000-0000-0000-0000-000000000194"),
                "reservation-timestamp",
                default));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_reservation_retention_executions_version",
            TerminalExecutionInsert(
                Guid.Parse("93000000-0000-0000-0000-000000000195"),
                "reservation-version",
                "reservations.reservation-operational.completed",
                version: 1));

        await AssertCheckViolationAsync(
            upgraded,
            "CK_reservation_retention_checkpoints_coordinates",
            CheckpointInsert(
                Guid.Empty,
                "reservation-coordinate",
                afterProjectionOrdinal: 0,
                lastExecutionId: null,
                Now,
                version: 1));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_reservation_retention_checkpoints_key",
            CheckpointInsert(
                Guid.Parse("94000000-0000-0000-0000-000000000193"),
                "Reservation-Uppercase",
                afterProjectionOrdinal: 0,
                lastExecutionId: null,
                Now,
                version: 1));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_reservation_retention_checkpoints_lifecycle",
            CheckpointInsert(
                Guid.Parse("94000000-0000-0000-0000-000000000194"),
                "reservation-lifecycle",
                afterProjectionOrdinal: 1,
                lastExecutionId: null,
                Now,
                version: 2));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_reservation_retention_checkpoints_timestamp",
            CheckpointInsert(
                Guid.Parse("94000000-0000-0000-0000-000000000195"),
                "reservation-timestamp",
                afterProjectionOrdinal: 0,
                lastExecutionId: null,
                updatedAtUtc: default,
                version: 1));
        await AssertUniqueViolationAsync(
            upgraded,
            "UX_reservation_retention_checkpoints_last_execution",
            CheckpointInsert(
                Guid.Parse("94000000-0000-0000-0000-000000000196"),
                "reservation-duplicate-execution",
                afterProjectionOrdinal: 99,
                currentExecutionId,
                Now.AddMinutes(3),
                version: 2));

        await AssertCheckViolationAsync(
            upgraded,
            "CK_reservation_retention_receipts_coordinates",
            ReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000193"),
                currentExecutionId,
                supportProof.Reservation.PropertyId,
                supportProof.Reservation.Id,
                eventId: Guid.Empty,
                ReservationRetentionAnonymisationReceipt.SystemActorId,
                Now.AddDays(-400),
                Now.AddDays(-35),
                Now));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_reservation_retention_receipts_actor",
            ReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000194"),
                currentExecutionId,
                supportProof.Reservation.PropertyId,
                supportProof.Reservation.Id,
                Guid.Parse("81000000-0000-0000-0000-000000000194"),
                "user:operator",
                Now.AddDays(-400),
                Now.AddDays(-35),
                Now));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_reservation_retention_receipts_digests",
            ReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000195"),
                currentExecutionId,
                supportProof.Reservation.PropertyId,
                supportProof.Reservation.Id,
                Guid.Parse("81000000-0000-0000-0000-000000000195"),
                ReservationRetentionAnonymisationReceipt.SystemActorId,
                Now.AddDays(-400),
                Now.AddDays(-35),
                Now,
                policyDigest: new string('A', 64)));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_reservation_retention_receipts_timestamps",
            ReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000196"),
                currentExecutionId,
                supportProof.Reservation.PropertyId,
                supportProof.Reservation.Id,
                Guid.Parse("81000000-0000-0000-0000-000000000196"),
                ReservationRetentionAnonymisationReceipt.SystemActorId,
                terminalAtUtc: default,
                Now.AddDays(-35),
                Now));
        await AssertForeignKeyViolationAsync(
            upgraded,
            "FK_reservation_retention_receipts_execution",
            ReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000197"),
                Guid.Parse("93000000-0000-0000-0000-000000000197"),
                supportProof.Reservation.PropertyId,
                supportProof.Reservation.Id,
                Guid.Parse("81000000-0000-0000-0000-000000000197"),
                ReservationRetentionAnonymisationReceipt.SystemActorId,
                Now.AddDays(-400),
                Now.AddDays(-35),
                Now));
        await AssertForeignKeyViolationAsync(
            upgraded,
            "FK_reservation_retention_receipts_tombstone",
            ReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000198"),
                currentExecutionId,
                reservationWithoutTombstone.PropertyId,
                reservationWithoutTombstone.Id,
                Guid.Parse("81000000-0000-0000-0000-000000000198"),
                ReservationRetentionAnonymisationReceipt.SystemActorId,
                Now.AddDays(-400),
                Now.AddDays(-35),
                Now));
        await AssertUniqueViolationAsync(
            upgraded,
            "UX_reservation_retention_receipts_event",
            ReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000199"),
                currentExecutionId,
                supportProof.Reservation.PropertyId,
                supportProof.Reservation.Id,
                currentProof.Receipt.EventId,
                ReservationRetentionAnonymisationReceipt.SystemActorId,
                Now.AddDays(-400),
                Now.AddDays(-35),
                Now));
        await AssertUniqueViolationAsync(
            upgraded,
            "UX_reservation_retention_receipts_reservation",
            ReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000200"),
                currentExecutionId,
                currentProof.Reservation.PropertyId,
                currentProof.Reservation.Id,
                Guid.Parse("81000000-0000-0000-0000-000000000200"),
                ReservationRetentionAnonymisationReceipt.SystemActorId,
                Now.AddDays(-400),
                Now.AddDays(-35),
                Now));

        await upgraded.Database.GetService<IMigrator>().MigrateAsync(
            BeforeRetentionControlProofAuditIntegrityMigration);
        Assert.Equal(3, await upgraded.RetentionExecutions.CountAsync());
        Assert.Equal(3, await upgraded.RetentionSweepCheckpoints.CountAsync());
        Assert.Single(await upgraded.RetentionAnonymisationReceipts
            .AsNoTracking()
            .ToArrayAsync());
        await AssertCheckViolationAsync(
            upgraded,
            "CK_reservation_retention_executions_state",
            TerminalExecutionInsert(
                Guid.Parse("93000000-0000-0000-0000-000000000201"),
                "reservation-old-outcome",
                "reservations/invalid/outcome",
                version: 2));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_reservation_retention_receipts_actor",
            ReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000201"),
                currentExecutionId,
                supportProof.Reservation.PropertyId,
                supportProof.Reservation.Id,
                Guid.Parse("81000000-0000-0000-0000-000000000201"),
                "user:operator",
                Now.AddDays(-400),
                Now.AddDays(-35),
                Now));
        await upgraded.Database.MigrateAsync();
    }

    private static ReservationRetentionExecution CreateTerminalExecution(
        Guid executionId,
        string dataClassKey)
    {
        ReservationRetentionExecution execution =
            ReservationRetentionExecution.Start(
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
            ReservationRetentionExecutionState.Completed,
            attempt: 1,
            scannedCount: 1,
            remainingCount: 0,
            "reservations.reservation-operational.completed",
            Now.AddMinutes(2),
            holdReviewDueAtUtc: null).IsSuccess);
        return execution;
    }

    private static ReservationRetentionExecution CreateFailedExecution(
        Guid executionId)
    {
        ReservationRetentionExecution execution =
            ReservationRetentionExecution.Start(
                executionId,
                ScopeId,
                "reservation-retry",
                executionPolicyVersion: 1,
                attempt: 1,
                startingProjectionOrdinal: 50,
                Now,
                Now.AddMinutes(15)).Value;
        Assert.True(execution.Complete(
            ReservationRetentionExecutionState.Failed,
            attempt: 1,
            scannedCount: 0,
            remainingCount: 1,
            "reservations.reservation-operational.mutation-failed",
            Now.AddMinutes(1),
            holdReviewDueAtUtc: null).IsSuccess);
        return execution;
    }

    private static RetentionProof CreateProof(
        Guid reservationId,
        Guid propertyId,
        Guid receiptId,
        Guid executionId,
        Guid eventId)
    {
        Reservation reservation = CreateTerminalReservation(
            reservationId,
            propertyId);
        ReservationAnonymisationOutcome outcome = reservation.Anonymise(
            reservation.Version,
            reservation.DetailsRevision,
            ReservationRetentionAnonymisationReceipt.SystemActorId,
            eventId,
            Now).Value;
        ReservationRetentionAnonymisationReceipt receipt =
            ReservationRetentionAnonymisationReceipt.Create(
                receiptId,
                ScopeId,
                executionId,
                propertyId,
                reservationId,
                outcome,
                reservation.TerminalAtUtc!.Value,
                Now.AddDays(-35),
                DigestA,
                redactedHistoryCount: 1,
                reducedExternalOperationCount: 0,
                suppressedReminderCount: 0).Value;
        ReservationAnonymisationTombstone tombstone =
            ReservationAnonymisationTombstone.CreateForRetention(receipt).Value;
        return new(reservation, receipt, tombstone);
    }

    private static Reservation CreateTerminalReservation(
        Guid reservationId,
        Guid propertyId)
    {
        Reservation reservation = Reservation.Create(
            reservationId,
            ScopeId,
            propertyId,
            Guid.NewGuid(),
            new DateOnly(2025, 6, 1),
            new DateOnly(2025, 6, 3),
            [Guid.NewGuid()],
            "Original Guest",
            "original@example.test",
            null,
            1,
            ReservationSource.Direct,
            null,
            null,
            null,
            Guid.NewGuid(),
            Guid.NewGuid(),
            ReservationDetailsChangeOrigin.Staff,
            "user:creator",
            null,
            null,
            Guid.NewGuid(),
            Now.AddDays(-401),
            expectedArrivalTime: null,
            expectedDepartureTime: null).Value;
        Assert.True(reservation.RejectAllocation(
            reservation.AllocationRequestId,
            ReservationAllocationRejection.UnitNotSellable,
            Guid.NewGuid(),
            Now.AddDays(-400)).IsSuccess);
        reservation.ClearDomainEvents();
        return reservation;
    }

    private static FormattableString RunningExecutionInsert(
        Guid executionId,
        string dataClassKey,
        DateTimeOffset startedAtUtc) => $"""
        INSERT INTO reservations.reservation_retention_executions
            ("Id", "DataClassKey", "ExecutionPolicyVersion", "Attempt",
             "StartingProjectionOrdinal", "State", "StartedAtUtc",
             "DeadlineUtc", "CompletedAtUtc", "AffectedCount",
             "ScannedCount", "RemainingCount", "OutcomeCode",
             "HoldReviewDueAtUtc", "Version", "ScopeId")
        VALUES
            ({executionId}, {dataClassKey}, {1}, {1}, {0L},
             {(int)ReservationRetentionExecutionState.Running},
             {startedAtUtc}, {Now.AddMinutes(15)}, NULL, {0}, NULL, NULL,
             NULL, NULL, {1L}, {ScopeId});
        """;

    private static FormattableString TerminalExecutionInsert(
        Guid executionId,
        string dataClassKey,
        string outcomeCode,
        long version) => $"""
        INSERT INTO reservations.reservation_retention_executions
            ("Id", "DataClassKey", "ExecutionPolicyVersion", "Attempt",
             "StartingProjectionOrdinal", "State", "StartedAtUtc",
             "DeadlineUtc", "CompletedAtUtc", "AffectedCount",
             "ScannedCount", "RemainingCount", "OutcomeCode",
             "HoldReviewDueAtUtc", "Version", "ScopeId")
        VALUES
            ({executionId}, {dataClassKey}, {1}, {1}, {0L},
             {(int)ReservationRetentionExecutionState.Completed}, {Now},
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
        INSERT INTO reservations.reservation_retention_sweep_checkpoints
            ("Id", "DataClassKey", "ExecutionPolicyVersion",
             "AfterProjectionOrdinal", "LastExecutionId", "UpdatedAtUtc",
             "Version", "ScopeId")
        VALUES
            ({checkpointId}, {dataClassKey}, {1}, {afterProjectionOrdinal},
             {lastExecutionId}, {updatedAtUtc}, {version}, {ScopeId});
        """;

    private static FormattableString ReceiptInsert(
        Guid receiptId,
        Guid executionId,
        Guid propertyId,
        Guid reservationId,
        Guid eventId,
        string actorId,
        DateTimeOffset terminalAtUtc,
        DateTimeOffset retentionDeadlineUtc,
        DateTimeOffset completedAtUtc,
        string? policyDigest = null) => $"""
        INSERT INTO reservations.reservation_retention_anonymisation_receipts
            ("Id", "ContractVersion", "ExecutionId", "PropertyId",
             "ReservationId", "SelectedReservationVersion",
             "ResultingReservationVersion", "SelectedDetailsRevision",
             "ResultingDetailsRevision", "TerminalAtUtc",
             "RetentionDeadlineUtc", "PolicyEvidenceSha256",
             "RedactedHistoryCount", "RemovedGuestLinkCount",
             "ReducedExternalOperationCount", "SuppressedReminderCount",
             "EventId", "ActorId", "CompletedAtUtc", "CanonicalSha256",
             "ScopeId")
        VALUES
            ({receiptId}, {1}, {executionId}, {propertyId}, {reservationId},
             {2L}, {3L}, {2L}, {3L}, {terminalAtUtc},
             {retentionDeadlineUtc}, {policyDigest ?? DigestA}, {1}, {0}, {0},
             {0}, {eventId}, {actorId}, {completedAtUtc}, {DigestC},
             {ScopeId});
        """;

    private static async Task AssertCheckViolationAsync(
        ReservationsDbContext dbContext,
        string expectedConstraint,
        FormattableString command)
    {
        PostgresException failure = await Assert.ThrowsAsync<PostgresException>(
            () => dbContext.Database.ExecuteSqlInterpolatedAsync(command));
        Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
        Assert.Equal(expectedConstraint, failure.ConstraintName);
    }

    private static async Task AssertForeignKeyViolationAsync(
        ReservationsDbContext dbContext,
        string expectedConstraint,
        FormattableString command)
    {
        PostgresException failure = await Assert.ThrowsAsync<PostgresException>(
            () => dbContext.Database.ExecuteSqlInterpolatedAsync(command));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, failure.SqlState);
        Assert.Equal(expectedConstraint, failure.ConstraintName);
    }

    private static async Task AssertUniqueViolationAsync(
        ReservationsDbContext dbContext,
        string expectedConstraint,
        FormattableString command)
    {
        PostgresException failure = await Assert.ThrowsAsync<PostgresException>(
            () => dbContext.Database.ExecuteSqlInterpolatedAsync(command));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, failure.SqlState);
        Assert.Equal(expectedConstraint, failure.ConstraintName);
    }

    private static ReservationsDbContext CreateDbContext(
        string connectionString)
    {
        DbContextOptions<ReservationsDbContext> options =
            new DbContextOptionsBuilder<ReservationsDbContext>()
                .UseNpgsql(connectionString, provider => provider
                    .MigrationsAssembly(
                        ReservationsMigrations.PostgreSqlAssembly)
                    .MigrationsHistoryTable(
                        ReservationsMigrations.HistoryTable,
                        ReservationsMigrations.Schema))
                .Options;
        return new(
            options,
            new TestScopeContext(),
            OpenWorkspaceTerminationFenceReader.Instance);
    }

    private sealed record RetentionProof(
        Reservation Reservation,
        ReservationRetentionAnonymisationReceipt Receipt,
        ReservationAnonymisationTombstone Tombstone);

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId =>
            ReservationRetentionControlProofAuditIntegrityMigrationIntegrationTests
                .ScopeId;
    }
}
