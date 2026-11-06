namespace BunkFy.Modules.Stations.Application;

using BunkFy.Modules.Inventory.Contracts.Stations;
using BunkFy.Modules.Reservations.Contracts.Stations;
using BunkFy.Modules.Stations.Contracts;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Microsoft.Extensions.DependencyInjection;

public sealed record StationFirstJobArrival(StationDueArrival Reservation, IReadOnlyList<StationInventoryLabel> Places);
public sealed record StationFirstJobPage(StationReservationState State, IReadOnlyList<StationFirstJobArrival> Items,
    StationArrivalCursor? Continuation = null, Guid? PropertyId = null, DateOnly? PropertyLocalDate = null);

public sealed record StationFirstJobDeparture(StationDueDeparture Reservation, IReadOnlyList<StationInventoryLabel> Places);
public sealed record StationFirstJobDeparturePage(StationReservationState State, IReadOnlyList<StationFirstJobDeparture> Items,
    StationDepartureCursor? Continuation = null, Guid? PropertyId = null, DateOnly? PropertyLocalDate = null);
public sealed record StationJobAvailability(StationReservationState CheckIn, StationReservationState CheckOut);

/// <summary>Isolated station jobs; no primary principal, client scope/date, general directory, or persistent authority cache.</summary>
public sealed class StationFirstJobService(IServiceScopeFactory scopes, IStationCredentialBootstrap bootstrap, ISystemClock clock)
{
    public Task<StationFirstJobPage> ListAsync(string opaqueCredential, StationActorCoordinate actor, int pageSize = 25,
        StationArrivalCursor? after = null, CancellationToken cancellationToken = default) =>
        this.WithActorAsync(opaqueCredential, actor, async context =>
        {
            if (pageSize is < 1 or > 25)
            { return new(StationReservationState.Conflict, []); }
            var page = await context.Services.GetRequiredService<IStationReservationOperations>()
                .ListAsync(context.Device.PropertyId, context.LocalDate, pageSize, after, cancellationToken).ConfigureAwait(false);
            var state = await this.RevalidateAsync(context, opaqueCredential, actor, cancellationToken).ConfigureAwait(false);
            if (state != StationReservationState.Ready)
            { return new(state, []); }
            if (page.State != StationReservationState.Ready)
            { return new(page.State, []); }
            var labels = await this.LabelsAsync(context, opaqueCredential, actor, page.Items, cancellationToken).ConfigureAwait(false);
            if (labels.State != StationReservationState.Ready)
            { return new(labels.State, []); }
            state = await this.RevalidateAsync(context, opaqueCredential, actor, cancellationToken).ConfigureAwait(false);
            return state == StationReservationState.Ready
                ? new(StationReservationState.Ready, page.Items.Select(x => new StationFirstJobArrival(x,
                    x.Units.Select(u => labels.Items.Single(l => l.InventoryUnitId == u.InventoryUnitId)).ToArray())).ToArray(),
                    page.Continuation, context.Device.PropertyId, context.LocalDate)
                : new(state, []);
        }, state => new StationFirstJobPage(state, []), cancellationToken);

    public Task<StationCheckInResult> CheckInAsync(string opaqueCredential, StationActorCoordinate actor,
        Guid operationId, Guid reservationId, long expectedVersion, CancellationToken cancellationToken = default) =>
        this.WithActorAsync(opaqueCredential, actor, async context =>
        {
            var operations = context.Services.GetRequiredService<IStationReservationOperations>();
            var provenance = Provenance(context, actor);
            var preparation = await operations.PrepareAsync(context.Device.PropertyId, reservationId, operationId,
                expectedVersion, context.LocalDate, provenance, cancellationToken).ConfigureAwait(false);
            var state = await this.RevalidateAsync(context, opaqueCredential, actor, cancellationToken).ConfigureAwait(false);
            if (state != StationReservationState.Ready)
            { return new(state); }
            if (preparation.State != StationReservationState.Ready)
            { return new(preparation.State); }
            if (!preparation.Replay)
            {
                if (preparation.Arrival is null)
                { return new(StationReservationState.Incomplete); }
                var labels = await this.LabelsAsync(context, opaqueCredential, actor, [preparation.Arrival], cancellationToken).ConfigureAwait(false);
                if (labels.State != StationReservationState.Ready)
                { return new(labels.State); }
            }
            state = await this.RevalidateAsync(context, opaqueCredential, actor, cancellationToken).ConfigureAwait(false);
            if (state != StationReservationState.Ready)
            { return new(state); }
            var result = await operations.CheckInAsync(context.Device.PropertyId, reservationId, operationId, expectedVersion,
                preparation, provenance, cancellationToken).ConfigureAwait(false);
            state = await this.RevalidateAsync(context, opaqueCredential, actor, cancellationToken).ConfigureAwait(false);
            return state == StationReservationState.Ready ? result : new(state);
        }, state => new StationCheckInResult(state), cancellationToken);

    /// <summary>Confirms only the original persisted outcome, including after staff switch/lock. Never replays its mutation.</summary>
    public async Task<StationCheckInOutcome> ResolveCheckInOutcomeAsync(string credential, Guid browserSessionId,
        Guid actorSessionId, long generation, Guid operationId, Guid reservationId, long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (StationCredentialEncoding.Digest(credential) is null || browserSessionId == Guid.Empty ||
            actorSessionId == Guid.Empty || generation < 1 || operationId == Guid.Empty || reservationId == Guid.Empty || expectedVersion < 1)
        { return new(StationCheckInOutcomeState.Conflict); }
        try
        {
            var device = await bootstrap.FindAsync(credential, cancellationToken).ConfigureAwait(false);
            if (device is null || !Guid.TryParseExact(device.ScopeId, "D", out Guid tenant) || tenant == Guid.Empty ||
                tenant.ToString("D") != device.ScopeId || device.StationId == Guid.Empty || device.PropertyId == Guid.Empty ||
                device.BrowserSessionId != browserSessionId)
            { return new(StationCheckInOutcomeState.Conflict); }
            await using var child = scopes.CreateAsyncScope();
            var services = child.ServiceProvider;
            services.GetRequiredService<IScopeContextAccessor>().SetScope(device.ScopeId);
            var store = services.GetRequiredService<IStationRuntimeStore>();
            var handoff = services.GetRequiredService<StationHandoffAdmission>();
            var admission = services.GetRequiredService<StationAdmissionCoordinator>();
            DateTimeOffset started = clock.UtcNow;
            var before = await PairingAsync().ConfigureAwait(false);
            if (before is not null)
            { return new(before.Value); }
            var result = await services.GetRequiredService<IStationCheckInOutcomeReader>().ResolveAsync(device.PropertyId,
                device.StationId, device.BrowserSessionId, actorSessionId, generation, reservationId, operationId,
                expectedVersion, cancellationToken).ConfigureAwait(false);
            var after = await PairingAsync().ConfigureAwait(false);
            return after is not null ? new(after.Value) : Enum.IsDefined(result.State) ? result : new(StationCheckInOutcomeState.Unavailable);

            async Task<StationCheckInOutcomeState?> PairingAsync()
            {
                var facts = await store.ReadAsync(device, credential, null, clock.UtcNow, cancellationToken).ConfigureAwait(false);
                if (!CurrentPairing(facts))
                { return StationCheckInOutcomeState.Conflict; }
                var handoffState = await handoff.ObserveAsync(device, cancellationToken).ConfigureAwait(false);
                if (handoffState is not null)
                { return handoffState == StationSessionState.Unavailable ? StationCheckInOutcomeState.Unavailable : StationCheckInOutcomeState.Conflict; }
                var property = await admission.ObservePropertyAsync(device.PropertyId, cancellationToken).ConfigureAwait(false);
                if (property.State != StationAdmissionState.Current)
                { return property.State == StationAdmissionState.Unavailable ? StationCheckInOutcomeState.Unavailable : StationCheckInOutcomeState.Conflict; }
                var repeated = await store.ReadAsync(device, credential, null, clock.UtcNow, cancellationToken).ConfigureAwait(false);
                return CurrentPairing(repeated) ? null : StationCheckInOutcomeState.Conflict;
            }
            bool CurrentPairing(StationRuntimeFacts? facts) => clock.UtcNow >= started && facts is not null &&
                facts.Session.StationId == device.StationId && facts.Session.PropertyId == device.PropertyId &&
                facts.Session.BrowserSessionId == device.BrowserSessionId && facts.Session.Generation >= generation &&
                facts.Session.PairingExpiresAtUtc > clock.UtcNow;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return new(StationCheckInOutcomeState.Unavailable); }
    }

    public Task<StationFirstJobDeparturePage> ListDeparturesAsync(string opaqueCredential, StationActorCoordinate actor, int pageSize = 25,
        StationDepartureCursor? after = null, CancellationToken cancellationToken = default) =>
        this.WithActorAsync(opaqueCredential, actor, async context =>
        {
            if (pageSize is < 1 or > 25)
            { return new(StationReservationState.Conflict, []); }
            var page = await context.Services.GetRequiredService<IStationDepartureOperations>()
                .ListAsync(context.Device.PropertyId, context.LocalDate, pageSize, after, cancellationToken).ConfigureAwait(false);
            var state = await this.RevalidateAsync(context, opaqueCredential, actor, cancellationToken).ConfigureAwait(false);
            if (state != StationReservationState.Ready)
            { return new(state, []); }
            if (page.State != StationReservationState.Ready)
            { return new(page.State, []); }
            var labels = await this.LabelsAsync(context, opaqueCredential, actor, page.Items.Select(AsArrival).ToArray(), cancellationToken).ConfigureAwait(false);
            if (labels.State != StationReservationState.Ready)
            { return new(labels.State, []); }
            state = await this.RevalidateAsync(context, opaqueCredential, actor, cancellationToken).ConfigureAwait(false);
            return state == StationReservationState.Ready
                ? new(StationReservationState.Ready, page.Items.Select(x => new StationFirstJobDeparture(x,
                    x.Units.Select(u => labels.Items.Single(l => l.InventoryUnitId == u.InventoryUnitId)).ToArray())).ToArray(),
                    page.Continuation, context.Device.PropertyId, context.LocalDate)
                : new(state, []);
        }, state => new StationFirstJobDeparturePage(state, []), cancellationToken, checkOut: true);

    public Task<StationCheckOutResult> CheckOutAsync(string opaqueCredential, StationActorCoordinate actor,
        Guid operationId, Guid reservationId, long expectedVersion, CancellationToken cancellationToken = default) =>
        this.WithActorAsync(opaqueCredential, actor, async context =>
        {
            var operations = context.Services.GetRequiredService<IStationDepartureOperations>();
            var provenance = Provenance(context, actor);
            var preparation = await operations.PrepareAsync(context.Device.PropertyId, reservationId, operationId,
                expectedVersion, context.LocalDate, provenance, cancellationToken).ConfigureAwait(false);
            var state = await this.RevalidateAsync(context, opaqueCredential, actor, cancellationToken).ConfigureAwait(false);
            if (state != StationReservationState.Ready)
            { return new(state); }
            if (preparation.State != StationReservationState.Ready)
            { return new(preparation.State); }
            if (!preparation.Replay)
            {
                if (preparation.Departure is null)
                { return new(StationReservationState.Incomplete); }
                var labels = await this.LabelsAsync(context, opaqueCredential, actor, [AsArrival(preparation.Departure)], cancellationToken).ConfigureAwait(false);
                if (labels.State != StationReservationState.Ready)
                { return new(labels.State); }
            }
            state = await this.RevalidateAsync(context, opaqueCredential, actor, cancellationToken).ConfigureAwait(false);
            if (state != StationReservationState.Ready)
            { return new(state); }
            var result = await operations.CheckOutAsync(context.Device.PropertyId, reservationId, operationId, expectedVersion,
                preparation, provenance, cancellationToken).ConfigureAwait(false);
            state = await this.RevalidateAsync(context, opaqueCredential, actor, cancellationToken).ConfigureAwait(false);
            return state == StationReservationState.Ready ? result : new(state);
        }, state => new StationCheckOutResult(state), cancellationToken, checkOut: true);

    /// <summary>Confirms only the original persisted outcome, including after staff switch/lock. Never replays its mutation.</summary>
    public async Task<StationCheckOutOutcome> ResolveCheckOutOutcomeAsync(string credential, Guid browserSessionId,
        Guid actorSessionId, long generation, Guid operationId, Guid reservationId, long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (StationCredentialEncoding.Digest(credential) is null || browserSessionId == Guid.Empty ||
            actorSessionId == Guid.Empty || generation < 1 || operationId == Guid.Empty || reservationId == Guid.Empty || expectedVersion < 1)
        { return new(StationCheckOutOutcomeState.Conflict); }
        try
        {
            var device = await bootstrap.FindAsync(credential, cancellationToken).ConfigureAwait(false);
            if (device is null || !Guid.TryParseExact(device.ScopeId, "D", out Guid tenant) || tenant == Guid.Empty ||
                tenant.ToString("D") != device.ScopeId || device.StationId == Guid.Empty || device.PropertyId == Guid.Empty ||
                device.BrowserSessionId != browserSessionId)
            { return new(StationCheckOutOutcomeState.Conflict); }
            await using var child = scopes.CreateAsyncScope();
            var services = child.ServiceProvider;
            services.GetRequiredService<IScopeContextAccessor>().SetScope(device.ScopeId);
            var store = services.GetRequiredService<IStationRuntimeStore>();
            var handoff = services.GetRequiredService<StationHandoffAdmission>();
            var admission = services.GetRequiredService<StationAdmissionCoordinator>();
            DateTimeOffset started = clock.UtcNow;
            var before = await PairingAsync().ConfigureAwait(false);
            if (before is not null)
            { return new(before.Value); }
            var result = await services.GetRequiredService<IStationCheckOutOutcomeReader>().ResolveAsync(device.PropertyId,
                device.StationId, device.BrowserSessionId, actorSessionId, generation, reservationId, operationId,
                expectedVersion, cancellationToken).ConfigureAwait(false);
            var after = await PairingAsync().ConfigureAwait(false);
            return after is not null ? new(after.Value) : Enum.IsDefined(result.State) ? result : new(StationCheckOutOutcomeState.Unavailable);

            async Task<StationCheckOutOutcomeState?> PairingAsync()
            {
                var facts = await store.ReadAsync(device, credential, null, clock.UtcNow, cancellationToken).ConfigureAwait(false);
                if (!CurrentPairing(facts))
                { return StationCheckOutOutcomeState.Conflict; }
                var handoffState = await handoff.ObserveAsync(device, cancellationToken).ConfigureAwait(false);
                if (handoffState is not null)
                { return handoffState == StationSessionState.Unavailable ? StationCheckOutOutcomeState.Unavailable : StationCheckOutOutcomeState.Conflict; }
                var property = await admission.ObservePropertyAsync(device.PropertyId, cancellationToken).ConfigureAwait(false);
                if (property.State != StationAdmissionState.Current)
                { return property.State == StationAdmissionState.Unavailable ? StationCheckOutOutcomeState.Unavailable : StationCheckOutOutcomeState.Conflict; }
                var repeated = await store.ReadAsync(device, credential, null, clock.UtcNow, cancellationToken).ConfigureAwait(false);
                return CurrentPairing(repeated) ? null : StationCheckOutOutcomeState.Conflict;
            }
            bool CurrentPairing(StationRuntimeFacts? facts) => clock.UtcNow >= started && facts is not null &&
                facts.Session.StationId == device.StationId && facts.Session.PropertyId == device.PropertyId &&
                facts.Session.BrowserSessionId == device.BrowserSessionId && facts.Session.Generation >= generation &&
                facts.Session.PairingExpiresAtUtc > clock.UtcNow;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return new(StationCheckOutOutcomeState.Unavailable); }
    }

    public async Task<StationJobAvailability> ReadJobsAsync(string credential, StationActorCoordinate actor,
        CancellationToken cancellationToken = default)
    {
        var checkIn = await this.WithActorAsync(credential, actor, _ => Task.FromResult(StationReservationState.Ready),
            state => state, cancellationToken).ConfigureAwait(false);
        var checkOut = await this.WithActorAsync(credential, actor, _ => Task.FromResult(StationReservationState.Ready),
            state => state, cancellationToken, checkOut: true).ConfigureAwait(false);
        return new(checkIn, checkOut);
    }

    private static StationDueArrival AsArrival(StationDueDeparture departure) => new(departure.ReservationId,
        departure.PrimaryGuestName, departure.Arrival, departure.Departure, departure.ExpectedVersion,
        departure.AllocationId, departure.AllocationVersion, departure.Units);

    private async Task<T> WithActorAsync<T>(string credential, StationActorCoordinate actor, Func<Context, Task<T>> action,
        Func<StationReservationState, T> failure, CancellationToken ct, bool checkOut = false)
    {
        ct.ThrowIfCancellationRequested();
        if (StationCredentialEncoding.Digest(credential) is null || actor.StaffMemberId == Guid.Empty ||
            actor.ActorSessionId == Guid.Empty || actor.Generation < 1 ||
            actor.AuthorityKind is not (StationAuthorityKind.LinkedStation or StationAuthorityKind.StationOnly))
        { return failure(StationReservationState.Denied); }
        try
        {
            var device = await bootstrap.FindAsync(credential, ct).ConfigureAwait(false);
            if (device is null || !Guid.TryParseExact(device.ScopeId, "D", out var tenant) || tenant == Guid.Empty ||
                tenant.ToString("D") != device.ScopeId || device.StationId == Guid.Empty || device.BrowserSessionId == Guid.Empty || device.PropertyId == Guid.Empty)
            { return failure(StationReservationState.Denied); }
            await using var child = scopes.CreateAsyncScope();
            var services = child.ServiceProvider;
            services.GetRequiredService<IScopeContextAccessor>().SetScope(device.ScopeId);
            var store = services.GetRequiredService<IStationRuntimeStore>();
            var facts = await store.ReadAsync(device, credential, null, clock.UtcNow, ct).ConfigureAwait(false);
            if (facts is null || !Current(device, facts, actor))
            { return failure(StationReservationState.Denied); }
            var handoff = await services.GetRequiredService<StationHandoffAdmission>().ObserveAsync(device, ct).ConfigureAwait(false);
            if (handoff is not null)
            { return failure(handoff == StationSessionState.Unavailable ? StationReservationState.Unavailable : StationReservationState.Denied); }
            var admission = await services.GetRequiredService<StationAdmissionCoordinator>()
                .ObserveJobAsync(device.PropertyId, actor.StaffMemberId, facts.Credential!.Enrollment,
                    checkOut ? WorkspaceStaffStationAction.ReservationCheckOut : WorkspaceStaffStationAction.ReservationCheckIn, ct).ConfigureAwait(false);
            if (admission.State != StationAdmissionState.Current || admission.PropertyLocalDate is not { } date)
            { return failure(FromAdmission(admission.State)); }
            StationJobGrant? grant = actor.AuthorityKind == StationAuthorityKind.StationOnly
                ? await services.GetRequiredService<IStationJobGrantReader>().ReadJobGrantAsync(device.PropertyId,
                    actor.StaffMemberId, checkOut, ct).ConfigureAwait(false) : null;
            if (actor.AuthorityKind == StationAuthorityKind.StationOnly && grant is not { Revoked: false, Revision: > 0 })
            { return failure(StationReservationState.Denied); }
            var context = new Context(services, device, store, facts, date, checkOut, grant);
            var state = await this.RevalidateAsync(context, credential, actor, ct).ConfigureAwait(false);
            return state == StationReservationState.Ready ? await action(context).ConfigureAwait(false) : failure(state);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return failure(StationReservationState.Unavailable); }
    }

    private async Task<StationReservationState> RevalidateAsync(Context context, string credential, StationActorCoordinate actor, CancellationToken ct)
    {
        var before = await context.Store.ReadAsync(context.Device, credential, null, clock.UtcNow, ct).ConfigureAwait(false);
        if (before is null || !Current(context.Device, before, actor) || context.Facts != before)
        { return StationReservationState.Conflict; }
        var admission = await context.Services.GetRequiredService<StationAdmissionCoordinator>()
            .ObserveJobAsync(context.Device.PropertyId, actor.StaffMemberId, before.Credential!.Enrollment,
                context.CheckOut ? WorkspaceStaffStationAction.ReservationCheckOut : WorkspaceStaffStationAction.ReservationCheckIn, ct).ConfigureAwait(false);
        if (admission.State != StationAdmissionState.Current)
        { return FromAdmission(admission.State); }
        if (actor.AuthorityKind == StationAuthorityKind.StationOnly)
        {
            var grant = await context.Services.GetRequiredService<IStationJobGrantReader>()
                .ReadJobGrantAsync(context.Device.PropertyId, actor.StaffMemberId, context.CheckOut, ct).ConfigureAwait(false);
            if (grant is not { Revoked: false, Revision: > 0 } || grant != context.Grant)
            { return StationReservationState.Denied; }
        }
        var after = await context.Store.ReadAsync(context.Device, credential, null, clock.UtcNow, ct).ConfigureAwait(false);
        return before == after && admission.PropertyLocalDate == context.LocalDate
            ? StationReservationState.Ready : StationReservationState.Conflict;
    }

    private async Task<Labels> LabelsAsync(Context context, string credential, StationActorCoordinate actor,
        IReadOnlyList<StationDueArrival> arrivals, CancellationToken ct)
    {
        if (arrivals.Count > 25 || arrivals.Any(x => x.Units.Count is < 1 or > 100))
        { return new(StationReservationState.Incomplete, []); }
        var expected = arrivals.SelectMany(x => x.Units).GroupBy(x => x.InventoryUnitId).OrderBy(x => x.Key).ToArray();
        if (expected.Length > 100 || expected.Any(g => g.Key == Guid.Empty || g.Distinct().Count() != 1))
        { return new(StationReservationState.Incomplete, []); }
        var reader = context.Services.GetRequiredService<IStationInventoryLabelReader>();
        List<StationInventoryLabel> result = [];
        foreach (var batch in expected.Chunk(25))
        {
            var state = await this.RevalidateAsync(context, credential, actor, ct).ConfigureAwait(false);
            if (state != StationReservationState.Ready)
            { return new(state, []); }
            var labels = await reader.ReadAsync(context.Device.PropertyId, batch.Select(x => x.Key).ToArray(), ct).ConfigureAwait(false);
            if (labels.State != StationInventoryLabelsState.Current)
            { return new(labels.State == StationInventoryLabelsState.Unavailable ? StationReservationState.Unavailable : StationReservationState.Incomplete, []); }
            if (labels.Items.Count != batch.Length || labels.Items.Select(x => x.InventoryUnitId).Distinct().Count() != batch.Length)
            { return new(StationReservationState.Incomplete, []); }
            foreach (var group in batch)
            {
                var unit = group.First();
                var label = labels.Items.SingleOrDefault(x => x.InventoryUnitId == unit.InventoryUnitId);
                if (label is null || label.PropertyId != context.Device.PropertyId || label.RoomId != unit.RoomId || label.BedId != unit.BedId ||
                    (int)label.Kind != unit.Kind || label.UnitVersion != unit.UnitVersion || label.ConfigurationVersion != unit.ConfigurationVersion ||
                    label.UnitSourceVersion < 1 || label.RoomSourceVersion < 1 || string.IsNullOrWhiteSpace(label.RoomName) ||
                    (unit.BedId is not null && (label.BedSourceVersion is null or < 1 || string.IsNullOrWhiteSpace(label.BedLabel))))
                { return new(StationReservationState.Incomplete, []); }
                result.Add(label);
            }
        }
        return new(StationReservationState.Ready, result);
    }

    private static bool Current(StationDeviceReference device, StationRuntimeFacts facts, StationActorCoordinate actor) =>
        facts.ActorCurrent && facts.Registered && facts.RegistrationRevision > 0 && facts.Session.Actor == actor && facts.Session.Generation == actor.Generation &&
        facts.Session.PropertyId == device.PropertyId && facts.Session.StationId == device.StationId && facts.Session.BrowserSessionId == device.BrowserSessionId &&
        facts.Credential is { Revoked: false, Enrollment.IsBound: true } credential && credential.StaffMemberId == actor.StaffMemberId;
    private static StationCheckInProvenance Provenance(Context context, StationActorCoordinate actor) =>
        new(context.Device.StationId, context.Device.BrowserSessionId, actor.StaffMemberId, actor.ActorSessionId, actor.Generation,
            actor.AuthorityKind switch
            {
                StationAuthorityKind.LinkedStation => StationReservationAuthority.LinkedStation,
                StationAuthorityKind.StationOnly => StationReservationAuthority.StationOnly,
                _ => StationReservationAuthority.Unknown
            }, context.Grant?.Revision);
    private static StationReservationState FromAdmission(StationAdmissionState state) => state switch
    {
        StationAdmissionState.Denied => StationReservationState.Denied,
        StationAdmissionState.StateChanged => StationReservationState.Conflict,
        _ => StationReservationState.Unavailable
    };
    private sealed record Context(IServiceProvider Services, StationDeviceReference Device, IStationRuntimeStore Store,
        StationRuntimeFacts Facts, DateOnly LocalDate, bool CheckOut, StationJobGrant? Grant);
    private sealed record Labels(StationReservationState State, IReadOnlyList<StationInventoryLabel> Items);
}
