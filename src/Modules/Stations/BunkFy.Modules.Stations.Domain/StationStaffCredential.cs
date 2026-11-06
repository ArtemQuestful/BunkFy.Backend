namespace BunkFy.Modules.Stations.Domain;

using System.Text.Json.Serialization;
using Gma.Framework.Domain;

public sealed class StationPinMaterial
{
    public const int Iterations = 600_000;
    public const int AlgorithmVersion = 1;
    public StationPinMaterial(string salt, string verifier, string pepperVersion)
    {
        if (Convert.FromBase64String(salt).Length < 16 || Convert.FromBase64String(verifier).Length != 32 ||
            string.IsNullOrWhiteSpace(pepperVersion) || pepperVersion.Length > 64)
        {
            throw new ArgumentException("Invalid PIN verifier material.");
        }

        this.Salt = salt;
        this.Verifier = verifier;
        this.PepperVersion = pepperVersion;
    }
    [JsonIgnore] public string Salt { get; }
    [JsonIgnore] public string Verifier { get; }
    [JsonIgnore] public string PepperVersion { get; }
    public override string ToString() => "[protected station verifier]";
}

public sealed class StationStaffCredential : IScopedEntity
{
    private StationStaffCredential() { }
    public StationStaffCredential(string scopeId, Guid staffId, StationPinMaterial material, DateTimeOffset now,
        StationEnrollmentBinding? enrollment = null)
    {
        StationRules.Coordinates(scopeId, staffId);
        StationRules.Utc(now);
        this.ScopeId = scopeId;
        this.StaffMemberId = staffId;
        this.Replace(material, now, enrollment);
    }
    public string ScopeId { get; private set; } = "";
    public Guid StaffMemberId { get; private set; }
    public long Revision { get; private set; }
    public int AlgorithmVersion { get; private set; } = StationPinMaterial.AlgorithmVersion;
    public int Iterations { get; private set; } = StationPinMaterial.Iterations;
    [JsonIgnore] public string Salt { get; private set; } = "";
    [JsonIgnore] public string Verifier { get; private set; } = "";
    [JsonIgnore] public string PepperVersion { get; private set; } = "";
    public bool Revoked { get; private set; }
    public StationActorKind EnrollmentAuthorityKind { get; private set; }
    public string? EnrollmentAuthSubjectId { get; private set; }
    public StationEnrollmentBinding Enrollment() => new(this.EnrollmentAuthorityKind, this.EnrollmentAuthSubjectId);
    public DateTimeOffset LastObservedAtUtc { get; private set; }
    public DateTimeOffset? FailureWindowStartedAtUtc { get; private set; }
    public int FailureCount { get; private set; }
    public DateTimeOffset? CooldownUntilUtc { get; private set; }

    public bool CanAttempt(DateTimeOffset now) => !this.Revoked && now >= this.LastObservedAtUtc &&
        !(now < this.CooldownUntilUtc);
    public void RecordFailure(DateTimeOffset now, int maximum, TimeSpan window, TimeSpan cooldown)
    {
        if (!this.CanAttempt(now))
        {
            throw new InvalidOperationException("Credential cannot be attempted.");
        }

        this.LastObservedAtUtc = now;
        if (this.FailureWindowStartedAtUtc is null || now >= this.FailureWindowStartedAtUtc.Value + window)
        { this.FailureWindowStartedAtUtc = now; this.FailureCount = 0; this.CooldownUntilUtc = null; }
        this.FailureCount = checked(this.FailureCount + 1);
        if (this.FailureCount >= maximum)
        {
            this.CooldownUntilUtc = now + cooldown;
        }
    }
    public void RecordSuccess(DateTimeOffset now)
    {
        if (!this.CanAttempt(now))
        {
            throw new InvalidOperationException("Credential cannot be attempted.");
        }

        this.LastObservedAtUtc = now;
        this.FailureCount = 0;
        this.FailureWindowStartedAtUtc = null;
        this.CooldownUntilUtc = null;
    }
    public void Replace(StationPinMaterial material, DateTimeOffset now, StationEnrollmentBinding? enrollment = null)
    {
        ArgumentNullException.ThrowIfNull(material);
        StationRules.Utc(now);
        if (now < this.LastObservedAtUtc)
        {
            throw new InvalidOperationException("Clock moved backwards.");
        }

        this.Salt = material.Salt;
        this.Verifier = material.Verifier;
        this.PepperVersion = material.PepperVersion;
        this.EnrollmentAuthorityKind = enrollment?.Kind ?? StationActorKind.Unknown;
        this.EnrollmentAuthSubjectId = enrollment?.AuthSubjectId;
        this.AlgorithmVersion = StationPinMaterial.AlgorithmVersion;
        this.Iterations = StationPinMaterial.Iterations;
        this.Revoked = false;
        this.Revision = checked(this.Revision + 1);
        this.LastObservedAtUtc = now;
        this.FailureCount = 0;
        this.FailureWindowStartedAtUtc = null;
        this.CooldownUntilUtc = null;
    }
    public void Revoke(DateTimeOffset now)
    {
        StationRules.Utc(now);
        if (now < this.LastObservedAtUtc)
        {
            throw new InvalidOperationException("Clock moved backwards.");
        }

        this.Revoked = true;
        this.Revision = checked(this.Revision + 1);
        this.LastObservedAtUtc = now;
        this.Salt = "";
        this.Verifier = "";
        this.PepperVersion = "";
        this.EnrollmentAuthorityKind = StationActorKind.Unknown;
        this.EnrollmentAuthSubjectId = null;
    }
}

/// <summary>Current enrollment origin, not a history epoch or permission. Unknown is legacy and cannot enter runtime.</summary>
public sealed record StationEnrollmentBinding
{
    public StationEnrollmentBinding(StationActorKind kind, string? authSubjectId)
    {
        if (kind is not (StationActorKind.Unknown or StationActorKind.LinkedStation or StationActorKind.StationOnly) ||
            (kind == StationActorKind.LinkedStation && (string.IsNullOrWhiteSpace(authSubjectId) || authSubjectId.Length > 256 || authSubjectId.Trim() != authSubjectId)) ||
            (kind != StationActorKind.LinkedStation && authSubjectId is not null))
        {
            throw new ArgumentException("Invalid enrollment binding.");
        }
        this.Kind = kind;
        this.AuthSubjectId = authSubjectId;
    }
    public StationActorKind Kind { get; }
    public string? AuthSubjectId { get; }
    public bool IsBound => this.Kind != StationActorKind.Unknown;
}
