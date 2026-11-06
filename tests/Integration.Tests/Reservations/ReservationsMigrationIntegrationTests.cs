namespace Integration.Tests;

using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Domain.DataRights;
using BunkFy.Modules.Reservations.Persistence;
using Gma.Framework.Messaging.Infrastructure;
using Gma.Framework.Scoping;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class ReservationsMigrationIntegrationTests
{
    private const string InitialMigration = "20260710200251_InitialCreate";
    private const string CanonicalGuestLinksMigration = "20260712151534_AddCanonicalGuestLinks";
    private const string ArrivalRemindersMigration = "20260715123149_AddReservationArrivalReminders";
    private const string PreviousRestrictionEligibilityMigration =
        "20260722134132_AddInternationalMarketGate";
    private const string PreviousProcessingRestrictionMigration =
        "20260725190654_AddReservationDataRightsCorrectionReceipts";
    private const string PreviousDataHoldMigration =
        "20260725210703_AddReservationProcessingRestrictions";
    private const string PreviousAnonymisationMigration =
        "20260725224526_AddReservationDataHoldsAndEligibility";
    private const string PreviousAnonymisationRestoreMigration =
        "20260726002653_AddReservationAnonymisationOwnerProof";
    private const string PreviousManagementOperationsMigration =
        "20260806224147_AddReservationManagementOperations";
    private const string PreviousInventoryAmendmentOperationMigration =
        "20260806233308_ExtendReservationManagementOperationsForGuestDetails";

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Details_history_migration_backfills_existing_reservations()
    {
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("bunkfy_reservations_history_migration_tests")
            .Build();
        await postgreSql.StartAsync();

        Guid reservationId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        Guid propertyId = Guid.Parse("20000000-0000-0000-0000-000000000001");
        Guid allocationRequestId = Guid.Parse("30000000-0000-0000-0000-000000000001");
        Guid unitId = Guid.Parse("40000000-0000-0000-0000-000000000001");
        DateTimeOffset createdAtUtc = new(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);

        await using (ReservationsDbContext initial = CreateDbContext(postgreSql.GetConnectionString()))
        {
            await initial.Database.GetService<IMigrator>().MigrateAsync(InitialMigration);
            await initial.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO reservations.reservations (
                    "Id", "PropertyId", "AllocationRequestId", "Arrival", "Departure",
                    "PrimaryGuestName", "Email", "GuestCount", "Source", "Status",
                    "Version", "CreatedAtUtc", "ScopeId")
                VALUES (
                    {reservationId}, {propertyId}, {allocationRequestId},
                    {new DateOnly(2026, 8, 1)}, {new DateOnly(2026, 8, 3)},
                    {"Ada Guest"}, {"ada@example.test"}, {1}, {1}, {1},
                    {1L}, {createdAtUtc}, {"tenant-a"});

                INSERT INTO reservations.requested_inventory_units ("Id", "ScopeId", "ReservationId")
                VALUES ({unitId}, {"tenant-a"}, {reservationId});
                """);
        }

        Guid guestId = Guid.Parse("50000000-0000-0000-0000-000000000001");
        await using (ReservationsDbContext canonicalLinks = CreateDbContext(postgreSql.GetConnectionString()))
        {
            await canonicalLinks.Database.GetService<IMigrator>().MigrateAsync(CanonicalGuestLinksMigration);
            await canonicalLinks.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO reservations.reservation_guests (
                    "Id", "ScopeId", "ReservationId", "Role", "LinkedBy", "LinkedAtUtc")
                VALUES (
                    {guestId}, {"tenant-a"}, {reservationId}, {1}, {"staff:migration"}, {createdAtUtc});
                """);
        }

        await using ReservationsDbContext upgraded = CreateDbContext(postgreSql.GetConnectionString());
        await upgraded.Database.MigrateAsync();

        Reservation reservation = await upgraded.Reservations
            .Include(item => item.Guests)
            .SingleAsync(item => item.Id == reservationId);
        ReservationDetailsHistoryEntry history = await upgraded.ReservationDetailsHistory.SingleAsync(
            item => item.ReservationId == reservationId);

        Assert.Equal(1, reservation.DetailsRevision);
        Assert.Equal(ReservationDetailsChangeOrigin.System, reservation.LastDetailsChangeOrigin);
        Assert.Equal(createdAtUtc, reservation.LastDetailsChangedAtUtc);
        Assert.Equal(0, history.FromRevision);
        Assert.Equal(1, history.ToRevision);
        Assert.Equal(ReservationDetailsChangeOrigin.System, history.Origin);
        Assert.Contains("Ada Guest", history.AfterSnapshotJson, StringComparison.Ordinal);
        Assert.Contains(unitId.ToString(), history.AfterSnapshotJson, StringComparison.OrdinalIgnoreCase);
        BunkFy.Modules.Reservations.Domain.Entities.ReservationGuest guest = Assert.Single(reservation.Guests);
        Assert.Equal(guestId, guest.GuestId);
        Assert.True(guest.IsCurrent);
        Assert.Equal(1, guest.LinkVersion);
        Assert.Null(guest.UnlinkedAtUtc);
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Arrival_reminder_redaction_migration_neutralizes_legacy_guest_names()
    {
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("bunkfy_reservations_reminder_redaction_tests")
            .Build();
        await postgreSql.StartAsync();

        Guid eventId = Guid.Parse("60000000-0000-0000-0000-000000000001");
        DateTimeOffset occurredAtUtc = new(2026, 7, 16, 10, 30, 0, TimeSpan.Zero);
        const string eventType =
            "BunkFy.Modules.Reservations.Contracts.ReservationArrivalReminderDueIntegrationEvent";
        const string payload = /*lang=json,strict*/
            "{\"eventId\":\"60000000-0000-0000-0000-000000000001\",\"primaryGuestName\":\"Maya Chen\"}";

        await using (ReservationsDbContext previous = CreateDbContext(postgreSql.GetConnectionString()))
        {
            await previous.Database.GetService<IMigrator>().MigrateAsync(ArrivalRemindersMigration);
            await previous.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO reservations.outbox_messages (
                    "Id", "Subject", "EventType", "Version", "ScopeId", "OccurredAtUtc",
                    "CreatedAtUtc", "Payload", "Attempts")
                VALUES (
                    {eventId}, {"gma.reservations.reservation-arrival-reminder-due.v1"},
                    {eventType}, {1}, {"tenant-a"}, {occurredAtUtc}, {occurredAtUtc}, {payload}, {0});
                """);
        }

        await using ReservationsDbContext upgraded = CreateDbContext(postgreSql.GetConnectionString());
        await upgraded.Database.MigrateAsync();

        OutboxMessage message = await upgraded.OutboxMessages.SingleAsync(item => item.Id == eventId);
        Assert.DoesNotContain("Maya Chen", message.Payload, StringComparison.Ordinal);
        Assert.Contains("A guest", message.Payload, StringComparison.Ordinal);
        Assert.Equal(1, message.Version);
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Guest_restriction_eligibility_migration_starts_empty_and_fail_closed()
    {
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("bunkfy_reservations_guest_restriction_migration_tests")
            .Build();
        await postgreSql.StartAsync();

        Guid propertyId = Guid.Parse("70000000-0000-0000-0000-000000000001");
        Guid guestId = Guid.Parse("80000000-0000-0000-0000-000000000001");
        await using (ReservationsDbContext previous = CreateDbContext(postgreSql.GetConnectionString()))
        {
            await previous.Database.GetService<IMigrator>()
                .MigrateAsync(PreviousRestrictionEligibilityMigration);
            await previous.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO reservations.guest_profile_projection (
                    "ScopeId", "Id", "OriginPropertyId", "Status", "Version")
                VALUES (
                    {"tenant-a"}, {guestId}, {propertyId},
                    {(int)GuestStatus.Active}, {1L});
                """);
        }

        await using ReservationsDbContext upgraded = CreateDbContext(postgreSql.GetConnectionString());
        await upgraded.Database.MigrateAsync();

        Assert.True(await upgraded.GuestProfileProjections.AnyAsync(
            profile => profile.Id == guestId && profile.OriginPropertyId == propertyId));
        Assert.Empty(await upgraded.GuestProcessingRestrictionProjections.ToArrayAsync());
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Processing_restriction_migration_backfills_existing_reservations()
    {
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("bunkfy_reservation_processing_restriction_migration_tests")
            .Build();
        await postgreSql.StartAsync();

        Guid reservationId = Guid.NewGuid();
        Guid propertyId = Guid.NewGuid();
        DateTimeOffset createdAtUtc =
            new(2026, 7, 25, 18, 30, 0, TimeSpan.Zero);
        await using (ReservationsDbContext previous =
            CreateDbContext(postgreSql.GetConnectionString()))
        {
            await previous.Database.GetService<IMigrator>()
                .MigrateAsync(PreviousProcessingRestrictionMigration);
            await SeedReservationAtPreviousSchemaAsync(
                previous,
                reservationId,
                propertyId,
                createdAtUtc);
        }

        await using ReservationsDbContext upgraded =
            CreateDbContext(postgreSql.GetConnectionString());
        await upgraded.Database.MigrateAsync();

        ReservationProcessingRestrictionProjection projection =
            await upgraded.ProcessingRestrictionProjections
                .AsNoTracking()
                .SingleAsync(item =>
                    item.PropertyId == propertyId &&
                    item.ReservationId == reservationId);
        Assert.Equal(
            ReservationProcessingRestrictionContract.CurrentVersion,
            projection.ContractVersion);
        Assert.Equal(0, projection.Revision);
        Assert.Equal(0, projection.ActiveRestrictionCount);
        Assert.False(projection.IsRestricted);
        Assert.Equal(createdAtUtc, projection.LastTransitionAtUtc);
        Assert.True(projection.ProjectionOrdinal > 0);
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Data_hold_migration_preserves_reservations_and_current_upgrade_backfills_operation_lock()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_reservation_data_hold_migration_tests")
                .Build();
        await postgreSql.StartAsync();

        Guid reservationId = Guid.NewGuid();
        Guid propertyId = Guid.NewGuid();
        await using (ReservationsDbContext previous =
            CreateDbContext(postgreSql.GetConnectionString()))
        {
            await previous.Database.GetService<IMigrator>()
                .MigrateAsync(PreviousDataHoldMigration);
            await SeedReservationAtPreviousSchemaAsync(
                previous,
                reservationId,
                propertyId,
                new DateTimeOffset(
                    2026,
                    7,
                    25,
                    21,
                    0,
                    0,
                    TimeSpan.Zero));
        }

        await using ReservationsDbContext upgraded =
            CreateDbContext(postgreSql.GetConnectionString());
        await upgraded.Database.MigrateAsync();

        Assert.True(await upgraded.Reservations
            .AsNoTracking()
            .AnyAsync(reservation => reservation.Id == reservationId));
        Assert.Empty(await upgraded.DataHolds.AsNoTracking().ToArrayAsync());
        Assert.Empty(await upgraded.DataHoldReceipts
            .AsNoTracking()
            .ToArrayAsync());
        int operationLockCount = await upgraded.Database
            .SqlQueryRaw<int>(
                "SELECT COUNT(*)::int AS \"Value\" " +
                "FROM reservations.reservation_operation_locks")
            .SingleAsync();
        Assert.Equal(1, operationLockCount);
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Anonymisation_migration_preserves_existing_reservations_and_starts_empty()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_reservation_anonymisation_migration_tests")
                .Build();
        await postgreSql.StartAsync();

        Guid reservationId = Guid.NewGuid();
        Guid propertyId = Guid.NewGuid();
        Guid allocationRequestId = Guid.NewGuid();
        DateTimeOffset createdAtUtc =
            new(2026, 7, 25, 23, 30, 0, TimeSpan.Zero);
        await using (ReservationsDbContext previous =
            CreateDbContext(postgreSql.GetConnectionString()))
        {
            await previous.Database.GetService<IMigrator>()
                .MigrateAsync(PreviousAnonymisationMigration);
            await previous.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO reservations.reservations (
                    "Id", "PropertyId", "AllocationRequestId",
                    "Arrival", "Departure",
                    "PrimaryGuestName", "PrimaryGuestNameSearch",
                    "Email", "EmailSearch", "Phone", "PhoneSearch",
                    "GuestCount", "Source", "Status", "Version",
                    "DetailsRevision", "LastDetailsChangeOrigin",
                    "LastDetailsChangedAtUtc", "PendingDetailsChangeOrigin",
                    "CreatedAtUtc", "ScopeId")
                VALUES (
                    {reservationId}, {propertyId}, {allocationRequestId},
                    {new DateOnly(2026, 8, 1)}, {new DateOnly(2026, 8, 3)},
                    {"Existing Guest"}, {"EXISTING GUEST"},
                    {"existing@example.test"}, {"EXISTING@EXAMPLE.TEST"},
                    {"+44 20 1234 5678"}, {"+44 20 1234 5678"},
                    {1}, {1}, {5}, {2L},
                    {1L}, {1}, {createdAtUtc}, {0},
                    {createdAtUtc}, {"tenant-a"});
                """);
        }

        await using ReservationsDbContext upgraded =
            CreateDbContext(postgreSql.GetConnectionString());
        await upgraded.Database.MigrateAsync();

        Reservation reservation = await upgraded.Reservations
            .AsNoTracking()
            .SingleAsync(item => item.Id == reservationId);
        Assert.False(reservation.IsAnonymised);
        Assert.Null(reservation.AnonymisedAtUtc);
        Assert.Equal("Existing Guest", reservation.PrimaryGuestName);
        Assert.Empty(await upgraded.AnonymisationReceipts
            .AsNoTracking()
            .ToArrayAsync());
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Anonymisation_restore_migration_backfills_owner_proof_tombstones()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_reservation_restore_proof_migration_tests")
                .Build();
        await postgreSql.StartAsync();

        Guid reservationId = Guid.NewGuid();
        Guid propertyId = Guid.NewGuid();
        Guid receiptId = Guid.NewGuid();
        DateTimeOffset completedAtUtc =
            new(2026, 7, 26, 2, 30, 0, TimeSpan.Zero);
        const string receiptSha256 =
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        await using (ReservationsDbContext previous =
            CreateDbContext(postgreSql.GetConnectionString()))
        {
            await previous.Database.GetService<IMigrator>()
                .MigrateAsync(PreviousAnonymisationRestoreMigration);
            await SeedReservationAtPreviousSchemaAsync(
                previous,
                reservationId,
                propertyId,
                completedAtUtc.AddHours(-1));
            await previous.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE reservations.reservations
                SET "Version" = {2L},
                    "DetailsRevision" = {2L},
                    "IsAnonymised" = TRUE,
                    "AnonymisedAtUtc" = {completedAtUtc}
                WHERE "ScopeId" = {"tenant-a"} AND "Id" = {reservationId};

                INSERT INTO reservations.reservation_anonymisation_receipts (
                    "Id", "ContractVersion", "IdempotencyKey", "PropertyId",
                    "CaseId", "ApprovalRevision", "OperationRevision",
                    "ReservationId", "SelectedReservationVersion",
                    "ResultingReservationVersion", "SelectedDetailsRevision",
                    "ResultingDetailsRevision", "Disposition", "Reason",
                    "RedactedHistoryCount", "RemovedGuestLinkCount",
                    "ReducedExternalOperationCount", "SuppressedReminderCount",
                    "ApprovalEvidenceSha256", "PolicyEvidenceSha256",
                    "EventId", "ActorId", "CompletedAtUtc",
                    "CanonicalSha256", "ScopeId")
                VALUES (
                    {receiptId}, {1}, {Guid.NewGuid()}, {propertyId},
                    {Guid.NewGuid()}, {1L}, {2L},
                    {reservationId}, {1L}, {2L}, {1L}, {2L}, {1}, {1},
                    {1}, {0}, {0}, {0},
                    {receiptSha256}, {receiptSha256},
                    {Guid.NewGuid()}, {"staff:migration"}, {completedAtUtc},
                    {receiptSha256}, {"tenant-a"});
                """);
        }

        await using ReservationsDbContext upgraded =
            CreateDbContext(postgreSql.GetConnectionString());
        await upgraded.Database.MigrateAsync();

        ReservationAnonymisationTombstone tombstone =
            await upgraded.AnonymisationTombstones
                .AsNoTracking()
                .SingleAsync(item => item.Id == reservationId);
        Assert.Equal(
            ReservationAnonymisationTombstone.CurrentContractVersion,
            tombstone.ContractVersion);
        Assert.Equal(1, tombstone.Revision);
        Assert.Equal(propertyId, tombstone.PropertyId);
        Assert.Equal(receiptId, tombstone.OwnerReceiptId);
        Assert.Equal(receiptSha256, tombstone.OwnerReceiptSha256);
        Assert.Equal(2, tombstone.ResultingReservationVersion);
        Assert.Equal(2, tombstone.ResultingDetailsRevision);
        Assert.Equal(completedAtUtc, tombstone.CompletedAtUtc);
        Assert.Null(tombstone.LedgerEntryId);
        Assert.Null(tombstone.LastReplayedAtUtc);
        Assert.Empty(await upgraded.AnonymisationRestoreReceipts
            .AsNoTracking()
            .ToArrayAsync());
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Guest_details_operation_migration_preserves_lifecycle_rows_and_enforces_revision_shape()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_reservation_guest_details_operation_migration_tests")
                .Build();
        await postgreSql.StartAsync();

        Guid reservationId = Guid.NewGuid();
        Guid propertyId = Guid.NewGuid();
        Guid lifecycleOperationId = Guid.NewGuid();
        DateTimeOffset createdAtUtc =
            new(2026, 8, 6, 22, 45, 0, TimeSpan.Zero);
        await using (ReservationsDbContext previous =
            CreateDbContext(postgreSql.GetConnectionString()))
        {
            await previous.Database.GetService<IMigrator>()
                .MigrateAsync(PreviousManagementOperationsMigration);
            await SeedReservationAtPreviousSchemaAsync(
                previous,
                reservationId,
                propertyId,
                createdAtUtc);
            await previous.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO reservations.management_operations (
                    "Id", "ScopeId", "ReservationId", "PropertyId", "Kind",
                    "ExpectedVersion", "BusinessDate", "CreatedAtUtc")
                VALUES (
                    {lifecycleOperationId}, {"tenant-a"}, {reservationId}, {propertyId}, {2},
                    {7L}, {new DateOnly(2026, 8, 7)}, {createdAtUtc});
                """);
        }

        await using ReservationsDbContext upgraded =
            CreateDbContext(postgreSql.GetConnectionString());
        await upgraded.Database.MigrateAsync();

        int preservedLifecycleRows = await upgraded.Database.SqlQuery<int>($"""
                SELECT COUNT(*)::int AS "Value"
                FROM reservations.management_operations
                WHERE "Id" = {lifecycleOperationId}
                  AND "ExpectedVersion" = 7
                  AND "ExpectedDetailsRevision" IS NULL
                """)
            .SingleAsync();
        Assert.Equal(1, preservedLifecycleRows);

        Guid detailsOperationId = Guid.NewGuid();
        int inserted = await upgraded.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO reservations.management_operations (
                "Id", "ScopeId", "ReservationId", "PropertyId", "Kind",
                "ExpectedVersion", "ExpectedDetailsRevision", "BusinessDate", "CreatedAtUtc")
            VALUES (
                {detailsOperationId}, {"tenant-a"}, {reservationId}, {propertyId}, {5},
                NULL, {3L}, NULL, {createdAtUtc.AddMinutes(1)});
            """);
        Assert.Equal(1, inserted);

        PostgresException invalidShape = await Assert.ThrowsAsync<PostgresException>(() =>
            upgraded.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO reservations.management_operations (
                    "Id", "ScopeId", "ReservationId", "PropertyId", "Kind",
                    "ExpectedVersion", "ExpectedDetailsRevision", "BusinessDate", "CreatedAtUtc")
                VALUES (
                    {Guid.NewGuid()}, {"tenant-a"}, {reservationId}, {propertyId}, {5},
                    {7L}, NULL, NULL, {createdAtUtc.AddMinutes(2)});
                """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, invalidShape.SqlState);
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Inventory_amendment_operation_migration_preserves_existing_rows_and_enforces_fingerprint_shape()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_reservation_inventory_amendment_operation_migration_tests")
                .Build();
        await postgreSql.StartAsync();

        Guid reservationId = Guid.NewGuid();
        Guid propertyId = Guid.NewGuid();
        Guid guestDetailsOperationId = Guid.NewGuid();
        DateTimeOffset createdAtUtc =
            new(2026, 8, 7, 0, 15, 0, TimeSpan.Zero);
        await using (ReservationsDbContext previous =
            CreateDbContext(postgreSql.GetConnectionString()))
        {
            await previous.Database.GetService<IMigrator>()
                .MigrateAsync(PreviousInventoryAmendmentOperationMigration);
            await SeedReservationAtPreviousSchemaAsync(
                previous,
                reservationId,
                propertyId,
                createdAtUtc);
            await previous.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO reservations.management_operations (
                    "Id", "ScopeId", "ReservationId", "PropertyId", "Kind",
                    "ExpectedVersion", "ExpectedDetailsRevision", "BusinessDate", "CreatedAtUtc")
                VALUES (
                    {guestDetailsOperationId}, {"tenant-a"}, {reservationId}, {propertyId}, {5},
                    NULL, {1L}, NULL, {createdAtUtc});
                """);
        }

        await using ReservationsDbContext upgraded =
            CreateDbContext(postgreSql.GetConnectionString());
        await upgraded.Database.MigrateAsync();

        int preservedRows = await upgraded.Database.SqlQuery<int>($"""
                SELECT COUNT(*)::int AS "Value"
                FROM reservations.management_operations
                WHERE "Id" = {guestDetailsOperationId}
                  AND "ExpectedDetailsRevision" = 1
                  AND "RequestFingerprint" IS NULL
                """)
            .SingleAsync();
        Assert.Equal(1, preservedRows);

        string fingerprint = new('a', Reservation.RequestFingerprintLength);
        PostgresException orphanAmendment = await Assert.ThrowsAsync<
            PostgresException>(() =>
            upgraded.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO reservations.management_operations (
                    "Id", "ScopeId", "ReservationId", "PropertyId", "Kind",
                    "ExpectedVersion", "ExpectedDetailsRevision", "BusinessDate",
                    "CreatedAtUtc", "RequestFingerprint")
                VALUES (
                    {Guid.NewGuid()}, {"tenant-a"}, {reservationId}, {propertyId}, {6},
                    NULL, {1L}, NULL, {createdAtUtc.AddMinutes(1)}, {fingerprint});
                """));
        Assert.Equal(PostgresErrorCodes.RaiseException, orphanAmendment.SqlState);
        Assert.Contains(
            "stay-amendment parent requires exact child evidence",
            orphanAmendment.MessageText,
            StringComparison.Ordinal);

        PostgresException missingFingerprint = await Assert.ThrowsAsync<PostgresException>(() =>
            upgraded.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO reservations.management_operations (
                    "Id", "ScopeId", "ReservationId", "PropertyId", "Kind",
                    "ExpectedVersion", "ExpectedDetailsRevision", "BusinessDate",
                    "CreatedAtUtc", "RequestFingerprint")
                VALUES (
                    {Guid.NewGuid()}, {"tenant-a"}, {reservationId}, {propertyId}, {6},
                    NULL, {1L}, NULL, {createdAtUtc.AddMinutes(2)}, NULL);
                """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, missingFingerprint.SqlState);

        PostgresException mixedGuestDetailsShape = await Assert.ThrowsAsync<PostgresException>(() =>
            upgraded.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO reservations.management_operations (
                    "Id", "ScopeId", "ReservationId", "PropertyId", "Kind",
                    "ExpectedVersion", "ExpectedDetailsRevision", "BusinessDate",
                    "CreatedAtUtc", "RequestFingerprint")
                VALUES (
                    {Guid.NewGuid()}, {"tenant-a"}, {reservationId}, {propertyId}, {5},
                    NULL, {1L}, NULL, {createdAtUtc.AddMinutes(3)}, {fingerprint});
                """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, mixedGuestDetailsShape.SqlState);

        string uppercaseFingerprint = fingerprint.ToUpperInvariant();
        PostgresException nonCanonicalFingerprint = await Assert.ThrowsAsync<PostgresException>(() =>
            upgraded.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO reservations.management_operations (
                    "Id", "ScopeId", "ReservationId", "PropertyId", "Kind",
                    "ExpectedVersion", "ExpectedDetailsRevision", "BusinessDate",
                    "CreatedAtUtc", "RequestFingerprint")
                VALUES (
                    {Guid.NewGuid()}, {"tenant-a"}, {reservationId}, {propertyId}, {6},
                    NULL, {1L}, NULL, {createdAtUtc.AddMinutes(4)}, {uppercaseFingerprint});
                """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, nonCanonicalFingerprint.SqlState);
    }

    private static Task<int> SeedReservationAtPreviousSchemaAsync(
        ReservationsDbContext context,
        Guid reservationId,
        Guid propertyId,
        DateTimeOffset createdAtUtc)
    {
        Guid allocationRequestId = Guid.NewGuid();
        return context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO reservations.reservations (
                "Id", "PropertyId", "AllocationRequestId",
                "Arrival", "Departure",
                "PrimaryGuestName", "PrimaryGuestNameSearch",
                "Email", "EmailSearch", "Phone", "PhoneSearch",
                "GuestCount", "Source", "Status", "Version",
                "DetailsRevision", "LastDetailsChangeOrigin",
                "LastDetailsChangedAtUtc", "PendingDetailsChangeOrigin",
                "CreatedAtUtc", "ScopeId")
            VALUES (
                {reservationId}, {propertyId}, {allocationRequestId},
                {new DateOnly(2026, 8, 1)}, {new DateOnly(2026, 8, 3)},
                {"Existing Guest"}, {"EXISTING GUEST"},
                {"existing@example.test"}, {"EXISTING@EXAMPLE.TEST"},
                {"+44 20 1234 5678"}, {"+44 20 1234 5678"},
                {1}, {1}, {1}, {1L},
                {1L}, {1}, {createdAtUtc}, {0},
                {createdAtUtc}, {"tenant-a"});
            """);
    }

    private static ReservationsDbContext CreateDbContext(string connectionString)
    {
        DbContextOptions<ReservationsDbContext> options = new DbContextOptionsBuilder<ReservationsDbContext>()
            .UseNpgsql(connectionString, provider => provider
                .MigrationsAssembly(ReservationsMigrations.PostgreSqlAssembly)
                .MigrationsHistoryTable(ReservationsMigrations.HistoryTable, ReservationsMigrations.Schema))
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
