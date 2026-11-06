namespace BunkFy.Modules.Stations.Domain;

using System.Text.Json.Serialization;
using Gma.Framework.Domain;

public sealed class StationBrowserSession : IScopedEntity
{
    private StationBrowserSession() { }
    public StationBrowserSession(Guid id, string scopeId, Guid stationId, Guid propertyId,
        string credentialDigest, DateTimeOffset issuedAtUtc, DateTimeOffset expiresAtUtc, long externalEpoch)
    {
        StationRules.Coordinates(scopeId, id, stationId, propertyId);
        StationRules.Digest(credentialDigest);
        StationRules.Utc(issuedAtUtc);
        StationRules.Utc(expiresAtUtc);
        if (expiresAtUtc <= issuedAtUtc || expiresAtUtc - issuedAtUtc > TimeSpan.FromDays(30) || externalEpoch < 1)
        {
            throw new ArgumentException("Invalid device lifetime or epoch.");
        }

        this.Id = id;
        this.ScopeId = scopeId;
        this.StationId = stationId;
        this.PropertyId = propertyId;
        this.CredentialDigest = credentialDigest;
        this.IssuedAtUtc = issuedAtUtc;
        this.PairingExpiresAtUtc = expiresAtUtc;
        this.LastObservedAtUtc = issuedAtUtc;
        this.ExternalEpoch = externalEpoch;
    }
    public Guid Id { get; private set; }
    public string ScopeId { get; private set; } = "";
    public Guid StationId { get; private set; }
    public Guid PropertyId { get; private set; }
    [JsonIgnore] public string CredentialDigest { get; private set; } = "";
    public long ExternalEpoch { get; private set; }
    public DateTimeOffset IssuedAtUtc { get; private set; }
    public DateTimeOffset PairingExpiresAtUtc { get; private set; }
    public DateTimeOffset LastObservedAtUtc { get; private set; }
    public bool Revoked { get; private set; }
    public long Generation { get; private set; } = 1;
    public Guid? StaffMemberId { get; private set; }
    public Guid? ActorSessionId { get; private set; }
    public StationActorKind AuthorityKind { get; private set; }
    public long? CredentialRevision { get; private set; }
    public long? GrantRevision { get; private set; }
    public long? RegistrationRevision { get; private set; }
    public DateTimeOffset? ActorIdleExpiresAtUtc { get; private set; }
    public DateTimeOffset? ActorAbsoluteExpiresAtUtc { get; private set; }
    public DateTimeOffset? AttemptWindowStartedAtUtc { get; private set; }
    public int AttemptCount { get; private set; }
    public DateTimeOffset? CooldownUntilUtc { get; private set; }

    public bool PairingCurrent(DateTimeOffset now, long epoch) => !this.Revoked && now >= this.LastObservedAtUtc &&
        now < this.PairingExpiresAtUtc && this.ExternalEpoch == epoch;
    public bool ActorCurrent(DateTimeOffset now, long epoch) => this.PairingCurrent(now, epoch) &&
        this.StaffMemberId.HasValue && this.ActorSessionId.HasValue &&
        now < this.ActorIdleExpiresAtUtc && now < this.ActorAbsoluteExpiresAtUtc;

    public bool ReserveAttempt(DateTimeOffset now, int maximum, TimeSpan window, TimeSpan cooldown)
    {
        StationRules.Utc(now);
        if (now < this.LastObservedAtUtc || now < this.CooldownUntilUtc)
        {
            return false;
        }

        this.LastObservedAtUtc = now;
        if (this.AttemptWindowStartedAtUtc is null || now >= this.AttemptWindowStartedAtUtc.Value + window)
        { this.AttemptWindowStartedAtUtc = now; this.AttemptCount = 0; this.CooldownUntilUtc = null; }
        this.AttemptCount = checked(this.AttemptCount + 1);
        if (this.AttemptCount >= maximum)
        {
            this.CooldownUntilUtc = now + cooldown;
        }

        return true;
    }
    public bool CanAttempt(DateTimeOffset now) => now >= this.LastObservedAtUtc && !(now < this.CooldownUntilUtc);
    public bool RecordForegroundActivity(Guid actorId, long generation, DateTimeOffset now, TimeSpan idle)
    {
        if (idle <= TimeSpan.Zero || idle > TimeSpan.FromMinutes(30) || actorId != this.ActorSessionId ||
            generation != this.Generation || !this.ActorCurrent(now, this.ExternalEpoch))
        {
            return false;
        }

        DateTimeOffset proposed = now + idle;
        DateTimeOffset cap = this.ActorAbsoluteExpiresAtUtc!.Value < this.PairingExpiresAtUtc
            ? this.ActorAbsoluteExpiresAtUtc.Value : this.PairingExpiresAtUtc;
        this.ActorIdleExpiresAtUtc = proposed < cap ? proposed : cap;
        this.LastObservedAtUtc = now;
        return true;
    }
    public void Activate(Guid staffId, Guid actorId, StationActorKind kind, long credentialRevision,
        long? registrationRevision, DateTimeOffset now, TimeSpan idle, TimeSpan absolute)
    {
        StationRules.Coordinates(this.ScopeId, staffId, actorId);
        if (!this.PairingCurrent(now, this.ExternalEpoch) || credentialRevision < 1 ||
            kind is not (StationActorKind.LinkedStation or StationActorKind.StationOnly) ||
            registrationRevision is not > 0 ||
            idle <= TimeSpan.Zero || absolute < idle)
        {
            throw new InvalidOperationException("Invalid actor activation.");
        }

        this.Generation = checked(this.Generation + 1);
        this.StaffMemberId = staffId;
        this.ActorSessionId = actorId;
        this.AuthorityKind = kind;
        this.CredentialRevision = credentialRevision;
        this.GrantRevision = null;
        this.RegistrationRevision = registrationRevision;
        this.LastObservedAtUtc = now;
        this.ActorAbsoluteExpiresAtUtc = now + absolute < this.PairingExpiresAtUtc ? now + absolute : this.PairingExpiresAtUtc;
        this.ActorIdleExpiresAtUtc = now + idle < this.ActorAbsoluteExpiresAtUtc ? now + idle : this.ActorAbsoluteExpiresAtUtc;
    }
    public void Lock(DateTimeOffset now)
    {
        StationRules.Utc(now);
        if (now < this.LastObservedAtUtc)
        {
            throw new InvalidOperationException("Clock moved backwards.");
        }

        this.Generation = checked(this.Generation + 1);
        this.StaffMemberId = null;
        this.ActorSessionId = null;
        this.AuthorityKind = StationActorKind.Unknown;
        this.CredentialRevision = null;
        this.GrantRevision = null;
        this.RegistrationRevision = null;
        this.ActorIdleExpiresAtUtc = null;
        this.ActorAbsoluteExpiresAtUtc = null;
        this.LastObservedAtUtc = now;
    }
    public void Revoke(DateTimeOffset now) { this.Lock(now); this.Revoked = true; }
}
