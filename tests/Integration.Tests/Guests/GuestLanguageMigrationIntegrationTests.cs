namespace Integration.Tests;

using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.DataRights;
using BunkFy.Modules.Guests.Domain.Models;
using BunkFy.Modules.Guests.Persistence;
using Gma.Framework.Scoping;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class GuestLanguageMigrationIntegrationTests
{
    private const string PreviousMigration = "20260822005901_AddGuestRetentionRetryAttemptIntegrity";
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 0, 0, 0, TimeSpan.Zero);

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Actual_upgrade_backfills_exact_values_preserves_v1_receipt_and_roundtrips_array_changes()
    {
        await using PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("bunkfy_guest_multilanguage_upgrade_tests").Build();
        await postgres.StartAsync();
        await using GuestsDbContext db = Context(postgres.GetConnectionString());
        await db.Database.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        GuestProfile legacy = Profile("EN-gB");
        Assert.True(legacy.Update(legacy.DisplayName, null, null, null, null, null,
            legacy.PreferredLanguageTag, null, legacy.Version, "operator", Guid.NewGuid(), Now).IsSuccess);
        GuestProfile unknown = Profile("Unknown-١");
        GuestProfile empty = Profile(null);
        GuestProfile anonymised = GuestProfile.RestoreMissingAnonymised(Guid.NewGuid(), "tenant-a",
            Guid.NewGuid(), "privacy", Guid.NewGuid(), Now, Now).Value;
        await GuestLanguageMigrationSeed.ProfilesAsync(db, legacy, unknown, empty, anonymised);
        Guid receiptId = Guid.NewGuid(), operationId = Guid.NewGuid(), caseId = Guid.NewGuid();
        Guid eventId = Guid.NewGuid(), completionId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO guests.guest_data_rights_correction_receipts (
                "Id", "ScopeId", "ContractVersion", "IdempotencyKey", "PropertyId", "CaseId",
                "ApprovalRevision", "GuestId", "SelectedRecordVersion", "CurrentRecordVersion",
                "ChangedFieldsMask", "EventId", "CompletionEventId", "CompletedAtUtc")
            VALUES ({receiptId}, {"tenant-a"}, 1, {operationId}, {legacy.OriginPropertyId}, {caseId},
                1, {legacy.Id}, 1, 2, 1, {eventId}, {completionId}, {Now});
            """);
        string before = await db.Database.SqlQueryRaw<string>(
            "SELECT row_to_json(r)::text AS \"Value\" FROM guests.guest_data_rights_correction_receipts r").SingleAsync();
        await db.Database.MigrateAsync();
        string after = await db.Database.SqlQueryRaw<string>(
            "SELECT row_to_json(r)::text AS \"Value\" FROM guests.guest_data_rights_correction_receipts r").SingleAsync();
        Assert.Equal(before, after);
        GuestDataRightsCorrectionReceipt old = await db.DataRightsCorrectionReceipts.SingleAsync();
        Assert.Equal(1, old.ContractVersion);
        Assert.Equal(GuestProfileField.DisplayName, Assert.Single(old.ChangedFields));
        Assert.Equal(eventId, old.EventId);
        Assert.Empty(old.DomainEvents);
        await db.Database.GetService<IMigrator>().MigrateAsync(PreviousMigration);
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>(
            "SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_schema = 'guests' AND table_name = 'guest_profiles' AND column_name = 'LanguageTags'").SingleAsync());
        await db.Database.MigrateAsync();
        Assert.Equal(before, await db.Database.SqlQueryRaw<string>(
            "SELECT row_to_json(r)::text AS \"Value\" FROM guests.guest_data_rights_correction_receipts r").SingleAsync());

        GuestProfile profile = await db.GuestProfiles.SingleAsync(p => p.Id == legacy.Id);
        Assert.Equal("EN-gB", profile.PreferredLanguageTag);
        Assert.Equal("EN-gB", Assert.Single(profile.LanguageTags));
        Assert.Equal("Unknown-١", Assert.Single((await db.GuestProfiles.SingleAsync(p => p.Id == unknown.Id)).LanguageTags));
        Assert.Empty((await db.GuestProfiles.SingleAsync(p => p.Id == empty.Id)).LanguageTags);
        Assert.Empty((await db.GuestProfiles.SingleAsync(p => p.Id == anonymised.Id)).LanguageTags);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE guests.guest_profiles SET "LanguageTags" = ARRAY['en-gb'] WHERE "Id" = {legacy.Id}
            """);
        PostgresException spellingDown = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.GetService<IMigrator>().MigrateAsync(PreviousMigration));
        Assert.Equal(PostgresErrorCodes.RaiseException, spellingDown.SqlState);
        Assert.Equal(before, await db.Database.SqlQueryRaw<string>(
            "SELECT row_to_json(r)::text AS \"Value\" FROM guests.guest_data_rights_correction_receipts r").SingleAsync());
        Assert.Contains("20260913013156_AddGuestLanguageTags", await db.Database.GetAppliedMigrationsAsync());
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE guests.guest_profiles SET "LanguageTags" = ARRAY['EN-gB'] WHERE "Id" = {legacy.Id}
            """);
        Assert.True(Change(profile, "EN-gB", ["en-gb", "fr", "unknown-١"]));
        profile.ClearDomainEvents();
        // The production context also advances its tenant revision, unlike the isolated mapping spike.
        Assert.True(await db.SaveChangesAsync() > 0);
        Assert.True(Change(profile, "EN-gB", ["UNKNOWN-١", "FR", "EN-GB"]));
        db.ChangeTracker.DetectChanges();
        Assert.False(db.Entry(profile).Property(p => p.LanguageTags).IsModified);
        profile.ClearDomainEvents();
        Assert.True(await db.SaveChangesAsync() > 0);
        Assert.True(Change(profile, null, ["fr"]));
        profile.ClearDomainEvents();
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        profile = await db.GuestProfiles.SingleAsync(p => p.Id == legacy.Id);
        Assert.Equal("fr", Assert.Single(profile.LanguageTags));
        Assert.Null(profile.PreferredLanguageTag);

        PostgresException unsafeDown = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.GetService<IMigrator>().MigrateAsync(PreviousMigration));
        Assert.Equal(PostgresErrorCodes.RaiseException, unsafeDown.SqlState);
        Assert.Equal("fr", Assert.Single(await db.GuestProfiles.Where(p => p.Id == legacy.Id)
            .SelectMany(p => p.LanguageTags).ToArrayAsync()));
        Assert.True(Change(profile, null, []));
        profile.ClearDomainEvents();
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Empty((await db.GuestProfiles.SingleAsync(p => p.Id == legacy.Id)).LanguageTags);

        PostgresException invalid = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE guests.guest_profiles SET "LanguageTags" = ARRAY['fr'] WHERE "Id" = {anonymised.Id}
                """));
        Assert.Equal("CK_guest_profiles_anonymised_profile", invalid.ConstraintName);
        await AssertInvalidReceiptAsync(db, receiptId, 1, 256);
        await AssertInvalidReceiptAsync(db, receiptId, 2, 512);
        await AssertInvalidReceiptAsync(db, receiptId, 2, 0);
        await AssertInvalidReceiptAsync(db, receiptId, 3, 1);
        GuestDataRightsCorrectionReceipt newReceipt = GuestDataRightsCorrectionReceipt.Create(
            Guid.NewGuid(), "tenant-a", Guid.NewGuid(), legacy.OriginPropertyId, Guid.NewGuid(),
            1, legacy.Id, profile.Version - 1, profile.Version, [GuestProfileField.LanguageTags],
            Guid.NewGuid(), Guid.NewGuid(), Now).Value;
        newReceipt.ClearDomainEvents();
        db.DataRightsCorrectionReceipts.Add(newReceipt);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        GuestDataRightsCorrectionReceipt current = await db.DataRightsCorrectionReceipts.SingleAsync(r => r.Id == newReceipt.Id);
        Assert.Equal(2, current.ContractVersion);
        Assert.Equal(GuestProfileField.LanguageTags, Assert.Single(current.ChangedFields));
        Assert.Equal(1, (await db.DataRightsCorrectionReceipts.SingleAsync(r => r.Id == receiptId)).ContractVersion);
        PostgresException v2Down = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.GetService<IMigrator>().MigrateAsync(PreviousMigration));
        Assert.Equal(PostgresErrorCodes.RaiseException, v2Down.SqlState);
        Assert.Equal(2, await db.DataRightsCorrectionReceipts.CountAsync());
        Assert.Empty((await db.GuestProfiles.SingleAsync(p => p.Id == legacy.Id)).LanguageTags);
    }

    private static async Task AssertInvalidReceiptAsync(GuestsDbContext db, Guid id, int version, int mask)
    {
        PostgresException error = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO guests.guest_data_rights_correction_receipts (
                    "Id", "ScopeId", "ContractVersion", "IdempotencyKey", "PropertyId", "CaseId",
                    "ApprovalRevision", "GuestId", "SelectedRecordVersion", "CurrentRecordVersion",
                    "ChangedFieldsMask", "EventId", "CompletionEventId", "CompletedAtUtc")
                SELECT {Guid.NewGuid()}, "ScopeId", {version}, {Guid.NewGuid()}, "PropertyId", {Guid.NewGuid()},
                    1, "GuestId", 99, 100, {mask}, {Guid.NewGuid()}, {Guid.NewGuid()}, "CompletedAtUtc"
                FROM guests.guest_data_rights_correction_receipts WHERE "Id" = {id}
                """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    private static bool Change(GuestProfile profile, string? preferred, string[] tags) =>
        profile.Update(profile.DisplayName, null, null, null, null, null, preferred, null,
            profile.Version, "operator", Guid.NewGuid(), Now, tags).IsSuccess;

    private static GuestProfile Profile(string? preferred) => GuestProfile.Create(Guid.NewGuid(), "tenant-a",
        Guid.NewGuid(), "Migration guest", null, null, null, null, null, preferred, null,
        "operator", Guid.NewGuid(), Now).Value;

    private static GuestsDbContext Context(string connection) => new(
        new DbContextOptionsBuilder<GuestsDbContext>().UseNpgsql(connection, provider => provider
            .MigrationsAssembly(GuestsMigrations.PostgreSqlAssembly)
            .MigrationsHistoryTable(GuestsMigrations.HistoryTable, GuestsMigrations.Schema)).Options,
        new Scope(), OpenWorkspaceTerminationFenceReader.Instance);

    private sealed class Scope : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }
}
