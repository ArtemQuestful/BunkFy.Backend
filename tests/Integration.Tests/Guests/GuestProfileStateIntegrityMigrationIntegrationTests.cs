namespace Integration.Tests;

using BunkFy.Modules.Guests.Domain.Aggregates;
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

public sealed class GuestProfileStateIntegrityMigrationIntegrationTests
{
    private const string BeforeAuthoritativeProfileStateIntegrityMigration =
        "20260815110326_AddGuestRetentionTimeZoneEvidence";
    private const string ScopeId = "tenant-a";

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Authoritative_profile_migration_preserves_valid_rows_and_rejects_malformed_state()
    {
        await using PostgreSqlContainer postgreSql = new PostgreSqlBuilder(
                "postgres:16-alpine")
            .WithDatabase("bunkfy_guest_profile_state_integrity_tests")
            .Build();
        await postgreSql.StartAsync();

        Guid activeGuestId = Guid.Parse(
            "11000000-0000-0000-0000-000000000040");
        Guid archivedGuestId = Guid.Parse(
            "11000000-0000-0000-0000-000000000041");
        Guid anonymisedGuestId = Guid.Parse(
            "11000000-0000-0000-0000-000000000042");
        Guid propertyId = Guid.Parse(
            "31000000-0000-0000-0000-000000000040");
        DateTimeOffset createdAtUtc = new(
            2026,
            8,
            21,
            12,
            0,
            0,
            TimeSpan.Zero);
        DateTimeOffset archivedAtUtc = createdAtUtc.AddHours(1);
        DateTimeOffset anonymisedAtUtc = createdAtUtc.AddHours(2);
        DateTimeOffset replayedAtUtc = anonymisedAtUtc.AddHours(1);

        await using (GuestsDbContext previous = CreateDbContext(
            postgreSql.GetConnectionString()))
        {
            await previous.Database.GetService<IMigrator>().MigrateAsync(
                BeforeAuthoritativeProfileStateIntegrityMigration);

            GuestProfile active = GuestProfile.Create(
                activeGuestId,
                ScopeId,
                propertyId,
                "Ada Guest",
                "Ada Lovelace",
                "ada@example.test",
                "+44 20 1000",
                new DateOnly(1990, 1, 2),
                "GB",
                "en-GB",
                "Requires a lower bunk",
                "user:owner",
                Guid.NewGuid(),
                createdAtUtc,
                Guid.NewGuid()).Value;
            GuestProfile archived = GuestProfile.Create(
                archivedGuestId,
                ScopeId,
                propertyId,
                "Archived Guest",
                legalName: null,
                email: null,
                phone: null,
                dateOfBirth: null,
                nationalityCountryCode: null,
                preferredLanguageTag: null,
                notes: null,
                "user:owner",
                Guid.NewGuid(),
                createdAtUtc).Value;
            Assert.True(archived.Archive(
                expectedVersion: 1,
                "user:owner",
                Guid.NewGuid(),
                archivedAtUtc).IsSuccess);
            GuestProfile anonymised = GuestProfile.RestoreMissingAnonymised(
                anonymisedGuestId,
                ScopeId,
                propertyId,
                "user:privacy",
                Guid.NewGuid(),
                anonymisedAtUtc,
                replayedAtUtc).Value;

            active.ClearDomainEvents();
            archived.ClearDomainEvents();
            anonymised.ClearDomainEvents();
            await GuestLanguageMigrationSeed.ProfilesAsync(previous, active, archived, anonymised);
            await previous.SaveChangesAsync();
        }

        await using GuestsDbContext upgraded = CreateDbContext(
            postgreSql.GetConnectionString());
        await upgraded.Database.MigrateAsync();

        GuestProfile[] retained = await upgraded.GuestProfiles
            .AsNoTracking()
            .OrderBy(profile => profile.Id)
            .ToArrayAsync();
        Assert.Equal(3, retained.Length);
        Assert.Contains(retained, profile =>
            profile.Id == activeGuestId &&
            profile.Status == GuestProfileState.Active);
        Assert.Contains(retained, profile =>
            profile.Id == archivedGuestId &&
            profile.Status == GuestProfileState.Archived &&
            profile.ArchivedAtUtc == archivedAtUtc);
        Assert.Contains(retained, profile =>
            profile.Id == anonymisedGuestId &&
            profile.MatchesAnonymisedState(anonymisedAtUtc) &&
            profile.LastChangedAtUtc == replayedAtUtc);

        await AssertConstraintViolationAsync(
            upgraded,
            "CK_guest_profiles_coordinates",
            $"""
            UPDATE guests.guest_profiles
            SET "OriginPropertyId" = {Guid.Empty}
            WHERE "Id" = {activeGuestId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_guest_profiles_projection_ordinal",
            $"""
            UPDATE guests.guest_profiles
            SET "ProjectionOrdinal" = {0L}
            WHERE "Id" = {activeGuestId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_guest_profiles_search_shape",
            $"""
            UPDATE guests.guest_profiles
            SET "EmailSearch" = {" "}
            WHERE "Id" = {activeGuestId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_guest_profiles_optional_text",
            $"""
            UPDATE guests.guest_profiles
            SET "Notes" = {" "}
            WHERE "Id" = {activeGuestId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_guest_profiles_state_versions",
            $"""
            UPDATE guests.guest_profiles
            SET "Version" = {1L}
            WHERE "Id" = {archivedGuestId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_guest_profiles_timestamps",
            $"""
            UPDATE guests.guest_profiles
            SET "LastChangedAtUtc" = {createdAtUtc.AddMinutes(-1)}
            WHERE "Id" = {activeGuestId};
            """);
        await AssertConstraintViolationAsync(
            upgraded,
            "CK_guest_profiles_anonymised_profile",
            $"""
            UPDATE guests.guest_profiles
            SET "DateOfBirth" = {new DateOnly(1990, 1, 2)}
            WHERE "Id" = {anonymisedGuestId};
            """);
    }

    private static async Task AssertConstraintViolationAsync(
        GuestsDbContext dbContext,
        string expectedConstraint,
        FormattableString command)
    {
        PostgresException failure = await Assert.ThrowsAsync<PostgresException>(
            () => dbContext.Database.ExecuteSqlInterpolatedAsync(command));
        Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
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
        public string ScopeId => GuestProfileStateIntegrityMigrationIntegrationTests.ScopeId;
    }
}
