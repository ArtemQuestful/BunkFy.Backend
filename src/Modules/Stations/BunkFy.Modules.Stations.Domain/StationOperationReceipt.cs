namespace BunkFy.Modules.Stations.Domain;

using Gma.Framework.Domain;

public sealed class StationOperationReceipt : IScopedEntity
{
    private StationOperationReceipt() { }
    public StationOperationReceipt(string scopeId, Guid id, StationMutationKind kind, string fingerprint,
        StationMutationResult result, DateTimeOffset now, string? issuerSubjectId = null, StationIssuerKind issuerKind = StationIssuerKind.Unknown,
        Guid? issuerSessionId = null)
    {
        StationRules.Coordinates(scopeId, id);
        StationRules.Digest(fingerprint);
        StationRules.Utc(now);
        if (!Enum.IsDefined(kind) || kind == StationMutationKind.Unknown || result.Outcome == StationMutationOutcome.Unknown)
        {
            throw new ArgumentException("Invalid operation receipt.");
        }

        this.ScopeId = scopeId;
        this.Id = id;
        this.Kind = kind;
        this.Fingerprint = fingerprint;
        this.Outcome = result.Outcome;
        this.ActorSessionId = result.ActorSessionId;
        this.Generation = result.Generation;
        this.RetryAfterUtc = result.RetryAfterUtc;
        this.CreatedAtUtc = now;
        if (issuerSubjectId is not null)
        { StationRules.Coordinates(issuerSubjectId); }
        if ((issuerSubjectId is null) != (issuerKind == StationIssuerKind.Unknown) || !Enum.IsDefined(issuerKind))
        { throw new ArgumentException("Invalid receipt issuer."); }
        this.IssuerSubjectId = issuerSubjectId;
        this.IssuerKind = issuerKind;
        if (issuerSessionId == Guid.Empty || (issuerSessionId is not null &&
            (issuerKind == StationIssuerKind.Unknown || kind is not (StationMutationKind.Register or StationMutationKind.Pair or StationMutationKind.OwnPin))))
        { throw new ArgumentException("Invalid original pairing session."); }
        this.IssuerSessionId = issuerSessionId;
        this.StationId = result.Management?.StationId;
        this.BrowserSessionId = result.Management?.BrowserSessionId;
        this.PropertyId = result.Management?.PropertyId;
        this.StaffMemberId = result.Management?.StaffMemberId;
        this.SetupGrantId = result.Management?.SetupGrantId;
        this.ResourceVersion = result.Management?.Version;
        if (kind == StationMutationKind.OwnPin &&
            (issuerKind != StationIssuerKind.Self || issuerSessionId is null ||
             this.PropertyId is null || this.PropertyId == Guid.Empty || this.StaffMemberId is null || this.StaffMemberId == Guid.Empty ||
             this.ResourceVersion is null || this.ResourceVersion < 0 || (result.Outcome == StationMutationOutcome.Applied && this.ResourceVersion < 1)))
        { throw new ArgumentException("Own PIN recovery requires its original issuer session and exact resource coordinates."); }
    }
    public string ScopeId { get; private set; } = "";
    public Guid Id { get; private set; }
    public StationMutationKind Kind { get; private set; }
    public string Fingerprint { get; private set; } = "";
    public StationMutationOutcome Outcome { get; private set; }
    public Guid? ActorSessionId { get; private set; }
    public long? Generation { get; private set; }
    public DateTimeOffset? RetryAfterUtc { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public string? IssuerSubjectId { get; private set; }
    public StationIssuerKind IssuerKind { get; private set; }
    public Guid? IssuerSessionId { get; private set; }
    public Guid? StationId { get; private set; }
    public Guid? BrowserSessionId { get; private set; }
    public Guid? PropertyId { get; private set; }
    public Guid? StaffMemberId { get; private set; }
    public Guid? SetupGrantId { get; private set; }
    public long? ResourceVersion { get; private set; }
    public bool Matches(StationMutationKind kind, string fingerprint) => this.Kind == kind && this.Fingerprint == fingerprint;
    public StationMutationResult Result() => new(this.Outcome, this.ActorSessionId, this.Generation, this.RetryAfterUtc,
        this.IssuerKind == StationIssuerKind.Unknown ? null : new(this.StationId, this.BrowserSessionId, this.PropertyId,
            this.StaffMemberId, this.SetupGrantId, this.ResourceVersion, this.Kind, this.IssuerKind, this.IssuerSubjectId, this.IssuerSessionId));
}


public enum StationMutationKind { Unknown = 0, Register = 1, RedeemSetup = 2, Unlock = 3, Lock = 4, Reset = 5, RevokeGrant = 6, RevokeStation = 7, ForegroundActivity = 8, Pair = 9, RegisterStaff = 10, UnregisterStaff = 11, GrantCheckIn = 12, IssueSetup = 13, CancelSetup = 14, OwnPin = 15, GrantCheckOut = 16, RevokeCheckOutGrant = 17 }
public enum StationMutationOutcome { Unknown = 0, Applied = 1, Rejected = 2, Throttled = 3, Conflict = 4, Unavailable = 5 }
public sealed record StationMutationResult(StationMutationOutcome Outcome, Guid? ActorSessionId = null, long? Generation = null, DateTimeOffset? RetryAfterUtc = null, StationManagementCoordinates? Management = null);
public sealed record StationManagementCoordinates(Guid? StationId, Guid? BrowserSessionId, Guid? PropertyId,
    Guid? StaffMemberId, Guid? SetupGrantId, long? Version, StationMutationKind Kind = StationMutationKind.Unknown,
    StationIssuerKind IssuerKind = StationIssuerKind.Unknown, string? IssuerSubjectId = null, Guid? IssuerSessionId = null);
