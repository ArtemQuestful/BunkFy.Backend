namespace BunkFy.Modules.Stations.Application;

using BunkFy.Modules.Stations.Contracts;
using BunkFy.Modules.Stations.Domain;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Microsoft.Extensions.DependencyInjection;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Properties.Contracts;

/// <summary>
/// Paired-device runtime only. No Auth principal, management permission, pairing issuance or HTTP activation.
/// Every call discovers the tenant from the opaque credential, then uses a fresh normally filtered scope.
/// </summary>
public sealed class StationRuntimeService(IServiceScopeFactory scopes, IStationCredentialBootstrap bootstrap,
    ISystemClock clock) : IStationSessionReader
{
    public Task<StationRuntimeResponse> ReadAsync(string opaqueCredential, CancellationToken cancellationToken = default) =>
        this.WithDeviceAsync(opaqueCredential, async (services, device, store) =>
            await this.CurrentAsync(services, device, store, opaqueCredential, cancellationToken).ConfigureAwait(false), cancellationToken);

    public Task<StationCurrentView> ReadViewAsync(string opaqueCredential, CancellationToken cancellationToken = default) =>
        this.WithDeviceValueAsync<StationCurrentView>(opaqueCredential, async (services, device, store) =>
        {
            StationRuntimeResponse current = await this.CurrentAsync(services, device, store, opaqueCredential, cancellationToken).ConfigureAwait(false);
            if (current.State is not (StationSessionState.Locked or StationSessionState.Active) || current.Session is not { } session)
            { return new(new(current.State)); }
            var admission = services.GetRequiredService<StationAdmissionCoordinator>();
            var propertyAdmission = await admission.ObservePropertyAsync(device.PropertyId, cancellationToken).ConfigureAwait(false);
            if (propertyAdmission.State != StationAdmissionState.Current)
            { return new(FromAdmission(propertyAdmission)); }
            var properties = services.GetRequiredService<IPropertyStationEligibilitySource>();
            var property = await properties.FindAsync(device.ScopeId, device.PropertyId, cancellationToken).ConfigureAwait(false);
            if (property is null || property.ScopeId != device.ScopeId || property.PropertyId != device.PropertyId ||
                !DisplayLabel(property.Name, 256) || !DisplayLabel(session.StationLabel, 100))
            { return new(Changed()); }
            StaffStationLabel? label = null;
            if (session.Actor is { } actor)
            {
                var labels = services.GetRequiredService<IStaffStationLabelReader>();
                Guid[] ids = [actor.StaffMemberId];
                var first = await labels.ResolveAsync(device.ScopeId, device.PropertyId, ids,
                    propertyAdmission.PropertyLocalDate!.Value, cancellationToken).ConfigureAwait(false);
                var second = await labels.ResolveAsync(device.ScopeId, device.PropertyId, ids,
                    propertyAdmission.PropertyLocalDate.Value, cancellationToken).ConfigureAwait(false);
                if (first.Count != 1 || !first.SequenceEqual(second) || first[0].StaffMemberId != actor.StaffMemberId ||
                    first[0].Version < 1 || !DisplayLabel(first[0].DisplayName, 300))
                { return new(Changed()); }
                label = first[0];
            }
            if (property != await properties.FindAsync(device.ScopeId, device.PropertyId, cancellationToken).ConfigureAwait(false) ||
                propertyAdmission != await admission.ObservePropertyAsync(device.PropertyId, cancellationToken).ConfigureAwait(false) ||
                current != await this.CurrentAsync(services, device, store, opaqueCredential, cancellationToken).ConfigureAwait(false))
            { return new(Changed()); }
            return new(current, property.Name, label?.DisplayName);
        }, () => new(Invalid()), () => new(new(StationSessionState.Unavailable)), state => new(new(state)), cancellationToken);

    private static bool DisplayLabel(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximum && !value.Any(char.IsControl);

    /// <summary>One deliberate PIN attempt. Reusing its operation ID resolves that attempt, not a corrected PIN.</summary>
    public Task<StationRuntimeResponse> UnlockAsync(string opaqueCredential, Guid operationId, Guid staffId,
        long expectedGeneration, string pin, CancellationToken cancellationToken = default) =>
        this.WithDeviceAsync(opaqueCredential, async (services, device, store) =>
        {
            if (operationId == Guid.Empty || staffId == Guid.Empty || expectedGeneration < 1)
            {
                return Changed();
            }
            StationRuntimeFacts? facts = await store.ReadAsync(device, opaqueCredential, staffId, clock.UtcNow, cancellationToken).ConfigureAwait(false);
            if (facts is null)
            {
                return Invalid();
            }
            if (!facts.Registered || facts.RegistrationRevision < 1)
            { return Changed(); }
            StationCredentialFacts? credential = facts.Credential;
            var binding = credential?.Enrollment ?? new StationEnrollmentBinding(StationActorKind.Unknown, null);
            bool denied = credential is null || credential.Revoked;
            if (!denied)
            {
                if (!binding.IsBound)
                {
                    return Changed();
                }
                StationAdmission admission = await services.GetRequiredService<StationAdmissionCoordinator>()
                    .ObserveIdentityAsync(device.PropertyId, staffId, binding, cancellationToken).ConfigureAwait(false);
                if (admission.State is StationAdmissionState.StateChanged or StationAdmissionState.Unavailable)
                {
                    return FromAdmission(admission);
                }
                denied = admission.State != StationAdmissionState.Current;
                if (facts != await store.ReadAsync(device, opaqueCredential, staffId, clock.UtcNow, cancellationToken).ConfigureAwait(false))
                {
                    return Changed();
                }
            }
            StationCoreResult result = await services.GetRequiredService<IStationsStore>().TryUnlockCoreAsync(operationId,
                device.StationId, device.BrowserSessionId, staffId, expectedGeneration, credential?.Revision ?? 0,
                (StationAuthorityKind)binding.Kind, facts.RegistrationRevision,
                pin, clock.UtcNow, binding, denied, opaqueCredential, cancellationToken).ConfigureAwait(false);
            return await this.ResultAsync(services, device, store, opaqueCredential, result, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task<StationRuntimeResponse> LockAsync(string opaqueCredential, Guid operationId, long expectedGeneration,
        CancellationToken cancellationToken = default) =>
        this.WithDeviceAsync(opaqueCredential, async (services, device, store) =>
        {
            if (operationId == Guid.Empty || expectedGeneration < 1)
            {
                return Changed();
            }
            StationCoreResult result = await services.GetRequiredService<IStationsStore>().LockCoreAsync(operationId,
                device.StationId, device.BrowserSessionId, expectedGeneration, clock.UtcNow, opaqueCredential, cancellationToken).ConfigureAwait(false);
            return await this.ResultAsync(services, device, store, opaqueCredential, result, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>Explicit, bounded foreground activity. Snapshot/background reads never call this mutation.</summary>
    public Task<StationRuntimeResponse> ForegroundActivityAsync(string opaqueCredential, Guid operationId,
        StationActorCoordinate actor, CancellationToken cancellationToken = default) =>
        this.WithDeviceAsync(opaqueCredential, async (services, device, store) =>
        {
            if (operationId == Guid.Empty)
            {
                return Changed();
            }
            StationRuntimeFacts? facts = await store.ReadAsync(device, opaqueCredential, null, clock.UtcNow, cancellationToken).ConfigureAwait(false);
            if (facts is null)
            {
                return Invalid();
            }
            if (!facts.ActorCurrent || !facts.Registered || facts.RegistrationRevision < 1 || facts.Session.Actor != actor || facts.Credential is not { Enrollment.IsBound: true } credential)
            {
                return Changed();
            }
            StationAdmission admission = await services.GetRequiredService<StationAdmissionCoordinator>()
                .ObserveIdentityAsync(device.PropertyId, actor.StaffMemberId, credential.Enrollment, cancellationToken).ConfigureAwait(false);
            if (admission.State != StationAdmissionState.Current)
            {
                return FromAdmission(admission);
            }
            if (facts != await store.ReadAsync(device, opaqueCredential, null, clock.UtcNow, cancellationToken).ConfigureAwait(false))
            {
                return Changed();
            }
            StationCoreResult result = await store.ForegroundActivityAsync(operationId, device, opaqueCredential, actor,
                credential, facts.RegistrationRevision, clock.UtcNow, cancellationToken).ConfigureAwait(false);
            return await this.ResultAsync(services, device, store, opaqueCredential, result, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>
    /// Private station-only setup. The old method name remains for source callers; unknown/legacy issuer is denied.
    /// An operation identifies one attempt; replay returns its outcome, never derives or replaces the PIN again.
    /// </summary>
    public Task<StationRuntimeResponse> RedeemSeededSetupAsync(string opaqueCredential, Guid operationId, Guid setupId,
        string pin, CancellationToken cancellationToken = default) =>
        this.RedeemAsync(opaqueCredential, operationId, setupId, pin, cancellationToken);

    private Task<StationRuntimeResponse> RedeemAsync(string opaqueCredential, Guid operationId, Guid setupId,
        string pin, CancellationToken cancellationToken) =>
        this.WithDeviceAsync(opaqueCredential, async (services, device, store) =>
        {
            if (operationId == Guid.Empty || setupId == Guid.Empty)
            {
                return Changed();
            }
            StationSetupFacts? setup = await store.ReadSetupAsync(device, setupId, clock.UtcNow, cancellationToken).ConfigureAwait(false);
            if (setup is null || !setup.Enrollment.IsBound || setup.Intent != setup.Enrollment.Kind ||
                setup.Intent != StationActorKind.StationOnly || setup.IssuerKind != StationIssuerKind.Manager)
            {
                return Changed();
            }
            var primary = services.GetRequiredService<StationPrimaryAdmission>();
            StationPrimaryObservation issuer = await primary.RevalidateIssuerAsync(setup, device.PropertyId, cancellationToken).ConfigureAwait(false);
            if (issuer.State != StationAdmissionState.Current)
            { return FromAdmission(new(issuer.State)); }
            StationAdmission admission = await services.GetRequiredService<StationAdmissionCoordinator>()
                .ObserveIdentityAsync(device.PropertyId, setup.StaffMemberId, setup.Enrollment, cancellationToken).ConfigureAwait(false);
            if (admission.State != StationAdmissionState.Current)
            {
                return FromAdmission(admission);
            }
            if (setup != await store.ReadSetupAsync(device, setupId, clock.UtcNow, cancellationToken).ConfigureAwait(false))
            {
                return Changed();
            }
            StationCoreResult? result = await store.ReadSetupOutcomeAsync(operationId, setup, device, cancellationToken).ConfigureAwait(false);
            if (result is null)
            {
                StationPinMaterial? material = await services.GetRequiredService<IStationPinVerifier>().CreateAsync(pin, cancellationToken).ConfigureAwait(false);
                if (material is null)
                {
                    return new(StationSessionState.Unavailable);
                }
                // KDF work is intentionally outside the mutation; re-observe pinned owners after that delay.
                StationAdmission afterKdf = await services.GetRequiredService<StationAdmissionCoordinator>()
                    .ObserveIdentityAsync(device.PropertyId, setup.StaffMemberId, setup.Enrollment, cancellationToken).ConfigureAwait(false);
                if (afterKdf.State != StationAdmissionState.Current)
                {
                    return FromAdmission(afterKdf);
                }
                StationPrimaryObservation issuerAfter = await primary.RevalidateIssuerAsync(setup, device.PropertyId, cancellationToken).ConfigureAwait(false);
                if (issuerAfter.State != StationAdmissionState.Current)
                { return FromAdmission(new(issuerAfter.State)); }
                if (afterKdf.PropertyLocalDate != admission.PropertyLocalDate ||
                    setup != await store.ReadSetupAsync(device, setupId, clock.UtcNow, cancellationToken).ConfigureAwait(false))
                {
                    return Changed();
                }
                result = await services.GetRequiredService<IStationsStore>().RedeemSetupCoreAsync(operationId, setupId,
                    device.BrowserSessionId, material, clock.UtcNow, setup.Enrollment, opaqueCredential, cancellationToken).ConfigureAwait(false);
            }
            return await this.ResultAsync(services, device, store, opaqueCredential, result, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task<StationRosterResponse> RosterAsync(string opaqueCredential, string? search = null, int page = 1,
        int pageSize = 25, CancellationToken cancellationToken = default) =>
        this.WithDeviceValueAsync<StationRosterResponse>(opaqueCredential, async (services, device, store) =>
        {
            if (page < 1 || page > 200 || pageSize is < 1 or > 50 || search?.Length > 100)
            { return new(StationSessionState.StateChanged, []); }
            var deviceBefore = await store.ReadAsync(device, opaqueCredential, null, clock.UtcNow, cancellationToken).ConfigureAwait(false);
            if (deviceBefore is null)
            { return new(StationSessionState.Invalid, []); }
            var owner = services.GetRequiredService<StationAdmissionCoordinator>();
            var property = await owner.ObservePropertyAsync(device.PropertyId, cancellationToken).ConfigureAwait(false);
            if (property.State != StationAdmissionState.Current)
            { return new(property.State == StationAdmissionState.Unavailable ? StationSessionState.Unavailable : StationSessionState.StateChanged, []); }
            var management = services.GetRequiredService<IStationManagementStore>();
            var registrations = await management.RegistrationsAsync(device.PropertyId, true, cancellationToken).ConfigureAwait(false);
            if (registrations.Count > 200)
            { return new(StationSessionState.Unavailable, []); }
            Guid[] ids = registrations.Where(r => r.Credential is { Revoked: false, Enrollment.IsBound: true } c &&
                c.Enrollment.IsBound)
                .Select(r => r.StaffMemberId).ToArray();
            var labels = services.GetRequiredService<IStaffStationLabelReader>();
            var before = await labels.ResolveAsync(device.ScopeId, device.PropertyId, ids, property.PropertyLocalDate!.Value, cancellationToken).ConfigureAwait(false);
            var after = await labels.ResolveAsync(device.ScopeId, device.PropertyId, ids, property.PropertyLocalDate.Value, cancellationToken).ConfigureAwait(false);
            var propertyAfter = await owner.ObservePropertyAsync(device.PropertyId, cancellationToken).ConfigureAwait(false);
            if (!before.SequenceEqual(after) || property != propertyAfter ||
                !registrations.SequenceEqual(await management.RegistrationsAsync(device.PropertyId, true, cancellationToken).ConfigureAwait(false)))
            { return new(StationSessionState.StateChanged, []); }
            var references = registrations.ToDictionary(r => r.StaffMemberId, r => r.RosterReference);
            StationRosterItem[] matches = before.Where(x => string.IsNullOrWhiteSpace(search) ||
                    x.DisplayName.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase))
                .Select(x => new StationRosterItem(x.StaffMemberId, x.DisplayName, references[x.StaffMemberId]))
                .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.RosterReference).ToArray();
            int offset = (page - 1) * pageSize;
            var deviceAfter = await store.ReadAsync(device, opaqueCredential, null, clock.UtcNow, cancellationToken).ConfigureAwait(false);
            if (deviceAfter is null)
            { return new(StationSessionState.Invalid, []); }
            if (deviceBefore != deviceAfter)
            { return new(StationSessionState.StateChanged, []); }
            return new(StationSessionState.Locked, matches.Skip(offset).Take(pageSize).ToArray(), page, pageSize, offset + pageSize < matches.Length);
        }, () => new(StationSessionState.Invalid, []), () => new(StationSessionState.Unavailable, []), state => new(state, []), cancellationToken);

    private async Task<StationRuntimeResponse> ResultAsync(IServiceProvider services, StationDeviceReference device,
        IStationRuntimeStore store, string opaqueCredential, StationCoreResult result, CancellationToken ct)
    {
        if (result.Outcome == StationCoreOutcome.Conflict)
        {
            return Changed() with { Outcome = result.Outcome };
        }
        if (result.Outcome == StationCoreOutcome.Unavailable)
        {
            return new(StationSessionState.Unavailable, Outcome: result.Outcome);
        }
        StationRuntimeResponse current = await this.CurrentAsync(services, device, store, opaqueCredential, ct).ConfigureAwait(false);
        // A late result for A must never return B's actor after a concurrent switch/lock/re-enrollment.
        if ((current.State == StationSessionState.Active && (result.Outcome != StationCoreOutcome.Applied ||
                current.Session!.Actor!.ActorSessionId != result.ActorSessionId)) ||
            (result.Outcome == StationCoreOutcome.Applied && result.Generation.HasValue &&
                current.Session is not null && current.Session.Generation != result.Generation))
        {
            return Changed() with { Outcome = StationCoreOutcome.Conflict };
        }
        return current with { Outcome = result.Outcome, RetryAfterUtc = result.RetryAfterUtc };
    }

    private async Task<StationRuntimeResponse> CurrentAsync(IServiceProvider services, StationDeviceReference device,
        IStationRuntimeStore store, string opaqueCredential, CancellationToken ct)
    {
        DateTimeOffset now = clock.UtcNow;
        StationRuntimeFacts? facts = await store.ReadAsync(device, opaqueCredential, null, now, ct).ConfigureAwait(false);
        if (facts is null)
        {
            return Invalid();
        }
        if (facts.Session.Actor is null || now >= facts.Session.ActorIdleExpiresAtUtc || now >= facts.Session.ActorAbsoluteExpiresAtUtc)
        {
            return new(StationSessionState.Locked, facts.Session with
            { Actor = null, ActorIdleExpiresAtUtc = null, ActorAbsoluteExpiresAtUtc = null });
        }
        if (!facts.ActorCurrent || !facts.Registered || facts.RegistrationRevision < 1 || facts.Credential is not { Enrollment.IsBound: true } credential)
        {
            return Changed();
        }
        StationAdmission admission = await services.GetRequiredService<StationAdmissionCoordinator>()
            .ObserveIdentityAsync(device.PropertyId, facts.Session.Actor.StaffMemberId, credential.Enrollment, ct).ConfigureAwait(false);
        if (admission.State != StationAdmissionState.Current)
        {
            return FromAdmission(admission);
        }
        StationRuntimeFacts? after = await store.ReadAsync(device, opaqueCredential, null, clock.UtcNow, ct).ConfigureAwait(false);
        return facts == after ? new(StationSessionState.Active, facts.Session) : Changed();
    }

    private Task<StationRuntimeResponse> WithDeviceAsync(string opaqueCredential,
        Func<IServiceProvider, StationDeviceReference, IStationRuntimeStore, Task<StationRuntimeResponse>> action, CancellationToken ct) =>
        this.WithDeviceValueAsync(opaqueCredential, action, Invalid, () => new(StationSessionState.Unavailable), state => new(state), ct);

    private async Task<T> WithDeviceValueAsync<T>(string opaqueCredential,
        Func<IServiceProvider, StationDeviceReference, IStationRuntimeStore, Task<T>> action, Func<T> invalid,
        Func<T> unavailable, Func<StationSessionState, T> halted, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (StationCredentialEncoding.Digest(opaqueCredential) is null)
        {
            return invalid();
        }
        try
        {
            StationDeviceReference? device = await bootstrap.FindAsync(opaqueCredential, ct).ConfigureAwait(false);
            if (device is null || !Guid.TryParseExact(device.ScopeId, "D", out Guid tenant) || tenant == Guid.Empty ||
                tenant.ToString("D") != device.ScopeId || device.BrowserSessionId == Guid.Empty || device.StationId == Guid.Empty || device.PropertyId == Guid.Empty)
            {
                return invalid();
            }
            await using AsyncServiceScope child = scopes.CreateAsyncScope();
            IServiceProvider services = child.ServiceProvider;
            services.GetRequiredService<IScopeContextAccessor>().SetScope(device.ScopeId);
            IStationRuntimeStore store = services.GetRequiredService<IStationRuntimeStore>();
            if (await store.ReadAsync(device, opaqueCredential, null, clock.UtcNow, ct).ConfigureAwait(false) is null)
            {
                return invalid();
            }
            StationSessionState? handoff = await services.GetRequiredService<StationHandoffAdmission>()
                .ObserveAsync(device, ct).ConfigureAwait(false);
            if (handoff is { } state)
            { return halted(state); }
            return await action(services, device, store).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return unavailable(); }
    }
    private static StationRuntimeResponse Invalid() => new(StationSessionState.Invalid);
    private static StationRuntimeResponse Changed() => new(StationSessionState.StateChanged);
    private static StationRuntimeResponse FromAdmission(StationAdmission admission) => admission.State == StationAdmissionState.Unavailable
        ? new(StationSessionState.Unavailable) : Changed();
}
