namespace Integration.Tests;

using BunkFy.Modules.Reservations.Persistence;
using Gma.Framework.Scoping;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class ReservationStayAmendmentMigrationIntegrationTests
{
    private const string PreviousMigration =
        "20260807092823_AddReservationGuestRecordLinkProcess";
    private const string CurrentMigration =
        "20260812011925_AddReservationStayAmendmentConvergence";
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private const string DowngradeEvidenceMessage =
        "stay-amendment convergence evidence prevents downgrade";

    private static readonly DateTimeOffset BaseNowUtc =
        new(2026, 8, 12, 1, 30, 0, TimeSpan.Zero);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Upgrade_nowait_failure_rolls_back_then_backfills_exact_and_unknown_rows()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_reservations_stay_amendment_upgrade_tests")
                .Build();
        await postgreSql.StartAsync();
        string connectionString = postgreSql.GetConnectionString();

        OperationSeed pendingA = CreateSeed(
            TenantA,
            'a',
            BaseNowUtc);
        OperationSeed historicalA = CreateSeed(
            TenantA,
            'b',
            BaseNowUtc.AddMinutes(1));
        OperationSeed pendingB = CreateSeed(
            TenantB,
            'c',
            BaseNowUtc.AddMinutes(2));
        OperationSeed adapterA = CreateSeed(
            TenantA,
            'd',
            BaseNowUtc.AddMinutes(3));

        await using (ReservationsDbContext previous =
            CreateDbContext(connectionString))
        {
            await previous.Database.GetService<IMigrator>()
                .MigrateAsync(PreviousMigration);
            await SeedLegacyOperationAsync(previous, pendingA, isPending: true);
            await SeedLegacyOperationAsync(previous, historicalA, isPending: false);
            await SeedLegacyOperationAsync(previous, pendingB, isPending: true);
            await SeedReservationAsync(previous, adapterA, isPending: true);
            await MarkPendingAsAdapterAsync(previous, adapterA);
        }

        await using NpgsqlConnection blocker = new(connectionString);
        await blocker.OpenAsync();
        await using NpgsqlTransaction blockerTransaction =
            await blocker.BeginTransactionAsync();
        await ExecuteAsync(
            blocker,
            blockerTransaction,
            "LOCK TABLE reservations.management_operations IN ROW EXCLUSIVE MODE;");

        await using (ReservationsDbContext blockedUpgrade =
            CreateDbContext(connectionString))
        {
            PostgresException unavailable = await AssertPostgresFailureAsync(
                PostgresErrorCodes.LockNotAvailable,
                () => blockedUpgrade.Database.GetService<IMigrator>()
                    .MigrateAsync(CurrentMigration));
            Assert.Contains("lock", unavailable.MessageText, StringComparison.OrdinalIgnoreCase);
        }

        Assert.True(await ScalarBoolAsync(
            connectionString,
            "SELECT to_regclass('reservations.stay_amendment_operations') IS NULL;"));
        Assert.Equal(
            0,
            await MigrationHistoryCountAsync(connectionString, CurrentMigration));
        Assert.Equal(
            3,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*)::int FROM reservations.management_operations WHERE \"Kind\" = 6;"));

        await blockerTransaction.RollbackAsync();

        await using (ReservationsDbContext upgraded =
            CreateDbContext(connectionString))
        {
            await upgraded.Database.GetService<IMigrator>()
                .MigrateAsync(CurrentMigration);
        }

        Assert.Equal(
            1,
            await MigrationHistoryCountAsync(connectionString, CurrentMigration));

        StayOperationRow exactA = await ReadStayOperationAsync(
            connectionString,
            pendingA);
        AssertExactLegacyBackfill(exactA, pendingA);

        StayOperationRow unknownA = await ReadStayOperationAsync(
            connectionString,
            historicalA);
        AssertHistoricalUnknownBackfill(unknownA, historicalA);

        StayOperationRow exactB = await ReadStayOperationAsync(
            connectionString,
            pendingB);
        AssertExactLegacyBackfill(exactB, pendingB);
        Assert.NotEqual(exactA.RequestFingerprint, exactB.RequestFingerprint);
        Assert.NotEqual(pendingA.OperationId, pendingB.OperationId);

        Assert.Equal(
            7,
            await ReservationVersionAsync(connectionString, pendingA));
        Assert.Equal(
            6,
            await ReservationVersionAsync(connectionString, historicalA));
        Assert.Equal(
            7,
            await ReservationVersionAsync(connectionString, pendingB));
        Assert.Equal(
            7,
            await ReservationVersionAsync(connectionString, adapterA));
        Assert.Equal(
            adapterA.OperationId,
            await PendingInventoryRequestIdAsync(connectionString, adapterA));
        Assert.Equal(0, await CountOperationRowsAsync(connectionString, adapterA));

        Assert.Equal(
            3,
            await ScalarIntAsync(
                connectionString,
            "SELECT COUNT(*)::int FROM reservations.stay_amendment_operations;"));

        string recoveryIndex = await ScalarStringAsync(
            connectionString,
            """
            SELECT indexdef
            FROM pg_indexes
            WHERE schemaname = 'reservations'
              AND tablename = 'stay_amendment_operations'
              AND indexdef LIKE '%\"Outcome\"%'
            LIMIT 1;
            """);
        Assert.Contains(
            "(\"ScopeId\", \"PropertyId\", \"Outcome\", \"UpdatedAtUtc\", \"Id\", \"ReservationId\")",
            recoveryIndex,
            StringComparison.Ordinal);
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Upgrade_version_overflow_aborts_atomically()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_reservations_stay_amendment_overflow_tests")
                .Build();
        await postgreSql.StartAsync();
        string connectionString = postgreSql.GetConnectionString();
        OperationSeed adapterPending = CreateSeed(
            TenantA,
            'v',
            BaseNowUtc);

        await using ReservationsDbContext context = CreateDbContext(connectionString);
        await context.Database.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        await SeedReservationAsync(context, adapterPending, isPending: true);
        await MarkPendingAsAdapterAsync(context, adapterPending);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE reservations.reservations
            SET "Version" = {long.MaxValue}
            WHERE "ScopeId" = {adapterPending.ScopeId}
              AND "Id" = {adapterPending.ReservationId};
            """);

        PostgresException rejected = await AssertPostgresFailureAsync(
            PostgresErrorCodes.RaiseException,
            () => context.Database.GetService<IMigrator>()
                .MigrateAsync(CurrentMigration));
        Assert.Contains(
            "reservation version overflow prevents stay-amendment backfill",
            rejected.MessageText,
            StringComparison.Ordinal);
        Assert.Equal(
            0,
            await MigrationHistoryCountAsync(connectionString, CurrentMigration));
        Assert.True(await ScalarBoolAsync(
            connectionString,
            "SELECT to_regclass('reservations.stay_amendment_operations') IS NULL;"));
        Assert.False(await ScalarBoolAsync(
            connectionString,
            """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.columns
                WHERE table_schema = 'reservations'
                  AND table_name = 'reservations'
                  AND column_name = 'PendingInventoryAmendmentRequestId');
            """));
        Assert.Equal(
            long.MaxValue,
            await ReservationVersionAsync(connectionString, adapterPending));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Upgrade_rejects_nonexact_active_legacy_and_rolls_back_history()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_reservations_stay_amendment_bad_legacy_tests")
                .Build();
        await postgreSql.StartAsync();
        string connectionString = postgreSql.GetConnectionString();
        OperationSeed malformedActive = CreateSeed(
            TenantA,
            '0',
            BaseNowUtc);

        await using ReservationsDbContext context = CreateDbContext(connectionString);
        await context.Database.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        await SeedLegacyOperationAsync(context, malformedActive, isPending: true);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE reservations.reservations
            SET "PendingDetailsActorId" = NULL
            WHERE "ScopeId" = {malformedActive.ScopeId}
              AND "Id" = {malformedActive.ReservationId};
            """);

        PostgresException rejected = await AssertPostgresFailureAsync(
            PostgresErrorCodes.RaiseException,
            () => context.Database.GetService<IMigrator>()
                .MigrateAsync(CurrentMigration));
        Assert.Contains(
            "nonexact active Staff stay amendment prevents upgrade",
            rejected.MessageText,
            StringComparison.Ordinal);
        Assert.Equal(
            0,
            await MigrationHistoryCountAsync(connectionString, CurrentMigration));
        Assert.True(await ScalarBoolAsync(
            connectionString,
            "SELECT to_regclass('reservations.stay_amendment_operations') IS NULL;"));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*)::int FROM reservations.management_operations WHERE \"Kind\" = 6;"));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Post_upgrade_predecessor_writer_orders_fail_and_roll_back()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_reservations_stay_amendment_old_writer_tests")
                .Build();
        await postgreSql.StartAsync();
        string connectionString = postgreSql.GetConnectionString();
        OperationSeed updateFirst = CreateSeed(TenantA, 'u', BaseNowUtc);
        OperationSeed parentFirst = CreateSeed(
            TenantA,
            'p',
            BaseNowUtc.AddMinutes(1));

        await using (ReservationsDbContext previous =
            CreateDbContext(connectionString))
        {
            await previous.Database.GetService<IMigrator>()
                .MigrateAsync(PreviousMigration);
            await SeedReservationAsync(previous, updateFirst, isPending: false);
            await SeedReservationAsync(previous, parentFirst, isPending: false);
            await previous.Database.GetService<IMigrator>()
                .MigrateAsync(CurrentMigration);
        }

        await AssertPredecessorWriterRejectedAsync(
            connectionString,
            updateFirst,
            insertParentFirst: false);
        await AssertPredecessorWriterRejectedAsync(
            connectionString,
            parentFirst,
            insertParentFirst: true);

        Assert.Equal(0, await CountOperationRowsAsync(connectionString, updateFirst));
        Assert.Equal(0, await CountOperationRowsAsync(connectionString, parentFirst));
        Assert.True(await HasNoPendingIdentifiersAsync(connectionString, updateFirst));
        Assert.True(await HasNoPendingIdentifiersAsync(connectionString, parentFirst));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Pending_staff_reservation_requires_exact_child_on_update_and_insert()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_reservations_stay_amendment_live_evidence_tests")
                .Build();
        await postgreSql.StartAsync();
        string connectionString = postgreSql.GetConnectionString();
        OperationSeed missingUpdate = CreateSeed(
            TenantA,
            'e',
            BaseNowUtc,
            inventoryRequestId: Guid.NewGuid(),
            schemaVersion: 2);
        OperationSeed forgedInsert = CreateSeed(
            TenantA,
            'f',
            BaseNowUtc.AddMinutes(1),
            inventoryRequestId: Guid.NewGuid(),
            schemaVersion: 2);

        await using ReservationsDbContext context = CreateDbContext(connectionString);
        await context.Database.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        await SeedReservationAsync(context, missingUpdate, isPending: false);
        await context.Database.GetService<IMigrator>().MigrateAsync(CurrentMigration);

        PostgresException updateRejected = await AssertPostgresFailureAsync(
            PostgresErrorCodes.RaiseException,
            () => SetPendingReservationAsync(context, missingUpdate));
        Assert.Contains(
            "Staff stay-amendment pending state requires durable evidence",
            updateRejected.MessageText,
            StringComparison.Ordinal);
        Assert.True(await HasNoPendingIdentifiersAsync(
            connectionString,
            missingUpdate));

        PostgresException insertRejected = await AssertPostgresFailureAsync(
            PostgresErrorCodes.RaiseException,
            () => InsertCurrentPendingReservationAsync(context, forgedInsert));
        Assert.Contains(
            "Staff stay-amendment pending state requires durable evidence",
            insertRejected.MessageText,
            StringComparison.Ordinal);
        Assert.Equal(
            0,
            await CountReservationRowsAsync(connectionString, forgedInsert));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Dual_identifiers_allow_local_collisions_and_reject_global_inventory_collisions()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_reservations_stay_amendment_dual_id_tests")
                .Build();
        await postgreSql.StartAsync();
        string connectionString = postgreSql.GetConnectionString();
        Guid sharedLocalId = Guid.NewGuid();
        OperationSeed localA = CreateSeed(
            TenantA,
            'l',
            BaseNowUtc,
            operationId: sharedLocalId,
            inventoryRequestId: Guid.NewGuid(),
            schemaVersion: 2);
        OperationSeed localB = CreateSeed(
            TenantA,
            'm',
            BaseNowUtc.AddMinutes(1),
            operationId: sharedLocalId,
            inventoryRequestId: Guid.NewGuid(),
            schemaVersion: 2);
        Guid duplicatedInventoryId = Guid.NewGuid();
        OperationSeed inventoryA = CreateSeed(
            TenantA,
            'n',
            BaseNowUtc.AddMinutes(2),
            inventoryRequestId: duplicatedInventoryId,
            schemaVersion: 2);
        OperationSeed inventoryB = CreateSeed(
            TenantA,
            'o',
            BaseNowUtc.AddMinutes(3),
            inventoryRequestId: duplicatedInventoryId,
            schemaVersion: 2);
        OperationSeed[] seeds = [localA, localB, inventoryA, inventoryB];

        await using ReservationsDbContext context = CreateDbContext(connectionString);
        await context.Database.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        foreach (OperationSeed seed in seeds)
        {
            await SeedReservationAsync(context, seed, isPending: false);
        }
        await context.Database.GetService<IMigrator>().MigrateAsync(CurrentMigration);

        foreach (OperationSeed seed in new[] { localA, localB })
        {
            await SetPendingAndInsertOperationPairAsync(
                context,
                seed,
                kind: 7,
                StayInsert.Pending(seed, schemaVersion: 2));
            StayOperationRow row = await ReadStayOperationAsync(
                connectionString,
                seed);
            Assert.Equal(seed.InventoryRequestId, row.InventoryRequestId);
        }
        Assert.Equal(localA.OperationId, localB.OperationId);
        Assert.NotEqual(localA.InventoryRequestId, localB.InventoryRequestId);

        await SetPendingAndInsertOperationPairAsync(
            context,
            inventoryA,
            kind: 7,
            StayInsert.Pending(inventoryA, schemaVersion: 2));
        PostgresException reservationDuplicate = await AssertPostgresFailureAsync(
            PostgresErrorCodes.UniqueViolation,
            () => SetPendingReservationAsync(context, inventoryB));
        Assert.Equal(
            "IX_reservations_PendingInventoryAmendmentRequestId",
            reservationDuplicate.ConstraintName);
        Assert.True(await HasNoPendingIdentifiersAsync(
            connectionString,
            inventoryB));

        await CompleteRejectedAsync(context, inventoryA);
        PostgresException childDuplicate = await AssertPostgresFailureAsync(
            PostgresErrorCodes.UniqueViolation,
            () => SetPendingAndInsertOperationPairAsync(
                context,
                inventoryB,
                kind: 7,
                StayInsert.Pending(inventoryB, schemaVersion: 2)));
        Assert.Equal(
            "IX_stay_amendment_operations_InventoryRequestId",
            childDuplicate.ConstraintName);
        Assert.Equal(0, await CountOperationRowsAsync(connectionString, inventoryB));
        Assert.True(await HasNoPendingIdentifiersAsync(connectionString, inventoryB));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Protocol_enforces_coordinates_shapes_transitions_immutability_and_deletion()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_reservations_stay_amendment_protocol_tests")
                .Build();
        await postgreSql.StartAsync();
        string connectionString = postgreSql.GetConnectionString();

        OperationSeed coordinatesV2 = CreateSeed(
            TenantA,
            '1',
            BaseNowUtc,
            schemaVersion: 2);
        OperationSeed coordinatesV1 = CreateSeed(
            TenantA,
            '2',
            BaseNowUtc.AddMinutes(1));
        OperationSeed malformed = CreateSeed(
            TenantA,
            '3',
            BaseNowUtc.AddMinutes(2));
        OperationSeed apply = CreateSeed(
            TenantA,
            '4',
            BaseNowUtc.AddMinutes(3),
            schemaVersion: 2);
        OperationSeed reject = CreateSeed(
            TenantA,
            '5',
            BaseNowUtc.AddMinutes(4),
            schemaVersion: 2);
        OperationSeed deleteParent = CreateSeed(
            TenantA,
            '6',
            BaseNowUtc.AddMinutes(5),
            schemaVersion: 2);
        OperationSeed deleteReservation = CreateSeed(
            TenantA,
            '7',
            BaseNowUtc.AddMinutes(6),
            schemaVersion: 2);
        OperationSeed deleteChild = CreateSeed(
            TenantA,
            '8',
            BaseNowUtc.AddMinutes(7),
            schemaVersion: 2);
        OperationSeed tenantB = CreateSeed(
            TenantB,
            '9',
            BaseNowUtc.AddMinutes(8));
        OperationSeed forged = CreateSeed(
            TenantA,
            'a',
            BaseNowUtc.AddMinutes(9),
            schemaVersion: 2);
        OperationSeed deploymentFence = CreateSeed(
            TenantA,
            'c',
            BaseNowUtc.AddMinutes(10),
            schemaVersion: 2);
        OperationSeed[] seeds =
        [
            coordinatesV2,
            coordinatesV1,
            malformed,
            apply,
            reject,
            deleteParent,
            deleteReservation,
            deleteChild,
            tenantB,
            forged,
            deploymentFence
        ];

        await using (ReservationsDbContext previous =
            CreateDbContext(connectionString))
        {
            await previous.Database.GetService<IMigrator>()
                .MigrateAsync(PreviousMigration);
            foreach (OperationSeed seed in seeds)
            {
                await SeedReservationAsync(previous, seed, isPending: false);
            }
        }

        await using ReservationsDbContext current = CreateDbContext(connectionString);
        await current.Database.GetService<IMigrator>()
            .MigrateAsync(CurrentMigration);

        StayInsert validV2 = StayInsert.Pending(coordinatesV2, schemaVersion: 2);
        StayInsert validV1 = StayInsert.Pending(coordinatesV1, schemaVersion: 1);
        await AssertPostgresFailureAsync(
            PostgresErrorCodes.RaiseException,
            () => InsertOperationPairAsync(
                current,
                coordinatesV2,
                kind: 7,
                validV2 with { RequestSchemaVersion = 1 }));
        await AssertPostgresFailureAsync(
            PostgresErrorCodes.RaiseException,
            () => InsertOperationPairAsync(
                current,
                coordinatesV1,
                kind: 6,
                validV1 with { RequestSchemaVersion = 2 }));
        OperationSeed forgedLegacyCoordinates = coordinatesV1 with
        {
            RequestFingerprint = new string('e', 64)
        };
        PostgresException forgedLegacyFingerprint = await AssertPostgresFailureAsync(
            PostgresErrorCodes.RaiseException,
            () => InsertOperationPairAsync(
                current,
                forgedLegacyCoordinates,
                kind: 6,
                validV1 with
                {
                    RequestFingerprint = forgedLegacyCoordinates.RequestFingerprint
                }));
        Assert.Contains(
            "request fingerprint does not match",
            forgedLegacyFingerprint.MessageText,
            StringComparison.Ordinal);
        OperationSeed forgedCoordinates = forged with
        {
            RequestFingerprint = new string('f', 64)
        };
        PostgresException forgedFingerprint = await AssertPostgresFailureAsync(
            PostgresErrorCodes.RaiseException,
            () => InsertOperationPairAsync(
                current,
                forgedCoordinates,
                kind: 7,
                StayInsert.Pending(forged, schemaVersion: 2) with
                {
                    RequestFingerprint = forgedCoordinates.RequestFingerprint
                }));
        Assert.Contains(
            "request fingerprint does not match",
            forgedFingerprint.MessageText,
            StringComparison.Ordinal);
        await AssertPostgresFailureAsync(
            PostgresErrorCodes.RaiseException,
            () => InsertOperationPairAsync(
                current,
                coordinatesV1,
                kind: 6,
                validV1 with { PropertyId = Guid.NewGuid() }));
        await AssertPostgresFailureAsync(
            PostgresErrorCodes.RaiseException,
            () => InsertOperationPairAsync(
                current,
                coordinatesV1,
                kind: 6,
                validV1 with
                {
                    ExpectedDetailsRevision =
                        coordinatesV1.ExpectedDetailsRevision + 1
                }));
        await SetPendingAndInsertOperationPairAsync(
            current,
            tenantB,
            kind: 6,
            StayInsert.Pending(tenantB, schemaVersion: 1));
        await AssertPostgresFailureAsync(
            PostgresErrorCodes.RaiseException,
            () => current.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE reservations.stay_amendment_operations
                SET "ScopeId" = {TenantA}
                WHERE "ScopeId" = {tenantB.ScopeId}
                  AND "ReservationId" = {tenantB.ReservationId}
                  AND "Id" = {tenantB.OperationId};
                """));
        await SetPendingAndInsertOperationPairAsync(
            current,
            coordinatesV2,
            kind: 7,
            validV2);

        await SetPendingAndInsertOperationPairAsync(
            current,
            deploymentFence,
            kind: 7,
            StayInsert.Pending(deploymentFence, schemaVersion: 2));
        PostgresException terminalOrphan = await AssertPostgresFailureAsync(
            PostgresErrorCodes.CheckViolation,
            () => ClearPendingReservationAsOldHandlerAsync(
                current,
                deploymentFence));
        Assert.Equal(
            "CK_reservations_pending_inventory_request",
            terminalOrphan.ConstraintName);

        await AssertPostgresFailureAsync(
            PostgresErrorCodes.RaiseException,
            () => current.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE reservations.management_operations
                SET "RequestFingerprint" = {new string('d', 64)}
                WHERE "ScopeId" = {coordinatesV2.ScopeId}
                  AND "ReservationId" = {coordinatesV2.ReservationId}
                  AND "Id" = {coordinatesV2.OperationId};
                """));

        StayInsert validMalformed = StayInsert.Pending(malformed, schemaVersion: 1);
        await AssertPostgresFailureAsync(
            PostgresErrorCodes.CheckViolation,
            () => InsertOperationPairAsync(
                current,
                malformed,
                kind: 6,
                validMalformed with { RequestedBy = " staff:operator " }));
        await AssertPostgresFailureAsync(
            PostgresErrorCodes.CheckViolation,
            () => InsertOperationPairAsync(
                current,
                malformed,
                kind: 6,
                validMalformed with
                {
                    TargetDeparture = validMalformed.TargetArrival
                }));
        string reverseUnits = string.Join(
            ',',
            malformed.TargetInventoryUnitIdsValue
                .OrderDescending()
                .Select(id => id.ToString("N")));
        PostgresException noncanonical = await AssertPostgresFailureAsync(
            PostgresErrorCodes.RaiseException,
            () => InsertOperationPairAsync(
                current,
                malformed,
                kind: 6,
                validMalformed with
                {
                    TargetInventoryUnitIds = reverseUnits
                }));
        Assert.Contains(
            "target units must be canonical",
            noncanonical.MessageText,
            StringComparison.Ordinal);
        await AssertPostgresFailureAsync(
            PostgresErrorCodes.CheckViolation,
            () => InsertOperationPairAsync(
                current,
                malformed,
                kind: 6,
                validMalformed with
                {
                    TargetInventoryUnitIds =
                        validMalformed.TargetInventoryUnitIds!.ToUpperInvariant()
                }));
        await AssertPostgresFailureAsync(
            PostgresErrorCodes.RaiseException,
            () => InsertOperationPairAsync(
                current,
                malformed,
                kind: 6,
                validMalformed with
                {
                    CompletedAtUtc = malformed.CreatedAtUtc,
                    ResultingDetailsRevision = 4,
                    ResultingReservationVersion = 7,
                    ResultingAllocationVersion = 3
                }));

        await SetPendingAndInsertOperationPairAsync(
            current,
            apply,
            kind: 7,
            StayInsert.Pending(apply, schemaVersion: 2));
        await AssertPostgresFailureAsync(
            PostgresErrorCodes.RaiseException,
            () => current.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE reservations.stay_amendment_operations
                SET "TargetDeparture" = {apply.TargetDeparture.AddDays(1)}
                WHERE "ScopeId" = {apply.ScopeId}
                  AND "ReservationId" = {apply.ReservationId}
                  AND "Id" = {apply.OperationId};
                """));

        await AssertPostgresFailureAsync(
            PostgresErrorCodes.RaiseException,
            () => current.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE reservations.stay_amendment_operations
                SET "Outcome" = 4,
                    "OperationVersion" = 2,
                    "UpdatedAtUtc" = {apply.CreatedAtUtc.AddMinutes(5)}
                WHERE "ScopeId" = {apply.ScopeId}
                  AND "ReservationId" = {apply.ReservationId}
                  AND "Id" = {apply.OperationId};
                """));

        await AssertPostgresFailureAsync(
            PostgresErrorCodes.RaiseException,
            () => current.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE reservations.stay_amendment_operations
                SET "OperationVersion" = 2,
                    "ReconciliationCount" = 1,
                    "LastReconciledAtUtc" = {apply.CreatedAtUtc.AddMinutes(4)},
                    "LastReconciledBy" = {"staff:recovery"},
                    "UpdatedAtUtc" = {apply.CreatedAtUtc.AddMinutes(4)}
                WHERE "ScopeId" = {apply.ScopeId}
                  AND "ReservationId" = {apply.ReservationId}
                  AND "Id" = {apply.OperationId};
                """));
        await AssertPostgresFailureAsync(
            PostgresErrorCodes.RaiseException,
            () => current.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE reservations.stay_amendment_operations
                SET "OperationVersion" = 2,
                    "UpdatedAtUtc" = {apply.CreatedAtUtc.AddMinutes(1)}
                WHERE "ScopeId" = {apply.ScopeId}
                  AND "ReservationId" = {apply.ReservationId}
                  AND "Id" = {apply.OperationId};
                """));

        PostgresException missingReservationAdvance =
            await AssertPostgresFailureAsync(
                PostgresErrorCodes.RaiseException,
                () => current.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE reservations.stay_amendment_operations
                    SET "OperationVersion" = 2,
                        "ReconciliationCount" = 1,
                        "LastReconciledAtUtc" = {apply.CreatedAtUtc.AddMinutes(5)},
                        "LastReconciledBy" = {"staff:recovery"},
                        "UpdatedAtUtc" = {apply.CreatedAtUtc.AddMinutes(5)}
                    WHERE "ScopeId" = {apply.ScopeId}
                      AND "ReservationId" = {apply.ReservationId}
                      AND "Id" = {apply.OperationId};
                    """));
        Assert.Contains(
            "graph change requires a reservation version advance",
            missingReservationAdvance.MessageText,
            StringComparison.Ordinal);

        PostgresException nonadvancingReservationUpdate =
            await AssertPostgresFailureAsync(
                PostgresErrorCodes.RaiseException,
                () => current.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE reservations.reservations
                    SET "Version" = "Version"
                    WHERE "ScopeId" = {apply.ScopeId}
                      AND "Id" = {apply.ReservationId};
                    """));
        Assert.Contains(
            "reservation updates require a version advance",
            nonadvancingReservationUpdate.MessageText,
            StringComparison.Ordinal);

        await using (Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction
            reconciliation = await current.Database.BeginTransactionAsync())
        {
            int advanced = await current.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE reservations.reservations
                SET "Version" = "Version" + 1,
                    "UpdatedAtUtc" = {apply.CreatedAtUtc.AddMinutes(5)}
                WHERE "ScopeId" = {apply.ScopeId}
                  AND "Id" = {apply.ReservationId};
                """);
            int reconciled = await current.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE reservations.stay_amendment_operations
                SET "OperationVersion" = 2,
                    "ReconciliationCount" = 1,
                    "LastReconciledAtUtc" = {apply.CreatedAtUtc.AddMinutes(5)},
                    "LastReconciledBy" = {"staff:recovery"},
                    "UpdatedAtUtc" = {apply.CreatedAtUtc.AddMinutes(5)}
                WHERE "ScopeId" = {apply.ScopeId}
                  AND "ReservationId" = {apply.ReservationId}
                  AND "Id" = {apply.OperationId};
                """);
            Assert.Equal(1, advanced);
            Assert.Equal(1, reconciled);
            await reconciliation.CommitAsync();
        }

        await CompleteAppliedAsync(current, apply);
        await AssertPostgresFailureAsync(
            PostgresErrorCodes.RaiseException,
            () => current.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE reservations.stay_amendment_operations
                SET "RequestedBy" = {"staff:other"}
                WHERE "ScopeId" = {apply.ScopeId}
                  AND "ReservationId" = {apply.ReservationId}
                  AND "Id" = {apply.OperationId};
                """));

        await SetPendingAndInsertOperationPairAsync(
            current,
            reject,
            kind: 7,
            StayInsert.Pending(reject, schemaVersion: 2));
        PostgresException orphanedTerminal = await AssertPostgresFailureAsync(
            PostgresErrorCodes.RaiseException,
            () => current.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE reservations.stay_amendment_operations
                SET "Outcome" = 3,
                    "OperationVersion" = 2,
                    "CompletedAtUtc" = {reject.CreatedAtUtc.AddMinutes(1)},
                    "ResultingDetailsRevision" = {3L},
                    "ResultingReservationVersion" = {6L},
                    "RejectionCode" = {9},
                    "UpdatedAtUtc" = {reject.CreatedAtUtc.AddMinutes(1)}
                WHERE "ScopeId" = {reject.ScopeId}
                  AND "ReservationId" = {reject.ReservationId}
                  AND "Id" = {reject.OperationId};
                """));
        Assert.Contains(
            "terminal stay-amendment result does not match",
            orphanedTerminal.MessageText,
            StringComparison.Ordinal);
        await CompleteRejectedAsync(current, reject);
        await AssertPostgresFailureAsync(
            PostgresErrorCodes.RaiseException,
            () => current.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE reservations.stay_amendment_operations
                SET "Outcome" = 2,
                    "RejectionCode" = NULL
                WHERE "ScopeId" = {reject.ScopeId}
                  AND "ReservationId" = {reject.ReservationId}
                  AND "Id" = {reject.OperationId};
                """));

        foreach (OperationSeed seed in new[]
                 {
                     deleteParent,
                     deleteReservation,
                     deleteChild
                 })
        {
            await SetPendingAndInsertOperationPairAsync(
                current,
                seed,
                kind: 7,
                StayInsert.Pending(seed, schemaVersion: 2));
        }
        await AssertPostgresFailureAsync(
            PostgresErrorCodes.RaiseException,
            () => DeleteChildAsync(current, deleteParent));
        await AssertPostgresFailureAsync(
            PostgresErrorCodes.RaiseException,
            () => DeleteParentAsync(current, deleteParent));
        await AssertPostgresFailureAsync(
            PostgresErrorCodes.RaiseException,
            () => DeleteReservationAsync(current, deleteReservation));

        Guid destroyOperationId = await BeginTenantDestructionAsync(
            current,
            TenantA,
            BaseNowUtc.AddHours(1));

        await ExecuteDestroyDeleteAsync(
            connectionString,
            destroyOperationId,
            """
            DELETE FROM reservations.stay_amendment_operations
            WHERE "ScopeId" = @scopeId
              AND "ReservationId" = @reservationId
              AND "Id" = @operationId;
            """,
            deleteChild);
        Assert.Equal(1, await CountOperationRowsAsync(connectionString, deleteChild));
        Assert.Equal(1, await CountReservationRowsAsync(connectionString, deleteChild));
        await AssertDestroyDeleteRejectedAsync(
            connectionString,
            destroyOperationId,
            """
            DELETE FROM reservations.management_operations
            WHERE "ScopeId" = @scopeId
              AND "ReservationId" = @reservationId
              AND "Id" = @operationId;
            """,
            tenantB);

        await ExecuteDestroyDeleteAsync(
            connectionString,
            destroyOperationId,
            """
            DELETE FROM reservations.management_operations
            WHERE "ScopeId" = @scopeId
              AND "ReservationId" = @reservationId
              AND "Id" = @operationId;
            """,
            deleteParent);
        Assert.Equal(0, await CountOperationRowsAsync(connectionString, deleteParent));
        Assert.Equal(
            1,
            await CountReservationRowsAsync(connectionString, deleteParent));

        await ExecuteDestroyDeleteAsync(
            connectionString,
            destroyOperationId,
            """
            DELETE FROM reservations.reservations
            WHERE "ScopeId" = @scopeId
              AND "Id" = @reservationId;
            """,
            deleteReservation);
        Assert.Equal(
            0,
            await CountReservationRowsAsync(connectionString, deleteReservation));
        Assert.Equal(
            0,
            await CountOperationRowsAsync(connectionString, deleteReservation));
        Assert.Equal(2, await CountOperationRowsAsync(connectionString, tenantB));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Downgrade_allows_only_historical_unknown_legacy_evidence()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_reservations_stay_amendment_safe_down_tests")
                .Build();
        await postgreSql.StartAsync();
        string connectionString = postgreSql.GetConnectionString();
        OperationSeed historical = CreateSeed(TenantA, '9', BaseNowUtc);

        await using ReservationsDbContext context = CreateDbContext(connectionString);
        await context.Database.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        await SeedLegacyOperationAsync(context, historical, isPending: false);
        await context.Database.GetService<IMigrator>().MigrateAsync(CurrentMigration);

        StayOperationRow unknown = await ReadStayOperationAsync(
            connectionString,
            historical);
        AssertHistoricalUnknownBackfill(unknown, historical);

        await context.Database.GetService<IMigrator>().MigrateAsync(PreviousMigration);

        Assert.True(await ScalarBoolAsync(
            connectionString,
            "SELECT to_regclass('reservations.stay_amendment_operations') IS NULL;"));
        Assert.Equal(
            0,
            await MigrationHistoryCountAsync(connectionString, CurrentMigration));
        Assert.Equal(
            1,
            await ScalarIntAsync(
                connectionString,
                "SELECT COUNT(*)::int FROM reservations.management_operations;"));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Downgrade_rejects_exact_legacy_evidence_and_preserves_history()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_reservations_stay_amendment_exact_down_tests")
                .Build();
        await postgreSql.StartAsync();
        string connectionString = postgreSql.GetConnectionString();
        OperationSeed exact = CreateSeed(TenantA, 'a', BaseNowUtc);

        await using ReservationsDbContext context = CreateDbContext(connectionString);
        await context.Database.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        await SeedLegacyOperationAsync(context, exact, isPending: true);
        await context.Database.GetService<IMigrator>().MigrateAsync(CurrentMigration);

        PostgresException blocked = await AssertPostgresFailureAsync(
            PostgresErrorCodes.RaiseException,
            () => context.Database.GetService<IMigrator>()
                .MigrateAsync(PreviousMigration));
        Assert.Contains(
            DowngradeEvidenceMessage,
            blocked.MessageText,
            StringComparison.Ordinal);
        Assert.Equal(
            1,
            await MigrationHistoryCountAsync(connectionString, CurrentMigration));
        Assert.False(await ScalarBoolAsync(
            connectionString,
            "SELECT to_regclass('reservations.stay_amendment_operations') IS NULL;"));
        Assert.Equal(2, await CountOperationRowsAsync(connectionString, exact));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Downgrade_rejects_distinct_adapter_pending_identifier()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_reservations_stay_amendment_adapter_down_tests")
                .Build();
        await postgreSql.StartAsync();
        string connectionString = postgreSql.GetConnectionString();
        OperationSeed adapterPending = CreateSeed(
            TenantA,
            'i',
            BaseNowUtc,
            inventoryRequestId: Guid.NewGuid(),
            schemaVersion: 2);

        await using ReservationsDbContext context = CreateDbContext(connectionString);
        await context.Database.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        await SeedReservationAsync(context, adapterPending, isPending: false);
        await context.Database.GetService<IMigrator>().MigrateAsync(CurrentMigration);
        await SetPendingAndInsertOperationPairAsync(
            context,
            adapterPending,
            kind: 7,
            StayInsert.Pending(adapterPending, schemaVersion: 2));
        Assert.NotEqual(
            adapterPending.OperationId,
            adapterPending.InventoryRequestId);

        Guid destroyOperationId = await BeginTenantDestructionAsync(
            context,
            adapterPending.ScopeId,
            BaseNowUtc.AddMinutes(1));
        await ExecuteDestroyDeleteAsync(
            connectionString,
            destroyOperationId,
            """
            DELETE FROM reservations.management_operations
            WHERE "ScopeId" = @scopeId
              AND "ReservationId" = @reservationId
              AND "Id" = @operationId;
            """,
            adapterPending);
        Assert.Equal(0, await CountOperationRowsAsync(connectionString, adapterPending));

        PostgresException blocked = await AssertPostgresFailureAsync(
            PostgresErrorCodes.RaiseException,
            () => context.Database.GetService<IMigrator>()
                .MigrateAsync(PreviousMigration));
        Assert.Contains(
            DowngradeEvidenceMessage,
            blocked.MessageText,
            StringComparison.Ordinal);
        Assert.Equal(
            1,
            await MigrationHistoryCountAsync(connectionString, CurrentMigration));
        Assert.False(await ScalarBoolAsync(
            connectionString,
            "SELECT to_regclass('reservations.stay_amendment_operations') IS NULL;"));
        Assert.False(await HasNoPendingIdentifiersAsync(
            connectionString,
            adapterPending));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Downgrade_rejects_kind_seven_and_reconciled_evidence()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_reservations_stay_amendment_v2_down_tests")
                .Build();
        await postgreSql.StartAsync();
        string connectionString = postgreSql.GetConnectionString();
        OperationSeed operation = CreateSeed(
            TenantA,
            'b',
            BaseNowUtc,
            schemaVersion: 2);

        await using ReservationsDbContext context = CreateDbContext(connectionString);
        await context.Database.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        await SeedReservationAsync(context, operation, isPending: false);
        await context.Database.GetService<IMigrator>().MigrateAsync(CurrentMigration);
        await SetPendingAndInsertOperationPairAsync(
            context,
            operation,
            kind: 7,
            StayInsert.Pending(operation, schemaVersion: 2));
        await using (Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction
            reconciliation = await context.Database.BeginTransactionAsync())
        {
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE reservations.reservations
                SET "Version" = "Version" + 1,
                    "UpdatedAtUtc" = {operation.CreatedAtUtc.AddMinutes(5)}
                WHERE "ScopeId" = {operation.ScopeId}
                  AND "Id" = {operation.ReservationId};

                UPDATE reservations.stay_amendment_operations
                SET "OperationVersion" = 2,
                    "ReconciliationCount" = 1,
                    "LastReconciledAtUtc" = {operation.CreatedAtUtc.AddMinutes(5)},
                    "LastReconciledBy" = {"staff:recovery"},
                    "UpdatedAtUtc" = {operation.CreatedAtUtc.AddMinutes(5)}
                WHERE "ScopeId" = {operation.ScopeId}
                  AND "ReservationId" = {operation.ReservationId}
                  AND "Id" = {operation.OperationId};
                """);
            await reconciliation.CommitAsync();
        }

        PostgresException blocked = await AssertPostgresFailureAsync(
            PostgresErrorCodes.RaiseException,
            () => context.Database.GetService<IMigrator>()
                .MigrateAsync(PreviousMigration));
        Assert.Contains(
            DowngradeEvidenceMessage,
            blocked.MessageText,
            StringComparison.Ordinal);
        Assert.Equal(
            1,
            await MigrationHistoryCountAsync(connectionString, CurrentMigration));
        Assert.Equal(2, await CountOperationRowsAsync(connectionString, operation));
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Downgrade_nowait_lock_failure_preserves_schema_and_history()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_reservations_stay_amendment_down_lock_tests")
                .Build();
        await postgreSql.StartAsync();
        string connectionString = postgreSql.GetConnectionString();
        OperationSeed historical = CreateSeed(TenantA, 'd', BaseNowUtc);

        await using ReservationsDbContext context = CreateDbContext(connectionString);
        await context.Database.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        await SeedLegacyOperationAsync(context, historical, isPending: false);
        await context.Database.GetService<IMigrator>().MigrateAsync(CurrentMigration);

        await using NpgsqlConnection blocker = new(connectionString);
        await blocker.OpenAsync();
        await using NpgsqlTransaction blockerTransaction =
            await blocker.BeginTransactionAsync();
        await ExecuteAsync(
            blocker,
            blockerTransaction,
            $"""
            UPDATE reservations.tenant_revisions
            SET "Revision" = "Revision"
            WHERE "ScopeId" = '{TenantA}';
            """);

        PostgresException unavailable = await AssertPostgresFailureAsync(
            PostgresErrorCodes.LockNotAvailable,
            () => context.Database.GetService<IMigrator>()
                .MigrateAsync(PreviousMigration));
        Assert.Contains(
            "lock",
            unavailable.MessageText,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            1,
            await MigrationHistoryCountAsync(connectionString, CurrentMigration));
        Assert.False(await ScalarBoolAsync(
            connectionString,
            "SELECT to_regclass('reservations.stay_amendment_operations') IS NULL;"));
        Assert.Equal(2, await CountOperationRowsAsync(connectionString, historical));

        await blockerTransaction.RollbackAsync();
    }

    private static OperationSeed CreateSeed(
        string scopeId,
        char fingerprintCharacter,
        DateTimeOffset createdAtUtc,
        Guid? operationId = null,
        Guid? inventoryRequestId = null,
        int schemaVersion = 1)
    {
        Guid localOperationId = operationId ?? Guid.NewGuid();
        OperationSeed seed = new(
            scopeId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            localOperationId,
            inventoryRequestId ?? localOperationId,
            Guid.NewGuid(),
            string.Empty,
            new DateOnly(2026, 9, 2),
            new DateOnly(2026, 9, 6),
            new TimeOnly(15, 30),
            new TimeOnly(10, 0),
            [Guid.NewGuid(), Guid.NewGuid()],
            ExpectedDetailsRevision: 3,
            RequestedBy: $"staff:operator-{fingerprintCharacter}",
            createdAtUtc);
        return seed with
        {
            RequestFingerprint = ComputeFingerprint(seed, schemaVersion)
        };
    }

    private static string ComputeFingerprint(OperationSeed seed, int schemaVersion)
    {
        string reservationId = seed.ReservationId.ToString("N");
        string operationId = seed.OperationId.ToString("N");
        string material = schemaVersion switch
        {
            1 => string.Join(
                '|',
                reservationId,
                operationId,
                seed.ExpectedDetailsRevision.ToString(CultureInfo.InvariantCulture),
                seed.TargetInventoryUnitIds),
            2 =>
                $"v2|reservation={reservationId}|operation={operationId}" +
                $"|expected-details-revision={seed.ExpectedDetailsRevision}" +
                $"|arrival={seed.TargetArrival:yyyy-MM-dd}" +
                $"|departure={seed.TargetDeparture:yyyy-MM-dd}" +
                $"|expected-arrival-time={FormatTime(seed.TargetExpectedArrivalTime)}" +
                $"|expected-departure-time={FormatTime(seed.TargetExpectedDepartureTime)}" +
                $"|units={seed.TargetInventoryUnitIds}",
            _ => throw new ArgumentOutOfRangeException(nameof(schemaVersion))
        };
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static string FormatTime(TimeOnly? value) =>
        value?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "-";

    private static async Task SeedLegacyOperationAsync(
        ReservationsDbContext context,
        OperationSeed seed,
        bool isPending)
    {
        await SeedReservationAsync(context, seed, isPending);
        await InsertManagementOperationAsync(context, seed, kind: 6);
    }

    private static async Task SeedReservationAsync(
        ReservationsDbContext context,
        OperationSeed seed,
        bool isPending)
    {
        Guid? pendingOperationId = isPending ? seed.OperationId : null;
        string? pendingFingerprint = isPending ? seed.RequestFingerprint : null;
        DateOnly? pendingArrival = isPending ? seed.TargetArrival : null;
        DateOnly? pendingDeparture = isPending ? seed.TargetDeparture : null;
        TimeOnly? pendingArrivalTime =
            isPending ? seed.TargetExpectedArrivalTime : null;
        TimeOnly? pendingDepartureTime =
            isPending ? seed.TargetExpectedDepartureTime : null;
        string? pendingUnits = isPending ? seed.TargetInventoryUnitIds : null;
        string? pendingName = isPending ? "Pending Guest" : null;
        string? pendingNameSearch = isPending ? "PENDING GUEST" : null;
        int? pendingGuestCount = isPending ? 2 : null;
        string? pendingActor = isPending ? seed.RequestedBy : null;
        Guid? pendingCorrelationId = isPending ? Guid.NewGuid() : null;
        DateTimeOffset? updatedAtUtc = isPending ? seed.CreatedAtUtc : null;

        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO reservations.tenant_revisions (
                "ScopeId", "Revision", "LifecycleStatus")
            VALUES ({seed.ScopeId}, {1L}, {1})
            ON CONFLICT ("ScopeId") DO NOTHING;

            INSERT INTO reservations.reservations (
                "Id", "PropertyId", "AllocationRequestId", "AllocationId",
                "AllocationVersion", "Arrival", "Departure",
                "ExpectedArrivalTime", "ExpectedDepartureTime",
                "PrimaryGuestName", "PrimaryGuestNameSearch",
                "Email", "EmailSearch", "Phone", "PhoneSearch",
                "GuestCount", "Source", "Status", "Version",
                "DetailsRevision", "LastDetailsChangeOrigin",
                "LastDetailsActorId", "LastDetailsChangedAtUtc",
                "PendingAllocationAmendmentId",
                "PendingAllocationAmendmentRequestFingerprint",
                "PendingArrival", "PendingDeparture",
                "PendingExpectedArrivalTime", "PendingExpectedDepartureTime",
                "PendingInventoryUnitIds", "PendingPrimaryGuestName",
                "PendingPrimaryGuestNameSearch", "PendingGuestCount",
                "PendingDetailsChangeOrigin", "PendingDetailsActorId",
                "PendingDetailsCorrelationId", "CreatedAtUtc", "UpdatedAtUtc",
                "ScopeId")
            VALUES (
                {seed.ReservationId}, {seed.PropertyId}, {Guid.NewGuid()},
                {seed.AllocationId}, {2L},
                {new DateOnly(2026, 9, 1)}, {new DateOnly(2026, 9, 5)},
                {new TimeOnly(14, 0)}, {new TimeOnly(11, 0)},
                {"Existing Guest"}, {"EXISTING GUEST"},
                {"existing@example.test"}, {"EXISTING@EXAMPLE.TEST"},
                {"+44 20 1234 5678"}, {"+44 20 1234 5678"},
                {1}, {1}, {2}, {(isPending ? 6L : 5L)},
                {seed.ExpectedDetailsRevision}, {1}, {"staff:seed"},
                {seed.CreatedAtUtc.AddDays(-1)},
                {pendingOperationId}, {pendingFingerprint},
                {pendingArrival}, {pendingDeparture},
                {pendingArrivalTime}, {pendingDepartureTime},
                {pendingUnits}, {pendingName}, {pendingNameSearch},
                {pendingGuestCount}, {(isPending ? 1 : 0)}, {pendingActor},
                {pendingCorrelationId}, {seed.CreatedAtUtc.AddDays(-2)},
                {updatedAtUtc}, {seed.ScopeId});
            """);
    }

    private static Task<int> MarkPendingAsAdapterAsync(
        ReservationsDbContext context,
        OperationSeed seed) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE reservations.reservations
            SET "PendingDetailsChangeOrigin" = {2},
                "PendingDetailsAdapterConnectionId" = {Guid.NewGuid()},
                "PendingDetailsExternalOperationId" = {seed.OperationId}
            WHERE "ScopeId" = {seed.ScopeId}
              AND "Id" = {seed.ReservationId};
            """);

    private static async Task AssertPredecessorWriterRejectedAsync(
        string connectionString,
        OperationSeed seed,
        bool insertParentFirst)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync();
        if (insertParentFirst)
        {
            await InsertManagementOperationAsync(connection, transaction, seed, kind: 6);
        }

        PostgresException rejected = await AssertPostgresFailureAsync(
            PostgresErrorCodes.CheckViolation,
            () => ExecutePredecessorPendingUpdateAsync(
                connection,
                transaction,
                seed));
        Assert.Equal(
            "CK_reservations_pending_inventory_request",
            rejected.ConstraintName);
        await transaction.RollbackAsync();
    }

    private static async Task ExecutePredecessorPendingUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OperationSeed seed)
    {
        await using NpgsqlCommand command = new(
            """
            UPDATE reservations.reservations
            SET "Version" = 6,
                "PendingAllocationAmendmentId" = @operationId,
                "PendingAllocationAmendmentRequestFingerprint" = @fingerprint,
                "PendingArrival" = @arrival,
                "PendingDeparture" = @departure,
                "PendingExpectedArrivalTime" = @arrivalTime,
                "PendingExpectedDepartureTime" = @departureTime,
                "PendingInventoryUnitIds" = @units,
                "PendingPrimaryGuestName" = 'Pending Guest',
                "PendingPrimaryGuestNameSearch" = 'PENDING GUEST',
                "PendingGuestCount" = 2,
                "PendingDetailsChangeOrigin" = 1,
                "PendingDetailsActorId" = @actor,
                "PendingDetailsCorrelationId" = @correlationId,
                "UpdatedAtUtc" = @updatedAtUtc
            WHERE "ScopeId" = @scopeId AND "Id" = @reservationId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("operationId", seed.OperationId);
        command.Parameters.AddWithValue("fingerprint", seed.RequestFingerprint);
        command.Parameters.AddWithValue("arrival", seed.TargetArrival);
        command.Parameters.AddWithValue("departure", seed.TargetDeparture);
        command.Parameters.AddWithValue(
            "arrivalTime",
            (object?)seed.TargetExpectedArrivalTime ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "departureTime",
            (object?)seed.TargetExpectedDepartureTime ?? DBNull.Value);
        command.Parameters.AddWithValue("units", seed.TargetInventoryUnitIds);
        command.Parameters.AddWithValue("actor", seed.RequestedBy);
        command.Parameters.AddWithValue("correlationId", Guid.NewGuid());
        command.Parameters.AddWithValue("updatedAtUtc", seed.CreatedAtUtc);
        command.Parameters.AddWithValue("scopeId", seed.ScopeId);
        command.Parameters.AddWithValue("reservationId", seed.ReservationId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertManagementOperationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        OperationSeed seed,
        int kind)
    {
        await using NpgsqlCommand command = new(
            """
            INSERT INTO reservations.management_operations (
                "Id", "ScopeId", "ReservationId", "PropertyId", "Kind",
                "ExpectedVersion", "ExpectedDetailsRevision", "BusinessDate",
                "CreatedAtUtc", "RequestFingerprint")
            VALUES (
                @operationId, @scopeId, @reservationId, @propertyId, @kind,
                NULL, @expectedDetailsRevision, NULL, @createdAtUtc,
                @fingerprint);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("operationId", seed.OperationId);
        command.Parameters.AddWithValue("scopeId", seed.ScopeId);
        command.Parameters.AddWithValue("reservationId", seed.ReservationId);
        command.Parameters.AddWithValue("propertyId", seed.PropertyId);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue(
            "expectedDetailsRevision",
            seed.ExpectedDetailsRevision);
        command.Parameters.AddWithValue("createdAtUtc", seed.CreatedAtUtc);
        command.Parameters.AddWithValue("fingerprint", seed.RequestFingerprint);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<bool> HasNoPendingIdentifiersAsync(
        string connectionString,
        OperationSeed seed)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            """
            SELECT "PendingAllocationAmendmentId" IS NULL
               AND "PendingInventoryAmendmentRequestId" IS NULL
            FROM reservations.reservations
            WHERE "ScopeId" = @scopeId AND "Id" = @reservationId;
            """,
            connection);
        command.Parameters.AddWithValue("scopeId", seed.ScopeId);
        command.Parameters.AddWithValue("reservationId", seed.ReservationId);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> ReservationVersionAsync(
        string connectionString,
        OperationSeed seed)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            """
            SELECT "Version"
            FROM reservations.reservations
            WHERE "ScopeId" = @scopeId AND "Id" = @reservationId;
            """,
            connection);
        command.Parameters.AddWithValue("scopeId", seed.ScopeId);
        command.Parameters.AddWithValue("reservationId", seed.ReservationId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<Guid?> PendingInventoryRequestIdAsync(
        string connectionString,
        OperationSeed seed)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            """
            SELECT "PendingInventoryAmendmentRequestId"
            FROM reservations.reservations
            WHERE "ScopeId" = @scopeId AND "Id" = @reservationId;
            """,
            connection);
        command.Parameters.AddWithValue("scopeId", seed.ScopeId);
        command.Parameters.AddWithValue("reservationId", seed.ReservationId);
        object? value = await command.ExecuteScalarAsync();
        return value is DBNull or null ? null : (Guid)value;
    }

    private static Task<int> SetPendingReservationAsync(
        ReservationsDbContext context,
        OperationSeed seed) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE reservations.reservations
            SET "Version" = {6L},
                "PendingAllocationAmendmentId" = {seed.OperationId},
                "PendingInventoryAmendmentRequestId" =
                    {seed.InventoryRequestId},
                "PendingAllocationAmendmentRequestFingerprint" =
                    {seed.RequestFingerprint},
                "PendingArrival" = {seed.TargetArrival},
                "PendingDeparture" = {seed.TargetDeparture},
                "PendingExpectedArrivalTime" =
                    {seed.TargetExpectedArrivalTime},
                "PendingExpectedDepartureTime" =
                    {seed.TargetExpectedDepartureTime},
                "PendingInventoryUnitIds" = {seed.TargetInventoryUnitIds},
                "PendingPrimaryGuestName" = {"Pending Guest"},
                "PendingPrimaryGuestNameSearch" = {"PENDING GUEST"},
                "PendingGuestCount" = {2},
                "PendingDetailsChangeOrigin" = {1},
                "PendingDetailsActorId" = {seed.RequestedBy},
                "PendingDetailsCorrelationId" = {Guid.NewGuid()},
                "UpdatedAtUtc" = {seed.CreatedAtUtc}
            WHERE "ScopeId" = {seed.ScopeId}
              AND "Id" = {seed.ReservationId};
            """);

    private static Task<int> InsertCurrentPendingReservationAsync(
        ReservationsDbContext context,
        OperationSeed seed) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO reservations.reservations (
                "Id", "PropertyId", "AllocationRequestId", "AllocationId",
                "AllocationVersion", "Arrival", "Departure",
                "ExpectedArrivalTime", "ExpectedDepartureTime",
                "PrimaryGuestName", "PrimaryGuestNameSearch",
                "Email", "EmailSearch", "Phone", "PhoneSearch",
                "GuestCount", "Source", "Status", "Version",
                "DetailsRevision", "LastDetailsChangeOrigin",
                "LastDetailsActorId", "LastDetailsChangedAtUtc",
                "PendingAllocationAmendmentId",
                "PendingInventoryAmendmentRequestId",
                "PendingAllocationAmendmentRequestFingerprint",
                "PendingArrival", "PendingDeparture",
                "PendingExpectedArrivalTime", "PendingExpectedDepartureTime",
                "PendingInventoryUnitIds", "PendingPrimaryGuestName",
                "PendingPrimaryGuestNameSearch", "PendingGuestCount",
                "PendingDetailsChangeOrigin", "PendingDetailsActorId",
                "PendingDetailsCorrelationId", "CreatedAtUtc", "UpdatedAtUtc",
                "ScopeId")
            VALUES (
                {seed.ReservationId}, {seed.PropertyId}, {Guid.NewGuid()},
                {seed.AllocationId}, {2L},
                {new DateOnly(2026, 9, 1)}, {new DateOnly(2026, 9, 5)},
                {new TimeOnly(14, 0)}, {new TimeOnly(11, 0)},
                {"Existing Guest"}, {"EXISTING GUEST"},
                {"existing@example.test"}, {"EXISTING@EXAMPLE.TEST"},
                {"+44 20 1234 5678"}, {"+44 20 1234 5678"},
                {1}, {1}, {2}, {6L}, {seed.ExpectedDetailsRevision}, {1},
                {"staff:seed"}, {seed.CreatedAtUtc.AddDays(-1)},
                {seed.OperationId}, {seed.InventoryRequestId},
                {seed.RequestFingerprint}, {seed.TargetArrival},
                {seed.TargetDeparture}, {seed.TargetExpectedArrivalTime},
                {seed.TargetExpectedDepartureTime},
                {seed.TargetInventoryUnitIds}, {"Pending Guest"},
                {"PENDING GUEST"}, {2}, {1}, {seed.RequestedBy},
                {Guid.NewGuid()}, {seed.CreatedAtUtc.AddDays(-2)},
                {seed.CreatedAtUtc}, {seed.ScopeId});
            """);

    private static Task<int> InsertManagementOperationAsync(
        ReservationsDbContext context,
        OperationSeed seed,
        int kind) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO reservations.management_operations (
                "Id", "ScopeId", "ReservationId", "PropertyId", "Kind",
                "ExpectedVersion", "ExpectedDetailsRevision", "BusinessDate",
                "CreatedAtUtc", "RequestFingerprint")
            VALUES (
                {seed.OperationId}, {seed.ScopeId}, {seed.ReservationId},
                {seed.PropertyId}, {kind}, NULL,
                {seed.ExpectedDetailsRevision}, NULL, {seed.CreatedAtUtc},
                {seed.RequestFingerprint});
            """);

    private static async Task InsertOperationPairAsync(
        ReservationsDbContext context,
        OperationSeed seed,
        int kind,
        StayInsert operation)
    {
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction
            transaction = await context.Database.BeginTransactionAsync();
        await InsertManagementOperationAsync(context, seed, kind);
        await InsertStayOperationAsync(context, operation);
        await transaction.CommitAsync();
    }

    private static async Task SetPendingAndInsertOperationPairAsync(
        ReservationsDbContext context,
        OperationSeed seed,
        int kind,
        StayInsert operation)
    {
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction
            transaction = await context.Database.BeginTransactionAsync();
        await SetPendingReservationAsync(context, seed);
        await InsertManagementOperationAsync(context, seed, kind);
        await InsertStayOperationAsync(context, operation);
        await transaction.CommitAsync();
    }

    private static async Task CompleteAppliedAsync(
        ReservationsDbContext context,
        OperationSeed seed)
    {
        DateTimeOffset completedAtUtc = seed.CreatedAtUtc.AddMinutes(6);
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction
            transaction = await context.Database.BeginTransactionAsync();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE reservations.reservations
            SET "Arrival" = {seed.TargetArrival},
                "Departure" = {seed.TargetDeparture},
                "AllocationVersion" = {3L},
                "ExpectedArrivalTime" = {seed.TargetExpectedArrivalTime},
                "ExpectedDepartureTime" = {seed.TargetExpectedDepartureTime},
                "Version" = {8L},
                "DetailsRevision" = {4L},
                "PendingAllocationAmendmentId" = NULL,
                "PendingInventoryAmendmentRequestId" = NULL,
                "PendingAllocationAmendmentRequestFingerprint" = NULL,
                "PendingArrival" = NULL,
                "PendingDeparture" = NULL,
                "PendingExpectedArrivalTime" = NULL,
                "PendingExpectedDepartureTime" = NULL,
                "PendingInventoryUnitIds" = NULL,
                "PendingPrimaryGuestName" = NULL,
                "PendingPrimaryGuestNameSearch" = NULL,
                "PendingGuestCount" = NULL,
                "PendingDetailsChangeOrigin" = 0,
                "PendingDetailsActorId" = NULL,
                "PendingDetailsCorrelationId" = NULL,
                "UpdatedAtUtc" = {completedAtUtc}
            WHERE "ScopeId" = {seed.ScopeId}
              AND "Id" = {seed.ReservationId};

            INSERT INTO reservations.requested_inventory_units (
                "Id", "ScopeId", "ReservationId")
            VALUES
                ({seed.TargetInventoryUnitIdsValue.ElementAt(0)},
                 {seed.ScopeId}, {seed.ReservationId}),
                ({seed.TargetInventoryUnitIdsValue.ElementAt(1)},
                 {seed.ScopeId}, {seed.ReservationId});

            UPDATE reservations.stay_amendment_operations
            SET "Outcome" = 2,
                "OperationVersion" = 3,
                "CompletedAtUtc" = {completedAtUtc},
                "ResultingDetailsRevision" = {4L},
                "ResultingReservationVersion" = {8L},
                "ResultingAllocationVersion" = {3L},
                "UpdatedAtUtc" = {completedAtUtc}
            WHERE "ScopeId" = {seed.ScopeId}
              AND "ReservationId" = {seed.ReservationId}
              AND "Id" = {seed.OperationId};
            """);
        await transaction.CommitAsync();
    }

    private static async Task CompleteRejectedAsync(
        ReservationsDbContext context,
        OperationSeed seed)
    {
        DateTimeOffset completedAtUtc = seed.CreatedAtUtc.AddMinutes(1);
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction
            transaction = await context.Database.BeginTransactionAsync();
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE reservations.reservations
            SET "Version" = {7L},
                "PendingAllocationAmendmentId" = NULL,
                "PendingInventoryAmendmentRequestId" = NULL,
                "PendingAllocationAmendmentRequestFingerprint" = NULL,
                "PendingArrival" = NULL,
                "PendingDeparture" = NULL,
                "PendingExpectedArrivalTime" = NULL,
                "PendingExpectedDepartureTime" = NULL,
                "PendingInventoryUnitIds" = NULL,
                "PendingPrimaryGuestName" = NULL,
                "PendingPrimaryGuestNameSearch" = NULL,
                "PendingGuestCount" = NULL,
                "PendingDetailsChangeOrigin" = 0,
                "PendingDetailsActorId" = NULL,
                "PendingDetailsCorrelationId" = NULL,
                "LastAllocationAmendmentRejectionCode" = {9},
                "UpdatedAtUtc" = {completedAtUtc}
            WHERE "ScopeId" = {seed.ScopeId}
              AND "Id" = {seed.ReservationId};

            UPDATE reservations.stay_amendment_operations
            SET "Outcome" = 3,
                "OperationVersion" = 2,
                "CompletedAtUtc" = {completedAtUtc},
                "ResultingDetailsRevision" = {3L},
                "ResultingReservationVersion" = {7L},
                "RejectionCode" = {9},
                "UpdatedAtUtc" = {completedAtUtc}
            WHERE "ScopeId" = {seed.ScopeId}
              AND "ReservationId" = {seed.ReservationId}
              AND "Id" = {seed.OperationId};
            """);
        await transaction.CommitAsync();
    }

    private static Task<int> ClearPendingReservationAsOldHandlerAsync(
        ReservationsDbContext context,
        OperationSeed seed) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE reservations.reservations
            SET "Version" = {7L},
                "PendingAllocationAmendmentId" = NULL,
                "PendingAllocationAmendmentRequestFingerprint" = NULL,
                "PendingArrival" = NULL,
                "PendingDeparture" = NULL,
                "PendingExpectedArrivalTime" = NULL,
                "PendingExpectedDepartureTime" = NULL,
                "PendingInventoryUnitIds" = NULL,
                "PendingPrimaryGuestName" = NULL,
                "PendingPrimaryGuestNameSearch" = NULL,
                "PendingGuestCount" = NULL,
                "PendingDetailsChangeOrigin" = 0,
                "PendingDetailsActorId" = NULL,
                "PendingDetailsCorrelationId" = NULL,
                "UpdatedAtUtc" = {seed.CreatedAtUtc.AddMinutes(1)}
            WHERE "ScopeId" = {seed.ScopeId}
              AND "Id" = {seed.ReservationId};
            """);

    private static Task<int> InsertStayOperationAsync(
        ReservationsDbContext context,
        StayInsert operation) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO reservations.stay_amendment_operations (
                "Id", "ScopeId", "ReservationId", "PropertyId",
                "InventoryRequestId",
                "RequestSchemaVersion", "RequestFingerprint",
                "TargetArrival", "TargetDeparture",
                "TargetExpectedArrivalTime", "TargetExpectedDepartureTime",
                "TargetInventoryUnitIds", "ExpectedDetailsRevision",
                "RequestedBy", "Outcome", "OperationVersion",
                "RequestedAtUtc", "UpdatedAtUtc", "CompletedAtUtc",
                "ResultingDetailsRevision", "ResultingReservationVersion",
                "ResultingAllocationVersion", "RejectionCode",
                "ReconciliationCount",
                "LastReconciledAtUtc", "LastReconciledBy")
            VALUES (
                {operation.OperationId}, {operation.ScopeId},
                {operation.ReservationId}, {operation.PropertyId},
                {operation.InventoryRequestId},
                {operation.RequestSchemaVersion}, {operation.RequestFingerprint},
                {operation.TargetArrival}, {operation.TargetDeparture},
                {operation.TargetExpectedArrivalTime},
                {operation.TargetExpectedDepartureTime},
                {operation.TargetInventoryUnitIds},
                {operation.ExpectedDetailsRevision}, {operation.RequestedBy},
                {operation.Outcome}, {operation.OperationVersion},
                {operation.RequestedAtUtc}, {operation.UpdatedAtUtc},
                {operation.CompletedAtUtc},
                {operation.ResultingDetailsRevision},
                {operation.ResultingReservationVersion},
                {operation.ResultingAllocationVersion},
                {operation.RejectionCode}, {operation.ReconciliationCount},
                {operation.LastReconciledAtUtc}, {operation.LastReconciledBy});
            """);

    private static Task<int> DeleteChildAsync(
        ReservationsDbContext context,
        OperationSeed seed) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM reservations.stay_amendment_operations
            WHERE "ScopeId" = {seed.ScopeId}
              AND "ReservationId" = {seed.ReservationId}
              AND "Id" = {seed.OperationId};
            """);

    private static Task<int> DeleteParentAsync(
        ReservationsDbContext context,
        OperationSeed seed) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM reservations.management_operations
            WHERE "ScopeId" = {seed.ScopeId}
              AND "ReservationId" = {seed.ReservationId}
              AND "Id" = {seed.OperationId};
            """);

    private static Task<int> DeleteReservationAsync(
        ReservationsDbContext context,
        OperationSeed seed) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM reservations.reservations
            WHERE "ScopeId" = {seed.ScopeId}
              AND "Id" = {seed.ReservationId};
            """);

    private static async Task<Guid> BeginTenantDestructionAsync(
        ReservationsDbContext context,
        string scopeId,
        DateTimeOffset startedAtUtc)
    {
        Guid operationId = Guid.NewGuid();
        string requestSha256 = new('c', 64);
        string proofSha256 = new('d', 64);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE reservations.tenant_revisions
            SET "Revision" = 2,
                "LifecycleStatus" = 2,
                "DestroyOperationId" = {operationId},
                "DestroyRequestSha256" = {requestSha256},
                "DestroyStartedAtUtc" = {startedAtUtc},
                "DestroyCompletedAtUtc" = NULL
            WHERE "ScopeId" = {scopeId};

            INSERT INTO reservations.tenant_destroy_operations (
                "OperationId", "ScopeId", "RequestSha256",
                "SelectedRevision", "ResultingRevision", "BatchSize", "Stage",
                "RemovedRecordCount", "CompletedBatchCount", "ProofVersion",
                "RemovalProofSha256", "StartedAtUtc", "UpdatedAtUtc",
                "ConcurrencyVersion")
            VALUES (
                {operationId}, {scopeId}, {requestSha256}, {1L}, {2L}, {100},
                {1}, {0L}, {0}, {1}, {proofSha256}, {startedAtUtc},
                {startedAtUtc}, {1});
            """);
        return operationId;
    }

    private static async Task AssertDestroyDeleteRejectedAsync(
        string connectionString,
        Guid destroyOperationId,
        string sql,
        OperationSeed seed)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync();
        await SetTenantDestroyOperationAsync(
            connection,
            transaction,
            destroyOperationId);
        await AssertPostgresFailureAsync(
            PostgresErrorCodes.RaiseException,
            () => ExecuteSeedCommandAsync(connection, transaction, sql, seed));
        await transaction.RollbackAsync();
    }

    private static async Task ExecuteDestroyDeleteAsync(
        string connectionString,
        Guid destroyOperationId,
        string sql,
        OperationSeed seed)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync();
        await SetTenantDestroyOperationAsync(
            connection,
            transaction,
            destroyOperationId);
        int affected = await ExecuteSeedCommandAsync(
            connection,
            transaction,
            sql,
            seed);
        Assert.Equal(1, affected);
        await transaction.CommitAsync();
    }

    private static Task<int> ExecuteSeedCommandAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        OperationSeed seed)
    {
        NpgsqlCommand command = new(sql, connection, transaction);
        command.Parameters.AddWithValue("scopeId", seed.ScopeId);
        command.Parameters.AddWithValue("reservationId", seed.ReservationId);
        command.Parameters.AddWithValue("operationId", seed.OperationId);
        return ExecuteAndDisposeAsync(command);
    }

    private static async Task<int> ExecuteAndDisposeAsync(NpgsqlCommand command)
    {
        await using (command)
        {
            return await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task SetTenantDestroyOperationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid operationId)
    {
        await using NpgsqlCommand command = new(
            "SELECT set_config('bunkfy.reservations_tenant_destroy_operation_id', @operationId, true);",
            connection,
            transaction);
        command.Parameters.AddWithValue("operationId", operationId.ToString("D"));
        await command.ExecuteScalarAsync();
    }

    private static async Task<StayOperationRow> ReadStayOperationAsync(
        string connectionString,
        OperationSeed seed)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            """
            SELECT
                "InventoryRequestId", "RequestSchemaVersion", "RequestFingerprint",
                "TargetArrival", "TargetDeparture",
                "TargetExpectedArrivalTime", "TargetExpectedDepartureTime",
                "TargetInventoryUnitIds", "ExpectedDetailsRevision",
                "RequestedBy", "Outcome", "OperationVersion",
                "ReconciliationCount",
                "RequestedAtUtc" = @createdAtUtc AND
                    "UpdatedAtUtc" = @createdAtUtc AS "TimesMatch"
            FROM reservations.stay_amendment_operations
            WHERE "ScopeId" = @scopeId
              AND "ReservationId" = @reservationId
              AND "Id" = @operationId;
            """,
            connection);
        command.Parameters.AddWithValue("createdAtUtc", seed.CreatedAtUtc);
        command.Parameters.AddWithValue("scopeId", seed.ScopeId);
        command.Parameters.AddWithValue("reservationId", seed.ReservationId);
        command.Parameters.AddWithValue("operationId", seed.OperationId);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new(
            GetNullableValue<Guid>(reader, 0),
            reader.GetInt32(1),
            reader.GetString(2),
            GetNullableValue<DateOnly>(reader, 3),
            GetNullableValue<DateOnly>(reader, 4),
            GetNullableValue<TimeOnly>(reader, 5),
            GetNullableValue<TimeOnly>(reader, 6),
            GetNullableString(reader, 7),
            reader.GetInt64(8),
            GetNullableString(reader, 9),
            reader.GetInt32(10),
            reader.GetInt64(11),
            reader.GetInt32(12),
            reader.GetBoolean(13));
    }

    private static T? GetNullableValue<T>(
        NpgsqlDataReader reader,
        int ordinal)
        where T : struct =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<T>(ordinal);

    private static string? GetNullableString(
        NpgsqlDataReader reader,
        int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static void AssertExactLegacyBackfill(
        StayOperationRow row,
        OperationSeed seed)
    {
        Assert.Equal(seed.OperationId, row.InventoryRequestId);
        Assert.Equal(1, row.RequestSchemaVersion);
        Assert.Equal(seed.RequestFingerprint, row.RequestFingerprint);
        Assert.Equal(seed.TargetArrival, row.TargetArrival);
        Assert.Equal(seed.TargetDeparture, row.TargetDeparture);
        Assert.Equal(
            seed.TargetExpectedArrivalTime,
            row.TargetExpectedArrivalTime);
        Assert.Equal(
            seed.TargetExpectedDepartureTime,
            row.TargetExpectedDepartureTime);
        Assert.Equal(seed.TargetInventoryUnitIds, row.TargetInventoryUnitIds);
        Assert.Equal(seed.ExpectedDetailsRevision, row.ExpectedDetailsRevision);
        Assert.Equal(seed.RequestedBy, row.RequestedBy);
        Assert.Equal(1, row.Outcome);
        Assert.Equal(1, row.OperationVersion);
        Assert.Equal(0, row.ReconciliationCount);
        Assert.True(row.TimesMatch);
    }

    private static void AssertHistoricalUnknownBackfill(
        StayOperationRow row,
        OperationSeed seed)
    {
        Assert.Null(row.InventoryRequestId);
        Assert.Equal(1, row.RequestSchemaVersion);
        Assert.Equal(seed.RequestFingerprint, row.RequestFingerprint);
        Assert.Null(row.TargetArrival);
        Assert.Null(row.TargetDeparture);
        Assert.Null(row.TargetExpectedArrivalTime);
        Assert.Null(row.TargetExpectedDepartureTime);
        Assert.Null(row.TargetInventoryUnitIds);
        Assert.Equal(seed.ExpectedDetailsRevision, row.ExpectedDetailsRevision);
        Assert.Null(row.RequestedBy);
        Assert.Equal(4, row.Outcome);
        Assert.Equal(1, row.OperationVersion);
        Assert.Equal(0, row.ReconciliationCount);
        Assert.True(row.TimesMatch);
    }

    private static async Task<int> MigrationHistoryCountAsync(
        string connectionString,
        string migrationId)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            """
            SELECT COUNT(*)::int
            FROM reservations.__ef_migrations_history
            WHERE "MigrationId" = @migrationId;
            """,
            connection);
        command.Parameters.AddWithValue("migrationId", migrationId);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<int> CountOperationRowsAsync(
        string connectionString,
        OperationSeed seed)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            """
            SELECT (
                (SELECT COUNT(*)
                 FROM reservations.management_operations
                 WHERE "ScopeId" = @scopeId
                   AND "ReservationId" = @reservationId
                   AND "Id" = @operationId) +
                (SELECT COUNT(*)
                 FROM reservations.stay_amendment_operations
                 WHERE "ScopeId" = @scopeId
                   AND "ReservationId" = @reservationId
                   AND "Id" = @operationId))::int;
            """,
            connection);
        command.Parameters.AddWithValue("scopeId", seed.ScopeId);
        command.Parameters.AddWithValue("reservationId", seed.ReservationId);
        command.Parameters.AddWithValue("operationId", seed.OperationId);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<int> CountReservationRowsAsync(
        string connectionString,
        OperationSeed seed)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(
            """
            SELECT COUNT(*)::int
            FROM reservations.reservations
            WHERE "ScopeId" = @scopeId AND "Id" = @reservationId;
            """,
            connection);
        command.Parameters.AddWithValue("scopeId", seed.ScopeId);
        command.Parameters.AddWithValue("reservationId", seed.ReservationId);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<int> ScalarIntAsync(
        string connectionString,
        string sql)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> ScalarBoolAsync(
        string connectionString,
        string sql)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> ScalarStringAsync(
        string connectionString,
        string sql)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql)
    {
        await using NpgsqlCommand command = new(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<PostgresException> AssertPostgresFailureAsync(
        string sqlState,
        Func<Task> action)
    {
        Exception failure = await Assert.ThrowsAnyAsync<Exception>(action);
        PostgresException? providerFailure = failure as PostgresException;
        for (Exception? current = failure.InnerException;
             providerFailure is null && current is not null;
             current = current.InnerException)
        {
            providerFailure = current as PostgresException;
        }

        Assert.NotNull(providerFailure);
        Assert.Equal(sqlState, providerFailure.SqlState);
        return providerFailure;
    }

    private static ReservationsDbContext CreateDbContext(string connectionString)
    {
        DbContextOptions<ReservationsDbContext> options =
            new DbContextOptionsBuilder<ReservationsDbContext>()
                .UseNpgsql(connectionString, provider => provider
                    .MigrationsAssembly(ReservationsMigrations.PostgreSqlAssembly)
                    .MigrationsHistoryTable(
                        ReservationsMigrations.HistoryTable,
                        ReservationsMigrations.Schema))
                .Options;
        return new(
            options,
            new TestScopeContext(),
            OpenWorkspaceTerminationFenceReader.Instance);
    }

    private sealed record OperationSeed(
        string ScopeId,
        Guid ReservationId,
        Guid PropertyId,
        Guid OperationId,
        Guid InventoryRequestId,
        Guid AllocationId,
        string RequestFingerprint,
        DateOnly TargetArrival,
        DateOnly TargetDeparture,
        TimeOnly? TargetExpectedArrivalTime,
        TimeOnly? TargetExpectedDepartureTime,
        IReadOnlyCollection<Guid> TargetInventoryUnitIdsValue,
        long ExpectedDetailsRevision,
        string RequestedBy,
        DateTimeOffset CreatedAtUtc)
    {
        public string TargetInventoryUnitIds => string.Join(
            ',',
            this.TargetInventoryUnitIdsValue
                .Order()
                .Select(id => id.ToString("N")));
    }

    private sealed record StayInsert(
        Guid OperationId,
        string ScopeId,
        Guid ReservationId,
        Guid PropertyId,
        Guid? InventoryRequestId,
        int RequestSchemaVersion,
        string RequestFingerprint,
        DateOnly? TargetArrival,
        DateOnly? TargetDeparture,
        TimeOnly? TargetExpectedArrivalTime,
        TimeOnly? TargetExpectedDepartureTime,
        string? TargetInventoryUnitIds,
        long ExpectedDetailsRevision,
        string? RequestedBy,
        int Outcome,
        long OperationVersion,
        DateTimeOffset RequestedAtUtc,
        DateTimeOffset UpdatedAtUtc,
        DateTimeOffset? CompletedAtUtc,
        long? ResultingDetailsRevision,
        long? ResultingReservationVersion,
        long? ResultingAllocationVersion,
        int? RejectionCode,
        int ReconciliationCount,
        DateTimeOffset? LastReconciledAtUtc,
        string? LastReconciledBy)
    {
        public static StayInsert Pending(
            OperationSeed seed,
            int schemaVersion) =>
            new(
                seed.OperationId,
                seed.ScopeId,
                seed.ReservationId,
                seed.PropertyId,
                seed.InventoryRequestId,
                schemaVersion,
                seed.RequestFingerprint,
                seed.TargetArrival,
                seed.TargetDeparture,
                seed.TargetExpectedArrivalTime,
                seed.TargetExpectedDepartureTime,
                seed.TargetInventoryUnitIds,
                seed.ExpectedDetailsRevision,
                seed.RequestedBy,
                Outcome: 1,
                OperationVersion: 1,
                seed.CreatedAtUtc,
                seed.CreatedAtUtc,
                CompletedAtUtc: null,
                ResultingDetailsRevision: null,
                ResultingReservationVersion: null,
                ResultingAllocationVersion: null,
                RejectionCode: null,
                ReconciliationCount: 0,
                LastReconciledAtUtc: null,
                LastReconciledBy: null);
    }

    private sealed record StayOperationRow(
        Guid? InventoryRequestId,
        int RequestSchemaVersion,
        string RequestFingerprint,
        DateOnly? TargetArrival,
        DateOnly? TargetDeparture,
        TimeOnly? TargetExpectedArrivalTime,
        TimeOnly? TargetExpectedDepartureTime,
        string? TargetInventoryUnitIds,
        long ExpectedDetailsRevision,
        string? RequestedBy,
        int Outcome,
        long OperationVersion,
        int ReconciliationCount,
        bool TimesMatch);

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => TenantA;
    }
}
