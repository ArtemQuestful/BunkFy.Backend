namespace Integration.Tests;

using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.DataRights;
using BunkFy.Modules.Guests.Domain.Models;
using BunkFy.Modules.Guests.Persistence;
using BunkFy.Modules.Guests.Persistence.Repositories;
using Gma.Framework.Scoping;
using Integration.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class GuestAnonymisationPersistenceIntegrationTests
{
    private const string PreviousMigration =
        "20260725010310_AddGuestDataHoldsAndEligibilityContracts";

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Owner_proof_is_persisted_and_unsafe_digest_or_downgrade_fails_closed()
    {
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("bunkfy_guest_anonymisation_proof_tests")
            .Build();
        await postgreSql.StartAsync();

        Guid guestId = Guid.NewGuid();
        Guid propertyId = Guid.NewGuid();
        DateTimeOffset createdAtUtc = new(2026, 7, 25, 4, 30, 0, TimeSpan.Zero);
        DateTimeOffset completedAtUtc = createdAtUtc.AddMinutes(1);
        await using GuestsDbContext dbContext = CreateDbContext(postgreSql.GetConnectionString());
        await dbContext.Database.MigrateAsync();

        GuestProfile profile = GuestProfile.Create(
            guestId,
            "tenant-a",
            propertyId,
            "Personal Guest",
            "Personal Legal Name",
            "guest@example.test",
            "+44 20 1234 5678",
            new DateOnly(1990, 1, 2),
            "GB",
            "en-GB",
            "Private note",
            "staff:creator",
            Guid.NewGuid(),
            createdAtUtc, languageTags: ["en-GB", "fr", "unknown-١"]).Value;
        dbContext.GuestProfiles.Add(profile);
        await dbContext.SaveChangesAsync();

        long selectedVersion = profile.Version;
        Guid eventId = Guid.NewGuid();
        Assert.True(profile.Anonymise(
            selectedVersion,
            "staff:privacy",
            eventId,
            completedAtUtc).IsSuccess);
        GuestAnonymisationReceipt receipt = GuestAnonymisationReceipt.Create(
            Guid.NewGuid(),
            "tenant-a",
            Guid.NewGuid(),
            propertyId,
            Guid.NewGuid(),
            approvalRevision: 2,
            operationRevision: 3,
            guestId,
            selectedVersion,
            profile.Version,
            affectedPropertyCount: 1,
            new string('a', GuestAnonymisationReceipt.Sha256Length),
            new string('b', GuestAnonymisationReceipt.Sha256Length),
            eventId,
            "staff:privacy",
            completedAtUtc).Value;
        GuestAnonymisationTombstone tombstone = GuestAnonymisationTombstone.Create(
            "tenant-a",
            guestId,
            completedAtUtc,
            receipt.CanonicalSha256).Value;
        dbContext.AnonymisationReceipts.Add(receipt);
        dbContext.AnonymisationTombstones.Add(tombstone);
        await dbContext.SaveChangesAsync();

        GuestProfile persisted = await dbContext.GuestProfiles
            .AsNoTracking()
            .SingleAsync(item => item.Id == guestId);
        Assert.Equal(GuestProfileState.Anonymised, persisted.Status);
        Assert.Empty(persisted.LanguageTags);
        Assert.True(persisted.MatchesAnonymisedState(profile.Version, completedAtUtc));
        Assert.True((await dbContext.AnonymisationTombstones
            .AsNoTracking()
            .SingleAsync(item => item.Id == guestId)).Matches(receipt));

        PostgresException invalidDigest = await Assert.ThrowsAsync<PostgresException>(() =>
            dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE guests.guest_anonymisation_receipts
                SET "ApprovalEvidenceSha256" = {new string('A', GuestAnonymisationReceipt.Sha256Length)}
                WHERE "ScopeId" = {"tenant-a"} AND "Id" = {receipt.Id}
                """));
        Assert.Equal(PostgresErrorCodes.RaiseException, invalidDigest.SqlState);
        Assert.Contains(
            "guest data-rights receipts are append-only",
            invalidDigest.MessageText,
            StringComparison.Ordinal);

        PostgresException unsafeDowngrade = await Assert.ThrowsAsync<PostgresException>(() =>
            dbContext.Database.GetService<IMigrator>().MigrateAsync(PreviousMigration));
        Assert.Equal(PostgresErrorCodes.RaiseException, unsafeDowngrade.SqlState);
        Assert.Contains(
            "Cannot downgrade Guests while anonymised profiles exist.",
            unsafeDowngrade.MessageText,
            StringComparison.Ordinal);
    }

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Guest_operation_lock_serializes_competing_transactions()
    {
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("bunkfy_guest_anonymisation_lock_tests")
            .Build();
        await postgreSql.StartAsync();

        string connectionString = postgreSql.GetConnectionString();
        Guid guestId = Guid.NewGuid();
        await using (GuestsDbContext seed = CreateDbContext(connectionString))
        {
            await seed.Database.MigrateAsync();
            await using var transaction = await seed.Database.BeginTransactionAsync();
            await new GuestOperationLockRepository(seed).AcquireGuestAsync(
                "tenant-a",
                guestId,
                CancellationToken.None);
            await transaction.CommitAsync();
        }

        await using GuestsDbContext first = CreateDbContext(connectionString);
        await using GuestsDbContext second = CreateDbContext(connectionString);
        await using var firstTransaction = await first.Database.BeginTransactionAsync();
        await new GuestOperationLockRepository(first).AcquireGuestAsync(
            "tenant-a",
            guestId,
            CancellationToken.None);

        await using (var secondTransaction = await second.Database.BeginTransactionAsync())
        {
            Task secondAcquisition = new GuestOperationLockRepository(second).AcquireGuestAsync(
                "tenant-a",
                guestId,
                CancellationToken.None);
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            Assert.False(secondAcquisition.IsCompleted);

            await firstTransaction.CommitAsync();
            await secondAcquisition.WaitAsync(TimeSpan.FromSeconds(5));
            await secondTransaction.CommitAsync();
        }

        second.ChangeTracker.Clear();
        await using (var retryTransaction = await second.Database.BeginTransactionAsync())
        {
            await new GuestOperationLockRepository(second).AcquireGuestAsync(
                "tenant-a",
                guestId,
                CancellationToken.None);
            await retryTransaction.CommitAsync();
        }

        await using GuestsDbContext verification = CreateDbContext(connectionString);
        Assert.Equal(
            4,
            await verification.OperationLocks
                .Where(item => item.ResourceId == guestId)
                .Select(item => item.Revision)
                .SingleAsync());
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
        public string ScopeId => "tenant-a";
    }
}
