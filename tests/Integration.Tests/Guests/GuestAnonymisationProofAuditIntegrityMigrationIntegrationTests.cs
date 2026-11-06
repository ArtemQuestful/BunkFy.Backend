namespace Integration.Tests;

using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.DataRights;
using BunkFy.Modules.Guests.Domain.Models;
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

public sealed class GuestAnonymisationProofAuditIntegrityMigrationIntegrationTests
{
    private const string BeforeAnonymisationProofAuditIntegrityMigration =
        "20260821221117_AddGuestCorrectionReceiptAuditIntegrity";
    private const string ScopeId = "tenant-a";
    private static readonly string DigestA = new('a', 64);
    private static readonly string DigestB = new('b', 64);
    private static readonly string DigestC = new('c', 64);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Proof_migration_preserves_reachable_states_and_rejects_malformed_rows()
    {
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder(
                "postgres:16-alpine")
            .WithDatabase("bunkfy_guest_anonymisation_proof_audit_tests")
            .Build();
        await postgreSql.StartAsync();

        DateTimeOffset createdAtUtc = new(
            2026,
            8,
            21,
            12,
            0,
            0,
            TimeSpan.Zero);
        Guid propertyId = Guid.Parse(
            "31000000-0000-0000-0000-000000000080");

        Guid ordinaryGuestId = Guid.Parse(
            "11000000-0000-0000-0000-000000000080");
        Guid ordinaryEventId = Guid.Parse(
            "81000000-0000-0000-0000-000000000080");
        GuestProfile ordinaryProfile = CreateProfile(
            ordinaryGuestId,
            propertyId,
            "Ordinary anonymised guest",
            createdAtUtc);
        long ordinarySelectedVersion = ordinaryProfile.Version;
        Assert.True(ordinaryProfile.Anonymise(
            ordinarySelectedVersion,
            "user:privacy",
            ordinaryEventId,
            createdAtUtc.AddMinutes(1)).IsSuccess);
        GuestAnonymisationReceipt ordinaryReceipt = CreateReceipt(
            Guid.Parse("61000000-0000-0000-0000-000000000080"),
            Guid.Parse("71000000-0000-0000-0000-000000000080"),
            propertyId,
            Guid.Parse("41000000-0000-0000-0000-000000000080"),
            ordinaryProfile,
            ordinarySelectedVersion,
            ordinaryEventId,
            createdAtUtc.AddMinutes(1));
        GuestAnonymisationTombstone ordinaryTombstone =
            GuestAnonymisationTombstone.Create(
                ScopeId,
                ordinaryGuestId,
                ordinaryReceipt.CompletedAtUtc,
                ordinaryReceipt.CanonicalSha256).Value;

        Guid attachedGuestId = Guid.Parse(
            "11000000-0000-0000-0000-000000000081");
        Guid attachedEventId = Guid.Parse(
            "81000000-0000-0000-0000-000000000081");
        GuestProfile attachedProfile = CreateProfile(
            attachedGuestId,
            propertyId,
            "Attached restore proof guest",
            createdAtUtc);
        long attachedSelectedVersion = attachedProfile.Version;
        Assert.True(attachedProfile.Anonymise(
            attachedSelectedVersion,
            "user:privacy",
            attachedEventId,
            createdAtUtc.AddMinutes(2)).IsSuccess);
        GuestAnonymisationReceipt attachedReceipt = CreateReceipt(
            Guid.Parse("61000000-0000-0000-0000-000000000081"),
            Guid.Parse("71000000-0000-0000-0000-000000000081"),
            propertyId,
            Guid.Parse("41000000-0000-0000-0000-000000000081"),
            attachedProfile,
            attachedSelectedVersion,
            attachedEventId,
            createdAtUtc.AddMinutes(2));
        GuestAnonymisationTombstone attachedTombstone =
            GuestAnonymisationTombstone.Create(
                ScopeId,
                attachedGuestId,
                attachedReceipt.CompletedAtUtc,
                attachedReceipt.CanonicalSha256).Value;
        Guid attachedLedgerEntryId = Guid.Parse(
            "91000000-0000-0000-0000-000000000081");
        Assert.True(attachedTombstone.AttachRestoreProof(
            attachedLedgerEntryId,
            attachedReceipt.CompletedAtUtc,
            attachedReceipt.CanonicalSha256,
            createdAtUtc.AddMinutes(4)).IsSuccess);

        Guid restoredGuestId = Guid.Parse(
            "11000000-0000-0000-0000-000000000082");
        Guid restoredLedgerEntryId = Guid.Parse(
            "91000000-0000-0000-0000-000000000082");
        DateTimeOffset restoredAtUtc = createdAtUtc.AddMinutes(3);
        DateTimeOffset replayedAtUtc = createdAtUtc.AddMinutes(5);
        GuestProfile restoredProfile = GuestProfile.RestoreMissingAnonymised(
            restoredGuestId,
            ScopeId,
            propertyId,
            "data-rights-restore",
            Guid.Parse("81000000-0000-0000-0000-000000000082"),
            restoredAtUtc,
            replayedAtUtc).Value;
        GuestAnonymisationTombstone restoredTombstone =
            GuestAnonymisationTombstone.Restore(
                ScopeId,
                restoredGuestId,
                restoredAtUtc,
                DigestB,
                restoredLedgerEntryId,
                replayedAtUtc).Value;
        GuestAnonymisationRestoreReceipt restoreReceipt =
            GuestAnonymisationRestoreReceipt.Create(
                ScopeId,
                restoredLedgerEntryId,
                restoredGuestId,
                ownerReceiptContractVersion: 1,
                Guid.Parse("92000000-0000-0000-0000-000000000082"),
                DigestB,
                restoredProfile.Version,
                restoredTombstone.Revision,
                replayedAtUtc).Value;

        Guid retentionGuestId = Guid.Parse(
            "11000000-0000-0000-0000-000000000083");
        Guid retentionEventId = Guid.Parse(
            "81000000-0000-0000-0000-000000000083");
        GuestProfile retentionProfile = CreateProfile(
            retentionGuestId,
            propertyId,
            "Retention anonymised guest",
            createdAtUtc);
        long retentionSelectedVersion = retentionProfile.Version;
        DateTimeOffset retentionCompletedAtUtc = createdAtUtc.AddDays(1);
        Assert.True(retentionProfile.AnonymiseForRetention(
            retentionSelectedVersion,
            "system:retention",
            retentionEventId,
            retentionCompletedAtUtc).IsSuccess);
        GuestRetentionAnonymisationReceipt retentionReceipt =
            GuestRetentionAnonymisationReceipt.Create(
                Guid.Parse("61000000-0000-0000-0000-000000000083"),
                ScopeId,
                Guid.Parse("93000000-0000-0000-0000-000000000083"),
                retentionGuestId,
                retentionSelectedVersion,
                retentionProfile.Version,
                affectedPropertyCount: 1,
                retentionCompletedAtUtc.AddDays(-1),
                DigestA,
                "tzdb-2026a",
                retentionEventId,
                "system:retention",
                retentionCompletedAtUtc).Value;
        GuestAnonymisationTombstone retentionTombstone =
            GuestAnonymisationTombstone.CreateForRetention(
                ScopeId,
                retentionReceipt).Value;

        await using (GuestsDbContext previous = CreateDbContext(
            postgreSql.GetConnectionString()))
        {
            await previous.Database.GetService<IMigrator>().MigrateAsync(
                BeforeAnonymisationProofAuditIntegrityMigration);
            await GuestLanguageMigrationSeed.ProfilesAsync(previous,
                ordinaryProfile,
                attachedProfile,
                restoredProfile,
                retentionProfile);
            previous.AnonymisationReceipts.AddRange(
                ordinaryReceipt,
                attachedReceipt);
            previous.AnonymisationTombstones.AddRange(
                ordinaryTombstone,
                attachedTombstone,
                restoredTombstone,
                retentionTombstone);
            previous.AnonymisationRestoreReceipts.Add(restoreReceipt);
            await previous.SaveChangesAsync();
        }

        await using GuestsDbContext upgraded = CreateDbContext(
            postgreSql.GetConnectionString());
        await upgraded.Database.MigrateAsync();

        GuestAnonymisationTombstone[] tombstones = await upgraded
            .AnonymisationTombstones
            .AsNoTracking()
            .OrderBy(tombstone => tombstone.Id)
            .ToArrayAsync();
        Assert.Equal(4, tombstones.Length);
        Assert.Contains(tombstones, tombstone =>
            tombstone.Id == ordinaryGuestId &&
            tombstone.Authority == GuestAnonymisationAuthority.DataRights &&
            tombstone.Revision == 1 &&
            !tombstone.LedgerEntryId.HasValue);
        Assert.Contains(tombstones, tombstone =>
            tombstone.Id == restoredGuestId &&
            tombstone.Authority == GuestAnonymisationAuthority.DataRights &&
            tombstone.Revision == 1 &&
            tombstone.LedgerEntryId == restoredLedgerEntryId);
        Assert.Contains(tombstones, tombstone =>
            tombstone.Id == attachedGuestId &&
            tombstone.Authority == GuestAnonymisationAuthority.DataRights &&
            tombstone.Revision == 2 &&
            tombstone.LedgerEntryId == attachedLedgerEntryId);
        Assert.Contains(tombstones, tombstone =>
            tombstone.Id == retentionGuestId &&
            tombstone.Authority == GuestAnonymisationAuthority.Retention &&
            tombstone.Revision == 1 &&
            !tombstone.LedgerEntryId.HasValue);
        Assert.Equal(
            2,
            await upgraded.AnonymisationReceipts.AsNoTracking().CountAsync());
        Assert.Equal(
            1,
            await upgraded.AnonymisationRestoreReceipts
                .AsNoTracking()
                .CountAsync());

        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_anonymisation_receipts_coordinates",
            AnonymisationReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000084"),
                Guid.Parse("71000000-0000-0000-0000-000000000084"),
                propertyId,
                Guid.Parse("41000000-0000-0000-0000-000000000084"),
                ordinaryGuestId,
                eventId: Guid.Empty,
                "user:privacy",
                createdAtUtc.AddMinutes(6)));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_anonymisation_receipts_actor",
            AnonymisationReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000085"),
                Guid.Parse("71000000-0000-0000-0000-000000000085"),
                propertyId,
                Guid.Parse("41000000-0000-0000-0000-000000000085"),
                ordinaryGuestId,
                Guid.Parse("81000000-0000-0000-0000-000000000085"),
                " user:privacy ",
                createdAtUtc.AddMinutes(6)));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_anonymisation_receipts_digests",
            AnonymisationReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000086"),
                Guid.Parse("71000000-0000-0000-0000-000000000086"),
                propertyId,
                Guid.Parse("41000000-0000-0000-0000-000000000086"),
                ordinaryGuestId,
                Guid.Parse("81000000-0000-0000-0000-000000000086"),
                "user:privacy",
                createdAtUtc.AddMinutes(6),
                approvalDigest: new string('A', 64)));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_anonymisation_receipts_timestamp",
            AnonymisationReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000087"),
                Guid.Parse("71000000-0000-0000-0000-000000000087"),
                propertyId,
                Guid.Parse("41000000-0000-0000-0000-000000000087"),
                ordinaryGuestId,
                Guid.Parse("81000000-0000-0000-0000-000000000087"),
                "user:privacy",
                completedAtUtc: default));
        await AssertForeignKeyViolationAsync(
            upgraded,
            "FK_guest_anonymisation_receipts_guest_profile",
            AnonymisationReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000088"),
                Guid.Parse("71000000-0000-0000-0000-000000000088"),
                propertyId,
                Guid.Parse("41000000-0000-0000-0000-000000000088"),
                Guid.Parse("11000000-0000-0000-0000-000000000088"),
                Guid.Parse("81000000-0000-0000-0000-000000000088"),
                "user:privacy",
                createdAtUtc.AddMinutes(6)));
        await AssertUniqueViolationAsync(
            upgraded,
            "UX_guest_anonymisation_receipts_event",
            AnonymisationReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000089"),
                Guid.Parse("71000000-0000-0000-0000-000000000089"),
                propertyId,
                Guid.Parse("41000000-0000-0000-0000-000000000089"),
                ordinaryGuestId,
                ordinaryEventId,
                "user:privacy",
                createdAtUtc.AddMinutes(6)));

        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_anonymisation_tombstones_lifecycle",
            $"""
            UPDATE guests.guest_anonymisation_tombstones
            SET "Revision" = {2L}
            WHERE "Id" = {ordinaryGuestId};
            """);
        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_anonymisation_tombstones_restore_pair",
            $"""
            UPDATE guests.guest_anonymisation_tombstones
            SET "LedgerEntryId" = {Guid.Parse(
                "91000000-0000-0000-0000-000000000090")}
            WHERE "Id" = {ordinaryGuestId};
            """);
        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_anonymisation_tombstones_receipt_digest",
            $"""
            UPDATE guests.guest_anonymisation_tombstones
            SET "OwnerReceiptSha256" = {new string('A', 64)}
            WHERE "Id" = {ordinaryGuestId};
            """);
        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_anonymisation_tombstones_timestamps",
            $"""
            UPDATE guests.guest_anonymisation_tombstones
            SET "CompletedAtUtc" = {default(DateTimeOffset)}
            WHERE "Id" = {ordinaryGuestId};
            """);
        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_anonymisation_tombstones_lifecycle",
            $"""
            UPDATE guests.guest_anonymisation_tombstones
            SET "Revision" = {2L}
            WHERE "Id" = {retentionGuestId};
            """);
        await AssertUniqueViolationAsync(
            upgraded,
            "UX_guest_anonymisation_tombstones_ledger_entry",
            $"""
            UPDATE guests.guest_anonymisation_tombstones
            SET "LedgerEntryId" = {restoredLedgerEntryId}
            WHERE "Id" = {attachedGuestId};
            """);

        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_anonymisation_restore_receipts_coordinates",
            RestoreReceiptInsert(
                Guid.Parse("91000000-0000-0000-0000-000000000091"),
                ordinaryGuestId,
                ownerReceiptId: Guid.Empty,
                tombstoneRevision: 1,
                createdAtUtc.AddMinutes(6)));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_anonymisation_restore_receipts_digests",
            RestoreReceiptInsert(
                Guid.Parse("91000000-0000-0000-0000-000000000092"),
                ordinaryGuestId,
                Guid.Parse("92000000-0000-0000-0000-000000000092"),
                tombstoneRevision: 1,
                createdAtUtc.AddMinutes(6),
                ownerDigest: new string('B', 64)));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_anonymisation_restore_receipts_timestamp",
            RestoreReceiptInsert(
                Guid.Parse("91000000-0000-0000-0000-000000000093"),
                ordinaryGuestId,
                Guid.Parse("92000000-0000-0000-0000-000000000093"),
                tombstoneRevision: 1,
                replayedAtUtc: default));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_anonymisation_restore_receipts_versions",
            RestoreReceiptInsert(
                Guid.Parse("91000000-0000-0000-0000-000000000094"),
                ordinaryGuestId,
                Guid.Parse("92000000-0000-0000-0000-000000000094"),
                tombstoneRevision: 3,
                createdAtUtc.AddMinutes(6)));
        await AssertForeignKeyViolationAsync(
            upgraded,
            "FK_guest_anonymisation_restore_receipts_tombstone",
            RestoreReceiptInsert(
                Guid.Parse("91000000-0000-0000-0000-000000000095"),
                Guid.Parse("11000000-0000-0000-0000-000000000095"),
                Guid.Parse("92000000-0000-0000-0000-000000000095"),
                tombstoneRevision: 1,
                createdAtUtc.AddMinutes(6)));
        await AssertUniqueViolationAsync(
            upgraded,
            "UX_guest_anonymisation_restore_receipts_tombstone",
            RestoreReceiptInsert(
                Guid.Parse("91000000-0000-0000-0000-000000000096"),
                restoredGuestId,
                Guid.Parse("92000000-0000-0000-0000-000000000096"),
                tombstoneRevision: 1,
                createdAtUtc.AddMinutes(6)));

        await upgraded.Database.GetService<IMigrator>().MigrateAsync(
            BeforeAnonymisationProofAuditIntegrityMigration);
        Assert.Equal(
            4,
            await upgraded.AnonymisationTombstones.AsNoTracking().CountAsync());
        Assert.Equal(
            2,
            await upgraded.AnonymisationReceipts.AsNoTracking().CountAsync());
        Assert.Equal(
            1,
            await upgraded.AnonymisationRestoreReceipts
                .AsNoTracking()
                .CountAsync());
        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_anonymisation_receipts_digests",
            AnonymisationReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000097"),
                Guid.Parse("71000000-0000-0000-0000-000000000097"),
                propertyId,
                Guid.Parse("41000000-0000-0000-0000-000000000097"),
                ordinaryGuestId,
                Guid.Parse("81000000-0000-0000-0000-000000000097"),
                "user:privacy",
                createdAtUtc.AddMinutes(6),
                approvalDigest: new string('A', 64)));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_anonymisation_tombstones_receipt_digest",
            $"""
            UPDATE guests.guest_anonymisation_tombstones
            SET "OwnerReceiptSha256" = {new string('A', 64)}
            WHERE "Id" = {ordinaryGuestId};
            """);
        await upgraded.Database.MigrateAsync();
    }

    private static GuestProfile CreateProfile(
        Guid guestId,
        Guid propertyId,
        string displayName,
        DateTimeOffset createdAtUtc) => GuestProfile.Create(
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
            "user:privacy",
            Guid.NewGuid(),
            createdAtUtc).Value;

    private static GuestAnonymisationReceipt CreateReceipt(
        Guid receiptId,
        Guid idempotencyKey,
        Guid propertyId,
        Guid caseId,
        GuestProfile profile,
        long selectedVersion,
        Guid eventId,
        DateTimeOffset completedAtUtc) => GuestAnonymisationReceipt.Create(
            receiptId,
            ScopeId,
            idempotencyKey,
            propertyId,
            caseId,
            approvalRevision: 1,
            operationRevision: 2,
            profile.Id,
            selectedVersion,
            profile.Version,
            affectedPropertyCount: 1,
            DigestA,
            DigestB,
            eventId,
            "user:privacy",
            completedAtUtc).Value;

    private static FormattableString AnonymisationReceiptInsert(
        Guid receiptId,
        Guid idempotencyKey,
        Guid propertyId,
        Guid caseId,
        Guid guestId,
        Guid eventId,
        string actorId,
        DateTimeOffset completedAtUtc,
        string? approvalDigest = null) => $"""
        INSERT INTO guests.guest_anonymisation_receipts
            ("Id", "ContractVersion", "IdempotencyKey", "RoutingPropertyId",
             "CaseId", "ApprovalRevision", "OperationRevision", "GuestId",
             "SelectedGuestVersion", "ResultingGuestVersion", "Disposition",
             "Reason", "AffectedPropertyCount", "ApprovalEvidenceSha256",
             "PolicySetSha256", "EventId", "ActorId", "CompletedAtUtc",
             "CanonicalSha256", "ScopeId")
        VALUES
            ({receiptId}, {GuestAnonymisationReceipt.CurrentContractVersion},
             {idempotencyKey}, {propertyId}, {caseId}, {1L}, {2L}, {guestId},
             {2L}, {3L}, {(int)GuestAnonymisationDisposition.Completed},
             {(int)GuestAnonymisationReason.ProfileAnonymised}, {1},
             {approvalDigest ?? DigestA}, {DigestB}, {eventId}, {actorId},
             {completedAtUtc}, {DigestC}, {ScopeId});
        """;

    private static FormattableString RestoreReceiptInsert(
        Guid ledgerEntryId,
        Guid guestId,
        Guid ownerReceiptId,
        long tombstoneRevision,
        DateTimeOffset replayedAtUtc,
        string? ownerDigest = null) => $"""
        INSERT INTO guests.guest_anonymisation_restore_receipts
            ("Id", "ContractVersion", "LedgerEntryId", "GuestId",
             "OwnerReceiptContractVersion", "OwnerReceiptId",
             "OwnerReceiptSha256", "ResultingGuestVersion",
             "TombstoneRevision", "ReplayedAtUtc", "CanonicalSha256",
             "ScopeId")
        VALUES
            ({ledgerEntryId},
             {GuestAnonymisationRestoreReceipt.CurrentContractVersion},
             {ledgerEntryId}, {guestId}, {1}, {ownerReceiptId},
             {ownerDigest ?? DigestB}, {2L}, {tombstoneRevision},
             {replayedAtUtc}, {DigestC}, {ScopeId});
        """;

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

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId =>
            GuestAnonymisationProofAuditIntegrityMigrationIntegrationTests
                .ScopeId;
    }
}
