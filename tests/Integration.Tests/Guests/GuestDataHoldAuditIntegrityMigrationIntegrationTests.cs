namespace Integration.Tests;

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

public sealed class GuestDataHoldAuditIntegrityMigrationIntegrationTests
{
    private const string BeforeDataHoldAuditIntegrityMigration =
        "20260821154538_AddGuestProfileAuthoritativeStateIntegrity";
    private const string ScopeId = "tenant-a";

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Data_hold_audit_migration_preserves_valid_evidence_and_rejects_malformed_rows()
    {
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder(
                "postgres:16-alpine")
            .WithDatabase("bunkfy_guest_data_hold_audit_integrity_tests")
            .Build();
        await postgreSql.StartAsync();

        Guid propertyId = Guid.Parse(
            "31000000-0000-0000-0000-000000000050");
        Guid activeGuestId = Guid.Parse(
            "11000000-0000-0000-0000-000000000050");
        Guid releasedGuestId = Guid.Parse(
            "11000000-0000-0000-0000-000000000051");
        Guid evidenceGuestId = Guid.Parse(
            "11000000-0000-0000-0000-000000000052");
        DateTimeOffset placedAtUtc = new(
            2026,
            8,
            21,
            12,
            0,
            0,
            TimeSpan.Zero);

        GuestDataHold active = GuestDataHold.Place(
            Guid.Parse("51000000-0000-0000-0000-000000000050"),
            ScopeId,
            propertyId,
            activeGuestId,
            "legal-obligation",
            "user:privacy",
            placedAtUtc).Value;
        GuestDataHoldReceipt activePlaced = GuestDataHoldReceipt.Create(
            Guid.Parse("61000000-0000-0000-0000-000000000050"),
            ScopeId,
            Guid.Parse("71000000-0000-0000-0000-000000000050"),
            active,
            GuestDataHoldAction.Place,
            selectedGuestVersion: 3,
            "user:privacy",
            placedAtUtc).Value;

        GuestDataHold released = GuestDataHold.Place(
            Guid.Parse("51000000-0000-0000-0000-000000000051"),
            ScopeId,
            propertyId,
            releasedGuestId,
            "regulatory-request",
            "user:privacy",
            placedAtUtc.AddMinutes(1)).Value;
        GuestDataHoldReceipt releasedPlaced = GuestDataHoldReceipt.Create(
            Guid.Parse("61000000-0000-0000-0000-000000000051"),
            ScopeId,
            Guid.Parse("71000000-0000-0000-0000-000000000051"),
            released,
            GuestDataHoldAction.Place,
            selectedGuestVersion: 4,
            "user:privacy",
            placedAtUtc.AddMinutes(1)).Value;
        Assert.True(released.Release(
            expectedVersion: 1,
            "user:decision-maker",
            placedAtUtc.AddMinutes(2)).IsSuccess);
        GuestDataHoldReceipt releasedReceipt = GuestDataHoldReceipt.Create(
            Guid.Parse("61000000-0000-0000-0000-000000000052"),
            ScopeId,
            Guid.Parse("71000000-0000-0000-0000-000000000052"),
            released,
            GuestDataHoldAction.Release,
            selectedGuestVersion: 4,
            "user:decision-maker",
            placedAtUtc.AddMinutes(2)).Value;

        GuestDataHold evidence = GuestDataHold.Place(
            Guid.Parse("51000000-0000-0000-0000-000000000052"),
            ScopeId,
            propertyId,
            evidenceGuestId,
            "dispute",
            "user:privacy",
            placedAtUtc.AddMinutes(3)).Value;

        await using (GuestsDbContext previous = CreateDbContext(
            postgreSql.GetConnectionString()))
        {
            await previous.Database.GetService<IMigrator>().MigrateAsync(
                BeforeDataHoldAuditIntegrityMigration);
            previous.DataHolds.AddRange(active, released, evidence);
            previous.DataHoldReceipts.AddRange(
                activePlaced,
                releasedPlaced,
                releasedReceipt);
            await previous.SaveChangesAsync();
        }

        await using GuestsDbContext upgraded = CreateDbContext(
            postgreSql.GetConnectionString());
        await upgraded.Database.MigrateAsync();

        GuestDataHold[] retainedHolds = await upgraded.DataHolds
            .AsNoTracking()
            .OrderBy(hold => hold.Id)
            .ToArrayAsync();
        GuestDataHoldReceipt[] retainedReceipts = await upgraded.DataHoldReceipts
            .AsNoTracking()
            .OrderBy(receipt => receipt.Id)
            .ToArrayAsync();
        Assert.Equal(3, retainedHolds.Length);
        Assert.Equal(3, retainedReceipts.Length);
        Assert.Contains(retainedHolds, hold =>
            hold.Id == released.Id &&
            hold.State == GuestDataHoldState.Released &&
            hold.Version == 2);
        Assert.All(retainedReceipts, receipt =>
        {
            GuestDataHold hold = Assert.Single(
                retainedHolds,
                candidate => candidate.Id == receipt.HoldId);
            Assert.Equal(hold.ScopeId, receipt.ScopeId);
            Assert.Equal(hold.PropertyId, receipt.PropertyId);
            Assert.Equal(hold.GuestId, receipt.GuestId);
            Assert.Equal(hold.ReasonCode, receipt.ReasonCode);
        });

        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_data_holds_coordinates",
            $"""
            UPDATE guests.data_holds
            SET "PropertyId" = {Guid.Empty}
            WHERE "Id" = {evidence.Id};
            """);
        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_data_holds_audit_text",
            $"""
            UPDATE guests.data_holds
            SET "ReasonCode" = {" Dispute "}
            WHERE "Id" = {evidence.Id};
            """);
        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_data_holds_timestamps",
            $"""
            UPDATE guests.data_holds
            SET "PlacedAtUtc" = TIMESTAMPTZ '0001-01-01 00:00:00+00'
            WHERE "Id" = {evidence.Id};
            """);

        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_data_hold_receipts_coordinates",
            ReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000053"),
                Guid.Empty,
                evidence,
                evidence.ReasonCode,
                "user:privacy",
                evidence.PlacedAtUtc));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_data_hold_receipts_audit_text",
            ReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000054"),
                Guid.Parse("71000000-0000-0000-0000-000000000054"),
                evidence,
                evidence.ReasonCode,
                " user:privacy ",
                evidence.PlacedAtUtc));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_data_hold_receipts_timestamp",
            ReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000055"),
                Guid.Parse("71000000-0000-0000-0000-000000000055"),
                evidence,
                evidence.ReasonCode,
                "user:privacy",
                default));
        await AssertForeignKeyViolationAsync(
            upgraded,
            "FK_guest_data_hold_receipts_hold_evidence",
            ReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000056"),
                Guid.Parse("71000000-0000-0000-0000-000000000056"),
                evidence,
                "security-investigation",
                "user:privacy",
                evidence.PlacedAtUtc));

        await upgraded.Database.GetService<IMigrator>().MigrateAsync(
            BeforeDataHoldAuditIntegrityMigration);
        Assert.Equal(3, await upgraded.DataHolds.AsNoTracking().CountAsync());
        Assert.Equal(
            3,
            await upgraded.DataHoldReceipts.AsNoTracking().CountAsync());
        await upgraded.Database.MigrateAsync();
    }

    private static FormattableString ReceiptInsert(
        Guid receiptId,
        Guid idempotencyKey,
        GuestDataHold hold,
        string reasonCode,
        string actorId,
        DateTimeOffset completedAtUtc) => $"""
        INSERT INTO guests.data_hold_receipts
            ("Id", "IdempotencyKey", "HoldId", "Action", "PropertyId",
             "GuestId", "ReasonCode", "SelectedGuestVersion",
             "ResultingHoldVersion", "ActorId", "CompletedAtUtc", "ScopeId")
        VALUES
            ({receiptId}, {idempotencyKey}, {hold.Id}, 1, {hold.PropertyId},
             {hold.GuestId}, {reasonCode}, 3, 1, {actorId},
             {completedAtUtc}, {hold.ScopeId});
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
            GuestDataHoldAuditIntegrityMigrationIntegrationTests.ScopeId;
    }
}
