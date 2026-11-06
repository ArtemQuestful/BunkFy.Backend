namespace Integration.Tests;

using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.DataRights;
using BunkFy.Modules.Guests.Domain.Models;
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

public sealed class GuestCorrectionReceiptAuditIntegrityMigrationIntegrationTests
{
    private const string BeforeCorrectionReceiptAuditIntegrityMigration =
        "20260821214454_AddGuestProcessingRestrictionAuditIntegrity";
    private const string ScopeId = "tenant-a";

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Correction_receipt_migration_preserves_valid_state_and_rejects_malformed_rows()
    {
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder(
                "postgres:16-alpine")
            .WithDatabase("bunkfy_guest_correction_receipt_audit_tests")
            .Build();
        await postgreSql.StartAsync();

        Guid propertyId = Guid.Parse(
            "31000000-0000-0000-0000-000000000070");
        Guid guestId = Guid.Parse(
            "11000000-0000-0000-0000-000000000070");
        Guid caseId = Guid.Parse(
            "41000000-0000-0000-0000-000000000070");
        Guid receiptId = Guid.Parse(
            "61000000-0000-0000-0000-000000000070");
        DateTimeOffset createdAtUtc = new(
            2026,
            8,
            21,
            12,
            0,
            0,
            TimeSpan.Zero);

        GuestProfile profile = GuestProfile.Create(
            guestId,
            ScopeId,
            propertyId,
            "Maya Chen",
            legalName: null,
            "maya@example.test",
            phone: null,
            dateOfBirth: null,
            nationalityCountryCode: null,
            preferredLanguageTag: null,
            notes: null,
            "user:privacy",
            Guid.Parse("81000000-0000-0000-0000-000000000070"),
            createdAtUtc).Value;
        Assert.True(profile.Update(
            "Maya Chen Updated",
            legalName: null,
            "maya@example.test",
            phone: null,
            dateOfBirth: null,
            nationalityCountryCode: null,
            preferredLanguageTag: null,
            notes: null,
            expectedVersion: 1,
            "user:privacy",
            Guid.Parse("81000000-0000-0000-0000-000000000071"),
            createdAtUtc.AddMinutes(1)).IsSuccess);
        GuestDataRightsCorrectionReceipt receipt =
            GuestDataRightsCorrectionReceipt.Create(
                receiptId,
                ScopeId,
                Guid.Parse("71000000-0000-0000-0000-000000000070"),
                propertyId,
                caseId,
                approvalRevision: 1,
                guestId,
                selectedRecordVersion: 1,
                currentRecordVersion: 2,
                [GuestProfileField.DisplayName],
                Guid.Parse("91000000-0000-0000-0000-000000000070"),
                Guid.Parse("92000000-0000-0000-0000-000000000070"),
                createdAtUtc.AddMinutes(1)).Value;

        await using (GuestsDbContext previous = CreateDbContext(
            postgreSql.GetConnectionString()))
        {
            await previous.Database.GetService<IMigrator>().MigrateAsync(
                BeforeCorrectionReceiptAuditIntegrityMigration);
            await GuestLanguageMigrationSeed.ProfilesAsync(previous, profile);
            await previous.Database.ExecuteSqlInterpolatedAsync(CorrectionReceiptInsert(
                receipt.Id, receipt.IdempotencyKey, receipt.PropertyId, receipt.CaseId,
                receipt.GuestId, receipt.SelectedRecordVersion, receipt.CurrentRecordVersion,
                receipt.EventId, receipt.CompletionEventId, receipt.CompletedAtUtc));
            await previous.SaveChangesAsync();
        }

        await using GuestsDbContext upgraded = CreateDbContext(
            postgreSql.GetConnectionString());
        await upgraded.Database.MigrateAsync();

        GuestDataRightsCorrectionReceipt persisted = await upgraded
            .DataRightsCorrectionReceipts
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == receiptId);
        Assert.Equal(guestId, persisted.GuestId);
        Assert.Equal(1, persisted.ContractVersion);
        Assert.Equal(2, persisted.CurrentRecordVersion);
        Assert.True(await upgraded.GuestProfiles
            .AsNoTracking()
            .AnyAsync(candidate => candidate.Id == persisted.GuestId));

        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_data_rights_correction_receipts_coordinates",
            CorrectionReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000071"),
                Guid.Parse("71000000-0000-0000-0000-000000000071"),
                propertyId,
                Guid.Parse("41000000-0000-0000-0000-000000000071"),
                guestId,
                selectedRecordVersion: 2,
                currentRecordVersion: 3,
                eventId: Guid.Empty,
                Guid.Parse("92000000-0000-0000-0000-000000000071"),
                createdAtUtc.AddMinutes(2)));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_data_rights_correction_receipts_timestamp",
            CorrectionReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000072"),
                Guid.Parse("71000000-0000-0000-0000-000000000072"),
                propertyId,
                Guid.Parse("41000000-0000-0000-0000-000000000072"),
                guestId,
                selectedRecordVersion: 2,
                currentRecordVersion: 3,
                Guid.Parse("91000000-0000-0000-0000-000000000072"),
                Guid.Parse("92000000-0000-0000-0000-000000000072"),
                completedAtUtc: default));
        await AssertForeignKeyViolationAsync(
            upgraded,
            "FK_guest_data_rights_correction_receipts_guest_profile",
            CorrectionReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000073"),
                Guid.Parse("71000000-0000-0000-0000-000000000073"),
                propertyId,
                Guid.Parse("41000000-0000-0000-0000-000000000073"),
                Guid.Parse("11000000-0000-0000-0000-000000000073"),
                selectedRecordVersion: 1,
                currentRecordVersion: 2,
                Guid.Parse("91000000-0000-0000-0000-000000000073"),
                Guid.Parse("92000000-0000-0000-0000-000000000073"),
                createdAtUtc.AddMinutes(2)));
        await AssertUniqueViolationAsync(
            upgraded,
            "UX_guest_correction_receipts_case_approval",
            CorrectionReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000074"),
                Guid.Parse("71000000-0000-0000-0000-000000000074"),
                propertyId,
                caseId,
                guestId,
                selectedRecordVersion: 2,
                currentRecordVersion: 3,
                Guid.Parse("91000000-0000-0000-0000-000000000074"),
                Guid.Parse("92000000-0000-0000-0000-000000000074"),
                createdAtUtc.AddMinutes(2)));
        await AssertUniqueViolationAsync(
            upgraded,
            "UX_guest_correction_receipts_guest_version",
            CorrectionReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000075"),
                Guid.Parse("71000000-0000-0000-0000-000000000075"),
                propertyId,
                Guid.Parse("41000000-0000-0000-0000-000000000075"),
                guestId,
                selectedRecordVersion: 1,
                currentRecordVersion: 2,
                Guid.Parse("91000000-0000-0000-0000-000000000075"),
                Guid.Parse("92000000-0000-0000-0000-000000000075"),
                createdAtUtc.AddMinutes(2)));

        await upgraded.Database.GetService<IMigrator>().MigrateAsync(
            BeforeCorrectionReceiptAuditIntegrityMigration);
        Assert.Equal(
            1,
            await upgraded.DataRightsCorrectionReceipts
                .AsNoTracking()
                .CountAsync());
        Assert.Equal(
            1,
            await upgraded.GuestProfiles.AsNoTracking().CountAsync());
        await upgraded.Database.MigrateAsync();
    }

    private static FormattableString CorrectionReceiptInsert(
        Guid receiptId,
        Guid idempotencyKey,
        Guid propertyId,
        Guid caseId,
        Guid guestId,
        long selectedRecordVersion,
        long currentRecordVersion,
        Guid eventId,
        Guid completionEventId,
        DateTimeOffset completedAtUtc) => $"""
        INSERT INTO guests.guest_data_rights_correction_receipts
            ("Id", "ContractVersion", "IdempotencyKey", "PropertyId",
             "CaseId", "ApprovalRevision", "GuestId", "SelectedRecordVersion",
             "CurrentRecordVersion", "ChangedFieldsMask", "EventId",
             "CompletionEventId", "CompletedAtUtc", "ScopeId")
        VALUES
            ({receiptId}, {1},
             {idempotencyKey}, {propertyId}, {caseId}, {1L}, {guestId},
             {selectedRecordVersion}, {currentRecordVersion}, {1}, {eventId},
             {completionEventId}, {completedAtUtc}, {ScopeId});
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
            GuestCorrectionReceiptAuditIntegrityMigrationIntegrationTests
                .ScopeId;
    }
}
