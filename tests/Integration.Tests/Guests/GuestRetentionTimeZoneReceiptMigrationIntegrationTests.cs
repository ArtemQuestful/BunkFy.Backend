namespace Integration.Tests;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.DataRights;
using BunkFy.Modules.Guests.Domain.Retention;
using BunkFy.Modules.Guests.Persistence;
using BunkFy.Modules.Guests.Persistence.Repositories;
using BunkFy.TimeZones;
using Gma.Framework.Scoping;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class GuestRetentionTimeZoneReceiptMigrationIntegrationTests
{
    private const string PreviousMigration =
        "20260807114926_AddGuestManagementOperations";
    private const string TenantId =
        "7e000000-0000-0000-0000-000000000001";
    private const string RunningUpgradeRefusal =
        "Cannot add Guests retention time-zone evidence while a retention " +
        "execution is running.";
    private const string LifecycleRefusal =
        "Cannot change Guests retention time-zone evidence while tenant " +
        "destruction is in progress.";
    private const string ClosedOwnerDataRefusal =
        "Cannot change Guests retention time-zone evidence because a " +
        "completed tenant still owns exportable data.";
    private const string RevisionExhaustedRefusal =
        "Cannot invalidate Guests tenant revisions because an affected " +
        "revision is exhausted.";
    private const string ReceiptDowngradeRefusal =
        "Cannot remove Guests retention time-zone evidence while version 2 " +
        "receipts exist.";
    private const string ProjectionDowngradeRefusal =
        "Cannot remove Guests retention time-zone evidence while projected " +
        "time-zone evidence exists.";
    private const string PolicyDowngradeRefusal =
        "Cannot remove Guests retention time-zone evidence while execution " +
        "policy version 2 state exists.";
    private static readonly JsonSerializerOptions ExportSerializerOptions =
        new(JsonSerializerDefaults.Web);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Upgrade_preserves_v1_receipt_and_refuses_policy_v2_downgrade()
    {
        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_guest_retention_tz_migration")
                .Build();
        await postgreSql.StartAsync().ConfigureAwait(false);

        Guid propertyId = Guid.Parse(
            "7e100000-0000-0000-0000-000000000001");
        Guid guestId = Guid.Parse(
            "7e200000-0000-0000-0000-000000000001");
        Guid executionId = Guid.Parse(
            "7e300000-0000-0000-0000-000000000001");
        Guid receiptId = Guid.Parse(
            "7e400000-0000-0000-0000-000000000001");
        Guid eventId = Guid.Parse(
            "7e500000-0000-0000-0000-000000000001");
        DateTimeOffset startedAtUtc =
            new(2026, 1, 2, 1, 0, 0, TimeSpan.Zero);
        DateTimeOffset deadlineUtc = startedAtUtc.AddMinutes(10);
        DateTimeOffset completedAtUtc = startedAtUtc.AddMinutes(1);
        DateTimeOffset retentionDeadlineUtc =
            new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        string policySetSha256 = new(
            'a',
            GuestRetentionAnonymisationReceipt.Sha256Length);
        const string actorId = GuestRetentionAnonymisationReceipt.SystemActorId;
        string v1CanonicalSha256 = ComputeV1CanonicalSha256(
            receiptId,
            executionId,
            guestId,
            selectedGuestVersion: 1,
            resultingGuestVersion: 2,
            affectedPropertyCount: 1,
            retentionDeadlineUtc,
            policySetSha256,
            eventId,
            actorId,
            completedAtUtc);

        await using (GuestsDbContext initial = CreateDbContext(
            postgreSql.GetConnectionString()))
        {
            await initial.Database.GetService<IMigrator>()
                .MigrateAsync(PreviousMigration).ConfigureAwait(false);
            GuestProfile profile = GuestProfile.Create(
                guestId,
                TenantId,
                propertyId,
                "Version One Receipt Guest",
                legalName: null,
                email: null,
                phone: null,
                dateOfBirth: null,
                nationalityCountryCode: null,
                preferredLanguageTag: null,
                notes: null,
                actorId,
                Guid.NewGuid(),
                startedAtUtc.AddYears(-2)).Value;
            profile.ClearDomainEvents();
            await GuestLanguageMigrationSeed.ProfilesAsync(initial, profile).ConfigureAwait(false);
            await initial.SaveChangesAsync().ConfigureAwait(false);

            await initial.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO guests.guest_retention_executions (
                    "Id", "DataClassKey", "ExecutionPolicyVersion",
                    "Attempt", "StartingProjectionOrdinal", "State",
                    "StartedAtUtc", "DeadlineUtc", "CompletedAtUtc",
                    "AffectedCount", "ScannedCount", "RemainingCount",
                    "OutcomeCode", "HoldReviewDueAtUtc", "Version",
                    "ScopeId")
                VALUES (
                    {executionId}, {"guest-operational"}, {1}, {1}, {0L},
                    {(int)GuestRetentionExecutionState.Completed},
                    {startedAtUtc}, {deadlineUtc}, {completedAtUtc},
                    {1}, {1}, {0},
                    {"guests.guest-operational.completed"}, NULL, {2L},
                    {TenantId});

                INSERT INTO guests.guest_anonymisation_tombstones (
                    "Id", "Authority", "CompletedAtUtc", "ContractVersion",
                    "LastReplayedAtUtc", "LedgerEntryId",
                    "OwnerReceiptSha256", "Revision", "ScopeId", "State")
                VALUES (
                    {guestId}, {2}, {completedAtUtc}, {2}, NULL, NULL,
                    {v1CanonicalSha256}, {1L}, {TenantId}, {1});

                INSERT INTO guests.guest_retention_anonymisation_receipts (
                    "Id", "ContractVersion", "ExecutionId", "GuestId",
                    "SelectedGuestVersion", "ResultingGuestVersion",
                    "AffectedPropertyCount", "RetentionDeadlineUtc",
                    "PolicySetSha256", "EventId", "ActorId",
                    "CompletedAtUtc", "CanonicalSha256", "ScopeId")
                VALUES (
                    {receiptId}, {1}, {executionId}, {guestId}, {1L}, {2L},
                    {1}, {retentionDeadlineUtc}, {policySetSha256}, {eventId},
                    {actorId}, {completedAtUtc}, {v1CanonicalSha256},
                    {TenantId});
                """).ConfigureAwait(false);
        }

        await using GuestsDbContext upgraded = CreateDbContext(
            postgreSql.GetConnectionString());
        await upgraded.Database.MigrateAsync().ConfigureAwait(false);

        GuestRetentionAnonymisationReceipt receipt = await upgraded
            .RetentionAnonymisationReceipts
            .AsNoTracking()
            .SingleAsync(item => item.Id == receiptId)
            .ConfigureAwait(false);
        Assert.Equal(1, receipt.ContractVersion);
        Assert.Null(receipt.TimeZoneCatalogVersion);
        Assert.Equal(v1CanonicalSha256, receipt.CanonicalSha256);
        Assert.True(receipt.Matches(executionId, guestId, 1));

        DataRightsExportRecord export = CreateRetentionExportRecord(receipt);
        DataRightsExportField proof = Assert.Single(
            export.Fields,
            field => field.FieldId == "guests.retention-proof");
        JsonElement expectedV1Proof = JsonSerializer.SerializeToElement(
            new
            {
                receipt.ContractVersion,
                receipt.ExecutionId,
                receipt.SelectedGuestVersion,
                receipt.ResultingGuestVersion,
                receipt.AffectedPropertyCount,
                receipt.RetentionDeadlineUtc,
                receipt.PolicySetSha256,
                receipt.EventId,
                receipt.CompletedAtUtc,
                receipt.CanonicalSha256
            },
            ExportSerializerOptions);
        Assert.Equal(expectedV1Proof.GetRawText(), proof.Value.GetRawText());
        Assert.False(proof.Value.TryGetProperty(
            "timeZoneCatalogVersion",
            out _));
        Assert.Equal(
            5,
            GuestsTenantTerminationMetadata.ExportSchemaVersion);
        Assert.Equal(
            GuestsTenantTerminationMetadata.ExportSchemaVersion,
            GuestsTenantTerminationExportSchema.Descriptor
                .ExportSchemaVersion);

        PostgresException appendOnly = await Assert.ThrowsAsync<
            PostgresException>(() =>
            upgraded.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE guests.guest_retention_anonymisation_receipts
                SET "TimeZoneCatalogVersion" =
                    {TimeZoneCatalog.Default.CatalogVersion}
                WHERE "Id" = {receiptId};
                """));
        Assert.Equal(PostgresErrorCodes.RaiseException, appendOnly.SqlState);
        Assert.Contains(
            "guest data-rights receipts are append-only",
            appendOnly.MessageText,
            StringComparison.Ordinal);

        long revisionBeforeSafeDown = await ReadTenantRevisionAsync(
                upgraded,
                TenantId)
            .ConfigureAwait(false);
        await upgraded.Database.GetService<IMigrator>()
            .MigrateAsync(PreviousMigration).ConfigureAwait(false);
        Assert.Equal(
            revisionBeforeSafeDown + 1,
            await ReadTenantRevisionAsync(upgraded, TenantId)
                .ConfigureAwait(false));
        Assert.Equal(
            0,
            await CountColumnAsync(
                upgraded,
                "property_projection",
                "CanonicalTimeZoneId").ConfigureAwait(false));
        await upgraded.Database.MigrateAsync().ConfigureAwait(false);
        GuestRetentionAnonymisationReceipt replayedV1 = await upgraded
            .RetentionAnonymisationReceipts
            .AsNoTracking()
            .SingleAsync(item => item.Id == receiptId)
            .ConfigureAwait(false);
        Assert.Equal(v1CanonicalSha256, replayedV1.CanonicalSha256);
        Assert.Null(replayedV1.TimeZoneCatalogVersion);

        Guid v2ExecutionId = Guid.Parse(
            "7e300000-0000-0000-0000-000000000002");
        await upgraded.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO guests.guest_retention_executions (
                "Id", "DataClassKey", "ExecutionPolicyVersion",
                "Attempt", "StartingProjectionOrdinal", "State",
                "StartedAtUtc", "DeadlineUtc", "CompletedAtUtc",
                "AffectedCount", "ScannedCount", "RemainingCount",
                "OutcomeCode", "HoldReviewDueAtUtc", "Version",
                "ScopeId")
            VALUES (
                {v2ExecutionId}, {"guest-operational"}, {2}, {1}, {0L},
                {(int)GuestRetentionExecutionState.Completed},
                {startedAtUtc}, {deadlineUtc}, {completedAtUtc},
                {0}, {0}, {0},
                {"guests.guest-operational.completed"}, NULL, {2L},
                {TenantId});
            """).ConfigureAwait(false);

        PostgresException unsafeDowngrade = await Assert.ThrowsAsync<
            PostgresException>(() => upgraded.Database.GetService<IMigrator>()
                .MigrateAsync(PreviousMigration));
        Assert.Equal(
            PostgresErrorCodes.RaiseException,
            unsafeDowngrade.SqlState);
        Assert.Contains(
            PolicyDowngradeRefusal,
            unsafeDowngrade.MessageText,
            StringComparison.Ordinal);

        await AssertUpgradeSafetyMatrixAsync(postgreSql)
            .ConfigureAwait(false);
        await AssertProjectionDowngradeRefusalAsync(postgreSql)
            .ConfigureAwait(false);
        await AssertReceiptDowngradeRefusalAsync(postgreSql)
            .ConfigureAwait(false);
        await AssertPolicyDowngradeRefusalAsync(postgreSql)
            .ConfigureAwait(false);
    }

    private static GuestsDbContext CreateDbContext(string connectionString)
    {
        DbContextOptions<GuestsDbContext> options =
            new DbContextOptionsBuilder<GuestsDbContext>()
                .UseNpgsql(connectionString, provider => provider
                    .MigrationsAssembly(GuestsMigrations.PostgreSqlAssembly)
                    .MigrationsHistoryTable(
                        GuestsMigrations.HistoryTable,
                        GuestsMigrations.Schema))
                .Options;
        return new(
            options,
            new MigrationScopeContext(),
            OpenWorkspaceTerminationFenceReader.Instance);
    }

    private static async Task AssertUpgradeSafetyMatrixAsync(
        PostgreSqlContainer postgreSql)
    {
        string connectionString = await CreateScenarioDatabaseAsync(
                postgreSql,
                "bunkfy_guests_tz_upgrade_matrix")
            .ConfigureAwait(false);
        await using GuestsDbContext dbContext = CreateDbContext(
            connectionString);
        IMigrator migrator = dbContext.Database.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration).ConfigureAwait(false);

        await AssertUpgradeLockIsFailFastAsync(connectionString)
            .ConfigureAwait(false);

        Guid runningExecutionId = Guid.Parse(
            "7f100000-0000-0000-0000-000000000001");
        const string runningScope = "guests-tz-running-upgrade";
        await InsertExecutionAsync(
                dbContext,
                runningExecutionId,
                runningScope,
                executionPolicyVersion: 1,
                GuestRetentionExecutionState.Running)
            .ConfigureAwait(false);
        await AssertMigrationRefusalAsync(
                migrator,
                targetMigration: null,
                RunningUpgradeRefusal)
            .ConfigureAwait(false);
        Assert.Equal(
            0,
            await CountTenantRevisionAsync(dbContext, runningScope)
                .ConfigureAwait(false));
        await DeleteExecutionAsync(dbContext, runningExecutionId)
            .ConfigureAwait(false);

        const string closingScope = "guests-tz-closing-upgrade";
        Guid closingOperationId = Guid.Parse(
            "7f200000-0000-0000-0000-000000000001");
        DateTimeOffset closingStartedAtUtc = new(
            2026,
            8,
            15,
            10,
            0,
            0,
            TimeSpan.Zero);
        await InsertTenantRevisionAsync(
                dbContext,
                closingScope,
                revision: 5,
                lifecycleStatus: 2,
                closingOperationId,
                closingStartedAtUtc,
                completedAtUtc: null)
            .ConfigureAwait(false);
        await AssertMigrationRefusalAsync(
                migrator,
                targetMigration: null,
                LifecycleRefusal)
            .ConfigureAwait(false);
        Assert.Equal(
            5,
            await ReadTenantRevisionAsync(dbContext, closingScope)
                .ConfigureAwait(false));
        await DeleteTenantRevisionAsync(dbContext, closingScope)
            .ConfigureAwait(false);

        const string closedScope = "guests-tz-clean-closed";
        Guid closedOperationId = Guid.Parse(
            "7f200000-0000-0000-0000-000000000002");
        DateTimeOffset closedStartedAtUtc = closingStartedAtUtc.AddHours(1);
        DateTimeOffset closedCompletedAtUtc =
            closedStartedAtUtc.AddMinutes(5);
        await InsertTenantRevisionAsync(
                dbContext,
                closedScope,
                revision: 11,
                lifecycleStatus: 3,
                closedOperationId,
                closedStartedAtUtc,
                closedCompletedAtUtc)
            .ConfigureAwait(false);
        await InsertTenantDestroyReceiptAsync(
                dbContext,
                closedScope,
                closedOperationId,
                selectedRevision: 10,
                resultingRevision: 11,
                closedStartedAtUtc,
                closedCompletedAtUtc)
            .ConfigureAwait(false);
        Guid dirtyClosedPropertyId = Guid.Parse(
            "7f300000-0000-0000-0000-000000000001");
        await InsertLegacyPropertyProjectionAsync(
                dbContext,
                closedScope,
                dirtyClosedPropertyId,
                "Etc/UTC",
                topologySourceVersion: 2)
            .ConfigureAwait(false);
        await AssertMigrationRefusalAsync(
                migrator,
                targetMigration: null,
                ClosedOwnerDataRefusal)
            .ConfigureAwait(false);
        Assert.Equal(
            11,
            await ReadTenantRevisionAsync(dbContext, closedScope)
                .ConfigureAwait(false));
        await DeletePropertyProjectionAsync(
                dbContext,
                closedScope,
                dirtyClosedPropertyId)
            .ConfigureAwait(false);

        const string openScope = "guests-tz-open-non-retention";
        const string exhaustedScope = "guests-tz-exhausted";
        await InsertOpenTenantRevisionAsync(
                dbContext,
                openScope,
                revision: 7)
            .ConfigureAwait(false);
        Guid openScopeHoldId = Guid.Parse(
            "7f700000-0000-0000-0000-000000000001");
        Guid openScopeHoldPropertyId = Guid.Parse(
            "7f700000-0000-0000-0000-000000000002");
        Guid openScopeHoldGuestId = Guid.Parse(
            "7f700000-0000-0000-0000-000000000003");
        await InsertActiveDataHoldAsync(
                dbContext,
                openScope,
                openScopeHoldId,
                openScopeHoldPropertyId,
                openScopeHoldGuestId,
                closingStartedAtUtc)
            .ConfigureAwait(false);
        Assert.Equal(
            1,
            await CountNonRetentionOwnerOnlyAsync(
                    dbContext,
                    openScope,
                    openScopeHoldId)
                .ConfigureAwait(false));
        await InsertOpenTenantRevisionAsync(
                dbContext,
                exhaustedScope,
                revision: long.MaxValue)
            .ConfigureAwait(false);
        await AssertMigrationRefusalAsync(
                migrator,
                targetMigration: null,
                RevisionExhaustedRefusal)
            .ConfigureAwait(false);
        Assert.Equal(
            7,
            await ReadTenantRevisionAsync(dbContext, openScope)
                .ConfigureAwait(false));
        Assert.Equal(
            long.MaxValue,
            await ReadTenantRevisionAsync(dbContext, exhaustedScope)
                .ConfigureAwait(false));
        Assert.Equal(
            0,
            await CountColumnAsync(
                    dbContext,
                    "property_projection",
                    "CanonicalTimeZoneId")
                .ConfigureAwait(false));
        await DeleteTenantRevisionAsync(dbContext, exhaustedScope)
            .ConfigureAwait(false);

        const string missingRevisionScope = "guests-tz-missing-revision";
        Guid legacyZonePropertyId = Guid.Parse(
            "7f300000-0000-0000-0000-000000000002");
        Guid absentZonePropertyId = Guid.Parse(
            "7f300000-0000-0000-0000-000000000003");
        await InsertLegacyPropertyProjectionAsync(
                dbContext,
                missingRevisionScope,
                legacyZonePropertyId,
                "UTC",
                topologySourceVersion: 3)
            .ConfigureAwait(false);
        await InsertLegacyPropertyProjectionAsync(
                dbContext,
                missingRevisionScope,
                absentZonePropertyId,
                timeZoneId: null,
                topologySourceVersion: 4)
            .ConfigureAwait(false);
        Assert.Equal(
            0,
            await CountTenantRevisionAsync(dbContext, missingRevisionScope)
                .ConfigureAwait(false));

        await migrator.MigrateAsync().ConfigureAwait(false);

        Assert.Equal(
            8,
            await ReadTenantRevisionAsync(dbContext, openScope)
                .ConfigureAwait(false));
        Assert.Equal(
            1,
            await CountNonRetentionOwnerOnlyAsync(
                    dbContext,
                    openScope,
                    openScopeHoldId)
                .ConfigureAwait(false));
        Assert.Equal(
            1,
            await ReadTenantRevisionAsync(dbContext, missingRevisionScope)
                .ConfigureAwait(false));
        Assert.Equal(
            1,
            await CountCleanTenantDestroyReceiptAsync(
                    dbContext,
                    closedScope,
                    closedOperationId,
                    selectedRevision: 10,
                    resultingRevision: 11,
                    closedStartedAtUtc,
                    closedCompletedAtUtc)
                .ConfigureAwait(false));
        Assert.Equal(
            1,
            await CountCleanClosedRevisionAsync(
                    dbContext,
                    closedScope,
                    revision: 11,
                    closedOperationId,
                    closedStartedAtUtc,
                    closedCompletedAtUtc)
                .ConfigureAwait(false));
        await AssertLegacyBackfillAsync(
                dbContext,
                missingRevisionScope,
                legacyZonePropertyId,
                expectedEvidenceSource: 1,
                expectedEvidenceSourceVersion: 3)
            .ConfigureAwait(false);
        await AssertLegacyBackfillAsync(
                dbContext,
                missingRevisionScope,
                absentZonePropertyId,
                expectedEvidenceSource: 0,
                expectedEvidenceSourceVersion: 0)
            .ConfigureAwait(false);

        Guid oldBinaryExecutionId = Guid.Parse(
            "7f100000-0000-0000-0000-000000000002");
        PostgresException oldBinaryRunning = await Assert.ThrowsAsync<
            PostgresException>(() => InsertExecutionAsync(
                dbContext,
                oldBinaryExecutionId,
                openScope,
                executionPolicyVersion: 1,
                GuestRetentionExecutionState.Running));
        Assert.Equal(
            PostgresErrorCodes.CheckViolation,
            oldBinaryRunning.SqlState);
        Assert.Equal(
            "CK_guest_retention_executions_policy",
            oldBinaryRunning.ConstraintName);
    }

    private static async Task AssertProjectionDowngradeRefusalAsync(
        PostgreSqlContainer postgreSql)
    {
        string connectionString = await CreateScenarioDatabaseAsync(
                postgreSql,
                "bunkfy_guests_tz_projection_down")
            .ConfigureAwait(false);
        await using GuestsDbContext dbContext = CreateDbContext(
            connectionString);
        IMigrator migrator = dbContext.Database.GetService<IMigrator>();
        await migrator.MigrateAsync().ConfigureAwait(false);
        Guid propertyId = Guid.Parse(
            "7f400000-0000-0000-0000-000000000001");
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO guests.property_projection (
                "ScopeId", "Id", "IsKnown", "Name",
                "PolicySourceVersion", "ProcessingStatus", "Status",
                "TimeZoneId", "TopologySourceVersion",
                "CanonicalTimeZoneId", "TimeZoneStatus",
                "TimeZoneCatalogVersion", "TimeZoneEvidenceSource",
                "TimeZoneEvidenceSourceVersion")
            VALUES (
                {TenantId}, {propertyId}, TRUE, {"Enriched projection"},
                {0L}, {1}, {1}, {"Etc/UTC"}, {1L}, {"Etc/UTC"}, {1},
                {TimeZoneCatalog.Default.CatalogVersion}, {2}, {1L});
            """).ConfigureAwait(false);

        await AssertMigrationRefusalAsync(
                migrator,
                PreviousMigration,
                ProjectionDowngradeRefusal)
            .ConfigureAwait(false);
    }

    private static async Task AssertReceiptDowngradeRefusalAsync(
        PostgreSqlContainer postgreSql)
    {
        string connectionString = await CreateScenarioDatabaseAsync(
                postgreSql,
                "bunkfy_guests_tz_receipt_down")
            .ConfigureAwait(false);
        await using GuestsDbContext dbContext = CreateDbContext(
            connectionString);
        IMigrator migrator = dbContext.Database.GetService<IMigrator>();
        await migrator.MigrateAsync().ConfigureAwait(false);

        Guid guestId = Guid.Parse(
            "7f500000-0000-0000-0000-000000000001");
        Guid propertyId = Guid.Parse(
            "7f500000-0000-0000-0000-000000000002");
        Guid executionId = Guid.Parse(
            "7f500000-0000-0000-0000-000000000003");
        Guid receiptId = Guid.Parse(
            "7f500000-0000-0000-0000-000000000004");
        DateTimeOffset createdAtUtc = new(
            2024,
            1,
            1,
            0,
            0,
            0,
            TimeSpan.Zero);
        GuestProfile profile = GuestProfile.Create(
            guestId,
            TenantId,
            propertyId,
            "Version Two Receipt Guest",
            legalName: null,
            email: null,
            phone: null,
            dateOfBirth: null,
            nationalityCountryCode: null,
            preferredLanguageTag: null,
            notes: null,
            actorId: "system:provider-test",
            Guid.NewGuid(),
            createdAtUtc).Value;
        profile.ClearDomainEvents();
        await GuestLanguageMigrationSeed.ProfilesAsync(dbContext, profile).ConfigureAwait(false);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);

        await InsertExecutionAsync(
                dbContext,
                executionId,
                TenantId,
                executionPolicyVersion: 2,
                GuestRetentionExecutionState.Completed)
            .ConfigureAwait(false);
        DateTimeOffset completedAtUtc = new(
            2026,
            1,
            2,
            1,
            1,
            0,
            TimeSpan.Zero);
        Guid eventId = Guid.NewGuid();
        var anonymised = profile.AnonymiseForRetention(
            profile.Version,
            GuestRetentionAnonymisationReceipt.SystemActorId,
            eventId,
            completedAtUtc);
        Assert.True(anonymised.IsSuccess);
        GuestRetentionAnonymisationReceipt receipt =
            GuestRetentionAnonymisationReceipt.Create(
                receiptId,
                TenantId,
                executionId,
                guestId,
                anonymised.Value.PreviousVersion,
                anonymised.Value.CurrentVersion,
                affectedPropertyCount: 1,
                completedAtUtc.AddDays(-1),
                new string('b', 64),
                TimeZoneCatalog.Default.CatalogVersion,
                eventId,
                GuestRetentionAnonymisationReceipt.SystemActorId,
                completedAtUtc).Value;
        dbContext.RetentionAnonymisationReceipts.Add(receipt);
        dbContext.AnonymisationTombstones.Add(
            GuestAnonymisationTombstone.CreateForRetention(
                TenantId,
                receipt).Value);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);

        await AssertMigrationRefusalAsync(
                migrator,
                PreviousMigration,
                ReceiptDowngradeRefusal)
            .ConfigureAwait(false);
    }

    private static async Task AssertPolicyDowngradeRefusalAsync(
        PostgreSqlContainer postgreSql)
    {
        string connectionString = await CreateScenarioDatabaseAsync(
                postgreSql,
                "bunkfy_guests_tz_policy_down")
            .ConfigureAwait(false);
        await using GuestsDbContext dbContext = CreateDbContext(
            connectionString);
        IMigrator migrator = dbContext.Database.GetService<IMigrator>();
        await migrator.MigrateAsync().ConfigureAwait(false);
        await InsertExecutionAsync(
                dbContext,
                Guid.Parse("7f600000-0000-0000-0000-000000000001"),
                TenantId,
                executionPolicyVersion: 2,
                GuestRetentionExecutionState.Completed)
            .ConfigureAwait(false);

        await AssertMigrationRefusalAsync(
                migrator,
                PreviousMigration,
                PolicyDowngradeRefusal)
            .ConfigureAwait(false);
    }

    private static async Task AssertUpgradeLockIsFailFastAsync(
        string connectionString)
    {
        await using NpgsqlConnection blocker = new(connectionString);
        await blocker.OpenAsync().ConfigureAwait(false);
        await using NpgsqlTransaction transaction =
            await blocker.BeginTransactionAsync().ConfigureAwait(false);
        await using (NpgsqlCommand command = new(
                         "LOCK TABLE guests.guest_profiles " +
                         "IN ACCESS SHARE MODE;",
                         blocker,
                         transaction))
        {
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await using GuestsDbContext competing = CreateDbContext(
            connectionString);
        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() =>
            competing.Database.GetService<IMigrator>().MigrateAsync());
        PostgresException providerFailure = Assert.IsType<PostgresException>(
            failure.GetBaseException());
        Assert.Equal(
            PostgresErrorCodes.LockNotAvailable,
            providerFailure.SqlState);
        await transaction.RollbackAsync().ConfigureAwait(false);
    }

    private static async Task AssertMigrationRefusalAsync(
        IMigrator migrator,
        string? targetMigration,
        string expectedMessage)
    {
        Exception failure = targetMigration is null
            ? await Assert.ThrowsAnyAsync<Exception>(() =>
                migrator.MigrateAsync())
            : await Assert.ThrowsAnyAsync<Exception>(() =>
                migrator.MigrateAsync(targetMigration));
        PostgresException providerFailure = Assert.IsType<PostgresException>(
            failure.GetBaseException());
        Assert.Equal(
            PostgresErrorCodes.RaiseException,
            providerFailure.SqlState);
        Assert.Equal(expectedMessage, providerFailure.MessageText);
    }

    private static async Task<string> CreateScenarioDatabaseAsync(
        PostgreSqlContainer postgreSql,
        string databaseName)
    {
        string createSql = databaseName switch
        {
            "bunkfy_guests_tz_upgrade_matrix" =>
                "CREATE DATABASE \"bunkfy_guests_tz_upgrade_matrix\";",
            "bunkfy_guests_tz_projection_down" =>
                "CREATE DATABASE \"bunkfy_guests_tz_projection_down\";",
            "bunkfy_guests_tz_receipt_down" =>
                "CREATE DATABASE \"bunkfy_guests_tz_receipt_down\";",
            "bunkfy_guests_tz_policy_down" =>
                "CREATE DATABASE \"bunkfy_guests_tz_policy_down\";",
            _ => throw new ArgumentOutOfRangeException(
                nameof(databaseName),
                databaseName,
                "Unknown Guests migration scenario database.")
        };
        NpgsqlConnectionStringBuilder adminBuilder = new(
            postgreSql.GetConnectionString())
        {
            Database = "postgres",
            Pooling = false
        };
        await using NpgsqlConnection admin = new(
            adminBuilder.ConnectionString);
        await admin.OpenAsync().ConfigureAwait(false);
        await using (NpgsqlCommand command = new(createSql, admin))
        {
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        NpgsqlConnectionStringBuilder scenarioBuilder = new(
            postgreSql.GetConnectionString())
        {
            Database = databaseName,
            Pooling = false
        };
        return scenarioBuilder.ConnectionString;
    }

    private static Task<int> InsertExecutionAsync(
        GuestsDbContext dbContext,
        Guid executionId,
        string scopeId,
        int executionPolicyVersion,
        GuestRetentionExecutionState state)
    {
        DateTimeOffset startedAtUtc = new(
            2026,
            1,
            2,
            1,
            0,
            0,
            TimeSpan.Zero);
        DateTimeOffset deadlineUtc = startedAtUtc.AddHours(1);
        bool isRunning = state == GuestRetentionExecutionState.Running;
        DateTimeOffset? completedAtUtc = isRunning
            ? null
            : startedAtUtc.AddMinutes(1);
        int? scannedCount = isRunning ? null : 0;
        int? remainingCount = isRunning ? null : 0;
        string? outcomeCode = isRunning
            ? null
            : "guests.guest-operational.completed";
        long version = isRunning ? 1 : 2;
        return dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO guests.guest_retention_executions (
                "Id", "DataClassKey", "ExecutionPolicyVersion",
                "Attempt", "StartingProjectionOrdinal", "State",
                "StartedAtUtc", "DeadlineUtc", "CompletedAtUtc",
                "AffectedCount", "ScannedCount", "RemainingCount",
                "OutcomeCode", "HoldReviewDueAtUtc", "Version",
                "ScopeId")
            VALUES (
                {executionId}, {"guest-operational"},
                {executionPolicyVersion}, {1}, {0L}, {(int)state},
                {startedAtUtc}, {deadlineUtc}, {completedAtUtc}, {0},
                {scannedCount}, {remainingCount}, {outcomeCode}, NULL,
                {version}, {scopeId});
            """);
    }

    private static Task<int> DeleteExecutionAsync(
        GuestsDbContext dbContext,
        Guid executionId) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM guests.guest_retention_executions
            WHERE "Id" = {executionId};
            """);

    private static Task<int> InsertTenantRevisionAsync(
        GuestsDbContext dbContext,
        string scopeId,
        long revision,
        int lifecycleStatus,
        Guid operationId,
        DateTimeOffset startedAtUtc,
        DateTimeOffset? completedAtUtc) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO guests.tenant_revisions (
                "ScopeId", "Revision", "LifecycleStatus",
                "DestroyOperationId", "DestroyRequestSha256",
                "DestroyStartedAtUtc", "DestroyCompletedAtUtc")
            VALUES (
                {scopeId}, {revision}, {lifecycleStatus}, {operationId},
                {new string('d', 64)}, {startedAtUtc}, {completedAtUtc});
            """);

    private static Task<int> InsertOpenTenantRevisionAsync(
        GuestsDbContext dbContext,
        string scopeId,
        long revision) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO guests.tenant_revisions (
                "ScopeId", "Revision", "LifecycleStatus")
            VALUES ({scopeId}, {revision}, {1});
            """);

    private static Task<int> InsertTenantDestroyReceiptAsync(
        GuestsDbContext dbContext,
        string scopeId,
        Guid operationId,
        long selectedRevision,
        long resultingRevision,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO guests.tenant_destroy_receipts (
                "OperationId", "ScopeId", "RequestSha256",
                "SelectedRevision", "ResultingRevision", "BatchSize",
                "RemovedRecordCount", "CompletedBatchCount",
                "RemovalProofVersion", "RemovalProofSha256",
                "StartedAtUtc", "CompletedAtUtc")
            VALUES (
                {operationId}, {scopeId}, {new string('d', 64)},
                {selectedRevision}, {resultingRevision}, {100}, {0L}, {0},
                {1}, {new string('e', 64)}, {startedAtUtc},
                {completedAtUtc});
            """);

    private static Task<int> InsertActiveDataHoldAsync(
        GuestsDbContext dbContext,
        string scopeId,
        Guid holdId,
        Guid propertyId,
        Guid guestId,
        DateTimeOffset placedAtUtc) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO guests.data_holds (
                "Id", "PropertyId", "GuestId", "ReasonCode", "State",
                "PlacedBy", "PlacedAtUtc", "ReleasedBy", "ReleasedAtUtc",
                "Version", "ScopeId")
            VALUES (
                {holdId}, {propertyId}, {guestId}, {"legal-review"}, {1},
                {"system:provider-test"}, {placedAtUtc}, NULL, NULL, {1L},
                {scopeId});
            """);

    private static Task<int> DeleteTenantRevisionAsync(
        GuestsDbContext dbContext,
        string scopeId) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM guests.tenant_revisions
            WHERE "ScopeId" = {scopeId};
            """);

    private static Task<int> InsertLegacyPropertyProjectionAsync(
        GuestsDbContext dbContext,
        string scopeId,
        Guid propertyId,
        string? timeZoneId,
        long topologySourceVersion) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO guests.property_projection (
                "ScopeId", "Id", "IsKnown", "Name",
                "PolicySourceVersion", "ProcessingStatus", "Status",
                "TimeZoneId", "TopologySourceVersion")
            VALUES (
                {scopeId}, {propertyId}, TRUE, {"Legacy projection"},
                {0L}, {1}, {1}, {timeZoneId}, {topologySourceVersion});
            """);

    private static Task<int> DeletePropertyProjectionAsync(
        GuestsDbContext dbContext,
        string scopeId,
        Guid propertyId) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM guests.property_projection
            WHERE "ScopeId" = {scopeId}
              AND "Id" = {propertyId};
            """);

    private static Task<long> ReadTenantRevisionAsync(
        GuestsDbContext dbContext,
        string scopeId) =>
        dbContext.Database.SqlQuery<long>($"""
            SELECT "Revision" AS "Value"
            FROM guests.tenant_revisions
            WHERE "ScopeId" = {scopeId}
            """).SingleAsync();

    private static Task<int> CountTenantRevisionAsync(
        GuestsDbContext dbContext,
        string scopeId) =>
        dbContext.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::integer AS "Value"
            FROM guests.tenant_revisions
            WHERE "ScopeId" = {scopeId}
            """).SingleAsync();

    private static Task<int> CountCleanClosedRevisionAsync(
        GuestsDbContext dbContext,
        string scopeId,
        long revision,
        Guid operationId,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc) =>
        dbContext.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::integer AS "Value"
            FROM guests.tenant_revisions
            WHERE "ScopeId" = {scopeId}
              AND "Revision" = {revision}
              AND "LifecycleStatus" = 3
              AND "DestroyOperationId" = {operationId}
              AND "DestroyRequestSha256" = {new string('d', 64)}
              AND "DestroyStartedAtUtc" = {startedAtUtc}
              AND "DestroyCompletedAtUtc" = {completedAtUtc}
            """).SingleAsync();

    private static Task<int> CountCleanTenantDestroyReceiptAsync(
        GuestsDbContext dbContext,
        string scopeId,
        Guid operationId,
        long selectedRevision,
        long resultingRevision,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc) =>
        dbContext.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::integer AS "Value"
            FROM guests.tenant_destroy_receipts
            WHERE "ScopeId" = {scopeId}
              AND "OperationId" = {operationId}
              AND "RequestSha256" = {new string('d', 64)}
              AND "SelectedRevision" = {selectedRevision}
              AND "ResultingRevision" = {resultingRevision}
              AND "BatchSize" = 100
              AND "RemovedRecordCount" = 0
              AND "CompletedBatchCount" = 0
              AND "RemovalProofVersion" = 1
              AND "RemovalProofSha256" = {new string('e', 64)}
              AND "StartedAtUtc" = {startedAtUtc}
              AND "CompletedAtUtc" = {completedAtUtc}
            """).SingleAsync();

    private static Task<int> CountNonRetentionOwnerOnlyAsync(
        GuestsDbContext dbContext,
        string scopeId,
        Guid holdId) =>
        dbContext.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::integer AS "Value"
            FROM guests.data_holds hold
            WHERE hold."ScopeId" = {scopeId}
              AND hold."Id" = {holdId}
              AND NOT EXISTS (
                  SELECT 1
                  FROM guests.guest_retention_executions execution
                  WHERE execution."ScopeId" = {scopeId})
              AND NOT EXISTS (
                  SELECT 1
                  FROM guests.guest_retention_anonymisation_receipts receipt
                  WHERE receipt."ScopeId" = {scopeId})
            """).SingleAsync();

    private static async Task AssertLegacyBackfillAsync(
        GuestsDbContext dbContext,
        string scopeId,
        Guid propertyId,
        int expectedEvidenceSource,
        long expectedEvidenceSourceVersion)
    {
        Assert.Equal(
            1,
            await dbContext.Database.SqlQuery<int>($"""
                SELECT COUNT(*)::integer AS "Value"
                FROM guests.property_projection
                WHERE "ScopeId" = {scopeId}
                  AND "Id" = {propertyId}
                  AND "TimeZoneStatus" = 0
                  AND "CanonicalTimeZoneId" IS NULL
                  AND "TimeZoneCatalogVersion" IS NULL
                  AND "TimeZoneEvidenceSource" = {expectedEvidenceSource}
                  AND "TimeZoneEvidenceSourceVersion" =
                      {expectedEvidenceSourceVersion}
                """).SingleAsync().ConfigureAwait(false));
    }

    private static Task<int> CountColumnAsync(
        GuestsDbContext dbContext,
        string tableName,
        string columnName) =>
        dbContext.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::integer AS "Value"
            FROM information_schema.columns
            WHERE table_schema = 'guests'
              AND table_name = {tableName}
              AND column_name = {columnName}
            """).SingleAsync();

    private static DataRightsExportRecord CreateRetentionExportRecord(
        GuestRetentionAnonymisationReceipt receipt) =>
        GuestsTenantTerminationExportSchema.CreateRecord(
            GuestsTenantTerminationMetadata
                .RetentionAnonymisationReceiptRecordType,
            receipt.Id,
            receipt.ResultingGuestVersion,
            new GuestRetentionAnonymisationReceiptTenantExport(
                receipt.ScopeId,
                receipt.GuestId,
                receipt.Id,
                new GuestRetentionAnonymisationProofTenantExport(
                    receipt.ContractVersion,
                    receipt.ExecutionId,
                    receipt.SelectedGuestVersion,
                    receipt.ResultingGuestVersion,
                    receipt.AffectedPropertyCount,
                    receipt.RetentionDeadlineUtc,
                    receipt.PolicySetSha256,
                    receipt.TimeZoneCatalogVersion,
                    receipt.EventId,
                    receipt.CompletedAtUtc,
                    receipt.CanonicalSha256),
                new GuestActorStaffTenantExport(receipt.ActorId)));

    private static string ComputeV1CanonicalSha256(
        Guid receiptId,
        Guid executionId,
        Guid guestId,
        long selectedGuestVersion,
        long resultingGuestVersion,
        int affectedPropertyCount,
        DateTimeOffset retentionDeadlineUtc,
        string policySetSha256,
        Guid eventId,
        string actorId,
        DateTimeOffset completedAtUtc)
    {
        StringBuilder canonical = new();
        Append(canonical, "1");
        Append(canonical, receiptId.ToString("N"));
        Append(canonical, TenantId);
        Append(canonical, executionId.ToString("N"));
        Append(canonical, guestId.ToString("N"));
        Append(
            canonical,
            selectedGuestVersion.ToString(CultureInfo.InvariantCulture));
        Append(
            canonical,
            resultingGuestVersion.ToString(CultureInfo.InvariantCulture));
        Append(
            canonical,
            affectedPropertyCount.ToString(CultureInfo.InvariantCulture));
        Append(
            canonical,
            retentionDeadlineUtc.ToUniversalTime().ToString(
                "O",
                CultureInfo.InvariantCulture));
        Append(canonical, policySetSha256);
        Append(canonical, eventId.ToString("N"));
        Append(canonical, actorId);
        Append(
            canonical,
            completedAtUtc.ToUniversalTime().ToString(
                "O",
                CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static void Append(StringBuilder target, string value)
    {
        target.Append(
            value.Length.ToString(CultureInfo.InvariantCulture));
        target.Append(':');
        target.Append(value);
    }

    private sealed class MigrationScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => TenantId;
    }
}
