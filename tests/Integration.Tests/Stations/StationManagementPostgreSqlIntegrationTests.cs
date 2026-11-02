namespace Integration.Tests;

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BunkFy.Modules.Properties.Persistence;
using BunkFy.Modules.Properties.Application.Ports;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.TimeZones;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.Aggregates;
using BunkFy.Modules.Staff.Persistence;
using BunkFy.Modules.Stations.Application;
using BunkFy.Modules.Stations.Contracts;
using BunkFy.Modules.Stations.Domain;
using BunkFy.Modules.Stations.Persistence;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Persistence;
using Gma.Framework.AccessControl;
using Gma.Modules.AccessControl.Contracts;
using Gma.Modules.AccessControl.Persistence;
using Gma.Modules.Auth.Domain.ValueObjects;
using Gma.Modules.Auth.Persistence;
using Gma.Modules.Organizations.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;
using static StationCredentialLifecyclePostgreSqlIntegrationTests;

public sealed class StationManagementPostgreSqlIntegrationTests
{
    private const string TenantA = "aa000000-0000-0000-0000-000000000001";
    private const string TenantB = "bb000000-0000-0000-0000-000000000002";
    private const string ManageRole = "stations-management-fixture";
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] Tables = ["stations", "browser_sessions", "staff_credentials", "staff_check_in_grants", "staff_registrations", "setup_grants", "operation_receipts"];

    [Fact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Persisted_original_pairing_session_gates_reload_recovery_and_repair_without_a_client_readiness_flag()
    {
        Assert.Equal("true", Environment.GetEnvironmentVariable("GMA_REQUIRE_DOCKER_TESTS"));
        await using var postgres = Container();
        await postgres.StartAsync();
        await using var services = Services(postgres.GetConnectionString(), new TestClock(), new KdfControl());
        await Migrate(services);
        Guid account = Guid.NewGuid();
        await SeedAccount(services, account);
        Device device = await Seed(services, TenantA, account, false, stationData: false);
        await Manager(services, device, account, root: true, enabled: true);
        var paired = await Manage(services, device, Primary(account, Now), Guid.NewGuid(),
            new(StationOperationKind.Register, device.Property, Label: "Handoff desk"));
        device = Paired(device, paired);
        var runtime = services.GetRequiredService<StationRuntimeService>();
        string unchanged = await Fingerprint(services, TenantA);
        Assert.Equal(new(StationSessionState.HandoffPending), await runtime.ReadAsync(device.Secret));
        Assert.Equal(new(StationSessionState.HandoffPending), await runtime.UnlockAsync(device.Secret, Guid.NewGuid(), device.Staff, 1, "000001"));
        var pending = await runtime.RosterAsync(device.Secret);
        Assert.Equal(StationSessionState.HandoffPending, pending.State);
        Assert.Empty(pending.Items);
        Assert.Equal(unchanged, await Fingerprint(services, TenantA));

        Guid recovery = await AddSession(services, account);
        // A new primary session does not replace immutable original pairing provenance.
        Assert.Equal(new(StationSessionState.HandoffPending), await runtime.ReadAsync(device.Secret));
        await SignOut(services, account, account);
        Assert.Equal(StationSessionState.Locked, (await runtime.ReadAsync(device.Secret)).State);
        var coordinate = new StationDeviceReference(TenantA, device.Browser, device.Station, device.Property);
        using (var fresh = Scope(services, TenantA))
        {
            var reader = fresh.ServiceProvider.GetRequiredService<IStationPairingHandoffReader>();
            Assert.Equal(new(coordinate, account.ToString("D"), account), await reader.ReadHandoffAsync(coordinate));
            Assert.Null(await reader.ReadHandoffAsync(coordinate with { PropertyId = Guid.NewGuid() }));
            Assert.Null(await reader.ReadHandoffAsync(coordinate with { BrowserSessionId = Guid.NewGuid() }));
            Assert.Null(await reader.ReadHandoffAsync(coordinate with { ScopeId = TenantB }));
        }

        // Corrupt/duplicate successful receipts cannot be resolved by selecting an arbitrary first row.
        Guid duplicate = Guid.NewGuid();
        using (var corrupt = Scope(services, TenantA))
        {
            var db = corrupt.ServiceProvider.GetRequiredService<StationsDbContext>();
            db.OperationReceipts.Add(new(TenantA, duplicate, StationMutationKind.Register, new string('b', 64),
                new(StationMutationOutcome.Applied, Management: new(device.Station, device.Browser, device.Property, null, null, 1)),
                Now, account.ToString("D"), StationIssuerKind.Manager, account));
            await db.SaveChangesAsync();
        }
        Assert.Equal(new(StationSessionState.StateChanged), await runtime.ReadAsync(device.Secret));
        using (var repairFixture = Scope(services, TenantA))
        {
            var db = repairFixture.ServiceProvider.GetRequiredService<StationsDbContext>();
            // Exact synthetic row only, inside this test's isolated PostgreSQL container.
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM stations.operation_receipts WHERE \"ScopeId\"={TenantA} AND \"Id\"={duplicate}");
            PostgresException rejected = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE stations.operation_receipts SET \"IssuerSubjectId\"='malformed' WHERE \"ScopeId\"={TenantA} AND \"BrowserSessionId\"={device.Browser}"));
            Assert.Equal(PostgresErrorCodes.CheckViolation, rejected.SqlState);
            Assert.Equal("CK_receipt_issuer", rejected.ConstraintName);
        }
        // PostgreSQL rejects malformed provenance before persistence; the original receipt remains valid.
        Assert.Equal(StationSessionState.Locked, (await runtime.ReadAsync(device.Secret)).State);
        var replacement = await Manage(services, device, Primary(account, Now, recovery), Guid.NewGuid(),
            new(StationOperationKind.Pair, device.Property, StationId: device.Station, ExpectedVersion: 1));
        Assert.Equal(StationSessionState.Invalid, (await runtime.ReadAsync(device.Secret)).State);
        device = Paired(device, replacement);
        Assert.Equal(new(StationSessionState.HandoffPending), await runtime.ReadAsync(device.Secret));
        await SignOut(services, account, recovery);
        Assert.Equal(StationSessionState.Locked, (await runtime.ReadAsync(device.Secret)).State);
    }

    [Fact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Real_owner_pairing_own_PIN_and_private_setup_preserve_scope_recovery_and_non_authority_boundaries()
    {
        Assert.Equal("true", Environment.GetEnvironmentVariable("GMA_REQUIRE_DOCKER_TESTS"));
        await using var postgres = Container();
        await postgres.StartAsync();
        var time = new TestClock();
        var kdf = new KdfControl();
        await using var services = Services(postgres.GetConnectionString(), time, kdf);
        await Migrate(services);
        Guid accountA = Guid.NewGuid(), accountB = Guid.NewGuid();
        await SeedAccount(services, accountA);
        await SeedAccount(services, accountB);
        Device a = await Seed(services, TenantA, accountA, true, stationData: false);
        Device b = await Seed(services, TenantB, accountB, false, stationData: false);
        await Manager(services, a, accountA, root: true, enabled: true);
        await Manager(services, b, accountB, root: true, enabled: true);
        Guid registerOp = Guid.NewGuid();
        var pairCommand = new StationManagementCommand(StationOperationKind.Register, a.Property, Label: "Synthetic A desk");
        var concurrent = await Task.WhenAll(
            Manage(services, a, Primary(accountA, Now), registerOp, pairCommand),
            Manage(services, a, Primary(accountA, Now), registerOp, pairCommand));
        Assert.All(concurrent, x => Assert.Equal(StationManagementState.Applied, x.Response.State));
        var delivered = Assert.Single(concurrent, x => x.Credential is not null);
        Assert.Single(concurrent, x => x.Credential is null);
        Assert.Equal(concurrent[0].Response, concurrent[1].Response);
        a = Paired(a, delivered);
        var bPair = await Manage(services, b, Primary(accountB, Now), Guid.NewGuid(), new(StationOperationKind.Register, b.Property, Label: "Synthetic B desk"));
        b = Paired(b, bPair);
        Assert.DoesNotContain(a.Secret, JsonSerializer.Serialize(delivered), StringComparison.Ordinal);
        Assert.Equal(accountA, delivered.Response.Receipt!.IssuerSessionId);
        var pendingRuntime = services.GetRequiredService<StationRuntimeService>();
        Assert.Equal(new(StationSessionState.HandoffPending), await pendingRuntime.ReadAsync(a.Secret));
        var pendingRoster = await pendingRuntime.RosterAsync(a.Secret);
        Assert.Equal(StationSessionState.HandoffPending, pendingRoster.State);
        Assert.Empty(pendingRoster.Items);
        await SetAccess(services, TenantA, a.Property, accountA, false);
        foreach (var target in new[] { (Device: a, Account: accountA), (Device: b, Account: accountB) })
        {
            var registered = await Manage(services, target.Device, Primary(target.Account, Now), Guid.NewGuid(),
                new(StationOperationKind.RegisterStaff, target.Device.Property, StaffMemberId: target.Device.Staff));
            Assert.Equal(StationManagementState.Applied, registered.Response.State);
        }
        Assert.Equal(StationManagementState.Applied, (await Manage(services, b, Primary(accountB, Now), Guid.NewGuid(),
            new(StationOperationKind.GrantCheckIn, b.Property, StaffMemberId: b.Staff))).Response.State);
        await Manager(services, a, accountA, root: true, enabled: false);
        await SetAccess(services, TenantA, a.Property, accountA, false);
        // No management or check-in permission, no station credential input: exact own status and PIN still work.
        using (var self = Scope(services, TenantA))
        {
            var management = self.ServiceProvider.GetRequiredService<StationManagementService>();
            Assert.Equal(new(StationManagementState.Applied, new(StationPinState.NotSet, 0)), await management.OwnPinStatusAsync(Primary(accountA, Now), a.Property, a.Staff));
            Assert.Null((await management.OwnPinStatusAsync(Primary(accountA, Now), a.Property, b.Staff)).Status);
            Assert.Null((await management.OwnPinStatusAsync(Primary(accountA, Now), b.Property, a.Staff)).Status);
        }
        Guid ownOp = Guid.NewGuid();
        var initial = await Own(services, a, Primary(accountA, Now), 0, ownOp, "000001");
        Assert.Equal(StationManagementState.Applied, initial.State);
        Assert.Equal(1, kdf.Creates);
        Assert.Equal(accountA, initial.Receipt!.IssuerSessionId);
        Assert.Null(initial.Receipt.StationId);
        Assert.Null(initial.Receipt.BrowserSessionId);
        Assert.Equal(initial, await Own(services, a, Primary(accountA, Now), 0, ownOp, "999999"));
        Assert.Equal(1, kdf.Creates);
        await IntegrityProof(services, a, ownOp, registerOp);
        Assert.NotEqual(StationSessionState.Active, (await services.GetRequiredService<StationRuntimeService>()
            .UnlockAsync(a.Secret, Guid.NewGuid(), a.Staff, 1, "000001")).State);
        Guid recoverySession = await AddSession(services, accountA);
        Assert.Equal(initial, await Own(services, a, Primary(accountA, Now, recoverySession), 0, ownOp, "222222"));
        Assert.Equal(1, kdf.Creates);
        using (var read = Scope(services, TenantA))
        {
            var db = read.ServiceProvider.GetRequiredService<StationsDbContext>();
            Assert.Equal(StationPinVerification.Valid, await read.ServiceProvider.GetRequiredService<IStationPinVerifier>().VerifyAsync("000001", await db.Credentials.SingleAsync()));
            Assert.Empty(await db.CheckInGrants.ToArrayAsync());
            Assert.Empty(await db.SetupGrants.ToArrayAsync());
            Assert.Single(await db.Stations.ToArrayAsync());
            Assert.Single(await db.StaffRegistrations.ToArrayAsync());
            Assert.Null((await db.BrowserSessions.SingleAsync()).ActorSessionId);
            Assert.Equal(accountA, (await db.OperationReceipts.SingleAsync(x => x.Id == ownOp)).IssuerSessionId);
        }
        await SignOut(services, accountA, accountA);
        Assert.Equal(StationManagementState.Denied, (await Own(services, a, Primary(accountA, Now), 1, Guid.NewGuid(), "000002")).State);
        Assert.Equal(1, kdf.Creates);
        await SetAccess(services, TenantA, a.Property, accountA, true);
        using var runtimeScope = Scope(services, TenantA);
        var runtime = runtimeScope.ServiceProvider.GetRequiredService<StationRuntimeService>();
        var active = await runtime.UnlockAsync(a.Secret, Guid.NewGuid(), a.Staff, 1, "000001");
        Assert.Equal(StationSessionState.Active, active.State);
        var extra = await AdditionalPropertyActors(services, a, accountA);
        Assert.Equal(StationSessionState.Active, (await runtime.UnlockAsync(extra.Second.Secret, Guid.NewGuid(), a.Staff, 1, "000001")).State);
        var unrelated = await runtime.UnlockAsync(extra.Other.Secret, Guid.NewGuid(), extra.Other.Staff, 1, "000006");
        Assert.Equal(StationSessionState.Active, unrelated.State);
        string preservedCoordinates = await MetadataFingerprint(services, TenantA);
        string beforeFailure = await Fingerprint(services, TenantA);
        Guid failedOwn = Guid.NewGuid();
        kdf.AfterCreate = () => SignOut(services, accountA, recoverySession);
        Assert.NotEqual(StationManagementState.Applied, (await Own(services, a, Primary(accountA, Now, recoverySession), 1, failedOwn, "000002")).State);
        Assert.Equal(beforeFailure, await Fingerprint(services, TenantA));
        // Correct recovery uses a fresh current primary session and new operation, not a repeated KDF on a completed op.
        Guid currentSession = await AddSession(services, accountA);
        Guid replace = Guid.NewGuid();
        var replaced = await Own(services, a, Primary(accountA, Now, currentSession), 1, replace, "000002");
        Assert.Equal(StationManagementState.Applied, replaced.State);
        Assert.NotEqual(accountA, currentSession);
        Assert.Equal(currentSession, replaced.Receipt!.IssuerSessionId);
        var locked = await runtime.ReadAsync(a.Secret);
        Assert.Equal(StationSessionState.Locked, locked.State);
        Assert.Equal(StationSessionState.Locked, (await runtime.ReadAsync(extra.Second.Secret)).State);
        Assert.Equal(unrelated.Session, (await runtime.ReadAsync(extra.Other.Secret)).Session);
        Assert.Equal(preservedCoordinates, await MetadataFingerprint(services, TenantA));
        using (var inspect = Scope(services, TenantA))
        { Assert.True((await inspect.ServiceProvider.GetRequiredService<StationsDbContext>().SetupGrants.SingleAsync(x => x.Id == extra.Pending)).Revoked); }
        var newActor = await runtime.UnlockAsync(a.Secret, Guid.NewGuid(), a.Staff, locked.Session!.Generation, "000002");
        Assert.Equal(StationSessionState.Active, newActor.State);
        var secondLocked = await runtime.ReadAsync(extra.Second.Secret);
        var secondNewActor = await runtime.UnlockAsync(extra.Second.Secret, Guid.NewGuid(), a.Staff, secondLocked.Session!.Generation, "000002");
        Assert.Equal(StationSessionState.Active, secondNewActor.State);
        using (var later = Scope(services, TenantA))
        {
            later.ServiceProvider.GetRequiredService<StationsDbContext>().SetupGrants.Add(new(Guid.NewGuid(), TenantA, a.Station,
                a.Browser, a.Property, a.Staff, StationActorKind.LinkedStation, 2, Now, Now.AddMinutes(5), new(StationActorKind.LinkedStation, accountA.ToString("D"))));
            await later.ServiceProvider.GetRequiredService<StationsDbContext>().SaveChangesAsync();
        }
        string beforeLateReplay = await Fingerprint(services, TenantA);
        int beforeReplayKdf = kdf.Creates;
        Assert.Equal(replaced, await Own(services, a, Primary(accountA, Now, currentSession), 1, replace, "333333"));
        Assert.Equal(beforeReplayKdf, kdf.Creates);
        Assert.Equal(newActor.Session, (await runtime.ReadAsync(a.Secret)).Session); // Late replay never locks a later actor.
        Assert.Equal(beforeLateReplay, await Fingerprint(services, TenantA)); // Nor cancels a newly issued pending setup.
        Assert.Equal(StationManagementState.StateChanged, (await Own(services, a, Primary(accountA, Now, currentSession), 0, Guid.NewGuid(), "000003")).State);
        Assert.Equal(beforeReplayKdf, kdf.Creates);
        using (var self = Scope(services, TenantA))
        {
            var management = self.ServiceProvider.GetRequiredService<StationManagementService>();
            Assert.Equal(new(StationManagementState.Applied, new(StationPinState.Set, 2)), await management.OwnPinStatusAsync(Primary(accountA, Now, currentSession), a.Property, a.Staff));
            Assert.Equal(replaced, await management.OutcomeAsync(Primary(accountA, Now, currentSession), replace, a.Property));
        }
        await Manager(services, a, accountA, root: true, enabled: true);
        string otherTenant = await Fingerprint(services, TenantB);
        Assert.Equal(StationManagementState.Applied, (await Manage(services, a, Primary(accountA, Now, currentSession), Guid.NewGuid(),
            new(StationOperationKind.UnregisterStaff, a.Property, StaffMemberId: a.Staff, ExpectedVersion: 1))).Response.State);
        Assert.Equal(StationSessionState.Locked, (await runtime.ReadAsync(a.Secret)).State);
        Assert.Equal(secondNewActor.Session, (await runtime.ReadAsync(extra.Second.Secret)).Session);
        Assert.Equal(unrelated.Session, (await runtime.ReadAsync(extra.Other.Secret)).Session);
        Assert.Equal(StationManagementState.Denied, (await Own(services, a, Primary(accountA, Now, currentSession), 2, Guid.NewGuid(), "000003")).State);
        Assert.Equal(otherTenant, await Fingerprint(services, TenantB));
        Assert.Equal(StationManagementState.Applied, (await Manage(services, a, Primary(accountA, Now, currentSession), Guid.NewGuid(),
            new(StationOperationKind.RegisterStaff, a.Property, StaffMemberId: a.Staff, ExpectedVersion: 2))).Response.State);
        using (var read = Scope(services, TenantA))
        { Assert.Equal(1, (await read.ServiceProvider.GetRequiredService<StationsDbContext>().StaffRegistrations.SingleAsync(x => x.PropertyId == a.Property && x.StaffMemberId == a.Staff)).RosterReference); }
        // Lost pairing delivery can resolve non-secret original coordinates, then explicit re-pair replaces the device.
        using (var request = Scope(services, TenantA))
        {
            var found = await request.ServiceProvider.GetRequiredService<StationManagementService>().OutcomeAsync(Primary(accountA, Now, currentSession), registerOp, a.Property);
            Assert.Equal(accountA, found.Receipt!.IssuerSessionId);
        }
        var rePair = await Manage(services, a, Primary(accountA, Now, currentSession), Guid.NewGuid(),
            new(StationOperationKind.Pair, a.Property, StationId: a.Station, ExpectedVersion: 1));
        Assert.Equal(StationManagementState.Applied, rePair.Response.State);
        Assert.Equal(currentSession, rePair.Response.Receipt!.IssuerSessionId);
        Assert.Equal(StationSessionState.Invalid, (await runtime.ReadAsync(a.Secret)).State);
        a = Paired(a, rePair);
        Assert.Equal(new(StationSessionState.HandoffPending), await runtime.ReadAsync(a.Secret));
        await SignOut(services, accountA, currentSession);
        Assert.Equal(StationSessionState.Locked, (await runtime.ReadAsync(a.Secret)).State);
        Guid rollbackSession = await AddSession(services, accountA);
        // Local management may never install even an INITIAL global PIN.
        await Manager(services, b, accountB, root: true, enabled: false);
        await Manager(services, b, accountB, root: false, enabled: true);
        var setupCommand = new StationManagementCommand(StationOperationKind.IssueSetup, b.Property, b.Station, b.Browser, b.Staff);
        Assert.Equal(StationManagementState.Denied, (await Manage(services, b, Primary(accountB, Now), Guid.NewGuid(), setupCommand)).Response.State);
        await Manager(services, b, accountB, root: true, enabled: true);
        var setup = await Manage(services, b, Primary(accountB, Now), Guid.NewGuid(), setupCommand);
        Assert.Equal(StationManagementState.Applied, setup.Response.State);
        await SignOut(services, accountB, accountB);
        Guid redeem = Guid.NewGuid();
        int createsBeforePrivate = kdf.Creates;
        var privateResult = await runtime.RedeemSeededSetupAsync(b.Secret, redeem, setup.Response.Receipt!.SetupGrantId!.Value, "000004");
        Assert.Equal(StationCoreOutcome.Applied, privateResult.Outcome); // Setup issuer authority remains valid after primary sign-out.
        Assert.Equal(StationCoreOutcome.Applied, (await runtime.RedeemSeededSetupAsync(b.Secret, redeem, setup.Response.Receipt.SetupGrantId.Value, "111111")).Outcome);
        Assert.Equal(createsBeforePrivate + 1, kdf.Creates);
        var roster = await runtime.RosterAsync(b.Secret);
        Assert.Equal(StationSessionState.Locked, roster.State);
        Assert.Equal(b.Staff, Assert.Single(roster.Items).StaffMemberId);
        Assert.DoesNotContain(accountB.ToString("D"), JsonSerializer.Serialize(roster), StringComparison.Ordinal);
        var bActive = await runtime.UnlockAsync(b.Secret, Guid.NewGuid(), b.Staff, 1, "000004");
        Assert.Equal(StationAuthorityKind.StationOnly, bActive.Session!.Actor!.AuthorityKind);
        // Job revocation does not revoke identity, and only the final job grant cancels pending setup.
        Guid jobsSession = await AddSession(services, accountB);
        var jobsPrimary = Primary(accountB, Now, jobsSession);
        Assert.Equal(StationManagementState.Applied, (await Manage(services, b, jobsPrimary, Guid.NewGuid(),
            new(StationOperationKind.GrantCheckOut, b.Property, StaffMemberId: b.Staff))).Response.State);
        long checkInRevision = 1, checkOutRevision = 1;
        foreach (bool checkOutFirst in new[] { false, true })
        {
            var pending = await Manage(services, b, jobsPrimary, Guid.NewGuid(), setupCommand with { ExpectedVersion = 1 });
            Assert.Equal(StationManagementState.Applied, pending.Response.State);
            var first = new StationManagementCommand(checkOutFirst ? StationOperationKind.RevokeCheckOutGrant : StationOperationKind.RevokeGrant,
                b.Property, StaffMemberId: b.Staff, ExpectedVersion: checkOutFirst ? checkOutRevision : checkInRevision);
            var last = new StationManagementCommand(checkOutFirst ? StationOperationKind.RevokeGrant : StationOperationKind.RevokeCheckOutGrant,
                b.Property, StaffMemberId: b.Staff, ExpectedVersion: checkOutFirst ? checkInRevision : checkOutRevision);
            Assert.Equal(StationManagementState.Applied, (await Manage(services, b, jobsPrimary, Guid.NewGuid(), first)).Response.State);
            using (var inspect = Scope(services, b.Tenant))
            {
                var db = inspect.ServiceProvider.GetRequiredService<StationsDbContext>();
                Assert.False((await db.SetupGrants.SingleAsync(x => x.Id == pending.Response.Receipt!.SetupGrantId)).Revoked);
            }
            Assert.Equal(StationManagementState.Applied, (await Manage(services, b, jobsPrimary, Guid.NewGuid(), last)).Response.State);
            using (var inspect = Scope(services, b.Tenant))
            {
                var db = inspect.ServiceProvider.GetRequiredService<StationsDbContext>();
                Assert.True((await db.SetupGrants.SingleAsync(x => x.Id == pending.Response.Receipt!.SetupGrantId)).Revoked);
            }
            Assert.Equal(bActive.Session.Actor, (await runtime.ReadAsync(b.Secret)).Session!.Actor);
            checkInRevision++;
            checkOutRevision++;
            if (!checkOutFirst)
            {
                Assert.Equal(StationManagementState.Applied, (await Manage(services, b, jobsPrimary, Guid.NewGuid(),
                    new(StationOperationKind.GrantCheckIn, b.Property, StaffMemberId: b.Staff, ExpectedVersion: checkInRevision))).Response.State);
                Assert.Equal(StationManagementState.Applied, (await Manage(services, b, jobsPrimary, Guid.NewGuid(),
                    new(StationOperationKind.GrantCheckOut, b.Property, StaffMemberId: b.Staff, ExpectedVersion: checkOutRevision))).Response.State);
                checkInRevision++;
                checkOutRevision++;
            }
        }
        Assert.Equal(StationManagementState.Applied, (await Manage(services, b, jobsPrimary, Guid.NewGuid(),
            new(StationOperationKind.GrantCheckOut, b.Property, StaffMemberId: b.Staff, ExpectedVersion: checkOutRevision))).Response.State);
        var race = await Task.WhenAll(
            Manage(services, b, jobsPrimary, Guid.NewGuid(), setupCommand with { ExpectedVersion = 1 }),
            Manage(services, b, jobsPrimary, Guid.NewGuid(), new(StationOperationKind.RevokeCheckOutGrant,
                b.Property, StaffMemberId: b.Staff, ExpectedVersion: checkOutRevision + 1)));
        Assert.Equal(StationManagementState.Applied, race[1].Response.State);
        using (var inspect = Scope(services, b.Tenant))
        {
            var db = inspect.ServiceProvider.GetRequiredService<StationsDbContext>();
            Assert.Empty(await db.SetupGrants.Where(x => x.StaffMemberId == b.Staff && !x.Revoked && x.ConsumedAtUtc == null).ToArrayAsync());
        }
        Assert.Equal(bActive.Session.Actor, (await runtime.ReadAsync(b.Secret)).Session!.Actor);
        await SignOut(services, accountB, jobsSession);

        // Controlled transaction failure must roll back own credential, actor invalidation, setup cancellation and receipt.
        await RollbackProof(services, a, accountA, rollbackSession);
    }

    private static async Task<(Device Second, Device Other, Guid Pending)> AdditionalPropertyActors(ServiceProvider services, Device original, Guid account)
    {
        using var scope = Scope(services, original.Tenant);
        var sp = scope.ServiceProvider;
        Guid propertyId = Guid.NewGuid();
        var property = Property.Create(propertyId, original.Tenant, "Second synthetic station property", "second", "America/New_York", Guid.NewGuid(), Now).Value;
        await sp.GetRequiredService<IPropertyRepository>().AddAsync(property, CancellationToken.None);
        await sp.GetRequiredService<IPropertyTimeZoneRevisionWriter>().AppendAsync(new(Guid.NewGuid(), original.Tenant,
            propertyId, propertyId, PropertyTimeZoneChangeKind.Created, property.TimeZoneId.Value, null, property.TimeZoneId.Value,
            TimeZoneCatalog.Default.CatalogVersion, 0, property.Version, "user:fixture", Now), CancellationToken.None);
        await sp.GetRequiredService<PropertiesDbContext>().SaveChangesAsync();
        var staffDb = sp.GetRequiredService<StaffDbContext>();
        var target = await staffDb.StaffMembers.Include(x => x.Assignments).SingleAsync(x => x.Id == original.Staff);
        Assert.True(target.AssignProperty(Guid.NewGuid(), propertyId, null, false, new(2026, 9, 20), target.Version, "user:fixture", Guid.NewGuid(), Now).IsSuccess);
        Guid otherId = Guid.NewGuid();
        var other = StaffMember.Create(otherId, original.Tenant, "Unrelated synthetic operator", null, null, null, null, null, null, null, "user:fixture", Guid.NewGuid(), Now).Value;
        Assert.True(other.AssignProperty(Guid.NewGuid(), propertyId, null, false, new(2026, 9, 20), other.Version, "user:fixture", Guid.NewGuid(), Now).IsSuccess);
        await sp.GetRequiredService<IStaffMemberRepository>().AddAsync(other, CancellationToken.None);
        await staffDb.SaveChangesAsync();
        await SetAccess(services, original.Tenant, propertyId, account, true);
        var db = sp.GetRequiredService<StationsDbContext>();
        Device second = Pair(original.Staff, 1), otherDevice = Pair(otherId, 2);
        db.Credentials.Add(new(original.Tenant, otherId, Assert.IsType<StationPinMaterial>(await sp.GetRequiredService<IStationPinVerifier>().CreateAsync("000006")), Now, new(StationActorKind.StationOnly, null)));
        db.CheckInGrants.Add(new(original.Tenant, propertyId, otherId));
        Guid pending = Guid.NewGuid();
        db.SetupGrants.Add(new(pending, original.Tenant, original.Station, original.Browser, original.Property, original.Staff,
            StationActorKind.LinkedStation, 1, Now, Now.AddMinutes(5), new(StationActorKind.LinkedStation, account.ToString("D"))));
        await db.SaveChangesAsync();
        return (second, otherDevice, pending);
        Device Pair(Guid staffId, long reference)
        {
            Guid station = Guid.NewGuid(), browser = Guid.NewGuid();
            string secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            db.Stations.Add(new(station, original.Tenant, propertyId, "Synthetic additional desk"));
            db.BrowserSessions.Add(new(browser, original.Tenant, station, propertyId, StationCredentialEncoding.Digest(secret)!, Now, Now.AddDays(1), 1));
            db.OperationReceipts.Add(new(original.Tenant, Guid.NewGuid(), StationMutationKind.Register, new string('a', 64),
                new(StationMutationOutcome.Applied, Management: new(station, browser, propertyId, null, null, 1)),
                Now, account.ToString("D"), StationIssuerKind.Manager, account));
            db.StaffRegistrations.Add(new(original.Tenant, propertyId, staffId, reference));
            return new(original.Tenant, propertyId, staffId, station, browser, secret);
        }
    }

    private static async Task RollbackProof(ServiceProvider services, Device device, Guid account, Guid session)
    {
        using var scope = Scope(services, device.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<StationsDbContext>();
        var runtime = scope.ServiceProvider.GetRequiredService<StationRuntimeService>();
        Assert.Equal(StationSessionState.Active, (await runtime.UnlockAsync(device.Secret, Guid.NewGuid(), device.Staff, 1, "000002")).State);
        // Historical linked setup cannot redeem under A2, but cancellation is still part of own-PIN replacement.
        db.SetupGrants.Add(new(Guid.NewGuid(), device.Tenant, device.Station, device.Browser, device.Property,
            device.Staff, StationActorKind.LinkedStation, 2, Now, Now.AddMinutes(5), new(StationActorKind.LinkedStation, account.ToString("D"))));
        await db.SaveChangesAsync();
        string before = await Fingerprint(services, device.Tenant);
        await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION stations.fixture_reject_receipt() RETURNS trigger LANGUAGE plpgsql AS 'BEGIN RAISE EXCEPTION ''synthetic rollback''; END'; CREATE TRIGGER fixture_reject_receipt BEFORE INSERT ON stations.operation_receipts FOR EACH ROW EXECUTE FUNCTION stations.fixture_reject_receipt()");
        try
        {
            var denied = await Own(services, device, Primary(account, Now, session), 2, Guid.NewGuid(), "000005");
            Assert.Equal(StationManagementState.Unavailable, denied.State);
            Assert.Equal(before, await Fingerprint(services, device.Tenant));
        }
        finally { await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fixture_reject_receipt ON stations.operation_receipts; DROP FUNCTION stations.fixture_reject_receipt()"); }
    }

    private static async Task IntegrityProof(ServiceProvider services, Device device, Guid ownOperation, Guid pairingOperation)
    {
        using var scope = Scope(services, device.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<StationsDbContext>();
        string before = await Fingerprint(services, device.Tenant);
        foreach (string assignment in new[] { "\"IssuerSessionId\"=NULL", "\"PropertyId\"=NULL", "\"StaffMemberId\"=NULL", "\"ResourceVersion\"=NULL", "\"ResourceVersion\"=0", "\"IssuerKind\"=1" })
        {
            // Assignment is one of the six literal corruption probes above, never caller input.
            string probeSql = $"UPDATE stations.operation_receipts SET {assignment} WHERE \"ScopeId\"=@tenant AND \"Id\"=@id";
            var failure = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(probeSql,
                new NpgsqlParameter("tenant", device.Tenant), new NpgsqlParameter("id", ownOperation)));
            Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
            Assert.Equal("CK_receipt_own_pin", failure.ConstraintName);
        }
        await Check(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE stations.operation_receipts SET \"IssuerSubjectId\"=NULL WHERE \"ScopeId\"={device.Tenant} AND \"Id\"={pairingOperation}"), "CK_receipt_issuer");
        await Check(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE stations.operation_receipts SET \"IssuerSessionId\"={Guid.Empty} WHERE \"ScopeId\"={device.Tenant} AND \"Id\"={pairingOperation}"), "CK_receipt_session");
        await Check(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE stations.staff_registrations SET \"Version\"=0 WHERE \"ScopeId\"={device.Tenant}"), "CK_registration_version");
        await Check(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE stations.staff_registrations SET \"RosterReference\"=0 WHERE \"ScopeId\"={device.Tenant}"), "CK_registration_version");
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO stations.staff_registrations (\"ScopeId\",\"PropertyId\",\"StaffMemberId\",\"RosterReference\",\"Version\",\"Active\") VALUES ({device.Tenant},{device.Property},{Guid.NewGuid()},1,1,true)"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        Assert.Equal(before, await Fingerprint(services, device.Tenant));
        static async Task Check(Func<Task<int>> write, string name)
        {
            var failure = await Assert.ThrowsAsync<PostgresException>(write);
            Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
            Assert.Equal(name, failure.ConstraintName);
        }
    }

    [Fact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Registration_capacity_is_serialized_and_roster_searches_all_200_with_stable_collision_references()
    {
        Assert.Equal("true", Environment.GetEnvironmentVariable("GMA_REQUIRE_DOCKER_TESTS"));
        await using var postgres = Container();
        await postgres.StartAsync();
        var time = new TestClock();
        var labels = new LabelControl();
        await using var services = Services(postgres.GetConnectionString(), time, new KdfControl(), labels: labels);
        await Migrate(services);
        Guid account = Guid.NewGuid();
        await SeedAccount(services, account);
        Device device = await Seed(services, TenantA, account, false, stationData: false);
        await Manager(services, device, account, root: true, enabled: true);
        Guid pairingSession = await AddSession(services, account);
        device = Paired(device, await Manage(services, device, Primary(account, Now, pairingSession), Guid.NewGuid(),
            new(StationOperationKind.Register, device.Property, Label: "Capacity desk")));
        await SignOut(services, account, pairingSession);
        var runtime = services.GetRequiredService<StationRuntimeService>();
        Assert.Empty((await runtime.RosterAsync(device.Secret)).Items);
        Guid[] ids = Enumerable.Range(0, 201).Select(_ => Guid.NewGuid()).ToArray();
        // Bulk existing operator history is synthetic. Both competing final registrations use actual owner admission/store.
        using (var seed = Scope(services, TenantA))
        {
            var staffDb = seed.ServiceProvider.GetRequiredService<StaffDbContext>();
            var stationDb = seed.ServiceProvider.GetRequiredService<StationsDbContext>();
            var material = Assert.IsType<StationPinMaterial>(await seed.ServiceProvider.GetRequiredService<IStationPinVerifier>().CreateAsync("000001"));
            for (int i = 0; i < ids.Length; i++)
            {
                var staff = StaffMember.Create(ids[i], TenantA, i == 198 ? "Zulu target" : "Same name", null, null, null,
                    null, null, null, null, "user:fixture", Guid.NewGuid(), Now).Value;
                Assert.True(staff.AssignProperty(Guid.NewGuid(), device.Property, null, false, new(2026, 9, 20),
                    staff.Version, "user:fixture", Guid.NewGuid(), Now).IsSuccess);
                await seed.ServiceProvider.GetRequiredService<IStaffMemberRepository>().AddAsync(staff, CancellationToken.None);
                var historicalStation = new Station(Guid.NewGuid(), TenantA, device.Property, $"History {i:000}");
                if (i % 2 == 0)
                { historicalStation.Revoke(); }
                stationDb.Stations.Add(historicalStation);
                stationDb.Credentials.Add(new(TenantA, ids[i], material, Now, new(StationActorKind.StationOnly, null)));
                stationDb.CheckInGrants.Add(new(TenantA, device.Property, ids[i]));
                if (i < 199)
                { stationDb.StaffRegistrations.Add(new(TenantA, device.Property, ids[i], i + 1)); }
            }
            await staffDb.SaveChangesAsync();
            await stationDb.SaveChangesAsync();
        }
        var competing = await Task.WhenAll(ids.Skip(199).Select(id => Manage(services, device, Primary(account, Now), Guid.NewGuid(),
            new(StationOperationKind.RegisterStaff, device.Property, StaffMemberId: id))));
        using (var request = Scope(services, TenantA))
        {
            var management = request.ServiceProvider.GetRequiredService<StationManagementService>();
            var stations = new List<StationListItem>();
            for (int page = 1; page <= 5; page++)
            {
                var result = await management.ListAsync(Primary(account, Now), device.Property, page, 50);
                Assert.Equal(StationManagementState.Applied, result.State);
                Assert.Equal(page < 5, result.HasMore);
                stations.AddRange(result.Items);
            }
            Assert.Equal(202, stations.Count);
            Assert.Equal(202, stations.Select(x => x.StationId).Distinct().Count());
            Assert.Equal(101, stations.Count(x => x.Revoked));
            Assert.Contains(stations, x => x.StationId == device.Station && !x.Revoked);
            Assert.Empty((await management.ListAsync(Primary(account, Now), device.Property, int.MaxValue, 50)).Items);
            Assert.Empty((await management.ListAsync(Primary(account, Now), device.Property, 1, 51)).Items);
        }
        Assert.Single(competing, x => x.Response.State == StationManagementState.Applied);
        Assert.Single(competing, x => x.Response.State == StationManagementState.CapacityReached);
        var all = new List<StationRosterItem>();
        for (int page = 1; page <= 4; page++)
        {
            var result = await runtime.RosterAsync(device.Secret, page: page, pageSize: 50);
            Assert.Equal(StationSessionState.Locked, result.State);
            Assert.Equal(50, result.Items.Count);
            Assert.Equal(page < 4, result.HasMore);
            all.AddRange(result.Items);
        }
        Assert.Equal(200, all.Select(x => x.StaffMemberId).Distinct().Count());
        Assert.Equal(200, all.Select(x => x.RosterReference).Distinct().Count());
        Assert.Equal(ids[198], all[^1].StaffMemberId);
        Assert.Equal(ids[198], Assert.Single((await runtime.RosterAsync(device.Secret, "zUlU", pageSize: 1)).Items).StaffMemberId);
        Assert.Empty((await runtime.RosterAsync(device.Secret, "missing")).Items);
        Assert.Empty((await runtime.RosterAsync(device.Secret, page: 5, pageSize: 50)).Items);
        Assert.Equal(StationSessionState.StateChanged, (await runtime.RosterAsync(device.Secret, new string('x', 101))).State);
        Assert.Empty((await runtime.RosterAsync(device.Secret, pageSize: 51)).Items);
        Assert.Equal(199, all.Count(x => x.DisplayName == "Same name"));
        Guid absent;
        using (var check = Scope(services, TenantA))
        {
            var db = check.ServiceProvider.GetRequiredService<StationsDbContext>();
            Assert.Equal(200, await db.StaffRegistrations.CountAsync(x => x.Active));
            absent = Assert.Single(ids.Except(await db.StaffRegistrations.Select(x => x.StaffMemberId).ToArrayAsync()));
        }
        Assert.Equal(StationManagementState.Applied, (await Manage(services, device, Primary(account, Now), Guid.NewGuid(),
            new(StationOperationKind.UnregisterStaff, device.Property, StaffMemberId: ids[0], ExpectedVersion: 1))).Response.State);
        Assert.Equal(StationManagementState.Applied, (await Manage(services, device, Primary(account, Now), Guid.NewGuid(),
            new(StationOperationKind.RegisterStaff, device.Property, StaffMemberId: absent))).Response.State);
        Assert.Equal(StationManagementState.CapacityReached, (await Manage(services, device, Primary(account, Now), Guid.NewGuid(),
            new(StationOperationKind.RegisterStaff, device.Property, StaffMemberId: ids[0], ExpectedVersion: 2))).Response.State);
        Assert.Equal(StationManagementState.Applied, (await Manage(services, device, Primary(account, Now), Guid.NewGuid(),
            new(StationOperationKind.UnregisterStaff, device.Property, StaffMemberId: absent, ExpectedVersion: 1))).Response.State);
        Assert.Equal(StationManagementState.Applied, (await Manage(services, device, Primary(account, Now), Guid.NewGuid(),
            new(StationOperationKind.RegisterStaff, device.Property, StaffMemberId: ids[0], ExpectedVersion: 2))).Response.State);
        using var final = Scope(services, TenantA);
        var registrations = await final.ServiceProvider.GetRequiredService<StationsDbContext>().StaffRegistrations.ToArrayAsync();
        Assert.Equal(201, registrations.Length);
        Assert.Equal(200, registrations.Count(x => x.Active));
        Assert.Equal(1, registrations.Single(x => x.StaffMemberId == ids[0]).RosterReference);
        Assert.Equal(3, registrations.Single(x => x.StaffMemberId == ids[0]).Version);
        // A previously valid cookie cannot receive names after owner work overlaps re-pair/revocation.
        StationPairingHandoff? replacement = null;
        Guid replacementSession = await AddSession(services, account);
        labels.AfterRead = async () => replacement = await Manage(services, device, Primary(account, Now, replacementSession), Guid.NewGuid(),
            new(StationOperationKind.Pair, device.Property, StationId: device.Station, ExpectedVersion: 1));
        var staleRoster = await runtime.RosterAsync(device.Secret);
        Assert.Equal(StationSessionState.Invalid, staleRoster.State);
        Assert.Empty(staleRoster.Items);
        device = Paired(device, Assert.IsType<StationPairingHandoff>(replacement));
        Assert.Equal(new(StationSessionState.HandoffPending), await runtime.ReadAsync(device.Secret));
        await SignOut(services, account, replacementSession);
        Assert.Equal(25, (await runtime.RosterAsync(device.Secret)).Items.Count);
        labels.AfterRead = () => throw new InvalidOperationException("Synthetic label owner unavailable");
        var unavailable = await runtime.RosterAsync(device.Secret);
        Assert.Equal(StationSessionState.Unavailable, unavailable.State);
        Assert.Empty(unavailable.Items);
        labels.AfterRead = async () => Assert.Equal(StationManagementState.Applied,
            (await Manage(services, device, Primary(account, Now), Guid.NewGuid(),
                new(StationOperationKind.RevokeStation, device.Property, StationId: device.Station, ExpectedVersion: 2))).Response.State);
        var revokedRoster = await runtime.RosterAsync(device.Secret);
        Assert.Equal(StationSessionState.Invalid, revokedRoster.State);
        Assert.Empty(revokedRoster.Items);
    }

    [Fact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Own_PIN_reobserves_registration_binding_session_owner_and_local_date_after_KDF_without_consuming_failed_intent()
    {
        Assert.Equal("true", Environment.GetEnvironmentVariable("GMA_REQUIRE_DOCKER_TESTS"));
        await using var postgres = Container();
        await postgres.StartAsync();
        var time = new TestClock();
        var kdf = new KdfControl();
        var reads = new ReadControl();
        await using var services = Services(postgres.GetConnectionString(), time, kdf, reads);
        await Migrate(services);
        Guid account = Guid.NewGuid();
        await SeedAccount(services, account);
        Device device = await Seed(services, TenantA, account, true, stationData: false);
        await Manager(services, device, account, root: true, enabled: true);
        Assert.Equal(StationManagementState.Applied, (await Manage(services, device, Primary(account, Now), Guid.NewGuid(),
            new(StationOperationKind.RegisterStaff, device.Property, StaffMemberId: device.Staff))).Response.State);
        await Manager(services, device, account, root: true, enabled: false);
        await SetAccess(services, TenantA, device.Property, account, false);
        long revision = 0;
        foreach (string scenario in new[] { "registration", "unlink", "session-expiry", "owner-unavailable", "cancelled", "local-date" })
        {
            Guid session = scenario == "session-expiry" ? await AddSession(services, account, time.UtcNow.AddSeconds(1)) : account;
            if (scenario == "local-date")
            { time.UtcNow = Now.AddHours(15).AddMinutes(59); } // 23:59 in the actual America/New_York property.
            ClaimsPrincipal principal = Primary(account, time.UtcNow, session);
            Guid operation = Guid.NewGuid();
            using var cancellation = new CancellationTokenSource();
            string? afterInjectedOwnerChange = null;
            int beforeCreates = kdf.Creates;
            kdf.AfterCreate = async () =>
            {
                switch (scenario)
                {
                    case "registration":
                        await Registered(false);
                        break;
                    case "unlink":
                        await ChangeLink(services, device, null, time.UtcNow);
                        break;
                    case "session-expiry":
                        time.UtcNow = time.UtcNow.AddSeconds(1);
                        break;
                    case "owner-unavailable":
                        reads.Unavailable = true;
                        break;
                    case "cancelled":
                        cancellation.Cancel();
                        break;
                    case "local-date":
                        time.UtcNow = time.UtcNow.AddMinutes(2);
                        break;
                    default:
                        throw new InvalidOperationException();
                }
                afterInjectedOwnerChange = await Fingerprint(services, TenantA);
            };
            if (scenario == "cancelled")
            {
                using var request = Scope(services, TenantA);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.ServiceProvider.GetRequiredService<StationManagementService>()
                    .SetOwnPinAsync(principal, device.Property, device.Staff, revision, operation, "000001", cancellation.Token));
            }
            else
            {
                var result = await Own(services, device, principal, revision, operation, "000001");
                Assert.Equal(scenario == "owner-unavailable" ? StationManagementState.Unavailable :
                    scenario is "local-date" or "unlink" ? StationManagementState.StateChanged : StationManagementState.Denied, result.State);
            }
            Assert.Equal(beforeCreates + 1, kdf.Creates);
            Assert.NotNull(afterInjectedOwnerChange);
            Assert.Equal(afterInjectedOwnerChange, await Fingerprint(services, TenantA));
            using (var check = Scope(services, TenantA))
            { Assert.False(await check.ServiceProvider.GetRequiredService<StationsDbContext>().OperationReceipts.AnyAsync(x => x.Id == operation)); }
            if (scenario == "registration")
            { await Registered(true); }
            if (scenario == "unlink")
            { await ChangeLink(services, device, account, time.UtcNow); }
            reads.Unavailable = false;
            // Explicit new intent after restoring current authority, not automatic retry of a failed/uncertain PIN.
            var recovered = await Own(services, device, Primary(account, time.UtcNow), revision, Guid.NewGuid(), "000002");
            Assert.Equal(StationManagementState.Applied, recovered.State);
            Assert.Equal(++revision, recovered.Receipt!.Version);
        }
        var concurrent = await Task.WhenAll(
            Own(services, device, Primary(account, time.UtcNow), revision, Guid.NewGuid(), "000003"),
            Own(services, device, Primary(account, time.UtcNow), revision, Guid.NewGuid(), "000004"));
        Assert.Single(concurrent, x => x.State == StationManagementState.Applied);
        Assert.Single(concurrent, x => x.State == StationManagementState.StateChanged);
        using (var check = Scope(services, TenantA))
        {
            var db = check.ServiceProvider.GetRequiredService<StationsDbContext>();
            Assert.Equal(revision + 1, (await db.Credentials.SingleAsync()).Revision);
            Assert.Equal(revision + 1, await db.OperationReceipts.LongCountAsync(x => x.Kind == StationMutationKind.OwnPin && x.Outcome == StationMutationOutcome.Applied));
        }
        async Task Registered(bool active)
        {
            using var scope = Scope(services, TenantA);
            var db = scope.ServiceProvider.GetRequiredService<StationsDbContext>();
            (await db.StaffRegistrations.SingleAsync()).SetActive(active);
            await db.SaveChangesAsync();
        }
    }

    private static PostgreSqlContainer Container() => new PostgreSqlBuilder("postgres:16-alpine")
        .WithName($"bunkfy-stations-p2b-{Guid.NewGuid():N}").WithLabel("bunkfy.test.grant", "STAFF-PIN-FIRST-JOB-P2B")
        .WithDatabase("station_management").Build();
    private static ServiceProvider Services(string connection, TestClock time, KdfControl kdf, ReadControl? reads = null,
        LabelControl? labels = null) => BuildServices(connection, time, reads ?? new ReadControl(), registrations =>
    {
        var descriptor = registrations.Single(x => x.ServiceType == typeof(IStationPinVerifier));
        registrations.Remove(descriptor);
        registrations.AddSingleton<IStationPinVerifier>(sp => new ControlledVerifier(
            (IStationPinVerifier)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!), kdf));
        if (labels is not null)
        {
            Type ownerType = registrations.Last(x => x.ServiceType == typeof(IStaffStationLabelReader)).ImplementationType!;
            registrations.AddScoped<IStaffStationLabelReader>(sp => new ControlledLabels(
                (IStaffStationLabelReader)ActivatorUtilities.CreateInstance(sp, ownerType), labels));
        }
    });
    private static async Task Migrate(ServiceProvider services)
    {
        using var migration = Scope(services, TenantA);
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
        Assert.Empty(await stations.Stations.ToArrayAsync());
    }
    private static Device Paired(Device device, StationPairingHandoff result)
    {
        Assert.Equal(StationManagementState.Applied, result.Response.State);
        Assert.NotNull(result.Credential);
        return device with { Station = result.Response.Receipt!.StationId!.Value, Browser = result.Response.Receipt.BrowserSessionId!.Value, Secret = result.Credential };
    }
    private static async Task<StationPairingHandoff> Manage(ServiceProvider services, Device device, ClaimsPrincipal principal, Guid op, StationManagementCommand request)
    { using var scope = Scope(services, device.Tenant); return await scope.ServiceProvider.GetRequiredService<StationManagementService>().ManageAsync(principal, op, request); }
    private static async Task<StationManagementResponse> Own(ServiceProvider services, Device device, ClaimsPrincipal principal, long revision, Guid op, string pin)
    { using var scope = Scope(services, device.Tenant); return await scope.ServiceProvider.GetRequiredService<StationManagementService>().SetOwnPinAsync(principal, device.Property, device.Staff, revision, op, pin); }
    private static async Task Manager(ServiceProvider services, Device device, Guid account, bool root, bool enabled)
    {
        using var scope = Scope(services, device.Tenant);
        var roles = scope.ServiceProvider.GetRequiredService<IAccessControlRoleProvisioner>();
        await roles.EnsureRoleAsync(new(ManageRole, [StationsPermissionCodes.Manage]));
        var target = root ? WorkspaceAccessScopes.Create(device.Tenant) : WorkspaceAccessScopes.CreateProperty(device.Tenant, device.Property);
        if (enabled)
        { await roles.EnsureAssignmentAsync(AccessSubject.User(account.ToString("D")), ManageRole, target); }
        else
        { await roles.RemoveAssignmentAsync(AccessSubject.User(account.ToString("D")), ManageRole, target); }
    }
    private static async Task<Guid> AddSession(ServiceProvider services, Guid account, DateTimeOffset? expires = null)
    {
        using var scope = Scope(services, TenantA);
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var member = await db.Members.Include(x => x.Sessions).SingleAsync(x => x.Id == new MemberId(account));
        Guid id = Guid.NewGuid();
        Assert.True(member.StartSession(new MemberSessionId(id), "synthetic-refresh-hash", expires ?? Now.AddDays(1), Now).IsSuccess);
        await db.SaveChangesAsync();
        return id;
    }
    private static async Task SignOut(ServiceProvider services, Guid account, Guid session)
    {
        using var scope = Scope(services, TenantA);
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var member = await db.Members.Include(x => x.Sessions).SingleAsync(x => x.Id == new MemberId(account));
        Assert.True(member.SignOutSession(new MemberSessionId(session), Now).IsSuccess);
        await db.SaveChangesAsync();
    }
    private static async Task<string> Fingerprint(ServiceProvider services, string tenant)
        => await FingerprintTables(services, tenant, Tables);
    private static Task<string> MetadataFingerprint(ServiceProvider services, string tenant) =>
        FingerprintTables(services, tenant, ["stations", "staff_registrations", "staff_check_in_grants"]);
    private static async Task<string> FingerprintTables(ServiceProvider services, string tenant, IReadOnlyList<string> tables)
    {
        using var scope = Scope(services, tenant);
        var db = scope.ServiceProvider.GetRequiredService<StationsDbContext>();
        var result = new StringBuilder();
        foreach (string table in tables)
        {
            string sql = $"SELECT to_jsonb(t)::text AS \"Value\" FROM stations.{table} t WHERE \"ScopeId\"=@tenant ORDER BY to_jsonb(t)::text";
            foreach (string row in await db.Database.SqlQueryRaw<string>(sql, new NpgsqlParameter("tenant", tenant)).ToArrayAsync())
            { result.Append(row); }
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(result.ToString())));
    }
    private sealed class KdfControl
    {
        public int Creates { get; set; }
        public Func<Task>? AfterCreate { get; set; }
    }
    private sealed class LabelControl { public Func<Task>? AfterRead { get; set; } }
    private sealed class ControlledLabels(IStaffStationLabelReader owner, LabelControl control) : IStaffStationLabelReader
    {
        public async Task<IReadOnlyList<StaffStationLabel>> ResolveAsync(string scopeId, Guid propertyId,
            IReadOnlyCollection<Guid> staffIds, DateOnly propertyLocalDate, CancellationToken cancellationToken = default)
        {
            var labels = await owner.ResolveAsync(scopeId, propertyId, staffIds, propertyLocalDate, cancellationToken);
            if (control.AfterRead is { } action)
            { control.AfterRead = null; await action(); }
            return labels;
        }
    }
    private sealed class ControlledVerifier(IStationPinVerifier owner, KdfControl control) : IStationPinVerifier
    {
        public async Task<StationPinMaterial?> CreateAsync(string pin, CancellationToken cancellationToken = default)
        {
            control.Creates++;
            var material = await owner.CreateAsync(pin, cancellationToken);
            if (control.AfterCreate is { } after)
            { control.AfterCreate = null; await after(); }
            return material;
        }
        public Task<StationPinVerification> VerifyAsync(string pin, StationStaffCredential credential, CancellationToken cancellationToken = default) => owner.VerifyAsync(pin, credential, cancellationToken);
    }
}
