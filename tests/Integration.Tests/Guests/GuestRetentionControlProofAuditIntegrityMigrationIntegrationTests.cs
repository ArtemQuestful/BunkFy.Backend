namespace Integration.Tests;

using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.DataRights;
using BunkFy.Modules.Guests.Domain.Retention;
using BunkFy.Modules.Guests.Persistence;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Scoping;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class GuestRetentionControlProofAuditIntegrityMigrationIntegrationTests
{
    private const string BeforeRetentionControlProofAuditIntegrityMigration =
        "20260821223525_AddGuestAnonymisationProofAuditIntegrity";
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
    public async Task Migration_preserves_reachable_control_and_proof_states_and_rejects_malformed_rows()
    {
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder(
                "postgres:16-alpine")
            .WithDatabase("bunkfy_guest_retention_control_proof_audit_tests")
            .Build();
        await postgreSql.StartAsync();

        Guid propertyId = Guid.Parse(
            "31000000-0000-0000-0000-000000000090");
        Guid historicalExecutionId = Guid.Parse(
            "93000000-0000-0000-0000-000000000090");
        Guid currentExecutionId = Guid.Parse(
            "93000000-0000-0000-0000-000000000091");
        Guid runningExecutionId = Guid.Parse(
            "93000000-0000-0000-0000-000000000092");

        GuestRetentionExecution currentExecution = CreateTerminalExecution(
            currentExecutionId,
            "guest-operational",
            executionPolicyVersion: 2);
        GuestRetentionExecution runningExecution =
            GuestRetentionExecution.Start(
                runningExecutionId,
                ScopeId,
                "guest-running",
                executionPolicyVersion: 2,
                attempt: 1,
                startingProjectionOrdinal: 42,
                Now,
                Now.AddMinutes(15)).Value;

        GuestRetentionSweepCheckpoint initialCheckpoint =
            GuestRetentionSweepCheckpoint.Create(
                Guid.Parse("94000000-0000-0000-0000-000000000090"),
                ScopeId,
                "guest-initial",
                Now).Value;
        GuestRetentionSweepCheckpoint advancedCheckpoint =
            GuestRetentionSweepCheckpoint.Create(
                Guid.Parse("94000000-0000-0000-0000-000000000091"),
                ScopeId,
                "guest-operational",
                Now).Value;
        Assert.True(advancedCheckpoint.Advance(
            expectedAfterProjectionOrdinal: 0,
            nextAfterProjectionOrdinal: 42,
            currentExecutionId,
            Now.AddMinutes(2)).IsSuccess);

        RetentionProof historicalProof = CreateProof(
            Guid.Parse("11000000-0000-0000-0000-000000000090"),
            Guid.Parse("61000000-0000-0000-0000-000000000090"),
            historicalExecutionId,
            Guid.Parse("81000000-0000-0000-0000-000000000090"),
            propertyId,
            contractVersion: 1,
            Now.AddMinutes(1));
        RetentionProof currentProof = CreateProof(
            Guid.Parse("11000000-0000-0000-0000-000000000091"),
            Guid.Parse("61000000-0000-0000-0000-000000000091"),
            currentExecutionId,
            Guid.Parse("81000000-0000-0000-0000-000000000091"),
            propertyId,
            contractVersion: 2,
            Now.AddMinutes(1));
        RetentionProof supportProof = CreateProof(
            Guid.Parse("11000000-0000-0000-0000-000000000092"),
            Guid.Parse("61000000-0000-0000-0000-000000000092"),
            currentExecutionId,
            Guid.Parse("81000000-0000-0000-0000-000000000092"),
            propertyId,
            contractVersion: 2,
            Now.AddMinutes(1));
        GuestProfile profileWithoutTombstone = CreateProfile(
            Guid.Parse("11000000-0000-0000-0000-000000000093"),
            propertyId,
            "Profile without tombstone");

        await using (GuestsDbContext previous = CreateDbContext(
            postgreSql.GetConnectionString()))
        {
            await previous.Database.GetService<IMigrator>().MigrateAsync(
                BeforeRetentionControlProofAuditIntegrityMigration);
            await previous.Database.ExecuteSqlInterpolatedAsync(
                TerminalExecutionInsert(
                    historicalExecutionId,
                    "guest-historical",
                    "guests.guest-operational.completed",
                    version: 2,
                    executionPolicyVersion: 1));

            previous.RetentionExecutions.AddRange(
                currentExecution,
                runningExecution);
            previous.RetentionSweepCheckpoints.AddRange(
                initialCheckpoint,
                advancedCheckpoint);
            await GuestLanguageMigrationSeed.ProfilesAsync(previous,
                historicalProof.Profile,
                currentProof.Profile,
                supportProof.Profile,
                profileWithoutTombstone);
            previous.AnonymisationTombstones.AddRange(
                historicalProof.Tombstone,
                currentProof.Tombstone,
                supportProof.Tombstone);
            previous.RetentionAnonymisationReceipts.AddRange(
                historicalProof.Receipt,
                currentProof.Receipt);
            await previous.SaveChangesAsync();
        }

        await using GuestsDbContext upgraded = CreateDbContext(
            postgreSql.GetConnectionString());
        await upgraded.Database.MigrateAsync();

        Assert.Equal(3, await upgraded.RetentionExecutions.CountAsync());
        Assert.Equal(2, await upgraded.RetentionSweepCheckpoints.CountAsync());
        Assert.Equal(
            2,
            await upgraded.RetentionAnonymisationReceipts.CountAsync());
        Assert.Equal(
            GuestRetentionExecutionState.Running,
            (await upgraded.RetentionExecutions.AsNoTracking().SingleAsync(
                execution => execution.Id == runningExecutionId)).State);
        Assert.Equal(
            1,
            (await upgraded.RetentionExecutions.AsNoTracking().SingleAsync(
                execution => execution.Id == historicalExecutionId))
                .ExecutionPolicyVersion);
        Assert.Null((await upgraded.RetentionSweepCheckpoints
            .AsNoTracking()
            .SingleAsync(checkpoint => checkpoint.Id == initialCheckpoint.Id))
            .LastExecutionId);
        Assert.Equal(
            currentExecutionId,
            (await upgraded.RetentionSweepCheckpoints
                .AsNoTracking()
                .SingleAsync(checkpoint =>
                    checkpoint.Id == advancedCheckpoint.Id)).LastExecutionId);
        int[] receiptContractVersions = await upgraded
            .RetentionAnonymisationReceipts
            .AsNoTracking()
            .OrderBy(receipt => receipt.ContractVersion)
            .Select(receipt => receipt.ContractVersion)
            .ToArrayAsync();
        Assert.Collection(
            receiptContractVersions,
            version => Assert.Equal(1, version),
            version => Assert.Equal(2, version));
        foreach (GuestRetentionAnonymisationReceipt receipt in await upgraded
                     .RetentionAnonymisationReceipts
                     .AsNoTracking()
                     .ToArrayAsync())
        {
            Assert.True(await upgraded.AnonymisationTombstones
                .AsNoTracking()
                .AnyAsync(tombstone =>
                    tombstone.ScopeId == receipt.ScopeId &&
                    tombstone.Id == receipt.GuestId));
        }

        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_retention_executions_coordinates",
            RunningExecutionInsert(
                Guid.Empty,
                "guest-coordinate",
                Now));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_retention_executions_key",
            RunningExecutionInsert(
                Guid.Parse("93000000-0000-0000-0000-000000000093"),
                "Guest-Uppercase",
                Now));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_retention_executions_timestamp",
            RunningExecutionInsert(
                Guid.Parse("93000000-0000-0000-0000-000000000094"),
                "guest-timestamp",
                default));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_retention_executions_version",
            TerminalExecutionInsert(
                Guid.Parse("93000000-0000-0000-0000-000000000095"),
                "guest-version",
                "guests.guest-operational.completed",
                version: 1,
                executionPolicyVersion: 2));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_retention_executions_state",
            TerminalExecutionInsert(
                Guid.Parse("93000000-0000-0000-0000-000000000096"),
                "guest-outcome",
                "guests/invalid/outcome",
                version: 2,
                executionPolicyVersion: 2));

        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_retention_sweep_checkpoints_coordinates",
            CheckpointInsert(
                Guid.Empty,
                "guest-coordinate",
                afterProjectionOrdinal: 0,
                lastExecutionId: null,
                Now,
                version: 1));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_retention_sweep_checkpoints_key",
            CheckpointInsert(
                Guid.Parse("94000000-0000-0000-0000-000000000092"),
                "Guest-Uppercase",
                afterProjectionOrdinal: 0,
                lastExecutionId: null,
                Now,
                version: 1));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_retention_sweep_checkpoints_lifecycle",
            CheckpointInsert(
                Guid.Parse("94000000-0000-0000-0000-000000000093"),
                "guest-lifecycle",
                afterProjectionOrdinal: 1,
                lastExecutionId: null,
                Now,
                version: 1));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_retention_sweep_checkpoints_timestamp",
            CheckpointInsert(
                Guid.Parse("94000000-0000-0000-0000-000000000094"),
                "guest-timestamp",
                afterProjectionOrdinal: 0,
                lastExecutionId: null,
                updatedAtUtc: default,
                version: 1));
        await AssertUniqueViolationAsync(
            upgraded,
            "UX_guest_retention_checkpoints_last_execution",
            CheckpointInsert(
                Guid.Parse("94000000-0000-0000-0000-000000000095"),
                "guest-duplicate-execution",
                afterProjectionOrdinal: 99,
                currentExecutionId,
                Now.AddMinutes(3),
                version: 2));

        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_retention_receipts_coordinates",
            ReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000093"),
                currentExecutionId,
                supportProof.Profile.Id,
                eventId: Guid.Empty,
                GuestRetentionAnonymisationReceipt.SystemActorId,
                Now.AddDays(-1),
                Now));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_retention_receipts_actor",
            ReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000094"),
                currentExecutionId,
                supportProof.Profile.Id,
                Guid.Parse("81000000-0000-0000-0000-000000000094"),
                "user:operator",
                Now.AddDays(-1),
                Now));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_retention_receipts_digests",
            ReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000095"),
                currentExecutionId,
                supportProof.Profile.Id,
                Guid.Parse("81000000-0000-0000-0000-000000000095"),
                GuestRetentionAnonymisationReceipt.SystemActorId,
                Now.AddDays(-1),
                Now,
                policyDigest: new string('A', 64)));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_retention_receipts_timestamps",
            ReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000096"),
                currentExecutionId,
                supportProof.Profile.Id,
                Guid.Parse("81000000-0000-0000-0000-000000000096"),
                GuestRetentionAnonymisationReceipt.SystemActorId,
                retentionDeadlineUtc: default,
                Now));
        await AssertForeignKeyViolationAsync(
            upgraded,
            "FK_guest_retention_receipts_execution",
            ReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000097"),
                Guid.Parse("93000000-0000-0000-0000-000000000097"),
                supportProof.Profile.Id,
                Guid.Parse("81000000-0000-0000-0000-000000000097"),
                GuestRetentionAnonymisationReceipt.SystemActorId,
                Now.AddDays(-1),
                Now));
        await AssertForeignKeyViolationAsync(
            upgraded,
            "FK_guest_retention_receipts_tombstone",
            ReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000098"),
                currentExecutionId,
                profileWithoutTombstone.Id,
                Guid.Parse("81000000-0000-0000-0000-000000000098"),
                GuestRetentionAnonymisationReceipt.SystemActorId,
                Now.AddDays(-1),
                Now));
        await AssertUniqueViolationAsync(
            upgraded,
            "UX_guest_retention_receipts_event",
            ReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000099"),
                currentExecutionId,
                supportProof.Profile.Id,
                currentProof.Receipt.EventId,
                GuestRetentionAnonymisationReceipt.SystemActorId,
                Now.AddDays(-1),
                Now));
        await AssertUniqueViolationAsync(
            upgraded,
            "UX_guest_retention_receipts_guest",
            ReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000100"),
                currentExecutionId,
                currentProof.Profile.Id,
                Guid.Parse("81000000-0000-0000-0000-000000000100"),
                GuestRetentionAnonymisationReceipt.SystemActorId,
                Now.AddDays(-1),
                Now));

        await upgraded.Database.GetService<IMigrator>().MigrateAsync(
            BeforeRetentionControlProofAuditIntegrityMigration);
        Assert.Equal(3, await upgraded.RetentionExecutions.CountAsync());
        Assert.Equal(2, await upgraded.RetentionSweepCheckpoints.CountAsync());
        Assert.Equal(
            2,
            await upgraded.RetentionAnonymisationReceipts.CountAsync());
        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_retention_executions_state",
            TerminalExecutionInsert(
                Guid.Parse("93000000-0000-0000-0000-000000000101"),
                "guest-old-outcome",
                "guests/invalid/outcome",
                version: 2,
                executionPolicyVersion: 2));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_retention_receipts_digests",
            ReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000101"),
                currentExecutionId,
                supportProof.Profile.Id,
                Guid.Parse("81000000-0000-0000-0000-000000000101"),
                GuestRetentionAnonymisationReceipt.SystemActorId,
                Now.AddDays(-1),
                Now,
                policyDigest: new string('A', 64)));
        await upgraded.Database.MigrateAsync();
    }

    private static GuestRetentionExecution CreateTerminalExecution(
        Guid executionId,
        string dataClassKey,
        int executionPolicyVersion)
    {
        GuestRetentionExecution execution = GuestRetentionExecution.Start(
            executionId,
            ScopeId,
            dataClassKey,
            executionPolicyVersion,
            attempt: 1,
            startingProjectionOrdinal: 0,
            Now,
            Now.AddMinutes(15)).Value;
        Assert.True(execution.RecordAffected().IsSuccess);
        Assert.True(execution.Complete(
            GuestRetentionExecutionState.Completed,
            attempt: execution.Attempt,
            scannedCount: 1,
            remainingCount: 0,
            "guests.guest-operational.completed",
            Now.AddMinutes(2),
            holdReviewDueAtUtc: null).IsSuccess);
        return execution;
    }

    private static RetentionProof CreateProof(
        Guid guestId,
        Guid receiptId,
        Guid executionId,
        Guid eventId,
        Guid propertyId,
        int contractVersion,
        DateTimeOffset completedAtUtc)
    {
        GuestProfile profile = CreateProfile(
            guestId,
            propertyId,
            $"Retention guest {guestId:N}");
        long selectedVersion = profile.Version;
        Assert.True(profile.AnonymiseForRetention(
            selectedVersion,
            GuestRetentionAnonymisationReceipt.SystemActorId,
            eventId,
            completedAtUtc).IsSuccess);
        GuestRetentionAnonymisationReceipt receipt =
            GuestRetentionAnonymisationReceipt.Create(
                receiptId,
                ScopeId,
                executionId,
                guestId,
                selectedVersion,
                profile.Version,
                affectedPropertyCount: 1,
                completedAtUtc.AddDays(-1),
                DigestA,
                "tzdb-2026a",
                eventId,
                GuestRetentionAnonymisationReceipt.SystemActorId,
                completedAtUtc).Value;
        if (contractVersion == 1)
        {
            Set(receipt, nameof(receipt.ContractVersion), 1);
            Set(receipt, nameof(receipt.TimeZoneCatalogVersion), null);
            Set(
                receipt,
                nameof(receipt.CanonicalSha256),
                ComputeVersionOneCanonicalSha256(receipt));
        }

        GuestAnonymisationTombstone tombstone =
            GuestAnonymisationTombstone.CreateForRetention(
                ScopeId,
                receipt).Value;
        return new(profile, receipt, tombstone);
    }

    private static GuestProfile CreateProfile(
        Guid guestId,
        Guid propertyId,
        string displayName) => GuestProfile.Create(
            guestId,
            ScopeId,
            propertyId,
            displayName,
            legalName: null,
            email: null,
            phone: null,
            dateOfBirth: null,
            nationalityCountryCode: null,
            preferredLanguageTag: null,
            notes: null,
            "user:creator",
            Guid.NewGuid(),
            Now.AddMinutes(-1)).Value;

    private static FormattableString RunningExecutionInsert(
        Guid executionId,
        string dataClassKey,
        DateTimeOffset startedAtUtc) => $"""
        INSERT INTO guests.guest_retention_executions
            ("Id", "DataClassKey", "ExecutionPolicyVersion", "Attempt",
             "StartingProjectionOrdinal", "State", "StartedAtUtc",
             "DeadlineUtc", "CompletedAtUtc", "AffectedCount",
             "ScannedCount", "RemainingCount", "OutcomeCode",
             "HoldReviewDueAtUtc", "Version", "ScopeId")
        VALUES
            ({executionId}, {dataClassKey}, {2}, {1}, {0L},
             {(int)GuestRetentionExecutionState.Running}, {startedAtUtc},
             {Now.AddMinutes(15)}, NULL, {0}, NULL, NULL, NULL, NULL, {1L},
             {ScopeId});
        """;

    private static FormattableString TerminalExecutionInsert(
        Guid executionId,
        string dataClassKey,
        string outcomeCode,
        long version,
        int executionPolicyVersion) => $"""
        INSERT INTO guests.guest_retention_executions
            ("Id", "DataClassKey", "ExecutionPolicyVersion", "Attempt",
             "StartingProjectionOrdinal", "State", "StartedAtUtc",
             "DeadlineUtc", "CompletedAtUtc", "AffectedCount",
             "ScannedCount", "RemainingCount", "OutcomeCode",
             "HoldReviewDueAtUtc", "Version", "ScopeId")
        VALUES
            ({executionId}, {dataClassKey}, {executionPolicyVersion}, {1},
             {0L}, {(int)GuestRetentionExecutionState.Completed}, {Now},
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
        INSERT INTO guests.guest_retention_sweep_checkpoints
            ("Id", "DataClassKey", "AfterProjectionOrdinal",
             "LastExecutionId", "UpdatedAtUtc", "Version", "ScopeId")
        VALUES
            ({checkpointId}, {dataClassKey}, {afterProjectionOrdinal},
             {lastExecutionId}, {updatedAtUtc}, {version}, {ScopeId});
        """;

    private static FormattableString ReceiptInsert(
        Guid receiptId,
        Guid executionId,
        Guid guestId,
        Guid eventId,
        string actorId,
        DateTimeOffset retentionDeadlineUtc,
        DateTimeOffset completedAtUtc,
        string? policyDigest = null) => $"""
        INSERT INTO guests.guest_retention_anonymisation_receipts
            ("Id", "ContractVersion", "ExecutionId", "GuestId",
             "SelectedGuestVersion", "ResultingGuestVersion",
             "AffectedPropertyCount", "RetentionDeadlineUtc",
             "PolicySetSha256", "TimeZoneCatalogVersion", "EventId",
             "ActorId", "CompletedAtUtc", "CanonicalSha256", "ScopeId")
        VALUES
            ({receiptId}, {2}, {executionId}, {guestId}, {2L}, {3L}, {1},
             {retentionDeadlineUtc}, {policyDigest ?? DigestA},
             {"tzdb-2026a"}, {eventId}, {actorId}, {completedAtUtc},
             {DigestC}, {ScopeId});
        """;

    private static string ComputeVersionOneCanonicalSha256(
        GuestRetentionAnonymisationReceipt receipt)
    {
        StringBuilder canonical = new();
        Append(canonical, "1");
        Append(canonical, receipt.Id.ToString("N"));
        Append(canonical, receipt.ScopeId);
        Append(canonical, receipt.ExecutionId.ToString("N"));
        Append(canonical, receipt.GuestId.ToString("N"));
        Append(
            canonical,
            receipt.SelectedGuestVersion.ToString(CultureInfo.InvariantCulture));
        Append(
            canonical,
            receipt.ResultingGuestVersion.ToString(CultureInfo.InvariantCulture));
        Append(
            canonical,
            receipt.AffectedPropertyCount.ToString(CultureInfo.InvariantCulture));
        Append(
            canonical,
            receipt.RetentionDeadlineUtc.ToUniversalTime().ToString(
                "O",
                CultureInfo.InvariantCulture));
        Append(canonical, receipt.PolicySetSha256);
        Append(canonical, receipt.EventId.ToString("N"));
        Append(canonical, receipt.ActorId);
        Append(
            canonical,
            receipt.CompletedAtUtc.ToUniversalTime().ToString(
                "O",
                CultureInfo.InvariantCulture));
        return Convert.ToHexString(
                SHA256.HashData(
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

    private static void Set(
        GuestRetentionAnonymisationReceipt receipt,
        string propertyName,
        object? value) =>
        typeof(GuestRetentionAnonymisationReceipt)
            .GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(receipt, value);

    private static async Task AssertCheckViolationAsync(
        GuestsDbContext dbContext,
        string expectedConstraint,
        FormattableString command)
    {
        PostgresException failure = await Assert.ThrowsAsync<PostgresException>(
            () => dbContext.Database.ExecuteSqlInterpolatedAsync(command));
        Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
        Assert.Equal(expectedConstraint, failure.ConstraintName);
    }

    private static async Task AssertForeignKeyViolationAsync(
        GuestsDbContext dbContext,
        string expectedConstraint,
        FormattableString command)
    {
        PostgresException failure = await Assert.ThrowsAsync<PostgresException>(
            () => dbContext.Database.ExecuteSqlInterpolatedAsync(command));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, failure.SqlState);
        Assert.Equal(expectedConstraint, failure.ConstraintName);
    }

    private static async Task AssertUniqueViolationAsync(
        GuestsDbContext dbContext,
        string expectedConstraint,
        FormattableString command)
    {
        PostgresException failure = await Assert.ThrowsAsync<PostgresException>(
            () => dbContext.Database.ExecuteSqlInterpolatedAsync(command));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, failure.SqlState);
        Assert.Equal(expectedConstraint, failure.ConstraintName);
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
            new TestScopeContext(),
            OpenWorkspaceTerminationFenceReader.Instance);
    }

    private sealed record RetentionProof(
        GuestProfile Profile,
        GuestRetentionAnonymisationReceipt Receipt,
        GuestAnonymisationTombstone Tombstone);

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId =>
            GuestRetentionControlProofAuditIntegrityMigrationIntegrationTests
                .ScopeId;
    }
}
