namespace BunkFy.Modules.Stations.Persistence;

using BunkFy.Modules.Stations.Application;
using BunkFy.Modules.Stations.Contracts;
using BunkFy.Modules.Stations.Domain;
using Microsoft.EntityFrameworkCore;

internal sealed partial class StationsStore
{
    public async Task<StationPairingHandoffFacts?> ReadHandoffAsync(StationDeviceReference device, CancellationToken cancellationToken = default)
    {
        if (device.ScopeId != db.CurrentScopeId || !await this.ManagementReadable(cancellationToken).ConfigureAwait(false))
        { return null; }
        var receipts = await db.OperationReceipts.AsNoTracking().Where(x => x.ScopeId == device.ScopeId &&
            x.StationId == device.StationId && x.BrowserSessionId == device.BrowserSessionId && x.PropertyId == device.PropertyId &&
            x.Outcome == StationMutationOutcome.Applied && (x.Kind == StationMutationKind.Register || x.Kind == StationMutationKind.Pair))
            .Take(2).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        if (receipts.Length != 1 || receipts[0].IssuerKind != StationIssuerKind.Manager ||
            receipts[0].IssuerSessionId is not { } session || session == Guid.Empty ||
            !Guid.TryParseExact(receipts[0].IssuerSubjectId, "D", out Guid issuer) || issuer == Guid.Empty ||
            receipts[0].IssuerSubjectId != issuer.ToString("D"))
        { return null; }
        return new(device, issuer.ToString("D"), session);
    }

    public async Task<StationOwnPinFacts?> OwnPinFactsAsync(Guid propertyId, Guid staffId, CancellationToken cancellationToken = default)
    {
        if (!await this.ManagementReadable(cancellationToken).ConfigureAwait(false))
        { return null; }
        var registration = await db.StaffRegistrations.AsNoTracking().SingleOrDefaultAsync(x => x.PropertyId == propertyId &&
            x.StaffMemberId == staffId && x.Active, cancellationToken).ConfigureAwait(false);
        if (registration is null)
        { return null; }
        var credential = await db.Credentials.AsNoTracking().SingleOrDefaultAsync(x => x.StaffMemberId == staffId, cancellationToken).ConfigureAwait(false);
        return new(registration.Version, credential is null ? null :
            new(credential.StaffMemberId, credential.Revision, credential.Revoked, credential.Enrollment()));
    }

    public async Task<StationCoreResult?> OwnPinOutcomeAsync(Guid operationId, StationIssuer issuer, Guid propertyId, Guid staffId,
        long expectedRevision, StationEnrollmentBinding binding, CancellationToken cancellationToken = default)
    {
        if (!await this.ManagementReadable(cancellationToken).ConfigureAwait(false))
        { return Rejected(); }
        var receipt = await db.OperationReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == operationId, cancellationToken).ConfigureAwait(false);
        if (receipt is null)
        { return null; }
        return receipt.IssuerKind == StationIssuerKind.Self && receipt.IssuerSubjectId == issuer.SubjectId &&
            receipt.Matches(StationMutationKind.OwnPin, OwnPinFingerprint(issuer, propertyId, staffId, expectedRevision, binding))
                ? ToCore(receipt.Result()) : Conflict();
    }

    public Task<StationCoreResult> SetOwnPinAsync(Guid operationId, StationIssuer issuer, Guid propertyId, Guid staffId,
        long expectedRevision, long registrationVersion, StationEnrollmentBinding binding, StationPinMaterial material,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (issuer.Kind != StationIssuerKind.Self || issuer.SessionId is null || issuer.SessionId == Guid.Empty ||
            issuer.AssuranceExpiresAtUtc is null || issuer.AssuranceExpiresAtUtc <= now ||
            binding.Kind != StationActorKind.LinkedStation || binding.AuthSubjectId != issuer.SubjectId)
        { return Task.FromResult(Rejected()); }
        return this.RunAsync(operationId, StationOperationKind.OwnPin,
            OwnPinFingerprint(issuer, propertyId, staffId, expectedRevision, binding), now, async () =>
            {
                if (!await db.StaffRegistrations.AnyAsync(x => x.PropertyId == propertyId && x.StaffMemberId == staffId &&
                    x.Active && x.Version == registrationVersion, cancellationToken).ConfigureAwait(false))
                { return Conflict(); }
                var credential = await db.Credentials.SingleOrDefaultAsync(x => x.StaffMemberId == staffId, cancellationToken).ConfigureAwait(false);
                var actors = await db.BrowserSessions.Where(x => x.StaffMemberId == staffId).ToArrayAsync(cancellationToken).ConfigureAwait(false);
                if ((credential?.Revision ?? 0) != expectedRevision || now < credential?.LastObservedAtUtc || actors.Any(x => now < x.LastObservedAtUtc))
                { return Conflict(); }
                if (credential is null)
                { credential = new(db.CurrentScopeId, staffId, material, now, binding); db.Credentials.Add(credential); }
                else
                { credential.Replace(material, now, binding); }
                foreach (var actor in actors)
                { actor.Lock(now); }
                foreach (var setup in await db.SetupGrants.Where(x => x.StaffMemberId == staffId && !x.Revoked && x.ConsumedAtUtc == null)
                    .ToArrayAsync(cancellationToken).ConfigureAwait(false))
                { setup.Revoke(); }
                return new(StationCoreOutcome.Applied, Management: new(PropertyId: propertyId, StaffMemberId: staffId, Version: credential.Revision));
            }, cancellationToken, issuer.SubjectId, issuer.Kind, issuer.SessionId,
            new(PropertyId: propertyId, StaffMemberId: staffId, Version: expectedRevision));
    }
    private static string OwnPinFingerprint(StationIssuer issuer, Guid property, Guid staff, long revision, StationEnrollmentBinding binding) =>
        Fingerprint(StationOperationKind.OwnPin, property, staff, revision, issuer.SubjectId, StationIssuerKind.Self, binding.Kind, binding.AuthSubjectId);

    public async Task<StationManagementWrite> ExecuteAsync(Guid operationId, StationIssuer issuer,
        StationManagementCommand command, DateTimeOffset now, string? newCredentialDigest = null,
        CancellationToken cancellationToken = default)
    {
        StationRules.Coordinates(issuer.SubjectId);
        if (issuer.SessionId is null || issuer.SessionId == Guid.Empty || issuer.Kind != StationIssuerKind.Manager ||
            issuer.AssuranceExpiresAtUtc <= now || issuer.AssuranceExpiresAtUtc is null)
        { return new(Rejected(), false); }
        bool executed = false;
        string fingerprint = Fingerprint(command.Kind, command.PropertyId, command.StationId, command.BrowserSessionId,
            command.StaffMemberId, command.SetupGrantId, command.ExpectedVersion, command.Label?.Trim(),
            command.Enrollment?.Kind, command.Enrollment?.AuthSubjectId, issuer.SubjectId, issuer.Kind);
        StationCoreResult result = await this.RunAsync(operationId, command.Kind, fingerprint, now, async () =>
        {
            executed = true;
            Guid property = command.PropertyId;
            StationRules.Coordinates(db.CurrentScopeId, property);
            if (command.Kind is StationOperationKind.Register or StationOperationKind.Pair)
            {
                if (newCredentialDigest is null)
                { return Rejected(); }
                StationRules.Digest(newCredentialDigest);
                Station? station;
                if (command.Kind == StationOperationKind.Register)
                {
                    station = new(Guid.NewGuid(), db.CurrentScopeId, property, command.Label ?? "");
                    db.Stations.Add(station);
                }
                else
                {
                    station = await db.Stations.SingleOrDefaultAsync(x => x.Id == command.StationId && x.PropertyId == property, cancellationToken).ConfigureAwait(false);
                    if (station is null || station.Revoked)
                    { return Rejected(); }
                    if (station.Version != command.ExpectedVersion)
                    { return Conflict(); }
                    var previous = await db.BrowserSessions.Where(x => x.StationId == station.Id && !x.Revoked).ToArrayAsync(cancellationToken).ConfigureAwait(false);
                    if (previous.Any(x => now < x.LastObservedAtUtc))
                    { return Conflict(); }
                    foreach (var browser in previous)
                    { browser.Revoke(now); }
                    await CancelPending(property, null, station.Id).ConfigureAwait(false);
                    station.RePair();
                }
                var paired = new StationBrowserSession(Guid.NewGuid(), db.CurrentScopeId, station.Id, property,
                    newCredentialDigest, now, now.AddDays(this.options.PairingDays), this.options.ExternalEpoch);
                db.BrowserSessions.Add(paired);
                return Applied(station.Id, paired.Id, version: station.Version);
            }

            if (command.Kind == StationOperationKind.RevokeStation)
            {
                var station = await db.Stations.SingleOrDefaultAsync(x => x.Id == command.StationId && x.PropertyId == property, cancellationToken).ConfigureAwait(false);
                if (station is null)
                { return Rejected(); }
                if (station.Version != command.ExpectedVersion)
                { return Conflict(); }
                var browsers = await db.BrowserSessions.Where(x => x.StationId == station.Id && !x.Revoked).ToArrayAsync(cancellationToken).ConfigureAwait(false);
                if (browsers.Any(x => now < x.LastObservedAtUtc))
                { return Conflict(); }
                station.Revoke();
                foreach (var browser in browsers)
                { browser.Revoke(now); }
                await CancelPending(property, null, station.Id).ConfigureAwait(false);
                return Applied(station.Id, version: station.Version);
            }
            if (command.Kind == StationOperationKind.CancelSetup)
            {
                var setup = await db.SetupGrants.SingleOrDefaultAsync(x => x.Id == command.SetupGrantId && x.PropertyId == property, cancellationToken).ConfigureAwait(false);
                if (setup is null)
                { return Rejected(); }
                if (now < setup.CreatedAtUtc || setup.ConsumedAtUtc is not null)
                { return Conflict(); }
                setup.Revoke();
                return Applied(setup.StationId, setup.BrowserSessionId, setup.StaffMemberId, setup.Id);
            }
            Guid staff = command.StaffMemberId ?? Guid.Empty;
            StationRules.Coordinates(db.CurrentScopeId, staff);
            if (command.Kind is StationOperationKind.RegisterStaff or StationOperationKind.UnregisterStaff)
            {
                var registration = await db.StaffRegistrations.SingleOrDefaultAsync(x => x.PropertyId == property && x.StaffMemberId == staff, cancellationToken).ConfigureAwait(false);
                if ((registration?.Version ?? 0) != command.ExpectedVersion)
                { return Conflict(); }
                if (command.Kind == StationOperationKind.RegisterStaff)
                {
                    if (registration?.Active != true && await db.StaffRegistrations.CountAsync(x => x.PropertyId == property && x.Active, cancellationToken).ConfigureAwait(false) >= 200)
                    { return new(StationCoreOutcome.Throttled); }
                    if (registration is null)
                    {
                        long reference = (await db.StaffRegistrations.Where(x => x.PropertyId == property)
                            .MaxAsync(x => (long?)x.RosterReference, cancellationToken).ConfigureAwait(false) ?? 0) + 1;
                        registration = new(db.CurrentScopeId, property, staff, reference);
                        db.StaffRegistrations.Add(registration);
                    }
                    else
                    { registration.SetActive(true); }
                }
                else
                {
                    if (registration is null)
                    { return Rejected(); }
                    var actors = await db.BrowserSessions.Where(x => x.PropertyId == property && x.StaffMemberId == staff).ToArrayAsync(cancellationToken).ConfigureAwait(false);
                    if (actors.Any(x => now < x.LastObservedAtUtc))
                    { return Conflict(); }
                    registration.SetActive(false);
                    foreach (var actor in actors)
                    { actor.Lock(now); }
                    await CancelPending(property, staff, null).ConfigureAwait(false);
                }
                return Applied(staffId: staff, version: registration.Version);
            }
            if (command.Kind == StationOperationKind.Reset)
            {
                var credential = await db.Credentials.SingleOrDefaultAsync(x => x.StaffMemberId == staff, cancellationToken).ConfigureAwait(false);
                if (credential is null)
                { return Rejected(); }
                if (credential.Revision != command.ExpectedVersion || now < credential.LastObservedAtUtc)
                { return Conflict(); }
                var actors = await db.BrowserSessions.Where(x => x.StaffMemberId == staff).ToArrayAsync(cancellationToken).ConfigureAwait(false);
                if (actors.Any(x => now < x.LastObservedAtUtc))
                { return Conflict(); }
                credential.Revoke(now);
                foreach (var actor in actors)
                { actor.Lock(now); }
                await CancelPending(null, staff, null).ConfigureAwait(false);
                return Applied(staffId: staff, version: credential.Revision);
            }
            if (command.Kind is StationOperationKind.GrantCheckIn or StationOperationKind.RevokeGrant)
            {
                var grant = await db.CheckInGrants.SingleOrDefaultAsync(x => x.PropertyId == property && x.StaffMemberId == staff, cancellationToken).ConfigureAwait(false);
                if ((grant?.Revision ?? 0) != command.ExpectedVersion)
                { return Conflict(); }
                if (command.Kind == StationOperationKind.GrantCheckIn)
                {
                    if (!await this.RegisteredAsync(property, staff, cancellationToken).ConfigureAwait(false) || command.Enrollment?.Kind != StationActorKind.StationOnly)
                    { return Rejected(); }
                    if (grant is null)
                    { grant = new(db.CurrentScopeId, property, staff); db.CheckInGrants.Add(grant); }
                    else
                    { grant.ReGrant(); }
                }
                else
                {
                    if (grant is null)
                    { return Rejected(); }
                    grant.Revoke();
                    if (!await db.CheckOutGrants.AnyAsync(x => x.PropertyId == property && x.StaffMemberId == staff && !x.Revoked, cancellationToken).ConfigureAwait(false))
                    { await CancelPending(property, staff, null).ConfigureAwait(false); }
                }
                return Applied(staffId: staff, version: grant.Revision);
            }
            if (command.Kind is StationOperationKind.GrantCheckOut or StationOperationKind.RevokeCheckOutGrant)
            {
                var grant = await db.CheckOutGrants.SingleOrDefaultAsync(x => x.PropertyId == property && x.StaffMemberId == staff, cancellationToken).ConfigureAwait(false);
                if ((grant?.Revision ?? 0) != command.ExpectedVersion)
                { return Conflict(); }
                if (command.Kind == StationOperationKind.GrantCheckOut)
                {
                    if (!await this.RegisteredAsync(property, staff, cancellationToken).ConfigureAwait(false) || command.Enrollment?.Kind != StationActorKind.StationOnly)
                    { return Rejected(); }
                    if (grant is null)
                    { grant = new(db.CurrentScopeId, property, staff); db.CheckOutGrants.Add(grant); }
                    else
                    { grant.ReGrant(); }
                }
                else
                {
                    if (grant is null)
                    { return Rejected(); }
                    grant.Revoke();
                    if (!await db.CheckInGrants.AnyAsync(x => x.PropertyId == property && x.StaffMemberId == staff && !x.Revoked, cancellationToken).ConfigureAwait(false))
                    { await CancelPending(property, staff, null).ConfigureAwait(false); }
                }
                return Applied(staffId: staff, version: grant.Revision);
            }
            if (command.Kind == StationOperationKind.IssueSetup)
            {
                var browser = await db.BrowserSessions.SingleOrDefaultAsync(x => x.Id == command.BrowserSessionId && x.StationId == command.StationId && x.PropertyId == property, cancellationToken).ConfigureAwait(false);
                if (browser is null || !browser.PairingCurrent(now, this.options.ExternalEpoch) ||
                    !await db.Stations.AnyAsync(x => x.Id == browser.StationId && !x.Revoked, cancellationToken).ConfigureAwait(false) ||
                    !await this.RegisteredAsync(property, staff, cancellationToken).ConfigureAwait(false))
                { return Rejected(); }
                var credential = await db.Credentials.SingleOrDefaultAsync(x => x.StaffMemberId == staff, cancellationToken).ConfigureAwait(false);
                if ((credential?.Revision ?? 0) != command.ExpectedVersion || now < credential?.LastObservedAtUtc)
                { return Conflict(); }
                if (command.Enrollment is not { Kind: StationActorKind.StationOnly } binding || command.SetupExpiresAtUtc is not { } expires || expires <= now ||
                    expires > issuer.AssuranceExpiresAtUtc || (binding.Kind == StationActorKind.StationOnly &&
                        !await db.CheckInGrants.AnyAsync(x => x.PropertyId == property && x.StaffMemberId == staff && !x.Revoked, cancellationToken).ConfigureAwait(false) &&
                        !await db.CheckOutGrants.AnyAsync(x => x.PropertyId == property && x.StaffMemberId == staff && !x.Revoked, cancellationToken).ConfigureAwait(false)))
                { return Rejected(); }
                await CancelPending(null, staff, null).ConfigureAwait(false);
                var setup = new StationSetupGrant(Guid.NewGuid(), db.CurrentScopeId, browser.StationId, browser.Id, property,
                    staff, binding.Kind, command.ExpectedVersion, now, expires, binding, issuer.Kind, issuer.SubjectId, issuer.AssuranceExpiresAtUtc);
                db.SetupGrants.Add(setup);
                return Applied(browser.StationId, browser.Id, staff, setup.Id, command.ExpectedVersion);
            }
            return Rejected();

            StationCoreResult Applied(Guid? stationId = null, Guid? browserId = null, Guid? staffId = null,
                Guid? setupId = null, long? version = null) => new(StationCoreOutcome.Applied,
                    Management: new(stationId, browserId, property, staffId, setupId, version));
            async Task CancelPending(Guid? targetProperty, Guid? targetStaff, Guid? targetStation)
            {
                var grants = await db.SetupGrants.Where(x => !x.Revoked && x.ConsumedAtUtc == null &&
                    (targetProperty == null || x.PropertyId == targetProperty) && (targetStaff == null || x.StaffMemberId == targetStaff) &&
                    (targetStation == null || x.StationId == targetStation)).ToArrayAsync(cancellationToken).ConfigureAwait(false);
                foreach (var grant in grants)
                { grant.Revoke(); }
            }
        }, cancellationToken, issuer.SubjectId, issuer.Kind,
            command.Kind is StationOperationKind.Register or StationOperationKind.Pair ? issuer.SessionId : null,
            new(command.StationId, command.BrowserSessionId, command.PropertyId, command.StaffMemberId, command.SetupGrantId, command.ExpectedVersion)).ConfigureAwait(false);
        return new(result, executed);
    }

    public async Task<StationCoreResult?> ReadOutcomeAsync(Guid operationId, string issuerSubjectId, CancellationToken cancellationToken = default)
    {
        if (!await this.ManagementReadable(cancellationToken).ConfigureAwait(false))
        { return null; }
        var receipt = await db.OperationReceipts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == operationId &&
            x.IssuerSubjectId == issuerSubjectId && x.IssuerKind != StationIssuerKind.Unknown, cancellationToken).ConfigureAwait(false);
        return receipt is null ? null : ToCore(receipt.Result());
    }
    public async Task<IReadOnlyList<StationListItem>> ListStationsAsync(Guid propertyId, int offset, int pageSize, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 50);
        if (!await this.ManagementReadable(cancellationToken).ConfigureAwait(false))
        { return []; }
        return await db.Stations.AsNoTracking().Where(x => x.PropertyId == propertyId).OrderBy(x => x.Label).ThenBy(x => x.Id)
            .Select(x => new StationListItem(x.Id, x.PropertyId, x.Label, x.Version, x.Revoked))
            .Skip(offset).Take(pageSize + 1).ToArrayAsync(cancellationToken).ConfigureAwait(false);
    }
    public async Task<StationListItem?> FindStationAsync(Guid stationId, CancellationToken cancellationToken = default)
    {
        if (!await this.ManagementReadable(cancellationToken).ConfigureAwait(false))
        { return null; }
        return await db.Stations.AsNoTracking().Where(x => x.Id == stationId)
            .Select(x => new StationListItem(x.Id, x.PropertyId, x.Label, x.Version, x.Revoked)).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }
    public async Task<IReadOnlyList<StationRegistrationFacts>> RegistrationsAsync(Guid propertyId, bool activeOnly, CancellationToken cancellationToken = default)
    {
        if (!await this.ManagementReadable(cancellationToken).ConfigureAwait(false))
        { return []; }
        return await (from registration in db.StaffRegistrations.AsNoTracking()
                      where registration.PropertyId == propertyId && (!activeOnly || registration.Active)
                      join credential in db.Credentials.AsNoTracking() on registration.StaffMemberId equals credential.StaffMemberId into credentials
                      from credential in credentials.DefaultIfEmpty()
                      join grant in db.CheckInGrants.AsNoTracking().Where(x => x.PropertyId == propertyId)
                          on registration.StaffMemberId equals grant.StaffMemberId into grants
                      from grant in grants.DefaultIfEmpty()
                      orderby registration.RosterReference
                      select new StationRegistrationFacts(registration.StaffMemberId, registration.Version, registration.RosterReference,
                          registration.Active, credential == null ? null : new StationCredentialFacts(credential.StaffMemberId,
                              credential.Revision, credential.Revoked, new StationEnrollmentBinding(credential.EnrollmentAuthorityKind, credential.EnrollmentAuthSubjectId)),
                          grant == null ? null : grant.Revision, grant == null || grant.Revoked))
            .Take(201).ToArrayAsync(cancellationToken).ConfigureAwait(false);
    }
    public async Task<StationStaffManagementItem?> StaffStatusAsync(Guid propertyId, Guid staffId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        if (!await this.ManagementReadable(cancellationToken).ConfigureAwait(false))
        { return null; }
        var registration = await db.StaffRegistrations.AsNoTracking().SingleOrDefaultAsync(x => x.PropertyId == propertyId && x.StaffMemberId == staffId, cancellationToken).ConfigureAwait(false);
        if (registration is null)
        { return null; }
        var credential = await db.Credentials.AsNoTracking().SingleOrDefaultAsync(x => x.StaffMemberId == staffId, cancellationToken).ConfigureAwait(false);
        var grant = await db.CheckInGrants.AsNoTracking().SingleOrDefaultAsync(x => x.PropertyId == propertyId && x.StaffMemberId == staffId, cancellationToken).ConfigureAwait(false);
        var checkOutGrant = await db.CheckOutGrants.AsNoTracking().SingleOrDefaultAsync(x => x.PropertyId == propertyId && x.StaffMemberId == staffId, cancellationToken).ConfigureAwait(false);
        var setup = await db.SetupGrants.AsNoTracking().Where(x => x.PropertyId == propertyId && x.StaffMemberId == staffId)
            .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return new(staffId, registration.RosterReference, registration.Active, registration.Version,
            credential is null ? StationPinState.NotSet : credential.Revoked ? StationPinState.Revoked : !credential.Enrollment().IsBound ? StationPinState.ReEnrollmentNeeded : StationPinState.Set,
            grant is not null, grant?.Revoked ?? false, grant?.Revision,
            setup is null ? StationSetupState.None : setup.ConsumedAtUtc is not null ? StationSetupState.Consumed : setup.Revoked ? StationSetupState.Cancelled : now >= setup.ExpiresAtUtc ? StationSetupState.Expired : StationSetupState.Pending, setup?.Id,
            credential?.Revision ?? 0, CheckOutGrantPresent: checkOutGrant is not null,
            CheckOutGrantRevoked: checkOutGrant?.Revoked ?? false, CheckOutGrantRevision: checkOutGrant?.Revision);
    }
    public Task<StationSetupFacts?> FindSetupAsync(Guid setupId, StationDeviceReference device, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        this.ReadSetupAsync(device, setupId, now, cancellationToken);
    private async Task<bool> ManagementReadable(CancellationToken ct)
    {
        db.RequirePostgreSql();
        return db.ScopeFilterEnabled && !await db.Lifecycle.AsNoTracking().AnyAsync(x => x.Closed, ct).ConfigureAwait(false);
    }
}
