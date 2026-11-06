namespace BunkFy.Modules.Reservations.Tests;

using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Domain.DataRights;
using BunkFy.Modules.Reservations.Domain.Retention;
using BunkFy.Modules.Reservations.Persistence;
using BunkFy.Modules.Reservations.Persistence.Repositories;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ReservationRetentionModelTests
{
    [Fact]
    public void Model_enforces_complete_retention_control_and_proof_shape()
    {
        using ReservationsDbContext dbContext = CreateDbContext();
        IModel designModel =
            dbContext.GetService<IDesignTimeModel>().Model;
        IEntityType reservation = designModel.FindEntityType(
            typeof(Reservation))!;
        IEntityType execution = designModel.FindEntityType(
            typeof(ReservationRetentionExecution))!;
        IEntityType checkpoint = designModel.FindEntityType(
            typeof(ReservationRetentionSweepCheckpoint))!;
        IEntityType receipt = designModel.FindEntityType(
            typeof(ReservationRetentionAnonymisationReceipt))!;
        IEntityType tombstone = designModel.FindEntityType(
            typeof(ReservationAnonymisationTombstone))!;

        Assert.Contains(
            reservation.GetCheckConstraints(),
            constraint =>
                constraint.Name == "CK_reservations_terminal_time");
        Assert.True(execution.FindProperty(
            nameof(ReservationRetentionExecution.Version))!
            .IsConcurrencyToken);
        AssertConstraintNames(
            execution,
            "CK_reservation_retention_executions_coordinates",
            "CK_reservation_retention_executions_policy",
            "CK_reservation_retention_executions_cursor",
            "CK_reservation_retention_executions_key",
            "CK_reservation_retention_executions_version",
            "CK_reservation_retention_executions_timestamp",
            "CK_reservation_retention_executions_counts",
            "CK_reservation_retention_executions_state");
        Assert.Equal(2, execution.GetKeys().Count());
        Assert.Contains(
            execution.GetIndexes(),
            index => index.GetDatabaseName() ==
                "IX_reservation_retention_executions_history");

        Assert.True(checkpoint.FindProperty(
            nameof(ReservationRetentionSweepCheckpoint.Version))!
            .IsConcurrencyToken);
        AssertConstraintNames(
            checkpoint,
            "CK_reservation_retention_checkpoints_coordinates",
            "CK_reservation_retention_checkpoints_cursor",
            "CK_reservation_retention_checkpoints_key",
            "CK_reservation_retention_checkpoints_policy",
            "CK_reservation_retention_checkpoints_version",
            "CK_reservation_retention_checkpoints_lifecycle",
            "CK_reservation_retention_checkpoints_timestamp");
        Assert.Single(checkpoint.GetKeys());
        Assert.Empty(checkpoint.GetForeignKeys());
        Assert.Contains(
            checkpoint.GetIndexes(),
            index => index.IsUnique &&
                index.GetDatabaseName() ==
                    "UX_reservation_retention_checkpoints_data_class_policy");
        Assert.Contains(
            checkpoint.GetIndexes(),
            index => index.IsUnique &&
                index.GetDatabaseName() ==
                    "UX_reservation_retention_checkpoints_last_execution" &&
                index.GetFilter() == "\"LastExecutionId\" IS NOT NULL");

        AssertConstraintNames(
            receipt,
            "CK_reservation_retention_receipts_coordinates",
            "CK_reservation_retention_receipts_contract",
            "CK_reservation_retention_receipts_actor",
            "CK_reservation_retention_receipts_versions",
            "CK_reservation_retention_receipts_counts",
            "CK_reservation_retention_receipts_digests",
            "CK_reservation_retention_receipts_timestamps");
        Assert.Single(receipt.GetKeys());
        Assert.Equal(
            3,
            receipt.GetForeignKeys().Count(foreignKey =>
                foreignKey.PrincipalEntityType.ClrType is not null));
        AssertForeignKey(
            receipt,
            typeof(ReservationRetentionExecution),
            "FK_reservation_retention_receipts_execution");
        AssertForeignKey(
            receipt,
            typeof(Reservation),
            "FK_reservation_retention_receipts_reservation");
        AssertForeignKey(
            receipt,
            typeof(ReservationAnonymisationTombstone),
            "FK_reservation_retention_receipts_tombstone");
        AssertIndex(
            receipt,
            "UX_reservation_retention_receipts_reservation",
            isUnique: true,
            nameof(ReservationRetentionAnonymisationReceipt.ScopeId),
            nameof(ReservationRetentionAnonymisationReceipt.ReservationId));
        AssertIndex(
            receipt,
            "IX_reservation_retention_receipts_execution",
            isUnique: false,
            nameof(ReservationRetentionAnonymisationReceipt.ScopeId),
            nameof(ReservationRetentionAnonymisationReceipt.ExecutionId));
        AssertIndex(
            receipt,
            "UX_reservation_retention_receipts_event",
            isUnique: true,
            nameof(ReservationRetentionAnonymisationReceipt.ScopeId),
            nameof(ReservationRetentionAnonymisationReceipt.EventId));
        Assert.False(tombstone.FindProperty(
            nameof(ReservationAnonymisationTombstone.Authority))!
            .IsNullable);
    }

    [Fact]
    public async Task Retention_receipt_is_append_only()
    {
        await using ReservationsDbContext dbContext =
            CreateDbContext();
        Reservation reservation = CreateTerminalReservation(
            "tenant-a",
            Guid.NewGuid());
        DateTimeOffset now =
            ReservationRetentionTestData.Now;
        ReservationRetentionExecution execution =
            ReservationRetentionExecution.Start(
                Guid.NewGuid(),
                reservation.ScopeId,
                "reservation-operational",
                executionPolicyVersion: 1,
                attempt: 1,
                startingProjectionOrdinal: 0,
                now.AddMinutes(-1),
                now.AddMinutes(10)).Value;
        ReservationAnonymisationOutcome outcome =
            reservation.Anonymise(
                reservation.Version,
                reservation.DetailsRevision,
                "system:retention",
                Guid.NewGuid(),
                now).Value;
        ReservationRetentionAnonymisationReceipt receipt =
            ReservationRetentionAnonymisationReceipt.Create(
                Guid.NewGuid(),
                reservation.ScopeId,
                execution.Id,
                reservation.PropertyId,
                reservation.Id,
                outcome,
                reservation.TerminalAtUtc!.Value,
                now.AddDays(-1),
                new string('a', 64),
                redactedHistoryCount: 1,
                reducedExternalOperationCount: 0,
                suppressedReminderCount: 0).Value;
        dbContext.Reservations.Add(reservation);
        dbContext.RetentionExecutions.Add(execution);
        dbContext.RetentionAnonymisationReceipts.Add(receipt);
        await dbContext.SaveChangesAsync();

        dbContext.Entry(receipt)
            .Property(nameof(
                ReservationRetentionAnonymisationReceipt
                    .PolicyEvidenceSha256))
            .CurrentValue = new string('b', 64);

        InvalidOperationException failure =
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => dbContext.SaveChangesAsync());
        Assert.Equal(
            "Reservation retention anonymisation receipts are append-only.",
            failure.Message);
    }

    [Fact]
    public async Task Checkpoint_lookup_isolated_by_execution_policy_version()
    {
        await using ReservationsDbContext dbContext =
            CreateDbContext();
        ReservationRetentionSweepCheckpoint versionOne =
            ReservationRetentionSweepCheckpoint.Create(
                Guid.NewGuid(),
                "tenant-a",
                "reservation-operational",
                executionPolicyVersion: 1,
                ReservationRetentionTestData.Now).Value;
        ReservationRetentionSweepCheckpoint versionTwo =
            ReservationRetentionSweepCheckpoint.Create(
                Guid.NewGuid(),
                "tenant-a",
                "reservation-operational",
                executionPolicyVersion: 2,
                ReservationRetentionTestData.Now).Value;
        dbContext.RetentionSweepCheckpoints.AddRange(
            versionOne,
            versionTwo);
        await dbContext.SaveChangesAsync();
        var repository =
            new ReservationRetentionExecutionRepository(dbContext);

        Assert.Same(
            versionOne,
            await repository.GetCheckpointAsync(
                "reservation-operational",
                executionPolicyVersion: 1,
                CancellationToken.None));
        Assert.Same(
            versionTwo,
            await repository.GetCheckpointAsync(
                "reservation-operational",
                executionPolicyVersion: 2,
                CancellationToken.None));
    }

    internal static Reservation CreateTerminalReservation(
        string tenantId,
        Guid propertyId,
        Guid? reservationId = null)
    {
        Reservation reservation = Reservation.Create(
            reservationId ?? Guid.NewGuid(),
            tenantId,
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
            ReservationRetentionTestData.Now.AddDays(-401),
            expectedArrivalTime: null,
            expectedDepartureTime: null).Value;
        Assert.True(reservation.RejectAllocation(
            reservation.AllocationRequestId,
            ReservationAllocationRejection.UnitNotSellable,
            Guid.NewGuid(),
            ReservationRetentionTestData.Now.AddDays(-400))
            .IsSuccess);
        reservation.ClearDomainEvents();
        return reservation;
    }

    private static ReservationsDbContext CreateDbContext()
    {
        DbContextOptions<ReservationsDbContext> options =
            new DbContextOptionsBuilder<ReservationsDbContext>()
                .UseInMemoryDatabase(
                    $"reservation-retention-model-{Guid.NewGuid():N}")
                .Options;
        return new(options, new TestScopeContext("tenant-a"));
    }

    private static void AssertConstraintNames(
        IEntityType entityType,
        params string[] expectedNames)
    {
        string[] actualNames = entityType.GetCheckConstraints()
            .Select(constraint => constraint.Name!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            expectedNames.Order(StringComparer.Ordinal),
            actualNames);
    }

    private static void AssertIndex(
        IEntityType entityType,
        string databaseName,
        bool isUnique,
        params string[] propertyNames) => Assert.Contains(
        entityType.GetIndexes(),
        index => index.GetDatabaseName() == databaseName &&
            index.IsUnique == isUnique &&
            index.Properties.Select(property => property.Name)
                .SequenceEqual(propertyNames));

    private static void AssertForeignKey(
        IEntityType dependent,
        Type principalType,
        string constraintName)
    {
        IForeignKey foreignKey = Assert.Single(
            dependent.GetForeignKeys(),
            candidate => candidate.PrincipalEntityType.ClrType ==
                principalType);
        Assert.Equal(constraintName, foreignKey.GetConstraintName());
        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
    }

    internal sealed class TestScopeContext(string scopeId)
        : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => scopeId;
    }
}
