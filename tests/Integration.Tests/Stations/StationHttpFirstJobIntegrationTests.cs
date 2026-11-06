namespace Integration.Tests;

using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using BunkFy.Modules.Inventory.Persistence;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Contracts.Stations;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Domain.Events;
using BunkFy.Modules.Reservations.Persistence;
using BunkFy.Modules.Stations.Api;
using BunkFy.Modules.Stations.Contracts;
using BunkFy.Modules.Stations.Persistence;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.AccessControl;
using Gma.Framework.Messaging;
using Gma.Framework.Security;
using Gma.Framework.Tenancy;
using Gma.Modules.AccessControl.Contracts;
using Gma.Modules.Auth.Contracts;
using Integration.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;
using Xunit;
using static StationCredentialLifecyclePostgreSqlIntegrationTests;

/// <summary>
/// Real Host.Api bearer/session, PostgreSQL owners and station-cookie flow. The test issuer supplies
/// an MFA assertion; it does not exercise or prove a real MFA provider or browser TLS. Confirmed reservation
/// and inventory rows are fixture data; the check-in and historical outcome use actual HTTP owners.
/// </summary>
public sealed class StationHttpFirstJobIntegrationTests
{
    private const string Tenant = "aa000000-0000-0000-0000-000000000001";
    private const string Origin = "https://localhost";
    private const string AuthScope = "global";
    private const string Pin = "000001";
    private const string ManagementRole = "stations-http-fixture";

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Primary_HTTP_pairing_requires_exact_session_signout_before_cookie_PIN_and_current_arrivals()
    {
        await using var nats = AuthTestContainers.CreateNatsContainer();
        await nats.StartAsync();
        await using var postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithName($"bunkfy-stations-p4-http-{Guid.NewGuid():N}")
            .WithLabel("bunkfy.test.grant", "STAFF-PIN-FIRST-JOB-P4")
            .WithDatabase("station_http_first_job").Build();
        await postgres.StartAsync();
        var clock = new TestClock();
        var reservations = new ReservationCalls();
        await using AuthTestApplication api = new("PostgreSql", postgres.GetConnectionString(),
            AuthTestContainers.GetNatsConnectionString(nats), systemClock: clock,
            configurationOverrides: Configuration(), configureServices: services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(clock);
                ObserveRealReservations(services, reservations);
            });
        await api.MigrateReservationsAuthorizationDatabaseAsync();
        await api.MigrateStaffAuthorizationDatabaseAsync();
        using (IServiceScope migration = api.Services.CreateScope())
        { await migration.ServiceProvider.GetRequiredService<StationsDbContext>().Database.MigrateAsync(); }

        // The existing P2 helper creates real owner rows, without station, credential or registration rows.
        await using ServiceProvider owners = BuildServices(postgres.GetConnectionString(), clock, new ReadControl());
        Guid account = Guid.NewGuid();
        await SeedAccount(owners, account);
        Device device = await Seed(owners, Tenant, account, linked: true, stationData: false);
        await SetManagementPermissionAsync(owners, account, enabled: true);
        await SeedEmptyArrivalsPropertyAsync(api, device, clock.UtcNow);
        using HttpClient client = api.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(Origin),
            HandleCookies = false,
            AllowAutoRedirect = false
        });
        string primaryToken = Token(account, clock.UtcNow);
        string managementPath = $"/api/station-management/properties/{device.Property:D}";
        string setupPath = $"/api/station-setup/properties/{device.Property:D}/pin";

        using (HttpResponseMessage registered = await SendAsync(client, HttpMethod.Post, managementPath + "/operations",
            new StationManagementRequest(Guid.NewGuid(), StationOperationKind.RegisterStaff, StaffMemberId: device.Staff),
            primaryToken, tenant: Tenant))
        {
            var result = await ReadAsync<StationManagementHttpResponse>(registered);
            Assert.Equal(StationManagementState.Applied, result.State);
            Assert.Equal(device.Staff, Assert.IsType<StationManagementHttpReceipt>(result.Receipt).StaffMemberId);
        }

        // Exact-own PIN does not require stations.manage or reservations.check-in.
        await SetManagementPermissionAsync(owners, account, enabled: false);
        await SetAccess(owners, Tenant, device.Property, account, enabled: false);
        using (HttpResponseMessage status = await SendAsync(client, HttpMethod.Get, setupPath, bearer: primaryToken, tenant: Tenant))
        {
            var result = await ReadAsync<StationOwnPinStatusResponse>(status);
            Assert.Equal(StationPinState.NotSet, Assert.IsType<StationOwnPinStatus>(result.Status).Pin);
        }
        using (HttpResponseMessage own = await SendAsync(client, HttpMethod.Put, setupPath,
            new StationOwnPinRequest(Guid.NewGuid(), 0, Pin), primaryToken, tenant: Tenant))
        {
            var result = await ReadAsync<StationManagementHttpResponse>(own);
            Assert.Equal(StationManagementState.Applied, result.State);
            Assert.Equal(StationOperationKind.OwnPin, Assert.IsType<StationManagementHttpReceipt>(result.Receipt).Kind);
            Assert.Null(result.Receipt!.OriginalIssuerSessionId);
            Assert.False(own.Headers.Contains("Set-Cookie"));
        }
        await SetManagementPermissionAsync(owners, account, enabled: true);
        await SetAccess(owners, Tenant, device.Property, account, enabled: true);

        var register = new StationManagementRequest(Guid.NewGuid(), StationOperationKind.Register, Label: "Synthetic HTTP desk");
        string cookie;
        StationManagementHttpReceipt pair;
        using (HttpResponseMessage paired = await SendAsync(client, HttpMethod.Post, managementPath + "/operations",
            register, primaryToken, tenant: Tenant))
        {
            var result = await ReadAsync<StationManagementHttpResponse>(paired);
            pair = Assert.IsType<StationManagementHttpReceipt>(result.Receipt);
            Assert.Equal(StationManagementState.Applied, result.State);
            Assert.Equal(account, pair.OriginalIssuerSessionId);
            Assert.Equal(device.Property, pair.PropertyId);
            Assert.True(paired.Headers.Contains("Set-Cookie"));
            string[] headers = paired.Headers.GetValues("Set-Cookie").ToArray();
            string setCookie = Assert.Single(headers);
            // Use boolean checks so an assertion failure never prints the credential.
            Assert.True(setCookie.Contains("secure", StringComparison.OrdinalIgnoreCase));
            Assert.True(setCookie.Contains("httponly", StringComparison.OrdinalIgnoreCase));
            Assert.True(setCookie.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase));
            Assert.True(setCookie.Contains("path=/api/station-runtime", StringComparison.OrdinalIgnoreCase));
            Assert.False(setCookie.Contains("domain=", StringComparison.OrdinalIgnoreCase));
            cookie = setCookie.Split(';', 2)[0];
            bool correctCookieName = cookie.StartsWith(StationApiOptions.CookieName + "=", StringComparison.Ordinal);
            Assert.True(correctCookieName);
            string body = await paired.Content.ReadAsStringAsync();
            Assert.False(body.Contains(cookie[(cookie.IndexOf('=', StringComparison.Ordinal) + 1)..], StringComparison.Ordinal));
            Assert.False(body.Contains("issuerSubjectId", StringComparison.OrdinalIgnoreCase));
        }
        using (HttpResponseMessage replay = await SendAsync(client, HttpMethod.Post, managementPath + "/operations",
            register, primaryToken, tenant: Tenant))
        {
            var result = await ReadAsync<StationManagementHttpResponse>(replay);
            Assert.Equal(pair, result.Receipt);
            Assert.False(replay.Headers.Contains("Set-Cookie"));
        }

        StationCurrentResponse pending = await CurrentAsync(client, cookie);
        Assert.Equal(StationSessionState.HandoffPending, pending.Runtime.State);
        Assert.Null(pending.Runtime.Session);
        Assert.Null(pending.CsrfToken);
        Assert.Null(pending.CsrfExpiresAtUtc);
        using (HttpResponseMessage pendingRoster = await SendAsync(client, HttpMethod.Get, "/api/station-runtime/roster", cookie: cookie))
        {
            var result = await ReadAsync<StationRosterResponse>(pendingRoster);
            Assert.Equal(StationSessionState.HandoffPending, result.State);
            Assert.Empty(result.Items);
        }
        using (HttpResponseMessage mixed = await SendAsync(client, HttpMethod.Get, "/api/station-runtime/current",
            bearer: primaryToken, cookie: cookie))
        { Assert.Equal(HttpStatusCode.Unauthorized, mixed.StatusCode); }
        using (HttpResponseMessage duplicate = await SendAsync(client, HttpMethod.Get, "/api/station-runtime/current",
            cookie: cookie + "; " + cookie))
        { Assert.Equal(HttpStatusCode.Unauthorized, duplicate.StatusCode); }
        Assert.Equal(0, reservations.ListCalls);

        // This is the exact original manager session from the pairing receipt, signed out through Auth HTTP.
        using (HttpResponseMessage signedOut = await SendAsync(client, HttpMethod.Post,
            $"/api/auth/sessions/{pair.OriginalIssuerSessionId!.Value:D}/sign-out", bearer: primaryToken))
        { Assert.Equal(HttpStatusCode.NoContent, signedOut.StatusCode); }
        using (IServiceScope inspect = owners.CreateScope())
        {
            Assert.False(await inspect.ServiceProvider.GetRequiredService<IAuthSessionAdmissionReader>()
                .IsActiveAsync(AuthScope, account, pair.OriginalIssuerSessionId.Value));
        }

        StationCurrentResponse locked = await CurrentAsync(client, cookie);
        Assert.Equal(StationSessionState.Locked, locked.Runtime.State);
        StationSessionSnapshot lockedSession = Assert.IsType<StationSessionSnapshot>(locked.Runtime.Session);
        Assert.NotNull(locked.CsrfToken);
        Assert.Null(lockedSession.Actor);
        var unlock = new StationUnlockRequest(Guid.NewGuid(), device.Staff, lockedSession.Generation, Pin);
        foreach (string? csrf in new[] { null, "invalid-protected-token" })
        {
            using HttpResponseMessage refused = await SendAsync(client, HttpMethod.Post, "/api/station-runtime/unlock",
                unlock, cookie: cookie, csrf: csrf);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }
        using (IServiceScope inspect = Scope(owners, Tenant))
        {
            var browser = await inspect.ServiceProvider.GetRequiredService<StationsDbContext>().BrowserSessions.SingleAsync();
            Assert.Null(browser.ActorSessionId);
            Assert.Equal(lockedSession.Generation, browser.Generation);
        }
        using (HttpResponseMessage roster = await SendAsync(client, HttpMethod.Get, "/api/station-runtime/roster", cookie: cookie))
        {
            var result = await ReadAsync<StationRosterResponse>(roster);
            Assert.Equal(device.Staff, Assert.Single(result.Items).StaffMemberId);
        }
        using (HttpResponseMessage unlocked = await SendAsync(client, HttpMethod.Post, "/api/station-runtime/unlock",
            unlock, cookie: cookie, csrf: locked.CsrfToken))
        {
            var result = await ReadAsync<StationRuntimeResponse>(unlocked);
            Assert.Equal(StationSessionState.Active, result.State);
            Assert.Equal(StationCoreOutcome.Applied, result.Outcome);
        }
        StationCurrentResponse active = await CurrentAsync(client, cookie);
        Assert.Equal(StationSessionState.Active, active.Runtime.State);
        StationSessionSnapshot activeSession = Assert.IsType<StationSessionSnapshot>(active.Runtime.Session);
        StationActorCoordinate actor = Assert.IsType<StationActorCoordinate>(activeSession.Actor);
        Assert.Equal(device.Staff, actor.StaffMemberId);
        Assert.NotNull(active.CsrfToken);

        // Retry the same deliberate attempt after refreshing the generation-bound token.
        using (HttpResponseMessage replay = await SendAsync(client, HttpMethod.Post, "/api/station-runtime/unlock",
            unlock, cookie: cookie, csrf: active.CsrfToken))
        {
            var result = await ReadAsync<StationRuntimeResponse>(replay);
            Assert.Equal(StationCoreOutcome.Applied, result.Outcome);
            Assert.Equal(actor, result.Session!.Actor);
        }
        foreach ((Guid actorId, long generation) in new[]
        {
            (Guid.NewGuid(), actor.Generation),
            (actor.ActorSessionId, actor.Generation - 1)
        })
        {
            using HttpResponseMessage stale = await SendAsync(client, HttpMethod.Get, "/api/station-runtime/arrivals",
                cookie: cookie, actor: actorId, generation: generation);
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            var rejected = Assert.IsType<StationRuntimeResponse>(await stale.Content.ReadFromJsonAsync<StationRuntimeResponse>());
            Assert.Equal(StationSessionState.StateChanged, rejected.State);
            Assert.Null(rejected.Session);
            Assert.Equal(0, reservations.ListCalls);
        }
        using (HttpResponseMessage legacyQuery = await SendAsync(client, HttpMethod.Get,
            "/api/station-runtime/arrivals?actorSessionId=obsolete&generation=1",
            cookie: cookie, actor: actor.ActorSessionId, generation: actor.Generation))
        {
            Assert.Equal(HttpStatusCode.BadRequest, legacyQuery.StatusCode);
            Assert.Equal(0, reservations.ListCalls);
        }
        using (HttpResponseMessage arrivals = await SendAsync(client, HttpMethod.Get, "/api/station-runtime/arrivals?pageSize=25",
            cookie: cookie, actor: actor.ActorSessionId, generation: actor.Generation))
        {
            var result = await ReadAsync<StationArrivalsResponse>(arrivals);
            Assert.Equal(StationReservationState.Ready, result.State);
            Assert.Empty(result.Items);
            Assert.Null(result.Continuation);
            Assert.Equal(device.Property, result.PropertyId);
            Assert.Equal(new DateOnly(2026, 9, 20), result.PropertyLocalDate);
            Assert.Equal(1, reservations.ListCalls);
        }
        StationCurrentResponse afterRead = await CurrentAsync(client, cookie);
        Assert.Equal(activeSession.ActorIdleExpiresAtUtc, afterRead.Runtime.Session!.ActorIdleExpiresAtUtc);

        await VerifyHistoricalCheckInOutcomeAsync(api, client, device, clock.UtcNow, cookie, primaryToken,
            afterRead, reservations);
        await VerifyCheckoutOnlyHttpAsync(api, owners, client, device, account, clock, cookie, primaryToken);
    }

    private static async Task VerifyCheckoutOnlyHttpAsync(AuthTestApplication api, ServiceProvider owners,
        HttpClient client, Device device, Guid account, TestClock clock, string cookie, string primaryToken)
    {
        await SetAccess(owners, Tenant, device.Property, account, enabled: false);
        using (IServiceScope scope = Scope(owners, Tenant))
        {
            var roles = scope.ServiceProvider.GetRequiredService<IAccessControlRoleProvisioner>();
            await roles.EnsureRoleAsync(new("stations-http-checkout-only", [ReservationsAdminPermissionCodes.CheckOut]));
            await roles.EnsureAssignmentAsync(AccessSubject.User(account.ToString("D")), "stations-http-checkout-only",
                WorkspaceAccessScopes.CreateProperty(Tenant, device.Property));
        }
        Guid reservationId;
        using (IServiceScope scope = api.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(Tenant);
            reservationId = await scope.ServiceProvider.GetRequiredService<ReservationsDbContext>().Reservations
                .Where(x => x.PropertyId == device.Property && x.Status == ReservationState.CheckedIn).Select(x => x.Id).SingleAsync();
        }
        // The property's local date is now the due departure date; idle actor expiry must not remove its PIN identity.
        clock.UtcNow = clock.UtcNow.AddDays(3);
        StationCurrentResponse locked = await CurrentAsync(client, cookie);
        Assert.Equal(StationSessionState.Locked, locked.Runtime.State);
        using (HttpResponseMessage unlocked = await SendAsync(client, HttpMethod.Post, "/api/station-runtime/unlock",
            new StationUnlockRequest(Guid.NewGuid(), device.Staff, locked.Runtime.Session!.Generation, Pin),
            cookie: cookie, csrf: locked.CsrfToken))
        { Assert.Equal(StationSessionState.Active, (await ReadAsync<StationRuntimeResponse>(unlocked)).State); }
        StationCurrentResponse current = await CurrentAsync(client, cookie);
        Assert.Equal(StationReservationState.Denied, current.Jobs!.CheckIn);
        Assert.Equal(StationReservationState.Ready, current.Jobs.CheckOut);
        StationActorCoordinate actor = current.Runtime.Session!.Actor!;
        using (HttpResponseMessage denied = await SendAsync(client, HttpMethod.Get, "/api/station-runtime/arrivals",
            cookie: cookie, actor: actor.ActorSessionId, generation: actor.Generation))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            var body = await denied.Content.ReadFromJsonAsync<StationArrivalsResponse>();
            Assert.Equal(StationReservationState.Denied, body!.State);
            Assert.Empty(body.Items);
        }
        using (HttpResponseMessage stale = await SendAsync(client, HttpMethod.Get, "/api/station-runtime/departures",
            cookie: cookie, actor: Guid.NewGuid(), generation: actor.Generation))
        { Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode); }
        StationDueDeparture departure;
        using (HttpResponseMessage listed = await SendAsync(client, HttpMethod.Get, "/api/station-runtime/departures",
            cookie: cookie, actor: actor.ActorSessionId, generation: actor.Generation))
        {
            StationDeparturesResponse body = await ReadAsync<StationDeparturesResponse>(listed);
            Assert.Equal(StationReservationState.Ready, body.State);
            Assert.Equal(new DateOnly(2026, 9, 23), body.PropertyLocalDate);
            var item = Assert.Single(body.Items);
            Assert.Single(item.Places);
            departure = item.Reservation;
            Assert.Equal(reservationId, departure.ReservationId);
            Assert.Equal(StationDepartureState.Due, departure.State);
        }
        var attempt = new StationCheckOutRequest(Guid.NewGuid(), reservationId, departure.ExpectedVersion,
            actor.ActorSessionId, actor.Generation);
        var outcome = new StationCheckOutOutcomeRequest(attempt.OperationId, reservationId, attempt.ExpectedVersion,
            current.Runtime.Session.BrowserSessionId, actor.ActorSessionId, actor.Generation);
        const string writePath = "/api/station-runtime/check-out";
        const string outcomePath = "/api/station-runtime/check-out/outcome";
        using (HttpResponseMessage noCsrf = await SendAsync(client, HttpMethod.Post, writePath, attempt, cookie: cookie))
        { Assert.Equal(HttpStatusCode.Forbidden, noCsrf.StatusCode); }
        using (HttpResponseMessage mixed = await SendAsync(client, HttpMethod.Post, writePath, attempt,
            bearer: primaryToken, cookie: cookie, csrf: current.CsrfToken))
        { Assert.Equal(HttpStatusCode.Unauthorized, mixed.StatusCode); }
        for (int retry = 0; retry < 2; retry++)
        {
            using HttpResponseMessage requested = await SendAsync(client, HttpMethod.Post, writePath, attempt,
                cookie: cookie, csrf: current.CsrfToken);
            var result = await ReadAsync<StationCheckOutResult>(requested);
            Assert.Equal(StationReservationState.Applied, result.State);
            Assert.Equal(StationCheckOutOutcomeState.ReleasePending, result.Checkout);
            Assert.Equal(ReservationStatus.CheckoutPending, result.Receipt!.Status);
        }
        using (HttpResponseMessage lockResponse = await SendAsync(client, HttpMethod.Post, "/api/station-runtime/lock",
            new StationLockRequest(Guid.NewGuid(), actor.Generation), cookie: cookie, csrf: current.CsrfToken))
        { Assert.Equal(StationSessionState.Locked, (await ReadAsync<StationRuntimeResponse>(lockResponse)).State); }
        StationCurrentResponse afterLock = await CurrentAsync(client, cookie);
        using (HttpResponseMessage noCsrf = await SendAsync(client, HttpMethod.Post, outcomePath, outcome, cookie: cookie))
        { Assert.Equal(HttpStatusCode.Forbidden, noCsrf.StatusCode); }
        using (HttpResponseMessage mixed = await SendAsync(client, HttpMethod.Post, outcomePath, outcome,
            bearer: primaryToken, cookie: cookie, csrf: afterLock.CsrfToken))
        { Assert.Equal(HttpStatusCode.Unauthorized, mixed.StatusCode); }
        using (HttpResponseMessage recovered = await SendAsync(client, HttpMethod.Post, outcomePath, outcome,
            cookie: cookie, csrf: afterLock.CsrfToken))
        {
            Assert.Equal(StationCheckOutOutcomeState.ReleasePending, (await ReadAsync<StationCheckOutOutcome>(recovered)).State);
            using JsonDocument json = JsonDocument.Parse(await recovered.Content.ReadAsStringAsync());
            Assert.Equal("state", Assert.Single(json.RootElement.EnumerateObject()).Name);
        }
        Assert.Equal(afterLock.Runtime.Session, (await CurrentAsync(client, cookie)).Runtime.Session);
    }

    private static async Task VerifyHistoricalCheckInOutcomeAsync(AuthTestApplication api, HttpClient client,
        Device device, DateTimeOffset now, string cookie, string primaryToken, StationCurrentResponse active,
        ReservationCalls calls)
    {
        StationSessionSnapshot session = Assert.IsType<StationSessionSnapshot>(active.Runtime.Session);
        StationActorCoordinate actor = Assert.IsType<StationActorCoordinate>(session.Actor);
        Reservation reservation = await SeedConfirmedArrivalAsync(api, device.Property, now);
        StationCheckInRequest checkIn = new(Guid.NewGuid(), reservation.Id, reservation.Version,
            actor.ActorSessionId, actor.Generation);
        StationCheckInOutcomeRequest attempt = new(checkIn.OperationId, checkIn.ReservationId,
            checkIn.ExpectedVersion, session.BrowserSessionId, actor.ActorSessionId, actor.Generation);
        const string outcomePath = "/api/station-runtime/check-in/outcome";

        using (HttpResponseMessage committed = await SendAsync(client, HttpMethod.Post, "/api/station-runtime/check-in",
            checkIn, cookie: cookie, csrf: active.CsrfToken))
        {
            StationCheckInResult result = await ReadAsync<StationCheckInResult>(committed);
            Assert.Equal(StationReservationState.Applied, result.State);
            ReservationMutationReceiptDto receipt = Assert.IsType<ReservationMutationReceiptDto>(result.Receipt);
            Assert.Equal(ReservationStatus.CheckedIn, receipt.Status);
            Assert.Equal(checkIn.ExpectedVersion + 1, receipt.Version);
        }
        Assert.Equal(1, calls.PrepareCalls);
        Assert.Equal(1, calls.CheckInCalls);
        (ReservationManagementOperationRecord, StationCheckInProvenance, long, int) persisted =
            await ReadCommittedCheckInAsync(api, device, session, checkIn);

        using (HttpResponseMessage lockedResponse = await SendAsync(client, HttpMethod.Post, "/api/station-runtime/lock",
            new StationLockRequest(Guid.NewGuid(), session.Generation), cookie: cookie, csrf: active.CsrfToken))
        {
            StationRuntimeResponse result = await ReadAsync<StationRuntimeResponse>(lockedResponse);
            Assert.Equal(StationSessionState.Locked, result.State);
            Assert.Equal(StationCoreOutcome.Applied, result.Outcome);
        }
        StationCurrentResponse locked = await CurrentAsync(client, cookie);
        StationSessionSnapshot lockedSession = Assert.IsType<StationSessionSnapshot>(locked.Runtime.Session);
        Assert.Null(lockedSession.Actor);
        Assert.True(lockedSession.Generation > actor.Generation);

        // Current pairing CSRF is required even though this endpoint only reads a historical outcome.
        // An otherwise valid token from before the lock is stale, just like a missing or invalid token.
        string?[] rejectedTokens = [null, "invalid-protected-token", active.CsrfToken];
        foreach (string? csrf in rejectedTokens)
        {
            using HttpResponseMessage rejected = await SendAsync(client, HttpMethod.Post, outcomePath,
                attempt, cookie: cookie, csrf: csrf);
            Assert.Equal(csrf == active.CsrfToken ? HttpStatusCode.Conflict : HttpStatusCode.Forbidden, rejected.StatusCode);
            Assert.Equal(0, calls.OutcomeCalls);
        }
        using (HttpResponseMessage mixed = await SendAsync(client, HttpMethod.Post, outcomePath,
            attempt, bearer: primaryToken, cookie: cookie, csrf: locked.CsrfToken))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, mixed.StatusCode);
            Assert.Equal(0, calls.OutcomeCalls);
        }
        using (HttpResponseMessage recovered = await SendAsync(client, HttpMethod.Post, outcomePath,
            attempt, cookie: cookie, csrf: locked.CsrfToken))
        {
            await AssertOutcomeAsync(recovered, StationCheckInOutcomeState.Applied, HttpStatusCode.OK);
        }
        Assert.Equal(1, calls.OutcomeCalls);
        Assert.Equal(lockedSession, (await CurrentAsync(client, cookie)).Runtime.Session);

        using (HttpResponseMessage unlocked = await SendAsync(client, HttpMethod.Post, "/api/station-runtime/unlock",
            new StationUnlockRequest(Guid.NewGuid(), device.Staff, lockedSession.Generation, Pin),
            cookie: cookie, csrf: locked.CsrfToken))
        {
            StationRuntimeResponse result = await ReadAsync<StationRuntimeResponse>(unlocked);
            Assert.Equal(StationSessionState.Active, result.State);
        }
        StationCurrentResponse changed = await CurrentAsync(client, cookie);
        StationSessionSnapshot changedSession = Assert.IsType<StationSessionSnapshot>(changed.Runtime.Session);
        StationActorCoordinate changedActor = Assert.IsType<StationActorCoordinate>(changedSession.Actor);
        Assert.NotEqual(actor.ActorSessionId, changedActor.ActorSessionId);
        Assert.True(changedActor.Generation > actor.Generation);

        // Recovery does not reopen the historical actor's write gate, even for the same staff member.
        using (HttpResponseMessage staleWrite = await SendAsync(client, HttpMethod.Post, "/api/station-runtime/check-in",
            checkIn, cookie: cookie, csrf: changed.CsrfToken))
        {
            Assert.Equal(HttpStatusCode.Conflict, staleWrite.StatusCode);
            StationRuntimeResponse result = Assert.IsType<StationRuntimeResponse>(
                await staleWrite.Content.ReadFromJsonAsync<StationRuntimeResponse>());
            Assert.Equal(StationSessionState.StateChanged, result.State);
            Assert.Null(result.Session);
        }
        using (HttpResponseMessage staleToken = await SendAsync(client, HttpMethod.Post, outcomePath,
            attempt, cookie: cookie, csrf: locked.CsrfToken))
        {
            Assert.Equal(HttpStatusCode.Conflict, staleToken.StatusCode);
            Assert.Equal(1, calls.OutcomeCalls);
        }
        for (int replay = 0; replay < 2; replay++)
        {
            using HttpResponseMessage recovered = await SendAsync(client, HttpMethod.Post, outcomePath,
                attempt, cookie: cookie, csrf: changed.CsrfToken);
            await AssertOutcomeAsync(recovered, StationCheckInOutcomeState.Applied, HttpStatusCode.OK);
        }
        StationCheckInOutcomeRequest substitutedAttempt = attempt with
        {
            ActorSessionId = changedActor.ActorSessionId,
            ExpectedGeneration = changedActor.Generation
        };
        using (HttpResponseMessage substituted = await SendAsync(client, HttpMethod.Post, outcomePath,
            substitutedAttempt,
            cookie: cookie, csrf: changed.CsrfToken))
        {
            await AssertOutcomeAsync(substituted, StationCheckInOutcomeState.Conflict, HttpStatusCode.Conflict);
        }

        Assert.Equal(4, calls.OutcomeCalls);
        Assert.Equal(1, calls.PrepareCalls);
        Assert.Equal(1, calls.CheckInCalls);
        Assert.Equal(changedSession, (await CurrentAsync(client, cookie)).Runtime.Session);
        Assert.Equal(persisted, await ReadCommittedCheckInAsync(api, device, session, checkIn));
    }

    private static async Task AssertOutcomeAsync(HttpResponseMessage response, StationCheckInOutcomeState state,
        HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        StationCheckInOutcome result = Assert.IsType<StationCheckInOutcome>(
            await response.Content.ReadFromJsonAsync<StationCheckInOutcome>());
        Assert.Equal(state, result.State);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        // An exact one-field allowlist excludes guest/issuer/staff data, receipts, credentials and CSRF.
        Assert.Equal("state", Assert.Single(document.RootElement.EnumerateObject()).Name);
    }

    private static async Task<Reservation> SeedConfirmedArrivalAsync(AuthTestApplication api, Guid propertyId,
        DateTimeOffset now)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(Tenant);
        Guid roomId = Guid.NewGuid();
        const string roomName = "Synthetic recovery room";
        DateOnly arrival = new(2026, 9, 20);
        InventoryDbContext inventory = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        await using var inventoryTransaction = await inventory.Database.BeginTransactionAsync();
        var selectionFence = scope.ServiceProvider.GetRequiredService<IInventoryAvailabilitySelectionFence>();
        await selectionFence.AcquireAsync(propertyId, CancellationToken.None);
        await selectionFence.AdvanceAsync(propertyId, CancellationToken.None);
        await inventory.SaveChangesAsync();
        InventoryRoomTopology room = InventoryRoomTopology.Create(roomId, Tenant, propertyId);
        room.Apply(propertyId, roomName, null, null, RoomStatus.Active, 1);
        InventoryUnit unit = InventoryUnit.CreateRoom(roomId, Tenant, propertyId);
        unit.Apply(propertyId, roomId, null, InventoryUnitKind.Room, roomName, true, 1);
        RoomInventoryConfiguration configuration = RoomInventoryConfiguration.Create(roomId, Tenant, propertyId, now).Value;
        Assert.True(configuration.Configure(RoomSalesMode.RoomLevel, configuration.Version, Guid.NewGuid(), now).IsSuccess);
        configuration.ClearDomainEvents();
        inventory.AddRange(room, unit, configuration);
        await inventory.SaveChangesAsync();
        await inventoryTransaction.CommitAsync();

        Reservation reservation = Reservation.Create(Guid.NewGuid(), Tenant, propertyId, Guid.NewGuid(),
            arrival, arrival.AddDays(3), [roomId], "Synthetic recovery guest", "recovery@example.test", null,
            1, ReservationSource.Direct, null, null, null, Guid.NewGuid(), Guid.NewGuid(),
            ReservationDetailsChangeOrigin.Staff, "user:synthetic-seed", null, null, Guid.NewGuid(),
            now.AddHours(-1), new TimeOnly(15, 0), new TimeOnly(11, 0)).Value;
        ReservationDetailsChangedDomainEvent history = Assert.Single(
            reservation.DomainEvents.OfType<ReservationDetailsChangedDomainEvent>());
        Guid allocationId = Guid.NewGuid();
        Assert.True(reservation.ConfirmAllocation(reservation.AllocationRequestId, allocationId, 1,
            Guid.NewGuid(), now.AddMinutes(-59)).IsSuccess);
        reservation.ClearDomainEvents();
        ReservationsDbContext db = scope.ServiceProvider.GetRequiredService<ReservationsDbContext>();
        await ModuleTransactionIntegrationTestData.ExecuteAsync(db, async token =>
        {
            await scope.ServiceProvider.GetRequiredService<IReservationRepository>().AddAsync(reservation, token);
            await scope.ServiceProvider.GetRequiredService<IReservationDetailsHistoryWriter>().AppendAsync(history, token);
            db.InventoryUnitProjections.Add(ReservationInventoryUnitProjection.Create(new(Tenant, roomId, propertyId,
                roomId, null, InventoryUnitKind.Room, roomName, true, true, configuration.Version,
                unit.AvailabilityMutationVersion)));
            db.InventoryAllocationProjections.Add(ReservationInventoryAllocationProjection.Create(new(Tenant,
                allocationId, reservation.Id, propertyId, reservation.Arrival, reservation.Departure,
                InventoryAllocationStatus.Active, [roomId], 1)));
        });
        return reservation;
    }

    private static async Task<(ReservationManagementOperationRecord Receipt, StationCheckInProvenance Provenance,
        long Version, int OutboxCount)> ReadCommittedCheckInAsync(AuthTestApplication api, Device device,
        StationSessionSnapshot session, StationCheckInRequest request)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(Tenant);
        ReservationsDbContext db = scope.ServiceProvider.GetRequiredService<ReservationsDbContext>();
        Reservation row = await db.Reservations.AsNoTracking().SingleAsync(item => item.Id == request.ReservationId);
        Assert.Equal(ReservationState.CheckedIn, row.Status);
        Assert.Equal(request.ExpectedVersion + 1, row.Version);
        Assert.Equal(device.Staff.ToString("D"), row.CheckedInBy);
        IReservationManagementOperationRepository operations = scope.ServiceProvider
            .GetRequiredService<IReservationManagementOperationRepository>();
        ReservationManagementOperationRecord receipt = Assert.IsType<ReservationManagementOperationRecord>(
            await operations.GetAsync(request.ReservationId, request.OperationId, CancellationToken.None));
        Assert.Equal(Tenant, receipt.ScopeId);
        Assert.Equal(device.Property, receipt.PropertyId);
        Assert.True(receipt.MatchesLifecycle(ReservationManagementOperationKind.CheckIn,
            request.ExpectedVersion, new DateOnly(2026, 9, 20)));
        ReservationStationAttributionRead attribution = await operations.GetStationAttributionAsync(
            request.ReservationId, request.OperationId, CancellationToken.None);
        Assert.True(attribution.Supported);
        StationCheckInProvenance provenance = Assert.IsType<StationCheckInProvenance>(attribution.Provenance);
        StationCheckInProvenance expectedProvenance = new(session.StationId, session.BrowserSessionId,
            device.Staff, request.ActorSessionId, request.ExpectedGeneration, StationReservationAuthority.LinkedStation);
        Assert.Equal(expectedProvenance, provenance);
        string reservationId = request.ReservationId.ToString("D");
        int outboxCount = await db.OutboxMessages.CountAsync(item =>
            item.EventType == typeof(ReservationCheckedInIntegrationEvent).FullName && item.Payload.Contains(reservationId));
        Assert.Equal(1, outboxCount);
        return (receipt, provenance, row.Version, outboxCount);
    }

    private static Dictionary<string, string?> Configuration() => new(StringComparer.Ordinal)
    {
        ["Auth:GlobalScopeId"] = AuthScope,
        ["Stations:Http:Enabled"] = "true",
        ["Stations:Http:AllowedOrigins:0"] = Origin,
        ["Stations:Http:CsrfMinutes"] = "5",
        ["Stations:Core:ExternalEpoch"] = "1",
        ["Stations:Core:PepperVersion"] = "fixture-v1",
        ["Stations:PepperKeys:fixture-v1"] = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray())
    };

    private static async Task SetManagementPermissionAsync(ServiceProvider owners, Guid account, bool enabled)
    {
        using IServiceScope scope = Scope(owners, Tenant);
        var roles = scope.ServiceProvider.GetRequiredService<IAccessControlRoleProvisioner>();
        await roles.EnsureRoleAsync(new(ManagementRole, [StationsPermissionCodes.Manage]));
        var subject = AccessSubject.User(account.ToString("D"));
        var target = WorkspaceAccessScopes.Create(Tenant);
        if (enabled)
        { await roles.EnsureAssignmentAsync(subject, ManagementRole, target); }
        else
        { await roles.RemoveAssignmentAsync(subject, ManagementRole, target); }
    }

    private static async Task SeedEmptyArrivalsPropertyAsync(AuthTestApplication api, Device device, DateTimeOffset now)
    {
        using IServiceScope scope = api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().SetTenant(Tenant);
        var db = scope.ServiceProvider.GetRequiredService<ReservationsDbContext>();
        await ModuleTransactionIntegrationTestData.ExecuteAsync(db, async token =>
        {
            IntegrationEventSubscription subscription = scope.ServiceProvider
                .GetRequiredService<IIntegrationEventSubscriptionRegistry>().Subscriptions.Single(item =>
                    item.ConsumerModule == ReservationsModuleMetadata.Name && item.EventType == typeof(PropertyCreatedIntegrationEvent));
            var handler = (IIntegrationEventHandler<PropertyCreatedIntegrationEvent>)scope.ServiceProvider.GetRequiredService(subscription.HandlerType);
            await handler.HandleAsync(new(Guid.NewGuid(), Tenant, now, device.Property, "Synthetic station property",
                "station", "America/New_York", PropertyStatus.Active, 1), token);
            await CountryPolicyIntegrationTestData.ApplyActivationAsync(scope.ServiceProvider,
                ReservationsModuleMetadata.Name, Tenant, device.Property, 2, token);
        });
        var inventory = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        await ModuleTransactionIntegrationTestData.ExecuteAsync(inventory, async token =>
        {
            IntegrationEventSubscription subscription = scope.ServiceProvider
                .GetRequiredService<IIntegrationEventSubscriptionRegistry>().Subscriptions.Single(item =>
                    item.ConsumerModule == InventoryModuleMetadata.Name && item.EventType == typeof(PropertyCreatedIntegrationEvent));
            var handler = (IIntegrationEventHandler<PropertyCreatedIntegrationEvent>)scope.ServiceProvider.GetRequiredService(subscription.HandlerType);
            await handler.HandleAsync(new(Guid.NewGuid(), Tenant, now, device.Property, "Synthetic station property",
                "station", "America/New_York", PropertyStatus.Active, 1), token);
        });
    }

    private static string Token(Guid account, DateTimeOffset authenticatedAt)
    {
        // JWT lifetime uses wall-clock time because the real bearer handler validates it.
        // Session admission and assurance retain the deterministic domain clock from P2.
        Claim[] claims =
        [
            new(ApplicationClaimNames.Subject, account.ToString("D")),
            new(ApplicationClaimNames.ScopeId, AuthScope),
            new(ApplicationClaimNames.SessionId, account.ToString("D")),
            new(ApplicationClaimNames.AuthenticationContextReference, "urn:gma:acr:mfa"),
            new(ApplicationClaimNames.AuthenticationTime, authenticatedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64)
        ];
        var signing = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(AuthTestApplication.JwtSigningKey)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken("BunkFy", "BunkFy", claims, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(10), signing);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path,
        object? body = null, string? bearer = null, string? tenant = null, string? cookie = null,
        string? csrf = null, Guid? actor = null, long? generation = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Origin", Origin);
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        request.Headers.Add("Sec-Fetch-Mode", "cors");
        request.Headers.Add("Sec-Fetch-Dest", "empty");
        if (bearer is not null)
        { request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer); }
        if (tenant is not null)
        { request.Headers.Add("X-Tenant-Id", tenant); }
        if (cookie is not null)
        { request.Headers.Add("Cookie", cookie); }
        if (csrf is not null)
        { request.Headers.Add(StationApiOptions.CsrfHeaderName, csrf); }
        if (actor is { } actorId)
        { request.Headers.Add(StationApiOptions.ActorHeaderName, actorId.ToString("D")); }
        if (generation is { } value)
        { request.Headers.Add(StationApiOptions.GenerationHeaderName, value.ToString(CultureInfo.InvariantCulture)); }
        if (body is not null)
        { request.Content = JsonContent.Create(body); }
        return await client.SendAsync(request);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) where T : class
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        return Assert.IsType<T>(await response.Content.ReadFromJsonAsync<T>());
    }

    private static async Task<StationCurrentResponse> CurrentAsync(HttpClient client, string cookie)
    {
        using HttpResponseMessage response = await SendAsync(client, HttpMethod.Get, "/api/station-runtime/current", cookie: cookie);
        return await ReadAsync<StationCurrentResponse>(response);
    }

    private static void ObserveRealReservations(IServiceCollection services, ReservationCalls calls)
    {
        ServiceDescriptor descriptor = services.Single(item => item.ServiceType == typeof(IStationReservationOperations));
        Type ownerType = descriptor.ImplementationType ?? throw new InvalidOperationException("Expected the concrete Reservations owner registration.");
        services.Remove(descriptor);
        services.AddScoped<IStationReservationOperations>(provider => new CountingReservations(
            (IStationReservationOperations)ActivatorUtilities.CreateInstance(provider, ownerType), calls));
        ServiceDescriptor outcomeDescriptor = services.Single(item => item.ServiceType == typeof(IStationCheckInOutcomeReader));
        Type outcomeType = outcomeDescriptor.ImplementationType ??
            throw new InvalidOperationException("Expected the concrete Reservations outcome owner registration.");
        services.Remove(outcomeDescriptor);
        services.AddScoped<IStationCheckInOutcomeReader>(provider => new CountingOutcomes(
            (IStationCheckInOutcomeReader)ActivatorUtilities.CreateInstance(provider, outcomeType), calls));
    }

    private sealed class ReservationCalls
    {
        public int ListCalls { get; set; }
        public int PrepareCalls { get; set; }
        public int CheckInCalls { get; set; }
        public int OutcomeCalls { get; set; }
    }

    private sealed class CountingReservations(IStationReservationOperations owner, ReservationCalls calls) : IStationReservationOperations
    {
        public Task<StationDueArrivalPage> ListAsync(Guid propertyId, DateOnly localDate, int pageSize,
            StationArrivalCursor? after, CancellationToken cancellationToken = default)
        {
            calls.ListCalls++;
            return owner.ListAsync(propertyId, localDate, pageSize, after, cancellationToken);
        }
        public Task<StationCheckInPreparation> PrepareAsync(Guid propertyId, Guid reservationId, Guid operationId,
            long expectedVersion, DateOnly localDate, StationCheckInProvenance provenance, CancellationToken cancellationToken = default)
        {
            calls.PrepareCalls++;
            return owner.PrepareAsync(propertyId, reservationId, operationId, expectedVersion, localDate, provenance, cancellationToken);
        }
        public Task<StationCheckInResult> CheckInAsync(Guid propertyId, Guid reservationId, Guid operationId,
            long expectedVersion, StationCheckInPreparation preparation, StationCheckInProvenance provenance,
            CancellationToken cancellationToken = default)
        {
            calls.CheckInCalls++;
            return owner.CheckInAsync(propertyId, reservationId, operationId, expectedVersion, preparation, provenance, cancellationToken);
        }
    }

    private sealed class CountingOutcomes(IStationCheckInOutcomeReader owner, ReservationCalls calls) : IStationCheckInOutcomeReader
    {
        public Task<StationCheckInOutcome> ResolveAsync(Guid propertyId, Guid stationId, Guid browserSessionId,
            Guid actorSessionId, long generation, Guid reservationId, Guid operationId, long expectedVersion,
            CancellationToken cancellationToken = default)
        {
            calls.OutcomeCalls++;
            return owner.ResolveAsync(propertyId, stationId, browserSessionId, actorSessionId, generation,
                reservationId, operationId, expectedVersion, cancellationToken);
        }
    }
}
