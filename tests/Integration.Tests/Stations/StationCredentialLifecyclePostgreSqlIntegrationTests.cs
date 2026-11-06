namespace Integration.Tests;

using System.Security.Cryptography;
using System.Security.Claims;
using System.Globalization;
using BunkFy.Extensions.Workspaces;
using BunkFy.Modules.Properties.Application.Ports;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Domain.ValueObjects;
using BunkFy.Modules.Properties.Persistence;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.Aggregates;
using BunkFy.Modules.Staff.Persistence;
using BunkFy.Modules.Stations.Application;
using BunkFy.Modules.Stations.Contracts;
using BunkFy.Modules.Stations.Domain;
using BunkFy.Modules.Stations.Persistence;
using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Persistence;
using BunkFy.TimeZones;
using Gma.Framework.AccessControl;
using Gma.Framework.Modules;
using Gma.Framework.Permissions;
using Gma.Framework.Runtime.Infrastructure;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Gma.Framework.Security;
using Gma.Modules.AccessControl.Application;
using Gma.Modules.AccessControl.Contracts;
using Gma.Modules.AccessControl.Persistence;
using Gma.Modules.Auth.Contracts;
using Gma.Modules.Auth.Domain.Aggregates;
using Gma.Modules.Auth.Domain.Enums;
using Gma.Modules.Auth.Domain.ValueObjects;
using Gma.Modules.Auth.Persistence;
using Gma.Modules.Organizations.Domain.Aggregates;
using Gma.Modules.Organizations.Domain.Enums;
using Gma.Modules.Organizations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class StationCredentialLifecyclePostgreSqlIntegrationTests
{
    private const string TenantA = "aa000000-0000-0000-0000-000000000001";
    private const string TenantB = "bb000000-0000-0000-0000-000000000002";
    private const string AuthScope = "global";
    private const string Role = "station-runtime-fixture";
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Registered_runtime_revalidates_real_owners_binding_activity_replay_and_device_isolation()
    {
        Assert.Equal("true", Environment.GetEnvironmentVariable("GMA_REQUIRE_DOCKER_TESTS"));
        await using PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithName($"bunkfy-stations-p2-{Guid.NewGuid():N}").WithLabel("bunkfy.test.grant", "STAFF-PIN-FIRST-JOB-P2A")
            .WithDatabase("station_runtime").Build();
        await postgres.StartAsync();
        var time = new TestClock();
        var reads = new ReadControl();
        await using ServiceProvider services = BuildServices(postgres.GetConnectionString(), time, reads);
        using (var migration = Scope(services, TenantA))
        {
            var sp = migration.ServiceProvider;
            await sp.GetRequiredService<PropertiesDbContext>().Database.MigrateAsync();
            await sp.GetRequiredService<StaffDbContext>().Database.MigrateAsync();
            await sp.GetRequiredService<WorkspacesDbContext>().Database.MigrateAsync();
            await sp.GetRequiredService<OrganizationsDbContext>().Database.MigrateAsync();
            await sp.GetRequiredService<AuthDbContext>().Database.MigrateAsync();
            await sp.GetRequiredService<AccessControlDbContext>().Database.MigrateAsync();
            var stations = sp.GetRequiredService<StationsDbContext>();
            await stations.Database.MigrateAsync();
            await Integration.Tests.Support.StationMigrationTestData.AssertCurrentAsync(stations);
        }
        Guid accountA = Guid.NewGuid(), accountB = Guid.NewGuid();
        await SeedAccount(services, accountA);
        await SeedAccount(services, accountB);
        Device a = await Seed(services, TenantA, accountA, linked: true);
        Device b = await Seed(services, TenantB, accountB, linked: false);
        using (var membership = Scope(services, TenantA))
        {
            var db = membership.ServiceProvider.GetRequiredService<OrganizationsDbContext>();
            db.Memberships.Add(OrganizationMembership.Create(Guid.NewGuid(), Guid.Parse(TenantA), accountB.ToString("D"),
                OrganizationMembershipRole.Member, "user:fixture", Guid.NewGuid(), Now).Value);
            await db.SaveChangesAsync();
        }
        using (var admissionScope = Scope(services, TenantB))
        {
            var coordinator = admissionScope.ServiceProvider.GetRequiredService<StationAdmissionCoordinator>();
            var localBinding = new StationEnrollmentBinding(StationActorKind.StationOnly, null);
            time.UtcNow = Now.AddHours(-10); // Sep20 UTC is still Sep19 at this property's pinned zone.
            var tooEarly = await coordinator.ObserveAsync(b.Property, b.Staff, localBinding);
            Assert.Equal(StationAdmissionState.Denied, tooEarly.State);
            Assert.Equal(new DateOnly(2026, 9, 19), tooEarly.PropertyLocalDate);
            time.UtcNow = Now;
            Assert.Equal(StationAdmissionState.Current, (await coordinator.ObserveAsync(b.Property, b.Staff, localBinding)).State);
            reads.Calls = 0;
            reads.AfterSecondRead = () => { time.UtcNow = Now.AddHours(16); return Task.CompletedTask; };
            Assert.Equal(StationAdmissionState.StateChanged, (await coordinator.ObserveAsync(b.Property, b.Staff, localBinding)).State);
            time.UtcNow = Now;
            reads.Calls = 0;
            reads.AfterSecondRead = async () =>
            {
                using var change = Scope(services, TenantB);
                var db = change.ServiceProvider.GetRequiredService<PropertiesDbContext>();
                var property = await db.Properties.SingleAsync();
                var binding = PropertyGovernanceBinding.Create("US", "fixture", 1, "fixture", "fixture", "fixture", 1,
                    new string('a', 64), Now.AddDays(-1), Now.AddDays(1), Now).Value;
                Assert.True(property.ActivateProcessing(binding, [], property.Version, Guid.NewGuid(), Now, "user:fixture").IsSuccess);
                Assert.True(property.SuspendProcessing(property.Version, Guid.NewGuid(), Now, "user:fixture").IsSuccess);
                await db.SaveChangesAsync();
            };
            Assert.Equal(StationAdmissionState.StateChanged, (await coordinator.ObserveAsync(b.Property, b.Staff, localBinding)).State);
            // P2 staff-session prerequisites are not legal processing admission; P3 must run the real operational policy.
            Assert.Equal(StationAdmissionState.Current, (await coordinator.ObserveAsync(b.Property, b.Staff, localBinding)).State);
        }
        // The request scope deliberately belongs to B: bootstrap alone derives A; ambient B is never replaced.
        using var request = Scope(services, TenantB);
        var runtime = request.ServiceProvider.GetRequiredService<StationRuntimeService>();
        Assert.Same(runtime, request.ServiceProvider.GetRequiredService<IStationSessionReader>());
        Assert.Equal(StationSessionState.Locked, (await runtime.ReadAsync(a.Secret)).State);
        Assert.Equal(TenantB, request.ServiceProvider.GetRequiredService<IScopeContext>().ScopeId);
        Assert.Equal(StationSessionState.Invalid, (await runtime.ReadAsync(a.Secret + "=")).State);
        var bootstrap = request.ServiceProvider.GetRequiredService<IStationCredentialBootstrap>();
        Assert.Equal(TenantA, (await bootstrap.FindAsync(a.Secret))!.ScopeId);
        Assert.Equal(TenantB, (await bootstrap.FindAsync(b.Secret))!.ScopeId);
        // Global uniqueness is a database constraint, not merely the bootstrap's Take(2).
        using (var duplicate = Scope(services, TenantB))
        {
            var db = duplicate.ServiceProvider.GetRequiredService<StationsDbContext>();
            db.BrowserSessions.Add(new(Guid.NewGuid(), TenantB, b.Station, b.Property, StationCredentialEncoding.Digest(a.Secret)!, Now, Now.AddDays(1), 1));
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        }
        Guid unlock = Guid.NewGuid();
        StationRuntimeResponse active = await runtime.UnlockAsync(a.Secret, unlock, a.Staff, 1, "000001");
        Assert.Equal(StationCoreOutcome.Applied, active.Outcome);
        Assert.Equal(StationSessionState.Active, active.State);
        StationActorCoordinate actor = active.Session!.Actor!;
        Assert.Equal(StationAuthorityKind.LinkedStation, actor.AuthorityKind);
        Assert.Equal(active, await runtime.UnlockAsync(a.Secret, unlock, a.Staff, 1, "999999"));
        reads.Unavailable = true;
        Assert.Equal(new(StationSessionState.Unavailable), await runtime.ReadAsync(a.Secret));
        Assert.Equal(new(StationSessionState.Unavailable), await runtime.ForegroundActivityAsync(a.Secret, Guid.NewGuid(), actor));
        reads.Unavailable = false;
        Assert.Equal(active.Session.ActorIdleExpiresAtUtc, (await runtime.ReadAsync(a.Secret)).Session!.ActorIdleExpiresAtUtc);
        // Polling does not renew idle time. Explicit touch persists once, even with a later replay clock.
        time.UtcNow = Now.AddMinutes(4);
        Assert.Equal(active.Session.ActorIdleExpiresAtUtc, (await runtime.ReadAsync(a.Secret)).Session!.ActorIdleExpiresAtUtc);
        Guid touch = Guid.NewGuid();
        var touched = await runtime.ForegroundActivityAsync(a.Secret, touch, actor);
        Assert.Equal(StationCoreOutcome.Applied, touched.Outcome);
        Assert.Equal(Now.AddMinutes(9), touched.Session!.ActorIdleExpiresAtUtc);
        time.UtcNow = Now.AddMinutes(5);
        Assert.Equal(touched.Session.ActorIdleExpiresAtUtc, (await runtime.ForegroundActivityAsync(a.Secret, touch, actor)).Session!.ActorIdleExpiresAtUtc);
        Assert.Equal(StationSessionState.StateChanged, (await runtime.ForegroundActivityAsync(a.Secret, Guid.NewGuid(), actor with { Generation = actor.Generation - 1 })).State);
        using (var read = Scope(services, TenantA))
        {
            var db = read.ServiceProvider.GetRequiredService<StationsDbContext>();
            Assert.Single(await db.OperationReceipts.Where(x => x.Kind == StationMutationKind.ForegroundActivity).ToArrayAsync());
        }
        // Removing one job permission does not revoke neutral identity or deliberate activity.
        await SetAccess(services, TenantA, a.Property, accountA, false);
        Assert.Equal(StationSessionState.Active, (await runtime.ReadAsync(a.Secret)).State);
        var neutralTouch = await runtime.ForegroundActivityAsync(a.Secret, Guid.NewGuid(), actor);
        Assert.Equal(StationCoreOutcome.Applied, neutralTouch.Outcome);
        await SetAccess(services, TenantA, a.Property, accountA, true);
        Assert.Equal(neutralTouch.Session!.ActorIdleExpiresAtUtc, (await runtime.ReadAsync(a.Secret)).Session!.ActorIdleExpiresAtUtc);
        // Old linked enrollment cannot follow Staff from A through the legitimate unlink/link transition to B.
        Guid pinnedSetup = await Setup(services, a, new(StationActorKind.LinkedStation, accountA.ToString("D")), 1, time.UtcNow);
        await ChangeLink(services, a, null, time.UtcNow);
        await AssertBindingMismatch(services, runtime, a, actor, pinnedSetup, time.UtcNow);
        await ChangeLink(services, a, accountB, time.UtcNow);
        await SetAccess(services, TenantA, a.Property, accountB, true);
        await AssertBindingMismatch(services, runtime, a, actor, pinnedSetup, time.UtcNow);
        // Linked setup history stays unavailable; the own-primary path installs B's current origin.
        Guid currentSetup = await Setup(services, a, new(StationActorKind.LinkedStation, accountB.ToString("D")), 1, time.UtcNow);
        Guid redeem = Guid.NewGuid();
        using (var self = Scope(services, TenantA))
        {
            var management = self.ServiceProvider.GetRequiredService<StationManagementService>();
            Assert.Equal(StationManagementState.Applied, (await management.SetOwnPinAsync(Primary(accountB, time.UtcNow), a.Property, a.Staff, 1, redeem, "000002")).State);
            Assert.Equal(StationManagementState.Applied, (await management.SetOwnPinAsync(Primary(accountB, time.UtcNow), a.Property, a.Staff, 1, redeem, "999999")).State);
        }
        using (var read = Scope(services, TenantA))
        {
            var db = read.ServiceProvider.GetRequiredService<StationsDbContext>();
            var credential = await db.Credentials.SingleAsync();
            Assert.Equal(2, credential.Revision);
            Assert.Equal(accountB.ToString("D"), credential.EnrollmentAuthSubjectId);
            Assert.Single(await db.OperationReceipts.Where(x => x.Kind == StationMutationKind.OwnPin).ToArrayAsync());
            Assert.True((await db.SetupGrants.SingleAsync(x => x.Id == currentSetup)).Revoked);
        }
        var locked = await runtime.ReadAsync(a.Secret);
        Assert.Equal(StationSessionState.Locked, locked.State);
        active = await runtime.UnlockAsync(a.Secret, Guid.NewGuid(), a.Staff, locked.Session!.Generation, "000002");
        Assert.Equal(StationSessionState.Active, active.State);
        // Old attempt and old actor cannot be used after replacement; Lock keeps device pairing.
        Assert.NotEqual(StationCoreOutcome.Applied, (await runtime.UnlockAsync(a.Secret, unlock, a.Staff, 1, "000001")).Outcome);
        Assert.Equal(StationSessionState.StateChanged, (await runtime.ForegroundActivityAsync(a.Secret, touch, actor)).State);
        Guid lockOperation = Guid.NewGuid();
        long priorGeneration = active.Session!.Generation;
        var lockedResult = await runtime.LockAsync(a.Secret, lockOperation, priorGeneration);
        Assert.Equal(StationSessionState.Locked, lockedResult.State);
        var newerActor = await runtime.UnlockAsync(a.Secret, Guid.NewGuid(), a.Staff, lockedResult.Session!.Generation, "000002");
        Assert.Equal(StationSessionState.Active, newerActor.State);
        var lateLock = await runtime.LockAsync(a.Secret, lockOperation, priorGeneration);
        Assert.Equal(StationSessionState.StateChanged, lateLock.State);
        Assert.Equal(StationCoreOutcome.Conflict, lateLock.Outcome);
        Assert.Null(lateLock.Session); // An obsolete A response must never carry the newer actor's identity.
        Assert.Equal(newerActor.Session, (await runtime.ReadAsync(a.Secret)).Session);
        Assert.Equal(StationSessionState.Locked, (await runtime.LockAsync(a.Secret, Guid.NewGuid(), newerActor.Session!.Generation)).State);

        // Station-only identity uses current registration/common facts; each job observes its own grant.
        StationRuntimeResponse local = await runtime.UnlockAsync(b.Secret, Guid.NewGuid(), b.Staff, 1, "000001");
        Assert.Equal(StationSessionState.Active, local.State);
        Assert.Equal(StationAuthorityKind.StationOnly, local.Session!.Actor!.AuthorityKind);
        time.UtcNow = local.Session.ActorIdleExpiresAtUtc!.Value;
        var idleLocked = await runtime.ReadAsync(b.Secret);
        Assert.Equal(StationSessionState.Locked, idleLocked.State);
        Assert.Null(idleLocked.Session!.Actor);
        Assert.Equal(StationSessionState.StateChanged, (await runtime.ForegroundActivityAsync(b.Secret, Guid.NewGuid(), local.Session.Actor)).State);
        local = await runtime.UnlockAsync(b.Secret, Guid.NewGuid(), b.Staff, idleLocked.Session.Generation, "000001");
        Assert.Equal(StationSessionState.Active, local.State);
        Guid localSetup = await Setup(services, b, new(StationActorKind.StationOnly, null), 1, time.UtcNow);
        await ChangeLink(services, b, accountB, time.UtcNow);
        await AssertBindingMismatch(services, runtime, b, Assert.IsType<StationActorCoordinate>(local.Session?.Actor), localSetup, time.UtcNow);
        await ChangeLink(services, b, null, time.UtcNow);
        using (var revoke = Scope(services, TenantB))
        {
            Assert.Equal(StationCoreOutcome.Applied, (await revoke.ServiceProvider.GetRequiredService<IStationsStore>()
                .RevokeGrantCoreAsync(Guid.NewGuid(), b.Property, b.Staff, 1, time.UtcNow)).Outcome);
        }
        var localLocked = await runtime.ReadAsync(b.Secret);
        Assert.Equal(StationSessionState.Active, localLocked.State);
        Assert.Equal(local.Session!.Actor, localLocked.Session!.Actor);
        Assert.Equal(StationCoreOutcome.Applied, (await runtime.UnlockAsync(b.Secret, Guid.NewGuid(), b.Staff, localLocked.Session.Generation, "000001")).Outcome);

        // Unknown/unbound historical credentials cannot gain authority from today's Staff facts.
        using (var change = Scope(services, TenantA))
        {
            var db = change.ServiceProvider.GetRequiredService<StationsDbContext>();
            var credential = await db.Credentials.SingleAsync();
            credential.Replace(new(credential.Salt, credential.Verifier, credential.PepperVersion), time.UtcNow);
            await db.SaveChangesAsync();
        }
        locked = await runtime.ReadAsync(a.Secret);
        Assert.Equal(StationSessionState.StateChanged, (await runtime.UnlockAsync(a.Secret, Guid.NewGuid(), a.Staff, locked.Session!.Generation, "000002")).State);

        // Retained revoked digest is discoverable internally but never valid to the runtime.
        using (var change = Scope(services, TenantB))
        {
            var db = change.ServiceProvider.GetRequiredService<StationsDbContext>();
            (await db.BrowserSessions.SingleAsync()).Revoke(time.UtcNow);
            await db.SaveChangesAsync();
        }
        Assert.NotNull(await bootstrap.FindAsync(b.Secret));
        Assert.Equal(new(StationSessionState.Invalid), await runtime.ReadAsync(b.Secret));
        services.GetRequiredService<IOptions<StationOptions>>().Value.ExternalEpoch = 2;
        Assert.Equal(new(StationSessionState.Invalid), await runtime.ReadAsync(a.Secret));
        services.GetRequiredService<IOptions<StationOptions>>().Value.ExternalEpoch = 1;
        time.UtcNow = Now.AddDays(2);
        Assert.Equal(new(StationSessionState.Invalid), await runtime.ReadAsync(a.Secret));
        time.UtcNow = Now.AddMinutes(11);
        using (var change = Scope(services, TenantA))
        {
            var db = change.ServiceProvider.GetRequiredService<StationsDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO stations.tenant_lifecycle (\"ScopeId\", \"Closed\", \"Revision\") VALUES ({TenantA}, true, 1)");
        }
        Assert.Equal(new(StationSessionState.Invalid), await runtime.ReadAsync(a.Secret));
        Assert.Equal(TenantB, request.ServiceProvider.GetRequiredService<IScopeContext>().ScopeId);
        // Exact digest discovery is not admission: a revoke between bootstrap and scoped re-read wins.
        string isolatedTenant = "cc000000-0000-0000-0000-000000000003";
        string extraSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Guid extraStation = Guid.NewGuid(), extraBrowser = Guid.NewGuid(), extraProperty = Guid.NewGuid();
        using (var seed = Scope(services, isolatedTenant))
        {
            var db = seed.ServiceProvider.GetRequiredService<StationsDbContext>();
            db.Stations.Add(new(extraStation, isolatedTenant, extraProperty, "Revocation fixture"));
            db.BrowserSessions.Add(new(extraBrowser, isolatedTenant, extraStation, extraProperty, StationCredentialEncoding.Digest(extraSecret)!, time.UtcNow, Now.AddDays(1), 1));
            await db.SaveChangesAsync();
        }
        var revokedAfterLookup = new AfterBootstrap(bootstrap, async () =>
        {
            using var change = Scope(services, isolatedTenant);
            var db = change.ServiceProvider.GetRequiredService<StationsDbContext>();
            (await db.Stations.SingleAsync()).Revoke();
            await db.SaveChangesAsync();
        });
        var raced = new StationRuntimeService(request.ServiceProvider.GetRequiredService<IServiceScopeFactory>(), revokedAfterLookup, time);
        int ownerReads = reads.Calls;
        Assert.Equal(new(StationSessionState.Invalid), await raced.ReadAsync(extraSecret));
        Assert.Equal(ownerReads, reads.Calls);
    }

    private static async Task AssertBindingMismatch(ServiceProvider services, StationRuntimeService runtime, Device device,
        StationActorCoordinate actor, Guid setupId, DateTimeOffset now)
    {
        using var read = Scope(services, device.Tenant);
        var db = read.ServiceProvider.GetRequiredService<StationsDbContext>();
        var before = await db.BrowserSessions.AsNoTracking().SingleAsync();
        var credential = await db.Credentials.AsNoTracking().SingleAsync();
        int receipts = await db.OperationReceipts.CountAsync();
        Assert.Equal(new(StationSessionState.StateChanged), await runtime.ReadAsync(device.Secret));
        Assert.Equal(new(StationSessionState.StateChanged), await runtime.ForegroundActivityAsync(device.Secret, Guid.NewGuid(), actor));
        Assert.Equal(new(StationSessionState.StateChanged), await runtime.UnlockAsync(device.Secret, Guid.NewGuid(), device.Staff, before.Generation, "000001"));
        Assert.Equal(new(StationSessionState.StateChanged), await runtime.RedeemSeededSetupAsync(device.Secret, Guid.NewGuid(), setupId, "000002"));
        var after = await db.BrowserSessions.AsNoTracking().SingleAsync();
        Assert.Equal(before.Generation, after.Generation);
        Assert.Equal(before.ActorSessionId, after.ActorSessionId);
        Assert.Equal(before.AttemptCount, after.AttemptCount);
        Assert.Equal(before.ActorIdleExpiresAtUtc, after.ActorIdleExpiresAtUtc);
        var unchanged = await db.Credentials.AsNoTracking().SingleAsync();
        Assert.Equal(credential.Revision, unchanged.Revision);
        Assert.Equal(credential.Verifier, unchanged.Verifier);
        Assert.Equal(credential.Enrollment(), unchanged.Enrollment());
        Assert.Equal(credential.FailureCount, unchanged.FailureCount);
        Assert.Null((await db.SetupGrants.AsNoTracking().SingleAsync(x => x.Id == setupId)).ConsumedAtUtc);
        Assert.Equal(receipts, await db.OperationReceipts.CountAsync());
        Assert.True(now < before.PairingExpiresAtUtc);
    }

    private static async Task<Guid> Setup(ServiceProvider services, Device device, StationEnrollmentBinding binding, long revision, DateTimeOffset now)
    {
        using var scope = Scope(services, device.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<StationsDbContext>();
        Guid id = Guid.NewGuid();
        db.SetupGrants.Add(new(id, device.Tenant, device.Station, device.Browser, device.Property, device.Staff,
            binding.Kind, revision, now, now.AddMinutes(10), binding));
        await db.SaveChangesAsync();
        return id;
    }
    internal static async Task ChangeLink(ServiceProvider services, Device device, Guid? account, DateTimeOffset now)
    {
        using var scope = Scope(services, device.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<StaffDbContext>();
        var member = await db.StaffMembers.SingleAsync(x => x.Id == device.Staff);
        if (account is null)
        {
            Assert.True(member.Suspend(member.Version, "user:fixture", "fixture", Guid.NewGuid(), now).IsSuccess);
            Assert.True(member.SetAuthSubject(null, member.Version, "user:fixture", Guid.NewGuid(), now).IsSuccess);
            Assert.True(member.Resume(member.Version, "user:fixture", "fixture", Guid.NewGuid(), now).IsSuccess);
        }
        else
        {
            Assert.True(member.SetAuthSubject(account.Value.ToString("D"), member.Version, "user:fixture", Guid.NewGuid(), now).IsSuccess);
        }
        await db.SaveChangesAsync();
    }
    internal static ServiceProvider BuildServices(string connectionString, TestClock time, ReadControl reads,
        Action<IServiceCollection>? configure = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        { ["Persistence:Provider"] = "PostgreSql", ["ConnectionStrings:PostgreSql"] = connectionString });
        builder.Services.AddSingleton<ISystemClock>(time);
        builder.Services.AddSingleton<TimeProvider>(time);
        builder.AddRuntimeInfrastructure();
        builder.Services.AddScoped<TestScope>();
        builder.Services.AddScoped<IScopeContext>(sp => sp.GetRequiredService<TestScope>());
        builder.Services.AddScoped<IScopeContextAccessor>(sp => sp.GetRequiredService<TestScope>());
        builder.AddPropertiesPersistence();
        builder.AddStaffPersistence();
        builder.AddWorkspacesPersistence();
        builder.AddOrganizationsPersistence();
        builder.AddAuthPersistence(AuthProfile.Global(AuthScope));
        builder.AddAccessControlPersistence();
        builder.Services.AddGmaAccessControl();
        // Synthetic P2B policy composition only. Production descriptor/seeds/host activation remain P4.
        builder.Services.AddGmaAccessControlPermissionPolicies(ModuleDescriptor.Create("stations-fixture")
            .WithPermission(new ModulePermissionDescriptor(StationsPermissionCodes.Manage,
                "Synthetic station management fixture", PermissionScopeRequirement.Scoped, PermissionScopeGrantPolicy.Descendants)).Build());
        builder.Services.AddAccessControlApplication(builder.Configuration);
        builder.Services.AddWorkspacesApplication(builder.Configuration, AuthScope);
        builder.Services.AddBunkFyWorkspaces(o => o.GlobalAuthScopeId = AuthScope);
        builder.Services.AddSingleton<IStationPepperProvider, TestPeppers>();
        builder.Services.AddStationsCore(o => { o.ExternalEpoch = 1; o.PepperVersion = "fixture-v1"; });
        builder.Services.AddStationsRuntime();
        builder.Services.AddStationsManagement(new AuthenticationAssuranceRequirement(["urn:gma:acr:mfa", "urn:gma:acr:two-step"], TimeSpan.FromMinutes(10)), AuthScope);
        builder.AddStationsPersistence();
        // Fault/timing injection wraps the real registered Staff reader, never replaces its successful facts.
        Type ownerType = builder.Services.Last(x => x.ServiceType == typeof(IStaffStationEligibilitySource)).ImplementationType!;
        builder.Services.AddScoped<IStaffStationEligibilitySource>(sp => new ControlledStaff(
            (IStaffStationEligibilitySource)ActivatorUtilities.CreateInstance(sp, ownerType), reads));
        configure?.Invoke(builder.Services);
        return builder.Services.BuildServiceProvider();
    }
    internal static async Task SeedAccount(ServiceProvider services, Guid account)
    {
        using var scope = Scope(services, TenantA);
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var member = Member.Create(new MemberId(account), AuthScope, $"station-{account:N}@example.test",
            MemberUsernameType.Email, "synthetic-non-authenticating-hash", new MemberUsernameId(Guid.NewGuid()), Guid.NewGuid(), Now).Value;
        Assert.True(member.StartSession(new MemberSessionId(account), "synthetic-refresh-hash", Now.AddDays(2), Now).IsSuccess);
        db.Members.Add(member);
        await db.SaveChangesAsync();
    }
    internal static async Task<Device> Seed(ServiceProvider services, string tenant, Guid account, bool linked, bool stationData = true)
    {
        using var scope = Scope(services, tenant);
        var sp = scope.ServiceProvider;
        Guid propertyId = Guid.NewGuid(), staffId = Guid.NewGuid(), stationId = Guid.NewGuid(), browserId = Guid.NewGuid();
        var org = sp.GetRequiredService<OrganizationsDbContext>();
        org.Organizations.Add(Organization.Create(Guid.Parse(tenant), "Synthetic station organization", tenant,
            "user:fixture", Guid.NewGuid(), Now).Value);
        org.Memberships.Add(OrganizationMembership.Create(Guid.NewGuid(), Guid.Parse(tenant), account.ToString("D"),
            OrganizationMembershipRole.Owner, "user:fixture", Guid.NewGuid(), Now).Value);
        await org.SaveChangesAsync();
        var property = Property.Create(propertyId, tenant, "Synthetic station property", "station", "America/New_York", Guid.NewGuid(), Now).Value;
        await sp.GetRequiredService<IPropertyRepository>().AddAsync(property, CancellationToken.None);
        await sp.GetRequiredService<IPropertyTimeZoneRevisionWriter>().AppendAsync(new(Guid.NewGuid(), tenant, propertyId, propertyId,
            PropertyTimeZoneChangeKind.Created, property.TimeZoneId.Value, null, property.TimeZoneId.Value,
            TimeZoneCatalog.Default.CatalogVersion, 0, property.Version, "user:fixture", Now), CancellationToken.None);
        await sp.GetRequiredService<PropertiesDbContext>().SaveChangesAsync();
        var staff = StaffMember.Create(staffId, tenant, "Synthetic station operator", null, null, null, null, null, null,
            linked ? account.ToString("D") : null, "user:fixture", Guid.NewGuid(), Now).Value;
        Assert.True(staff.AssignProperty(Guid.NewGuid(), propertyId, null, false, new(2026, 9, 20), staff.Version,
            "user:fixture", Guid.NewGuid(), Now).IsSuccess);
        await sp.GetRequiredService<IStaffMemberRepository>().AddAsync(staff, CancellationToken.None);
        await sp.GetRequiredService<StaffDbContext>().SaveChangesAsync();
        await SetAccess(services, tenant, propertyId, account, true);
        if (!stationData)
        { return new(tenant, propertyId, staffId, stationId, browserId, ""); }
        var stations = sp.GetRequiredService<StationsDbContext>();
        string secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        stations.Stations.Add(new(stationId, tenant, propertyId, "Synthetic desk"));
        stations.StaffRegistrations.Add(new(tenant, propertyId, staffId, 1));
        stations.BrowserSessions.Add(new(browserId, tenant, stationId, propertyId, StationCredentialEncoding.Digest(secret)!, Now, Now.AddDays(1), 1));
        // This pre-paired runtime fixture has a real, already signed-out original pairing session.
        // The separate account session remains available for the existing owner-recovery tests.
        var auth = sp.GetRequiredService<AuthDbContext>();
        var pairingMember = await auth.Members.Include(x => x.Sessions).SingleAsync(x => x.Id == new MemberId(account));
        Guid pairingSession = Guid.NewGuid();
        Assert.True(pairingMember.StartSession(new MemberSessionId(pairingSession), "synthetic-old-pairing", Now.AddDays(1), Now).IsSuccess);
        Assert.True(pairingMember.SignOutSession(new MemberSessionId(pairingSession), Now).IsSuccess);
        await auth.SaveChangesAsync();
        stations.OperationReceipts.Add(new(tenant, Guid.NewGuid(), StationMutationKind.Register, new string('a', 64),
            new(StationMutationOutcome.Applied, Management: new(stationId, browserId, propertyId, null, null, 1)),
            Now, account.ToString("D"), StationIssuerKind.Manager, pairingSession));
        StationPinMaterial material = Assert.IsType<StationPinMaterial>(await sp.GetRequiredService<IStationPinVerifier>().CreateAsync("000001"));
        stations.Credentials.Add(new(tenant, staffId, material, Now, new(linked ? StationActorKind.LinkedStation : StationActorKind.StationOnly,
            linked ? account.ToString("D") : null)));
        if (!linked)
        { stations.CheckInGrants.Add(new(tenant, propertyId, staffId)); }
        await stations.SaveChangesAsync();
        return new(tenant, propertyId, staffId, stationId, browserId, secret);
    }
    internal static async Task SetAccess(ServiceProvider services, string tenant, Guid property, Guid account, bool enabled)
    {
        using var scope = Scope(services, tenant);
        var roles = scope.ServiceProvider.GetRequiredService<IAccessControlRoleProvisioner>();
        await roles.EnsureRoleAsync(new(Role, [ReservationsAdminPermissionCodes.CheckIn]));
        if (enabled)
        {
            await roles.EnsureAssignmentAsync(AccessSubject.User(account.ToString("D")), Role, WorkspaceAccessScopes.CreateProperty(tenant, property));
        }
        else
        {
            await roles.RemoveAssignmentAsync(AccessSubject.User(account.ToString("D")), Role, WorkspaceAccessScopes.CreateProperty(tenant, property));
        }
    }
    internal static IServiceScope Scope(ServiceProvider services, string tenant)
    {
        var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IScopeContextAccessor>().SetScope(tenant);
        return scope;
    }
    internal sealed class ReadControl
    {
        public int Calls { get; set; }
        public bool Unavailable { get; set; }
        public Func<Task>? AfterSecondRead { get; set; }
    }
    private sealed class AfterBootstrap(IStationCredentialBootstrap owner, Func<Task> after) : IStationCredentialBootstrap
    {
        public async Task<StationDeviceReference?> FindAsync(string opaqueCredential, CancellationToken cancellationToken = default)
        {
            var found = await owner.FindAsync(opaqueCredential, cancellationToken);
            await after();
            return found;
        }
    }
    private sealed class ControlledStaff(IStaffStationEligibilitySource owner, ReadControl control) : IStaffStationEligibilitySource
    {
        public async Task<StaffStationEligibilitySnapshot?> FindAsync(string scopeId, Guid propertyId, Guid staffMemberId, CancellationToken cancellationToken = default)
        {
            control.Calls++;
            if (control.Unavailable)
            { throw new InvalidOperationException("Synthetic owner unavailable"); }
            var facts = await owner.FindAsync(scopeId, propertyId, staffMemberId, cancellationToken);
            if (control.Calls == 2 && control.AfterSecondRead is { } after)
            {
                control.AfterSecondRead = null;
                await after();
            }
            return facts;
        }
    }
    internal sealed record Device(string Tenant, Guid Property, Guid Staff, Guid Station, Guid Browser, string Secret);
    internal static ClaimsPrincipal Primary(Guid account, DateTimeOffset now, Guid? sessionId = null) => new(new ClaimsIdentity(
        [new("sub", account.ToString("D")), new("sid", (sessionId ?? account).ToString("D")), new("acr", "urn:gma:acr:mfa"),
            new("auth_time", now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))], "trusted-primary-fixture"));
    private sealed class TestScope : IScopeContextAccessor
    {
        public bool IsEnabled { get; private set; }
        public string? ScopeId { get; private set; }
        public void SetScope(string scopeId) { this.IsEnabled = true; this.ScopeId = scopeId; }
        public void ClearScope() { this.IsEnabled = false; this.ScopeId = null; }
    }
    internal sealed class TestClock : TimeProvider, ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = Now;
        public override DateTimeOffset GetUtcNow() => this.UtcNow;
    }
    private sealed class TestPeppers : IStationPepperProvider
    {
        public bool TryGet(string version, out ReadOnlyMemory<byte> pepper)
        { pepper = new byte[32].Select(_ => (byte)1).ToArray(); return version == "fixture-v1"; }
    }
}
