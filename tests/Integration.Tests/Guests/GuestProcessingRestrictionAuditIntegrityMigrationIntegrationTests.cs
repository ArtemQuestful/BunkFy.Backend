namespace Integration.Tests;

using BunkFy.Modules.Guests.Contracts;
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

public sealed class GuestProcessingRestrictionAuditIntegrityMigrationIntegrationTests
{
    private const string BeforeRestrictionAuditIntegrityMigration =
        "20260821211846_AddGuestDataHoldAuditIntegrity";
    private const string ScopeId = "tenant-a";

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Restriction_audit_migration_preserves_valid_state_and_rejects_malformed_rows()
    {
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder(
                "postgres:16-alpine")
            .WithDatabase("bunkfy_guest_restriction_audit_integrity_tests")
            .Build();
        await postgreSql.StartAsync();

        Guid propertyId = Guid.Parse(
            "31000000-0000-0000-0000-000000000060");
        Guid zeroGuestId = Guid.Parse(
            "11000000-0000-0000-0000-000000000060");
        Guid activeGuestId = Guid.Parse(
            "11000000-0000-0000-0000-000000000061");
        Guid releasedGuestId = Guid.Parse(
            "11000000-0000-0000-0000-000000000062");
        DateTimeOffset initializedAtUtc = new(
            2026,
            8,
            21,
            12,
            0,
            0,
            TimeSpan.Zero);

        GuestProcessingRestrictionProjection zeroProjection =
            GuestProcessingRestrictionProjection.Create(
                ScopeId,
                propertyId,
                zeroGuestId,
                GuestProcessingRestrictionContract.CurrentVersion,
                initializedAtUtc).Value;

        GuestProcessingRestrictionProjection activeProjection =
            GuestProcessingRestrictionProjection.Create(
                ScopeId,
                propertyId,
                activeGuestId,
                GuestProcessingRestrictionContract.CurrentVersion,
                initializedAtUtc).Value;
        GuestProcessingRestriction firstActive = CreateRestriction(
            Guid.Parse("51000000-0000-0000-0000-000000000060"),
            propertyId,
            activeGuestId,
            Guid.Parse("41000000-0000-0000-0000-000000000060"),
            initializedAtUtc.AddMinutes(1));
        Assert.True(activeProjection.Apply(
            expectedRevision: 0,
            GuestProcessingRestrictionContract.CurrentVersion,
            initializedAtUtc.AddMinutes(1)).IsSuccess);
        GuestProcessingRestrictionReceipt firstActiveReceipt = CreateReceipt(
            Guid.Parse("61000000-0000-0000-0000-000000000060"),
            Guid.Parse("71000000-0000-0000-0000-000000000060"),
            firstActive,
            GuestProcessingRestrictionAction.Apply,
            firstActive.ApplyCaseId,
            firstActive.ApplyApprovalRevision,
            firstActive.ApplySelectedGuestVersion,
            activeProjection.Revision,
            activeProjection.IsRestricted,
            initializedAtUtc.AddMinutes(1));

        GuestProcessingRestriction secondActive = CreateRestriction(
            Guid.Parse("51000000-0000-0000-0000-000000000061"),
            propertyId,
            activeGuestId,
            Guid.Parse("41000000-0000-0000-0000-000000000061"),
            initializedAtUtc.AddMinutes(2));
        Assert.True(activeProjection.Apply(
            expectedRevision: 1,
            GuestProcessingRestrictionContract.CurrentVersion,
            initializedAtUtc.AddMinutes(2)).IsSuccess);
        GuestProcessingRestrictionReceipt secondActiveReceipt = CreateReceipt(
            Guid.Parse("61000000-0000-0000-0000-000000000061"),
            Guid.Parse("71000000-0000-0000-0000-000000000061"),
            secondActive,
            GuestProcessingRestrictionAction.Apply,
            secondActive.ApplyCaseId,
            secondActive.ApplyApprovalRevision,
            secondActive.ApplySelectedGuestVersion,
            activeProjection.Revision,
            activeProjection.IsRestricted,
            initializedAtUtc.AddMinutes(2));

        GuestProcessingRestrictionProjection releasedProjection =
            GuestProcessingRestrictionProjection.Create(
                ScopeId,
                propertyId,
                releasedGuestId,
                GuestProcessingRestrictionContract.CurrentVersion,
                initializedAtUtc).Value;
        GuestProcessingRestriction released = CreateRestriction(
            Guid.Parse("51000000-0000-0000-0000-000000000062"),
            propertyId,
            releasedGuestId,
            Guid.Parse("41000000-0000-0000-0000-000000000062"),
            initializedAtUtc.AddMinutes(3));
        Assert.True(releasedProjection.Apply(
            expectedRevision: 0,
            GuestProcessingRestrictionContract.CurrentVersion,
            initializedAtUtc.AddMinutes(3)).IsSuccess);
        GuestProcessingRestrictionReceipt releasedApplyReceipt = CreateReceipt(
            Guid.Parse("61000000-0000-0000-0000-000000000062"),
            Guid.Parse("71000000-0000-0000-0000-000000000062"),
            released,
            GuestProcessingRestrictionAction.Apply,
            released.ApplyCaseId,
            released.ApplyApprovalRevision,
            released.ApplySelectedGuestVersion,
            releasedProjection.Revision,
            releasedProjection.IsRestricted,
            initializedAtUtc.AddMinutes(3));
        Guid releaseCaseId = Guid.Parse(
            "41000000-0000-0000-0000-000000000063");
        Assert.True(released.Release(
            releaseCaseId,
            releaseApprovalRevision: 2,
            releaseSelectedGuestVersion: 4,
            expectedVersion: 1,
            "user:decision-maker",
            initializedAtUtc.AddMinutes(4)).IsSuccess);
        Assert.True(releasedProjection.Release(
            expectedRevision: 1,
            GuestProcessingRestrictionContract.CurrentVersion,
            initializedAtUtc.AddMinutes(4)).IsSuccess);
        GuestProcessingRestrictionReceipt releasedReceipt = CreateReceipt(
            Guid.Parse("61000000-0000-0000-0000-000000000063"),
            Guid.Parse("71000000-0000-0000-0000-000000000063"),
            released,
            GuestProcessingRestrictionAction.Release,
            releaseCaseId,
            approvalRevision: 2,
            selectedGuestVersion: 4,
            releasedProjection.Revision,
            releasedProjection.IsRestricted,
            initializedAtUtc.AddMinutes(4));

        await using (GuestsDbContext previous = CreateDbContext(
            postgreSql.GetConnectionString()))
        {
            await previous.Database.GetService<IMigrator>().MigrateAsync(
                BeforeRestrictionAuditIntegrityMigration);
            previous.ProcessingRestrictionProjections.AddRange(
                zeroProjection,
                activeProjection,
                releasedProjection);
            previous.ProcessingRestrictions.AddRange(
                firstActive,
                secondActive,
                released);
            previous.ProcessingRestrictionReceipts.AddRange(
                firstActiveReceipt,
                secondActiveReceipt,
                releasedApplyReceipt,
                releasedReceipt);
            await previous.SaveChangesAsync();
        }

        await using GuestsDbContext upgraded = CreateDbContext(
            postgreSql.GetConnectionString());
        await upgraded.Database.MigrateAsync();

        GuestProcessingRestrictionProjection[] projections = await upgraded
            .ProcessingRestrictionProjections
            .AsNoTracking()
            .OrderBy(projection => projection.GuestId)
            .ToArrayAsync();
        GuestProcessingRestriction[] restrictions = await upgraded
            .ProcessingRestrictions
            .AsNoTracking()
            .OrderBy(restriction => restriction.Id)
            .ToArrayAsync();
        GuestProcessingRestrictionReceipt[] receipts = await upgraded
            .ProcessingRestrictionReceipts
            .AsNoTracking()
            .OrderBy(receipt => receipt.Id)
            .ToArrayAsync();
        Assert.Equal(3, projections.Length);
        Assert.Equal(3, restrictions.Length);
        Assert.Equal(4, receipts.Length);
        Assert.Contains(projections, projection =>
            projection.GuestId == zeroGuestId &&
            projection.Revision == 0 &&
            projection.ActiveRestrictionCount == 0 &&
            !projection.IsRestricted);
        Assert.Contains(projections, projection =>
            projection.GuestId == activeGuestId &&
            projection.Revision == 2 &&
            projection.ActiveRestrictionCount == 2 &&
            projection.IsRestricted);
        Assert.Contains(projections, projection =>
            projection.GuestId == releasedGuestId &&
            projection.Revision == 2 &&
            projection.ActiveRestrictionCount == 0 &&
            !projection.IsRestricted);
        Assert.Contains(restrictions, restriction =>
            restriction.Id == released.Id &&
            restriction.Status == GuestProcessingRestrictionState.Released &&
            restriction.Version == 2);
        Assert.All(receipts, receipt =>
        {
            GuestProcessingRestriction restriction = Assert.Single(
                restrictions,
                candidate => candidate.Id == receipt.RestrictionId);
            Assert.Equal(restriction.ScopeId, receipt.ScopeId);
            Assert.Equal(restriction.PropertyId, receipt.PropertyId);
            Assert.Equal(restriction.GuestId, receipt.GuestId);
        });

        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_processing_restrictions_coordinates",
            $"""
            UPDATE guests.guest_processing_restrictions
            SET "ApplyCaseId" = {Guid.Empty}
            WHERE "Id" = {firstActive.Id};
            """);
        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_processing_restrictions_audit_text",
            $"""
            UPDATE guests.guest_processing_restrictions
            SET "AppliedBy" = {" user:privacy "}
            WHERE "Id" = {firstActive.Id};
            """);
        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_processing_restrictions_timestamps",
            $"""
            UPDATE guests.guest_processing_restrictions
            SET "AppliedAtUtc" = {default(DateTimeOffset)}
            WHERE "Id" = {firstActive.Id};
            """);
        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_processing_restrictions_lifecycle",
            $"""
            UPDATE guests.guest_processing_restrictions
            SET "Version" = {2L}
            WHERE "Id" = {firstActive.Id};
            """);

        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_processing_restrictions_projection_coordinates",
            $"""
            UPDATE guests.guest_processing_restriction_state
            SET "GuestId" = {Guid.Empty}
            WHERE "GuestId" = {zeroGuestId};
            """);
        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_processing_restrictions_projection_ordinal",
            $"""
            UPDATE guests.guest_processing_restriction_state
            SET "ProjectionOrdinal" = {0L}
            WHERE "GuestId" = {zeroGuestId};
            """);
        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_processing_restrictions_transition_timestamp",
            $"""
            UPDATE guests.guest_processing_restriction_state
            SET "LastTransitionAtUtc" = {default(DateTimeOffset)}
            WHERE "GuestId" = {zeroGuestId};
            """);
        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_processing_restrictions_effective_state",
            $"""
            UPDATE guests.guest_processing_restriction_state
            SET "Revision" = {1L}
            WHERE "GuestId" = {zeroGuestId};
            """);

        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_processing_restriction_receipts_coordinates",
            ReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000064"),
                Guid.Parse("71000000-0000-0000-0000-000000000064"),
                firstActive,
                eventId: Guid.Empty,
                actorId: "user:privacy",
                completedAtUtc: initializedAtUtc.AddMinutes(5)));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_processing_restriction_receipts_audit_text",
            ReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000065"),
                Guid.Parse("71000000-0000-0000-0000-000000000065"),
                firstActive,
                eventId: Guid.NewGuid(),
                actorId: " user:privacy ",
                completedAtUtc: initializedAtUtc.AddMinutes(5)));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_processing_restriction_receipts_timestamp",
            ReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000066"),
                Guid.Parse("71000000-0000-0000-0000-000000000066"),
                firstActive,
                eventId: Guid.NewGuid(),
                actorId: "user:privacy",
                completedAtUtc: default));
        await AssertCheckViolationAsync(
            upgraded,
            "CK_guest_processing_restriction_receipts_versions",
            ReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000067"),
                Guid.Parse("71000000-0000-0000-0000-000000000067"),
                firstActive,
                eventId: Guid.NewGuid(),
                actorId: "user:privacy",
                completedAtUtc: initializedAtUtc.AddMinutes(5),
                action: GuestProcessingRestrictionAction.Release,
                resultingRestrictionVersion: 3));
        await AssertForeignKeyViolationAsync(
            upgraded,
            "FK_guest_processing_restriction_receipts_restriction_evidence",
            ReceiptInsert(
                Guid.Parse("61000000-0000-0000-0000-000000000068"),
                Guid.Parse("71000000-0000-0000-0000-000000000068"),
                firstActive,
                eventId: Guid.NewGuid(),
                actorId: "user:privacy",
                completedAtUtc: initializedAtUtc.AddMinutes(5),
                propertyId: Guid.Parse(
                    "31000000-0000-0000-0000-000000000069")));
        await AssertForeignKeyViolationAsync(
            upgraded,
            "FK_guest_processing_restrictions_effective_projection",
            RestrictionInsert(
                Guid.Parse("51000000-0000-0000-0000-000000000069"),
                Guid.Parse("31000000-0000-0000-0000-000000000069"),
                Guid.Parse("11000000-0000-0000-0000-000000000069"),
                Guid.Parse("41000000-0000-0000-0000-000000000069"),
                initializedAtUtc.AddMinutes(5)));

        await upgraded.Database.GetService<IMigrator>().MigrateAsync(
            BeforeRestrictionAuditIntegrityMigration);
        Assert.Equal(
            3,
            await upgraded.ProcessingRestrictionProjections
                .AsNoTracking()
                .CountAsync());
        Assert.Equal(
            3,
            await upgraded.ProcessingRestrictions.AsNoTracking().CountAsync());
        Assert.Equal(
            4,
            await upgraded.ProcessingRestrictionReceipts
                .AsNoTracking()
                .CountAsync());
        await upgraded.Database.MigrateAsync();
    }

    private static GuestProcessingRestriction CreateRestriction(
        Guid restrictionId,
        Guid propertyId,
        Guid guestId,
        Guid caseId,
        DateTimeOffset appliedAtUtc) => GuestProcessingRestriction.Create(
            restrictionId,
            ScopeId,
            propertyId,
            guestId,
            caseId,
            applyApprovalRevision: 1,
            applySelectedGuestVersion: 3,
            "user:privacy",
            appliedAtUtc).Value;

    private static GuestProcessingRestrictionReceipt CreateReceipt(
        Guid receiptId,
        Guid idempotencyKey,
        GuestProcessingRestriction restriction,
        GuestProcessingRestrictionAction action,
        Guid caseId,
        long approvalRevision,
        long selectedGuestVersion,
        long resultingProjectionRevision,
        bool effectiveRestricted,
        DateTimeOffset completedAtUtc) =>
        GuestProcessingRestrictionReceipt.Create(
            receiptId,
            ScopeId,
            idempotencyKey,
            restriction.Id,
            action,
            restriction.PropertyId,
            restriction.GuestId,
            caseId,
            approvalRevision,
            selectedGuestVersion,
            GuestProcessingRestrictionContract.CurrentVersion,
            restriction.Version,
            resultingProjectionRevision,
            effectiveRestricted,
            action == GuestProcessingRestrictionAction.Apply
                ? restriction.AppliedBy
                : restriction.ReleasedBy!,
            Guid.NewGuid(),
            completedAtUtc).Value;

    private static FormattableString ReceiptInsert(
        Guid receiptId,
        Guid idempotencyKey,
        GuestProcessingRestriction restriction,
        Guid eventId,
        string actorId,
        DateTimeOffset completedAtUtc,
        GuestProcessingRestrictionAction action =
            GuestProcessingRestrictionAction.Apply,
        long resultingRestrictionVersion = 1,
        Guid? propertyId = null) => $"""
        INSERT INTO guests.guest_processing_restriction_receipts
            ("Id", "IdempotencyKey", "RestrictionId", "Action", "PropertyId",
             "GuestId", "CaseId", "ApprovalRevision", "SelectedGuestVersion",
             "ResultingRestrictionVersion", "ResultingProjectionRevision",
             "EffectiveRestricted", "ActorId", "EventId", "CompletedAtUtc",
             "ScopeId")
        VALUES
            ({receiptId}, {idempotencyKey}, {restriction.Id}, {(int)action},
             {propertyId ?? restriction.PropertyId}, {restriction.GuestId},
             {Guid.NewGuid()}, {1L}, {3L}, {resultingRestrictionVersion}, {3L},
             {true}, {actorId}, {eventId}, {completedAtUtc}, {ScopeId});
        """;

    private static FormattableString RestrictionInsert(
        Guid restrictionId,
        Guid propertyId,
        Guid guestId,
        Guid applyCaseId,
        DateTimeOffset appliedAtUtc) => $"""
        INSERT INTO guests.guest_processing_restrictions
            ("Id", "PropertyId", "GuestId", "ApplyCaseId",
             "ApplyApprovalRevision", "ApplySelectedGuestVersion", "Status",
             "Version", "AppliedBy", "AppliedAtUtc", "ReleaseCaseId",
             "ReleaseApprovalRevision", "ReleaseSelectedGuestVersion",
             "ReleasedBy", "ReleasedAtUtc", "ScopeId")
        VALUES
            ({restrictionId}, {propertyId}, {guestId}, {applyCaseId}, {1L},
             {3L}, {1}, {1L}, {"user:privacy"}, {appliedAtUtc}, NULL, NULL,
             NULL, NULL, NULL, {ScopeId});
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
            GuestProcessingRestrictionAuditIntegrityMigrationIntegrationTests
                .ScopeId;
    }
}
