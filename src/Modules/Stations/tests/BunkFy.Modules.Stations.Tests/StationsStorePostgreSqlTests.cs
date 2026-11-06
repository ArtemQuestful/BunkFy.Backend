namespace BunkFy.Modules.Stations.Tests;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BunkFy.Modules.Stations.Application;
using BunkFy.Modules.Stations.Contracts;
using BunkFy.Modules.Stations.Domain;
using BunkFy.Modules.Stations.Persistence;
using Gma.Framework.Persistence.EntityFrameworkCore;
using Gma.Framework.Scoping;
using Gma.Framework.Runtime.Time;
using Gma.Modules.Auth.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class StationsStorePostgreSqlTests
{
    private sealed class NoSessionLookup : IAuthSessionAdmissionReader
    {
        public ValueTask<bool> IsActiveAsync(string scopeId, Guid memberId, Guid sessionId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Pre-management fixture has no pairing receipt, so Auth must not be queried.");
    }
    private const string TenantA = StationDomainTests.Tenant;
    private const string TenantB = "bb000000-0000-0000-0000-000000000002";
    private static readonly DateTimeOffset Now = StationDomainTests.Now;

    [Fact]
    [Trait("Category", "Docker")]
    public async Task Actual_migration_and_registered_store_prove_concurrency_scope_reset_and_replay()
    {
        Assert.Equal("true", Environment.GetEnvironmentVariable("GMA_REQUIRE_DOCKER_TESTS"));
        await using PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithName("bunkfy-stations-p1-" + Guid.NewGuid().ToString("N"))
            .WithLabel("bunkfy.test.grant", "STAFF-PIN-FIRST-JOB-P1").WithDatabase("stations_core").Build();
        await postgres.StartAsync();
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        { ["Persistence:Provider"] = "PostgreSql", ["ConnectionStrings:PostgreSql"] = postgres.GetConnectionString() });
        builder.Services.AddScoped<TestScope>();
        builder.Services.AddScoped<IScopeContext>(sp => sp.GetRequiredService<TestScope>());
        builder.Services.AddScoped<IScopeContextAccessor>(sp => sp.GetRequiredService<TestScope>());
        builder.Services.AddSingleton<ISystemClock, TestClock>();
        builder.Services.AddSingleton<IStationPepperProvider, StationPinVerifierTests.TestPeppers>();
        builder.Services.AddStationsCore(o => { o.ExternalEpoch = 1; o.PepperVersion = "test-v1"; o.ManagementAuthScopeId = "fixture-auth"; });
        builder.Services.AddSingleton<IAuthSessionAdmissionReader, NoSessionLookup>();
        builder.AddStationsPersistence();
        builder.Services.AddStationsRuntime();
        await using ServiceProvider services = builder.Services.BuildServiceProvider();
        using (var scope = Scope(services, TenantA))
        {
            var db = scope.ServiceProvider.GetRequiredService<StationsDbContext>();
            await db.GetService<IMigrator>().MigrateAsync("20260920000000_InitialStations");
            Guid legacyStaff = Guid.NewGuid();
            StationPinMaterial legacyMaterial = Assert.IsType<StationPinMaterial>(await services.GetRequiredService<IStationPinVerifier>().CreateAsync("000001"));
            string legacyTenant = "cc000000-0000-0000-0000-000000000003";
            Guid legacyStation = Guid.NewGuid(), legacyBrowserId = Guid.NewGuid(), legacyProperty = Guid.NewGuid();
            Guid legacyActor = Guid.NewGuid(), legacySetup = Guid.NewGuid(), legacyOperation = Guid.NewGuid();
            string legacySecret = StationRuntimeServiceTests.Encode(RandomNumberGenerator.GetBytes(32));
            using var legacy = Scope(services, legacyTenant);
            var legacyDb = legacy.ServiceProvider.GetRequiredService<StationsDbContext>();
            var paired = new StationBrowserSession(legacyBrowserId, legacyTenant, legacyStation, legacyProperty,
                StationCredentialEncoding.Digest(legacySecret)!, Now, Now.AddDays(1), 1);
            paired.ReserveAttempt(Now, 20, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));
            paired.ReserveAttempt(Now, 20, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));
            paired.Activate(legacyStaff, legacyActor, StationActorKind.StationOnly, 1, 1, Now, TimeSpan.FromMinutes(5), TimeSpan.FromHours(12));
            legacyDb.Stations.Add(new(legacyStation, legacyTenant, legacyProperty, "P1 upgrade fixture"));
            // Seed the actual legacy table shape without the newly mapped neutral revision column.
            legacyDb.CheckInGrants.Add(new(legacyTenant, legacyProperty, legacyStaff));
            await legacyDb.SaveChangesAsync();
            var legacyBrowser = typeof(StationBrowserSession).GetProperties()
                .Where(x => x.Name != nameof(StationBrowserSession.RegistrationRevision))
                .ToDictionary(x => x.Name, x => x.GetValue(paired));
            legacyBrowser[nameof(StationBrowserSession.GrantRevision)] = 1L;
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO stations.browser_sessions SELECT (jsonb_populate_record(NULL::stations.browser_sessions, @row::jsonb)).*",
                new NpgsqlParameter("row", JsonSerializer.Serialize(legacyBrowser)));
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO stations.operation_receipts (\"ScopeId\", \"Id\", \"Kind\", \"Fingerprint\", \"Outcome\", \"ActorSessionId\", \"Generation\", \"CreatedAtUtc\") VALUES ({legacyTenant}, {legacyOperation}, 3, {Digest()}, 1, {legacyActor}, {paired.Generation}, {Now})");
            // Actual pre-P2 table shape: no binding columns exist yet; upgrade may not infer current authority.
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO stations.staff_credentials (\"ScopeId\", \"StaffMemberId\", \"Revision\", \"AlgorithmVersion\", \"Iterations\", \"Salt\", \"Verifier\", \"PepperVersion\", \"Revoked\", \"LastObservedAtUtc\", \"FailureCount\", \"FailureWindowStartedAtUtc\") VALUES ({legacyTenant}, {legacyStaff}, 1, 1, 600000, {legacyMaterial.Salt}, {legacyMaterial.Verifier}, 'test-v1', false, {Now}, 2, {Now})");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO stations.setup_grants (\"ScopeId\", \"Id\", \"StationId\", \"BrowserSessionId\", \"PropertyId\", \"StaffMemberId\", \"AuthorityKind\", \"ExpectedCredentialRevision\", \"CreatedAtUtc\", \"ExpiresAtUtc\", \"Revoked\") VALUES ({legacyTenant}, {legacySetup}, {legacyStation}, {legacyBrowserId}, {legacyProperty}, {legacyStaff}, 2, 1, {Now}, {Now.AddMinutes(10)}, false)");
            string beforeUpgrade = await LegacyFingerprint(legacyDb, legacyTenant);
            await db.GetService<IMigrator>().MigrateAsync("20260920010000_AddStationRuntimeBindingsAndActivity");
            string beforeManagementUpgrade = await LegacyFingerprint(legacyDb, legacyTenant, removeRuntimeColumns: false);
            // Coherent bound P2 actor plus counters/setup/receipt, not merely an empty migration or unbound P1 row.
            string boundTenant = "dd000000-0000-0000-0000-000000000004";
            string boundSecret = StationRuntimeServiceTests.Encode(RandomNumberGenerator.GetBytes(32));
            foreach (string table in new[] { "stations", "browser_sessions", "staff_credentials", "staff_check_in_grants", "setup_grants", "operation_receipts" })
            {
                var replacement = new Dictionary<string, object?> { ["ScopeId"] = boundTenant };
                if (table == "browser_sessions")
                {
                    replacement["CredentialDigest"] = StationCredentialEncoding.Digest(boundSecret);
                    replacement["AuthorityKind"] = 1;
                    replacement["GrantRevision"] = null;
                }
                if (table == "staff_credentials")
                {
                    replacement["EnrollmentAuthorityKind"] = 1;
                    replacement["EnrollmentAuthSubjectId"] = legacyActor.ToString("D");
                }
                if (table == "setup_grants")
                {
                    replacement["AuthorityKind"] = 1;
                    replacement["ExpectedEnrollmentAuthorityKind"] = 1;
                    replacement["ExpectedEnrollmentAuthSubjectId"] = legacyActor.ToString("D");
                }
                // Only the six literal table identifiers above enter SQL; every row value remains a parameter.
                string cloneSql = $"INSERT INTO stations.{table} SELECT (jsonb_populate_record(NULL::stations.{table}, to_jsonb(t) || @changes::jsonb)).* FROM stations.{table} t WHERE \"ScopeId\"=@tenant";
                await db.Database.ExecuteSqlRawAsync(cloneSql,
                    new NpgsqlParameter("changes", JsonSerializer.Serialize(replacement)), new NpgsqlParameter("tenant", legacyTenant));
            }
            string boundBefore = await LegacyFingerprint(db, boundTenant, removeRuntimeColumns: false);
            await db.GetService<IMigrator>().MigrateAsync("20260920020000_AddStationManagementPrerequisites");
            Assert.Equal(3, (await db.Database.GetAppliedMigrationsAsync()).Count());
            Assert.Equal(beforeUpgrade, await LegacyFingerprint(legacyDb, legacyTenant));
            Assert.Equal(beforeManagementUpgrade, await LegacyFingerprint(legacyDb, legacyTenant, removeRuntimeColumns: false));
            Assert.Equal(boundBefore, await LegacyFingerprint(db, boundTenant, removeRuntimeColumns: false));
            Assert.Empty(await legacyDb.StaffRegistrations.ToArrayAsync());
            using (var boundScope = Scope(services, boundTenant))
            {
                var boundDb = boundScope.ServiceProvider.GetRequiredService<StationsDbContext>();
                Assert.Empty(await boundDb.StaffRegistrations.ToArrayAsync());
                Assert.Equal(StationActorKind.LinkedStation, (await boundDb.Credentials.SingleAsync()).EnrollmentAuthorityKind);
                Assert.Equal(legacyActor, await boundDb.Database.SqlQuery<Guid>(
                    $"SELECT \"ActorSessionId\" AS \"Value\" FROM stations.browser_sessions WHERE \"ScopeId\"={boundTenant}").SingleAsync());
                var management = boundScope.ServiceProvider.GetRequiredService<IStationManagementStore>();
                Assert.Null(await management.OwnPinFactsAsync(legacyProperty, legacyStaff));
                Assert.Null(await management.ReadOutcomeAsync(legacyOperation, Guid.NewGuid().ToString("D")));
                Assert.Equal(boundBefore, await LegacyFingerprint(boundDb, boundTenant, removeRuntimeColumns: false));
            }
            Assert.Null((await legacyDb.OperationReceipts.SingleAsync()).IssuerSessionId);
            Assert.Equal(StationIssuerKind.Unknown, (await legacyDb.SetupGrants.SingleAsync()).IssuerKind);
            var unbound = await legacyDb.Credentials.SingleAsync();
            Assert.Equal(1, unbound.Revision);
            Assert.Equal(StationActorKind.Unknown, unbound.EnrollmentAuthorityKind);
            Assert.Null(unbound.EnrollmentAuthSubjectId);
            Assert.Equal(legacyMaterial.Verifier, unbound.Verifier);
            Assert.Equal(StationPinVerification.Valid, await services.GetRequiredService<IStationPinVerifier>().VerifyAsync("000001", unbound));
            Assert.Equal(2, unbound.FailureCount);
            Assert.Equal(StationActorKind.Unknown, (await legacyDb.SetupGrants.SingleAsync()).ExpectedEnrollmentAuthorityKind);
            string neutralLegacyBefore = await LegacyFingerprint(legacyDb, legacyTenant, neutralActor: true);
            string neutralBoundBefore = await LegacyFingerprint(db, boundTenant, removeRuntimeColumns: false, neutralActor: true);
            await db.Database.MigrateAsync();
            Assert.Equal(5, (await db.Database.GetAppliedMigrationsAsync()).Count());
            Assert.False(db.Database.HasPendingModelChanges());
            Assert.Equal(neutralLegacyBefore, await LegacyFingerprint(legacyDb, legacyTenant, neutralActor: true));
            Assert.Equal(neutralBoundBefore, await LegacyFingerprint(db, boundTenant, removeRuntimeColumns: false, neutralActor: true));
            foreach (string tenant in new[] { legacyTenant, boundTenant })
            {
                using var upgraded = Scope(services, tenant);
                var upgradedDb = upgraded.ServiceProvider.GetRequiredService<StationsDbContext>();
                var locked = await upgradedDb.BrowserSessions.SingleAsync();
                Assert.Null(locked.ActorSessionId);
                Assert.Null(locked.StaffMemberId);
                Assert.Null(locked.RegistrationRevision);
                Assert.Null(locked.GrantRevision);
                Assert.Equal(paired.Generation + 1, locked.Generation);
                Assert.Empty(await upgradedDb.CheckOutGrants.ToArrayAsync());
                Assert.False((await upgradedDb.CheckInGrants.SingleAsync()).Revoked);
            }
            var runtime = legacy.ServiceProvider.GetRequiredService<StationRuntimeService>();
            Assert.Equal(new(StationSessionState.StateChanged), await runtime.ReadAsync(legacySecret));
            Assert.Equal(new(StationSessionState.StateChanged), await runtime.UnlockAsync(legacySecret, Guid.NewGuid(), legacyStaff, paired.Generation, "000001"));
            Assert.Equal(new(StationSessionState.StateChanged), await runtime.ForegroundActivityAsync(legacySecret, Guid.NewGuid(),
                new(legacyStaff, legacyActor, paired.Generation, StationAuthorityKind.StationOnly)));
            Assert.Equal(new(StationSessionState.StateChanged), await runtime.RedeemSeededSetupAsync(legacySecret, Guid.NewGuid(), legacySetup, "000001"));
            Assert.Equal(neutralLegacyBefore, await LegacyFingerprint(legacyDb, legacyTenant, neutralActor: true));
            PostgresException missingSubject = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE stations.staff_credentials SET \"EnrollmentAuthorityKind\" = 1 WHERE \"ScopeId\" = {legacyTenant} AND \"StaffMemberId\" = {legacyStaff}"));
            Assert.Equal(PostgresErrorCodes.CheckViolation, missingSubject.SqlState);
            Assert.Equal("CK_credential_enrollment", missingSubject.ConstraintName);
            PostgresException missingIssuer = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE stations.setup_grants SET \"IssuerKind\"=1 WHERE \"ScopeId\"={legacyTenant}"));
            Assert.Equal(PostgresErrorCodes.CheckViolation, missingIssuer.SqlState);
            Assert.Equal("CK_setup_issuer", missingIssuer.ConstraintName);
        }
        StationPinMaterial material = Assert.IsType<StationPinMaterial>(await services.GetRequiredService<IStationPinVerifier>().CreateAsync("000001"));
        Seed a = await SeedAsync(services, TenantA, material);
        Seed b = await SeedAsync(services, TenantB, material);
        // Actual independent scoped DbContexts/connections compete for one persisted credential budget.
        StationCoreResult[] failures = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Unlock(services, TenantA, a, Guid.NewGuid(), 1, 1, "000002")));
        Assert.Equal(4, failures.Count(x => x.Outcome == StationCoreOutcome.Rejected));
        Assert.Equal(4, failures.Count(x => x.Outcome == StationCoreOutcome.Throttled));
        using (var scope = Scope(services, TenantA))
        {
            var db = scope.ServiceProvider.GetRequiredService<StationsDbContext>();
            Assert.Equal(5, (await db.Credentials.SingleAsync()).FailureCount);
            Assert.Equal(5, (await db.BrowserSessions.SingleAsync()).AttemptCount);
            Assert.Equal(8, await db.OperationReceipts.CountAsync());
        }
        // Other tenant is unaffected; two simultaneous unlocks cannot both replace the same generation.
        Guid successfulOperation = Guid.NewGuid();
        StationCoreResult[] unlockRace = await Task.WhenAll(
            Unlock(services, TenantB, b, successfulOperation, 1, 1, "000001"),
            Unlock(services, TenantB, b, Guid.NewGuid(), 1, 1, "000001"));
        Assert.Single(unlockRace, x => x.Outcome == StationCoreOutcome.Applied);
        Assert.Single(unlockRace, x => x.Outcome == StationCoreOutcome.Conflict);
        StationCoreResult winner = unlockRace.Single(x => x.Outcome == StationCoreOutcome.Applied);
        using (var scope = Scope(services, TenantB))
        {
            var db = scope.ServiceProvider.GetRequiredService<StationsDbContext>();
            PostgresException missingCredentialRevision = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE stations.browser_sessions SET \"CredentialRevision\" = NULL WHERE \"ScopeId\" = {TenantB} AND \"Id\" = {b.Browser}"));
            Assert.Equal(PostgresErrorCodes.CheckViolation, missingCredentialRevision.SqlState);
            Assert.Equal("CK_browser_actor", missingCredentialRevision.ConstraintName);
            PostgresException missingRegistrationRevision = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE stations.browser_sessions SET \"RegistrationRevision\" = NULL WHERE \"ScopeId\" = {TenantB} AND \"Id\" = {b.Browser}"));
            Assert.Equal(PostgresErrorCodes.CheckViolation, missingRegistrationRevision.SqlState);
            Assert.Equal("CK_browser_actor", missingRegistrationRevision.ConstraintName);
        }
        using (var scope = Scope(services, TenantB))
        {
            var db = scope.ServiceProvider.GetRequiredService<StationsDbContext>();
            StationOperationReceipt receipt = await db.OperationReceipts.SingleAsync(x => x.Outcome == StationMutationOutcome.Applied);
            successfulOperation = receipt.Id;
        }
        StationCoreResult replay = await Unlock(services, TenantB, b, successfulOperation, 1, 1, "000001");
        Assert.Equal(winner, replay);
        Assert.Equal(StationCoreOutcome.Conflict, (await Unlock(services, TenantB, b, successfulOperation, 99, 1, "000001")).Outcome);
        // Wrong tenant sees no target; no cloned context or disabled tenant filter is used.
        Assert.Equal(StationCoreOutcome.Rejected, (await Unlock(services, TenantA, b, Guid.NewGuid(), 1, 1, "000001")).Outcome);
        using (var scope = Scope(services, TenantB))
        {
            var store = scope.ServiceProvider.GetRequiredService<IStationsStore>();
            Assert.Equal(StationCoreOutcome.Applied, (await store.LockCoreAsync(Guid.NewGuid(), b.Station, b.Browser, winner.Generation!.Value, Now)).Outcome);
        }
        using (var scope = Scope(services, TenantB))
        {
            StationBrowserSession device = await scope.ServiceProvider.GetRequiredService<StationsDbContext>().BrowserSessions.SingleAsync();
            Assert.True(device.PairingCurrent(Now, 1));
            Assert.Null(device.ActorSessionId);
            Assert.Equal(3, device.Generation);
        }
        Assert.Equal(StationCoreOutcome.Conflict, (await Unlock(services, TenantB, b, successfulOperation, 1, 1, "000001")).Outcome);
        StationCoreResult again = await Unlock(services, TenantB, b, Guid.NewGuid(), 3, 1, "000001");
        Assert.Equal(StationCoreOutcome.Applied, again.Outcome);
        // Reset serializes against unlock and invalidates every active session while preserving history.
        using (var scope = Scope(services, TenantB))
        {
            Assert.Equal(StationCoreOutcome.Applied, (await scope.ServiceProvider.GetRequiredService<IStationsStore>()
                .RevokeCredentialCoreAsync(Guid.NewGuid(), b.Staff, 1, Now)).Outcome);
        }

        using (var scope = Scope(services, TenantB))
        {
            var db = scope.ServiceProvider.GetRequiredService<StationsDbContext>();
            Assert.True((await db.Credentials.SingleAsync()).Revoked);
            Assert.Empty((await db.Credentials.SingleAsync()).Verifier);
            Assert.Null((await db.BrowserSessions.SingleAsync()).ActorSessionId);
        }
        Assert.Equal(StationCoreOutcome.Rejected, (await Unlock(services, TenantB, b, Guid.NewGuid(), 5, 2, "000001")).Outcome);
        // Redeem a one-use current revision grant in the actual database; second operation cannot consume it.
        Guid setupId = Guid.NewGuid();
        using (var scope = Scope(services, TenantB))
        {
            var db = scope.ServiceProvider.GetRequiredService<StationsDbContext>();
            db.SetupGrants.Add(new(setupId, TenantB, b.Station, b.Browser, b.Property, b.Staff,
                StationActorKind.StationOnly, 2, Now, Now.AddMinutes(10)));
            await db.SaveChangesAsync();
        }
        Guid redeemId = Guid.NewGuid();
        using (var scope = Scope(services, TenantB))
        {
            Assert.Equal(StationCoreOutcome.Applied, (await scope.ServiceProvider.GetRequiredService<IStationsStore>()
                .RedeemSetupCoreAsync(redeemId, setupId, b.Browser, material, Now)).Outcome);
        }

        using (var scope = Scope(services, TenantB))
        {
            var store = scope.ServiceProvider.GetRequiredService<IStationsStore>();
            Assert.Equal(StationCoreOutcome.Applied, (await store.RedeemSetupCoreAsync(redeemId, setupId, b.Browser, material, Now)).Outcome);
            Assert.Equal(StationCoreOutcome.Conflict, (await store.RedeemSetupCoreAsync(Guid.NewGuid(), setupId, b.Browser, material, Now)).Outcome);
        }
        StationCoreResult stationOnly = await Unlock(services, TenantB, b, Guid.NewGuid(), 5, 3, "000001");
        Assert.Equal(StationCoreOutcome.Applied, stationOnly.Outcome);
        using (var scope = Scope(services, TenantB))
        {
            Assert.Equal(StationCoreOutcome.Applied, (await scope.ServiceProvider.GetRequiredService<IStationsStore>()
                .RevokeGrantCoreAsync(Guid.NewGuid(), b.Property, b.Staff, 1, Now)).Outcome);
        }

        Assert.Equal(StationCoreOutcome.Conflict, (await Unlock(services, TenantB, b, Guid.NewGuid(), 7, 3, "000001")).Outcome);
        using (var scope = Scope(services, TenantB))
        {
            var db = scope.ServiceProvider.GetRequiredService<StationsDbContext>();
            db.BrowserSessions.Add(new(Guid.NewGuid(), TenantB, b.Station, Guid.NewGuid(), Digest(), Now, Now.AddDays(1), 1));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        using (var scope = Scope(services, TenantA))
        {
            var db = scope.ServiceProvider.GetRequiredService<StationsDbContext>();
            db.Stations.Add(new(Guid.NewGuid(), TenantB, Guid.NewGuid(), "Wrong tenant"));
            await Assert.ThrowsAsync<ScopeWriteGuardException>(() => db.SaveChangesAsync());
        }
        // Missing pepper and stale revisions do not consume either persisted failure budget.
        Seed unavailable = await SeedAsync(services, TenantA, material);
        var peppers = (StationPinVerifierTests.TestPeppers)services.GetRequiredService<IStationPepperProvider>();
        peppers.Missing = true;
        Assert.Equal(StationCoreOutcome.Unavailable, (await Unlock(services, TenantA, unavailable, Guid.NewGuid(), 1, 1, "000001")).Outcome);
        peppers.Missing = false;
        Assert.Equal(StationCoreOutcome.Conflict, (await Unlock(services, TenantA, unavailable, Guid.NewGuid(), 1, 99, "000001")).Outcome);
        using (var scope = Scope(services, TenantA))
        {
            var db = scope.ServiceProvider.GetRequiredService<StationsDbContext>();
            Assert.Equal(0, (await db.BrowserSessions.SingleAsync(x => x.Id == unavailable.Browser)).AttemptCount);
            Assert.Equal(0, (await db.Credentials.SingleAsync(x => x.StaffMemberId == unavailable.Staff)).FailureCount);
        }
        // Reset and unlock contend through independent real connections; reset always leaves no usable actor.
        Seed resetRace = await SeedAsync(services, TenantA, material);
        async Task<StationCoreResult> Reset()
        {
            using var scope = Scope(services, TenantA);
            return await scope.ServiceProvider.GetRequiredService<IStationsStore>().RevokeCredentialCoreAsync(Guid.NewGuid(), resetRace.Staff, 1, Now);
        }
        await Task.WhenAll(Unlock(services, TenantA, resetRace, Guid.NewGuid(), 1, 1, "000001"), Reset());
        using (var scope = Scope(services, TenantA))
        {
            var db = scope.ServiceProvider.GetRequiredService<StationsDbContext>();
            Assert.True((await db.Credentials.SingleAsync(x => x.StaffMemberId == resetRace.Staff)).Revoked);
            Assert.Null((await db.BrowserSessions.SingleAsync(x => x.Id == resetRace.Browser)).ActorSessionId);
        }
        // Independent device budget catches rotating registered identities, without touching another Staff's counter.
        Seed rotating = await SeedAsync(services, TenantA, material);
        for (int i = 0; i < 21; i++)
        {
            var unknown = rotating with { Staff = Guid.NewGuid() };
            using (var selected = Scope(services, TenantA))
            {
                var db = selected.ServiceProvider.GetRequiredService<StationsDbContext>();
                db.StaffRegistrations.Add(new(TenantA, unknown.Property, unknown.Staff, i + 2));
                db.Credentials.Add(new(TenantA, unknown.Staff, material, Now));
                await db.SaveChangesAsync();
            }
            StationCoreResult result = await Unlock(services, TenantA, unknown, Guid.NewGuid(), 1, 1, "000002");
            Assert.Equal(i >= 19 ? StationCoreOutcome.Throttled : StationCoreOutcome.Rejected, result.Outcome);
        }
        using (var scope = Scope(services, TenantA))
        {
            var db = scope.ServiceProvider.GetRequiredService<StationsDbContext>();
            StationBrowserSession browser = await db.BrowserSessions.SingleAsync(x => x.Id == rotating.Browser);
            Assert.Equal(20, browser.AttemptCount);
            Assert.Equal(Now.AddMinutes(15), browser.CooldownUntilUtc);
            Assert.Equal(0, (await db.Credentials.SingleAsync(x => x.StaffMemberId == rotating.Staff)).FailureCount);
        }
        using (var scope = Scope(services, TenantA))
        {
            var store = scope.ServiceProvider.GetRequiredService<IStationsStore>();
            Assert.Equal(StationCoreOutcome.Applied, (await store.RevokeStationCoreAsync(Guid.NewGuid(), rotating.Station, Now)).Outcome);
        }
        Assert.Equal(StationCoreOutcome.Rejected, (await Unlock(services, TenantA, rotating, Guid.NewGuid(), 2, 1, "000001")).Outcome);

        // Switching clears A even when B cannot unlock; returned generation makes a new attempt possible.
        foreach (string failure in new[] { "wrong", "missing", "revoked", "unavailable" })
        {
            Seed actorA = await SeedAsync(services, TenantA, material);
            Seed actorB = actorA with { Staff = Guid.NewGuid() };
            if (failure != "missing")
            {
                using var scope = Scope(services, TenantA);
                var db = scope.ServiceProvider.GetRequiredService<StationsDbContext>();
                var credential = new StationStaffCredential(TenantA, actorB.Staff, material, Now);
                if (failure == "revoked")
                {
                    credential.Revoke(Now);
                }
                db.Credentials.Add(credential);
                db.CheckInGrants.Add(new(TenantA, actorB.Property, actorB.Staff));
                db.StaffRegistrations.Add(new(TenantA, actorB.Property, actorB.Staff, 2));
                await db.SaveChangesAsync();
            }
            else
            {
                using var scope = Scope(services, TenantA);
                var db = scope.ServiceProvider.GetRequiredService<StationsDbContext>();
                db.StaffRegistrations.Add(new(TenantA, actorB.Property, actorB.Staff, 2));
                await db.SaveChangesAsync();
            }
            Guid originalAttempt = Guid.NewGuid();
            StationCoreResult activeA = await Unlock(services, TenantA, actorA, originalAttempt, 1, 1, "000001");
            Assert.Equal(StationCoreOutcome.Applied, activeA.Outcome);
            peppers.Missing = failure == "unavailable";
            Guid failedAttempt = Guid.NewGuid();
            StationCoreResult failedB = await Unlock(services, TenantA, actorB, failedAttempt, activeA.Generation!.Value,
                failure == "revoked" ? 2 : 1, failure == "wrong" ? "000002" : "000001");
            peppers.Missing = false;
            Assert.Equal(failure == "unavailable" ? StationCoreOutcome.Unavailable : StationCoreOutcome.Rejected, failedB.Outcome);
            Assert.Equal(3, failedB.Generation);
            using (var scope = Scope(services, TenantA))
            {
                var db = scope.ServiceProvider.GetRequiredService<StationsDbContext>();
                StationBrowserSession device = await db.BrowserSessions.SingleAsync(x => x.Id == actorA.Browser);
                Assert.Null(device.StaffMemberId);
                Assert.Null(device.ActorSessionId);
                Assert.True(device.PairingCurrent(Now, 1));
                Assert.Equal(3, device.Generation);
                Assert.Equal(failure == "unavailable" ? 0 : 1, device.AttemptCount);
            }
            Assert.Equal(StationCoreOutcome.Conflict, (await Unlock(services, TenantA, actorA, originalAttempt, 1, 1, "000001")).Outcome);
            Assert.Equal(StationCoreOutcome.Conflict, (await Unlock(services, TenantA, actorA, Guid.NewGuid(), 2, 1, "000001")).Outcome);
            // Reusing an attempt ID with corrected digits is an outcome lookup, never another PIN verification.
            Assert.Equal(failedB, await Unlock(services, TenantA, actorB, failedAttempt, 2, failure == "revoked" ? 2 : 1, "000001"));
            Seed recovering = failure is "wrong" or "unavailable" ? actorB : actorA;
            StationCoreResult recovered = await Unlock(services, TenantA, recovering, Guid.NewGuid(), failedB.Generation!.Value, 1, "000001");
            Assert.Equal(StationCoreOutcome.Applied, recovered.Outcome);
            Assert.Equal(4, recovered.Generation);
            using (var scope = Scope(services, TenantA))
            {
                var db = scope.ServiceProvider.GetRequiredService<StationsDbContext>();
                StationBrowserSession device = await db.BrowserSessions.SingleAsync(x => x.Id == actorA.Browser);
                Assert.Equal(recovering.Staff, device.StaffMemberId);
                Assert.Equal(failure == "unavailable" ? 0 : 1, device.AttemptCount);
                Assert.Equal(0, (await db.Credentials.SingleAsync(x => x.StaffMemberId == recovering.Staff)).FailureCount);
            }
        }

        // Independent connections with an earlier captured reset time have an explicit conflict, never a partial reset/500.
        Seed skew = await SeedAsync(services, TenantA, material);
        Guid staleSetup = Guid.NewGuid();
        using (var scope = Scope(services, TenantA))
        {
            var db = scope.ServiceProvider.GetRequiredService<StationsDbContext>();
            db.SetupGrants.Add(new(staleSetup, TenantA, skew.Station, skew.Browser, skew.Property, skew.Staff,
                StationActorKind.StationOnly, 1, Now, Now.AddMinutes(10)));
            await db.SaveChangesAsync();
            var store = scope.ServiceProvider.GetRequiredService<IStationsStore>();
            Assert.Equal(StationCoreOutcome.Applied, (await store.TryUnlockCoreAsync(Guid.NewGuid(), skew.Station,
                skew.Browser, skew.Staff, 1, 1, StationAuthorityKind.StationOnly, 1, "000001", Now.AddSeconds(2))).Outcome);
        }
        using (var scope = Scope(services, TenantA))
        {
            var store = scope.ServiceProvider.GetRequiredService<IStationsStore>();
            var db = scope.ServiceProvider.GetRequiredService<StationsDbContext>();
            StationBrowserSession beforeBrowser = await db.BrowserSessions.AsNoTracking().SingleAsync(x => x.Id == skew.Browser);
            StationStaffCredential beforeCredential = await db.Credentials.AsNoTracking().SingleAsync(x => x.StaffMemberId == skew.Staff);
            Assert.Equal(StationCoreOutcome.Conflict, (await store.TryUnlockCoreAsync(Guid.NewGuid(), skew.Station,
                skew.Browser, skew.Staff, 2, 99, StationAuthorityKind.StationOnly, 1, "000001", Now.AddSeconds(2))).Outcome);
            Assert.Equal(StationCoreOutcome.Conflict, (await store.TryUnlockCoreAsync(Guid.NewGuid(), skew.Station,
                skew.Browser, skew.Staff, 2, 1, StationAuthorityKind.StationOnly, 99, "000001", Now.AddSeconds(2))).Outcome);
            Assert.Equal(StationCoreOutcome.Conflict, (await store.TryUnlockCoreAsync(Guid.NewGuid(), skew.Station,
                skew.Browser, skew.Staff, 2, 1, StationAuthorityKind.StationOnly, 1, "000001", Now.AddSeconds(1))).Outcome);
            Assert.Equal(StationCoreOutcome.Conflict, (await store.LockCoreAsync(Guid.NewGuid(), skew.Station, skew.Browser, 2, Now.AddSeconds(1))).Outcome);
            Assert.Equal(StationCoreOutcome.Conflict, (await store.RedeemSetupCoreAsync(Guid.NewGuid(), staleSetup, skew.Browser, material, Now.AddSeconds(1))).Outcome);
            Assert.Equal(StationCoreOutcome.Conflict, (await store.RevokeCredentialCoreAsync(Guid.NewGuid(), skew.Staff, 1, Now.AddSeconds(1))).Outcome);
            Assert.Equal(StationCoreOutcome.Conflict, (await store.RevokeStationCoreAsync(Guid.NewGuid(), skew.Station, Now.AddSeconds(1))).Outcome);
            Assert.Equal(StationCoreOutcome.Conflict, (await store.RevokeGrantCoreAsync(Guid.NewGuid(), skew.Property, skew.Staff, 1, Now.AddSeconds(1))).Outcome);
            StationBrowserSession afterBrowser = await db.BrowserSessions.AsNoTracking().SingleAsync(x => x.Id == skew.Browser);
            StationStaffCredential afterCredential = await db.Credentials.AsNoTracking().SingleAsync(x => x.StaffMemberId == skew.Staff);
            Assert.Equal(beforeBrowser.Generation, afterBrowser.Generation);
            Assert.Equal(beforeBrowser.ActorSessionId, afterBrowser.ActorSessionId);
            Assert.Equal(beforeBrowser.StaffMemberId, afterBrowser.StaffMemberId);
            Assert.Equal(beforeBrowser.AttemptCount, afterBrowser.AttemptCount);
            Assert.Equal(beforeBrowser.ActorIdleExpiresAtUtc, afterBrowser.ActorIdleExpiresAtUtc);
            Assert.Equal(beforeCredential.Revision, afterCredential.Revision);
            Assert.Equal(beforeCredential.FailureCount, afterCredential.FailureCount);
            Assert.True(beforeCredential.Verifier == afterCredential.Verifier && beforeCredential.Salt == afterCredential.Salt);
            Assert.Null((await db.SetupGrants.AsNoTracking().SingleAsync(x => x.Id == staleSetup)).ConsumedAtUtc);
            Assert.False((await db.CheckInGrants.AsNoTracking().SingleAsync(x => x.StaffMemberId == skew.Staff)).Revoked);
            Assert.Equal(StationCoreOutcome.Applied, (await store.RevokeCredentialCoreAsync(Guid.NewGuid(), skew.Staff, 1, Now.AddSeconds(3))).Outcome);
        }
        using (var scope = Scope(services, TenantA))
        {
            var db = scope.ServiceProvider.GetRequiredService<StationsDbContext>();
            Assert.True((await db.Credentials.SingleAsync(x => x.StaffMemberId == skew.Staff)).Revoked);
            Assert.Null((await db.BrowserSessions.SingleAsync(x => x.Id == skew.Browser)).ActorSessionId);
            Assert.False((await db.Stations.SingleAsync(x => x.Id == skew.Station)).Revoked);
            Assert.False((await db.CheckInGrants.SingleAsync(x => x.StaffMemberId == skew.Staff)).Revoked);
            db.CheckOutGrants.Add(new(TenantA, skew.Property, skew.Staff));
            await db.SaveChangesAsync();
            var guarded = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>()
                .MigrateAsync("20260920020000_AddStationManagementPrerequisites"));
            Assert.Equal(PostgresErrorCodes.RaiseException, guarded.SqlState);
            Assert.Contains("20261008102749_AddNeutralStationAdmissionAndCheckOutGrant", await db.Database.GetAppliedMigrationsAsync());
            Assert.Single(await db.CheckOutGrants.ToArrayAsync());
        }
    }

    [Fact]
    public void Unsupported_provider_is_rejected_before_context_registration()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Persistence:Provider"] = "SqlServer" });
        Assert.Throws<NotSupportedException>(() => builder.AddStationsPersistence());
        Assert.DoesNotContain(builder.Services, d => d.ServiceType == typeof(StationsDbContext));
    }
    private static async Task<Seed> SeedAsync(ServiceProvider services, string tenant, StationPinMaterial material)
    {
        var seed = new Seed(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        using var scope = Scope(services, tenant);
        var db = scope.ServiceProvider.GetRequiredService<StationsDbContext>();
        db.Stations.Add(new(seed.Station, tenant, seed.Property, "Synthetic station"));
        db.BrowserSessions.Add(new(seed.Browser, tenant, seed.Station, seed.Property, Digest(), Now, Now.AddDays(30), 1));
        db.Credentials.Add(new(tenant, seed.Staff, material, Now));
        db.CheckInGrants.Add(new(tenant, seed.Property, seed.Staff));
        db.StaffRegistrations.Add(new(tenant, seed.Property, seed.Staff, 1));
        await db.SaveChangesAsync();
        return seed;
    }
    private static async Task<StationCoreResult> Unlock(ServiceProvider services, string tenant, Seed seed,
        Guid operation, long generation, long credentialRevision, string pin)
    {
        using var scope = Scope(services, tenant);
        return await scope.ServiceProvider.GetRequiredService<IStationsStore>().TryUnlockCoreAsync(operation, seed.Station,
            seed.Browser, seed.Staff, generation, credentialRevision, StationAuthorityKind.StationOnly, 1, pin, Now);
    }
    private static IServiceScope Scope(ServiceProvider provider, string tenant)
    {
        IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<TestScope>().ScopeId = tenant;
        return scope;
    }
    private static string Digest() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    private sealed record Seed(Guid Station, Guid Browser, Guid Property, Guid Staff);
    private static async Task<string> LegacyFingerprint(StationsDbContext db, string tenant, bool removeRuntimeColumns = true, bool neutralActor = false)
    {
        var content = new StringBuilder();
        foreach (string table in new[] { "stations", "browser_sessions", "staff_credentials", "staff_check_in_grants", "setup_grants", "operation_receipts" })
        {
            // Fixed tables and explicit additive columns only. Preexisting P1 and P2 bytes remain independently comparable.
            string runtimeColumns = removeRuntimeColumns ? " - 'EnrollmentAuthorityKind' - 'EnrollmentAuthSubjectId' - 'ExpectedEnrollmentAuthorityKind' - 'ExpectedEnrollmentAuthSubjectId'" : "";
            string neutralColumns = " - 'RegistrationRevision'";
            if (neutralActor && table == "browser_sessions")
            { neutralColumns += " - 'StaffMemberId' - 'ActorSessionId' - 'AuthorityKind' - 'CredentialRevision' - 'GrantRevision' - 'ActorIdleExpiresAtUtc' - 'ActorAbsoluteExpiresAtUtc' - 'Generation'"; }
            string managementColumns = " - 'IssuerKind' - 'IssuerSubjectId' - 'IssuerSessionId' - 'AssuranceExpiresAtUtc'";
            if (table == "operation_receipts")
            { managementColumns += " - 'StationId' - 'BrowserSessionId' - 'PropertyId' - 'StaffMemberId' - 'SetupGrantId' - 'ResourceVersion'"; }
            string sql = $"SELECT (to_jsonb(t){runtimeColumns}{managementColumns}{neutralColumns})::text AS \"Value\" FROM stations.{table} t WHERE \"ScopeId\" = @tenant";
            content.Append(await db.Database.SqlQueryRaw<string>(sql, new NpgsqlParameter("tenant", tenant)).SingleAsync());
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content.ToString())));
    }
    private sealed class TestClock : ISystemClock { public DateTimeOffset UtcNow => Now; }
    private sealed class TestScope : IScopeContextAccessor
    {
        public string? ScopeId { get; set; }
        public bool IsEnabled => true;
        public void SetScope(string scopeId) => this.ScopeId = scopeId;
        public void ClearScope() => this.ScopeId = null;
    }
}
