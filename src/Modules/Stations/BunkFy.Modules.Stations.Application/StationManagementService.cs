namespace BunkFy.Modules.Stations.Application;

using System.Security.Claims;
using System.Security.Cryptography;
using BunkFy.Modules.Stations.Contracts;
using BunkFy.Modules.Stations.Domain;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Microsoft.Extensions.Options;

/// <summary>Unhosted owner operations. Only the future primary-auth adapter may supply the principal.</summary>
public sealed class StationManagementService(StationPrimaryAdmission primary,
    StationAdmissionCoordinator admission, IStationManagementStore store, IStationPinVerifier verifier,
    IOptions<StationOptions> options, ISystemClock clock)
{
    public async Task<StationPairingHandoff> ManageAsync(ClaimsPrincipal principal, Guid operationId,
        StationManagementCommand request, CancellationToken cancellationToken = default)
    {
        if (!ValidRequest(operationId, request))
        { return Handoff(StationManagementState.Denied); }
        bool global = request.Kind is StationOperationKind.IssueSetup or StationOperationKind.Reset;
        StationPrimaryObservation actor = await primary.ManagerAsync(principal, global ? null : request.PropertyId,
            true, cancellationToken).ConfigureAwait(false);
        if (actor.State != StationAdmissionState.Current)
        { return Handoff(Map(actor.State)); }
        StationAdmission property = await admission.ObservePropertyAsync(request.PropertyId, cancellationToken).ConfigureAwait(false);
        if (property.State != StationAdmissionState.Current)
        { return Handoff(Map(property.State)); }
        // Ignore caller-supplied binding/expiry. They are owner-observed and server-clock-derived only.
        request = request with { Enrollment = null, SetupExpiresAtUtc = null };
        if (request.Kind is StationOperationKind.RegisterStaff or StationOperationKind.GrantCheckIn or StationOperationKind.GrantCheckOut or StationOperationKind.IssueSetup)
        {
            var target = await admission.ObserveRegistrationEnrollmentAsync(request.PropertyId, request.StaffMemberId!.Value, cancellationToken).ConfigureAwait(false);
            if (target.Admission.State != StationAdmissionState.Current)
            { return Handoff(Map(target.Admission.State)); }
            if (request.Kind is StationOperationKind.GrantCheckIn or StationOperationKind.GrantCheckOut or StationOperationKind.IssueSetup && target.Enrollment?.Kind != StationActorKind.StationOnly)
            { return Handoff(StationManagementState.Denied); }
            request = request with { Enrollment = target.Enrollment };
        }
        if (request.Kind == StationOperationKind.IssueSetup)
        {
            request = request with { SetupExpiresAtUtc = this.SetupExpiry(actor.Issuer!) };
        }
        return await this.Write(operationId, actor.Issuer!, request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Tenant-wide own PIN, primary-only. Never pairs, unlocks, registers, or grants authority.</summary>
    public async Task<StationManagementResponse> SetOwnPinAsync(ClaimsPrincipal principal, Guid propertyId,
        Guid staffId, long expectedCredentialRevision, Guid operationId, string pin, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty || propertyId == Guid.Empty || staffId == Guid.Empty || expectedCredentialRevision < 0)
        { return new(StationManagementState.Denied); }
        try
        {
            var before = await this.OwnContext(principal, propertyId, staffId, true, cancellationToken).ConfigureAwait(false);
            if (before.State != StationManagementState.Applied)
            { return new(before.State); }
            var issuer = before.Issuer!;
            var binding = new StationEnrollmentBinding(StationActorKind.LinkedStation, issuer.SubjectId);
            StationCoreResult? prior = await store.OwnPinOutcomeAsync(operationId, issuer, propertyId, staffId,
                expectedCredentialRevision, binding, cancellationToken).ConfigureAwait(false);
            if (prior is not null)
            { return FromCore(prior); }
            if ((before.Facts!.Credential?.Revision ?? 0) != expectedCredentialRevision)
            { return new(StationManagementState.StateChanged); }
            StationPinMaterial? material = await verifier.CreateAsync(pin, cancellationToken).ConfigureAwait(false);
            if (material is null)
            { return new(StationManagementState.Unavailable); }
            var after = await this.OwnContext(principal, propertyId, staffId, true, cancellationToken).ConfigureAwait(false);
            if (after.State != StationManagementState.Applied)
            { return new(after.State); }
            if (before != after)
            { return new(StationManagementState.StateChanged); }
            return FromCore(await store.SetOwnPinAsync(operationId, issuer, propertyId, staffId, expectedCredentialRevision,
                before.Facts.RegistrationVersion, binding, material, clock.UtcNow, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return new(StationManagementState.Unavailable); }
    }

    public async Task<StationOwnPinStatusResponse> OwnPinStatusAsync(ClaimsPrincipal principal, Guid propertyId,
        Guid staffId, CancellationToken cancellationToken = default)
    {
        try
        {
            var context = await this.OwnContext(principal, propertyId, staffId, false, cancellationToken).ConfigureAwait(false);
            if (context.State != StationManagementState.Applied)
            { return new(context.State); }
            var credential = context.Facts!.Credential;
            StationPinState pin = credential is null ? StationPinState.NotSet : credential.Revoked ? StationPinState.Revoked :
                credential.Enrollment.Kind != StationActorKind.LinkedStation || credential.Enrollment.AuthSubjectId != context.Issuer!.SubjectId
                    ? StationPinState.ReEnrollmentNeeded : StationPinState.Set;
            return new(StationManagementState.Applied, new(pin, credential?.Revision ?? 0));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return new(StationManagementState.Unavailable); }
    }

    private async Task<OwnPinContext> OwnContext(ClaimsPrincipal principal, Guid propertyId, Guid staffId, bool mutation, CancellationToken ct)
    {
        if (propertyId == Guid.Empty || staffId == Guid.Empty)
        { return new(StationManagementState.Denied); }
        StationPrimaryObservation actor = await primary.OwnOutcomeAsync(principal, propertyId, ct).ConfigureAwait(false);
        if (actor.State != StationAdmissionState.Current)
        { return new(Map(actor.State)); }
        if (mutation)
        {
            actor = await primary.SelfAsync(principal, actor.Issuer!.SubjectId, propertyId, ct).ConfigureAwait(false);
            if (actor.State != StationAdmissionState.Current)
            { return new(Map(actor.State)); }
        }
        StationAdmission owner = await admission.ObserveOwnPinEnrollmentAsync(propertyId, staffId, actor.Issuer!.SubjectId, ct).ConfigureAwait(false);
        if (owner.State != StationAdmissionState.Current)
        { return new(Map(owner.State)); }
        StationOwnPinFacts? facts = await store.OwnPinFactsAsync(propertyId, staffId, ct).ConfigureAwait(false);
        if (facts is null)
        { return new(StationManagementState.Denied); }
        var repeated = await admission.ObserveOwnPinEnrollmentAsync(propertyId, staffId, actor.Issuer.SubjectId, ct).ConfigureAwait(false);
        if (repeated.State != StationAdmissionState.Current)
        { return new(Map(repeated.State)); }
        if (owner != repeated || facts != await store.OwnPinFactsAsync(propertyId, staffId, ct).ConfigureAwait(false))
        { return new(StationManagementState.StateChanged); }
        return new(StationManagementState.Applied, actor.Issuer, owner.PropertyLocalDate, facts);
    }
    private sealed record OwnPinContext(StationManagementState State, StationIssuer? Issuer = null,
        DateOnly? LocalDate = null, StationOwnPinFacts? Facts = null);

    public async Task<StationListResponse> ListAsync(
        ClaimsPrincipal principal, Guid propertyId, int page = 1, int pageSize = 25, CancellationToken cancellationToken = default)
    {
        if (page < 1 || pageSize is < 1 or > 50 || (long)(page - 1) * pageSize > int.MaxValue)
        { return new(StationManagementState.Denied, []); }
        StationPrimaryObservation actor = await primary.ManagerAsync(principal, propertyId, false, cancellationToken).ConfigureAwait(false);
        if (actor.State != StationAdmissionState.Current)
        { return new(Map(actor.State), []); }
        StationAdmission property = await admission.ObservePropertyAsync(propertyId, cancellationToken).ConfigureAwait(false);
        if (property.State != StationAdmissionState.Current)
        { return new(Map(property.State), []); }
        try
        {
            var items = await store.ListStationsAsync(propertyId, (page - 1) * pageSize, pageSize, cancellationToken).ConfigureAwait(false);
            return new(StationManagementState.Applied, items.Take(pageSize).ToArray(), page, pageSize, items.Count > pageSize);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return new(StationManagementState.Unavailable, []); }
    }

    public async Task<(StationManagementState State, StationStaffManagementItem? Item)> StaffStatusAsync(
        ClaimsPrincipal principal, Guid propertyId, Guid staffId, CancellationToken cancellationToken = default)
    {
        StationPrimaryObservation actor = await primary.ManagerAsync(principal, propertyId, false, cancellationToken).ConfigureAwait(false);
        if (actor.State != StationAdmissionState.Current)
        { return (Map(actor.State), null); }
        StationAdmission property = await admission.ObservePropertyAsync(propertyId, cancellationToken).ConfigureAwait(false);
        if (property.State != StationAdmissionState.Current)
        { return (Map(property.State), null); }
        StationStaffManagementItem? item = await store.StaffStatusAsync(propertyId, staffId, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        if (item is not null)
        {
            var target = await admission.ObserveRegistrationEnrollmentAsync(propertyId, staffId, cancellationToken).ConfigureAwait(false);
            item = item with
            {
                CanIssueStationOnlySetup = item.Registered &&
                    target.Admission.State == StationAdmissionState.Current && target.Enrollment?.Kind == StationActorKind.StationOnly,
            };
        }
        return (StationManagementState.Applied, item);
    }

    public async Task<StationManagementResponse> OutcomeAsync(ClaimsPrincipal principal, Guid operationId,
        Guid propertyId, CancellationToken cancellationToken = default)
    {
        StationPrimaryObservation actor = await primary.OwnOutcomeAsync(principal, propertyId, cancellationToken).ConfigureAwait(false);
        if (actor.State != StationAdmissionState.Current)
        { return new(Map(actor.State)); }
        StationCoreResult? result = await store.ReadOutcomeAsync(operationId, actor.Issuer!.SubjectId, cancellationToken).ConfigureAwait(false);
        if (result?.Management is not { } receipt || receipt.PropertyId != propertyId)
        { return new(StationManagementState.NotFound); }
        if (receipt.IssuerKind == StationSetupIssuerKind.Manager)
        {
            actor = await primary.ManagerAsync(principal, receipt.Kind is StationOperationKind.IssueSetup or StationOperationKind.Reset ? null : propertyId,
                false, cancellationToken).ConfigureAwait(false);
            if (actor.State != StationAdmissionState.Current)
            { return new(Map(actor.State)); }
        }
        else if (receipt.IssuerKind == StationSetupIssuerKind.Self && receipt.StaffMemberId is { } staff)
        {
            var target = await this.OwnContext(principal, propertyId, staff, false, cancellationToken).ConfigureAwait(false);
            if (receipt.Kind != StationOperationKind.OwnPin || target.State != StationManagementState.Applied)
            { return new(target.State == StationManagementState.Applied ? StationManagementState.Denied : target.State); }
        }
        else
        { return new(StationManagementState.Denied); }
        StationAdmission property = await admission.ObservePropertyAsync(propertyId, cancellationToken).ConfigureAwait(false);
        if (property.State != StationAdmissionState.Current)
        { return new(Map(property.State)); }
        return FromCore(result);
    }

    private async Task<StationPairingHandoff> Write(Guid op, StationIssuer issuer, StationManagementCommand request, CancellationToken ct)
    {
        string? credential = null;
        if (request.Kind is StationOperationKind.Register or StationOperationKind.Pair)
        {
            byte[] bytes = RandomNumberGenerator.GetBytes(32);
            try
            { credential = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        StationManagementWrite written = await store.ExecuteAsync(op, issuer, request, clock.UtcNow,
            credential is null ? null : StationCredentialEncoding.Digest(credential), ct).ConfigureAwait(false);
        return new(FromCore(written.Result), written.Executed && written.Result.Outcome == StationCoreOutcome.Applied ? credential : null);
    }
    private DateTimeOffset SetupExpiry(StationIssuer issuer)
    {
        DateTimeOffset maximum = clock.UtcNow.AddMinutes(options.Value.SetupMinutes);
        return issuer.AssuranceExpiresAtUtc!.Value < maximum ? issuer.AssuranceExpiresAtUtc.Value : maximum;
    }
    private static bool ValidRequest(Guid op, StationManagementCommand r) => op != Guid.Empty && r.PropertyId != Guid.Empty &&
        r.ExpectedVersion >= 0 && (r.Kind switch
        {
            StationOperationKind.Register => r.Label is { Length: > 0 and <= 100 } && !string.IsNullOrWhiteSpace(r.Label),
            StationOperationKind.Pair or StationOperationKind.RevokeStation => r.StationId is { } s && s != Guid.Empty,
            StationOperationKind.RegisterStaff or StationOperationKind.UnregisterStaff or StationOperationKind.GrantCheckIn or
                StationOperationKind.RevokeGrant or StationOperationKind.GrantCheckOut or StationOperationKind.RevokeCheckOutGrant or StationOperationKind.Reset => r.StaffMemberId is { } m && m != Guid.Empty,
            StationOperationKind.IssueSetup => r.StaffMemberId is { } m && m != Guid.Empty && r.StationId is { } s && s != Guid.Empty && r.BrowserSessionId is { } b && b != Guid.Empty,
            StationOperationKind.CancelSetup => r.SetupGrantId is { } g && g != Guid.Empty,
            _ => false
        });
    internal static StationManagementResponse FromCore(StationCoreResult result) => new(result.Outcome switch
    {
        StationCoreOutcome.Applied => StationManagementState.Applied,
        StationCoreOutcome.Conflict => StationManagementState.StateChanged,
        StationCoreOutcome.Unavailable => StationManagementState.Unavailable,
        StationCoreOutcome.Throttled => StationManagementState.CapacityReached,
        _ => StationManagementState.Denied
    }, result.Management);
    private static StationManagementState Map(StationAdmissionState state) => state switch
    {
        StationAdmissionState.Unavailable => StationManagementState.Unavailable,
        StationAdmissionState.StateChanged => StationManagementState.StateChanged,
        _ => StationManagementState.Denied
    };
    private static StationPairingHandoff Handoff(StationManagementState state) => new(new(state), null);
}
