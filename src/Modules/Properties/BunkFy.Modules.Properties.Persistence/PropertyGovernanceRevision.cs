namespace BunkFy.Modules.Properties.Persistence;

using BunkFy.Modules.Properties.Application.Ports;
using BunkFy.Modules.Properties.Domain.Aggregates;
using Gma.Framework.Domain;
using Gma.Framework.Naming;

public sealed class PropertyGovernanceRevision
    : IScopedEntity
{
    internal const int DecisionReasonCodeMaxLength = 64;

    private PropertyGovernanceRevision() { }

    internal PropertyGovernanceRevision(PropertyGovernanceRevisionWriteModel revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        if (revision.RevisionId == Guid.Empty || revision.PropertyId == Guid.Empty)
        {
            throw new ArgumentException(
                "A property governance revision requires non-empty coordinates.",
                nameof(revision));
        }

        if (revision.PropertyVersion < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(revision),
                "A property governance revision requires a processing-state version.");
        }

        if (revision.Action <= PropertyGovernanceRevisionAction.Unknown ||
            !Enum.IsDefined(revision.Action))
        {
            throw new ArgumentException(
                "A property governance revision requires a defined action.",
                nameof(revision));
        }

        PropertyGovernanceRevisionCoordinatesRecord? previous =
            PropertyGovernanceRevisionCoordinatesRecord.From(revision.Previous);
        PropertyGovernanceRevisionCoordinatesRecord? current =
            PropertyGovernanceRevisionCoordinatesRecord.From(revision.Current);
        bool evidenceMatchesAction = revision.Action switch
        {
            PropertyGovernanceRevisionAction.Activated =>
                previous is null && current is not null,
            PropertyGovernanceRevisionAction.Rebound =>
                previous is not null && current is not null &&
                !previous.SameAs(current),
            PropertyGovernanceRevisionAction.Reactivated or
            PropertyGovernanceRevisionAction.Suspended =>
                previous is not null && current is not null &&
                previous.SameAs(current),
            _ => false
        };
        if (!evidenceMatchesAction)
        {
            throw new ArgumentException(
                "A property governance revision action does not match its policy evidence.",
                nameof(revision));
        }

        if (revision.OccurredAtUtc == default)
        {
            throw new ArgumentException(
                "A property governance revision requires an occurrence timestamp.",
                nameof(revision));
        }

        this.Id = revision.RevisionId;
        this.ScopeId = TenantIds.Normalize(revision.ScopeId);
        this.PropertyId = revision.PropertyId;
        this.PropertyVersion = revision.PropertyVersion;
        this.Action = revision.Action;
        this.DecisionReasonCode = NormalizeRequiredText(
            revision.DecisionReasonCode,
            DecisionReasonCodeMaxLength,
            nameof(revision.DecisionReasonCode));
        this.Previous = previous;
        this.Current = current;
        this.ActorId = NormalizeRequiredText(
            revision.ActorId,
            Property.ActorIdMaxLength,
            nameof(revision.ActorId));
        this.OccurredAtUtc = revision.OccurredAtUtc.ToUniversalTime();
    }

    public Guid Id { get; private set; }
    public string ScopeId { get; private set; } = string.Empty;
    public Guid PropertyId { get; private set; }
    public long PropertyVersion { get; private set; }
    public PropertyGovernanceRevisionAction Action { get; private set; }
    public string DecisionReasonCode { get; private set; } = string.Empty;
    public PropertyGovernanceRevisionCoordinatesRecord? Previous { get; private set; }
    public PropertyGovernanceRevisionCoordinatesRecord? Current { get; private set; }
    public string ActorId { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; private set; }

    private static string NormalizeRequiredText(
        string? value,
        int maximumLength,
        string parameterName)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is 0 ||
            normalized.Length > maximumLength ||
            normalized.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Property governance revision text is required, bounded, and cannot contain control characters.",
                parameterName);
        }

        return normalized;
    }
}

public sealed class PropertyGovernanceRevisionCoordinatesRecord
{
    private PropertyGovernanceRevisionCoordinatesRecord() { }

    private PropertyGovernanceRevisionCoordinatesRecord(PropertyGovernanceRevisionCoordinates coordinates)
    {
        ArgumentNullException.ThrowIfNull(coordinates);
        this.OperatingCountryCode = NormalizeCountryCode(
            coordinates.OperatingCountryCode);
        this.PolicyId = NormalizeKey(coordinates.PolicyId);
        if (coordinates.PolicyVersion <= 0 ||
            coordinates.RetentionPolicyVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(coordinates),
                "Property governance policy versions must be positive.");
        }

        this.PolicyVersion = coordinates.PolicyVersion;
        this.DataRegionId = NormalizeKey(coordinates.DataRegionId);
        this.TransferProfileId = NormalizeKey(coordinates.TransferProfileId);
        this.RetentionPolicyId = NormalizeKey(coordinates.RetentionPolicyId);
        this.RetentionPolicyVersion = coordinates.RetentionPolicyVersion;
        this.ContentSha256 = NormalizeDigest(coordinates.ContentSha256);
        this.AcknowledgementSetSha256 = NormalizeDigest(
            coordinates.AcknowledgementSetSha256);
    }

    public string OperatingCountryCode { get; private set; } = string.Empty;
    public string PolicyId { get; private set; } = string.Empty;
    public int PolicyVersion { get; private set; }
    public string DataRegionId { get; private set; } = string.Empty;
    public string TransferProfileId { get; private set; } = string.Empty;
    public string RetentionPolicyId { get; private set; } = string.Empty;
    public int RetentionPolicyVersion { get; private set; }
    public string ContentSha256 { get; private set; } = string.Empty;
    public string AcknowledgementSetSha256 { get; private set; } = string.Empty;

    internal static PropertyGovernanceRevisionCoordinatesRecord? From(
        PropertyGovernanceRevisionCoordinates? coordinates) =>
        coordinates is null ? null : new(coordinates);

    internal bool SameAs(PropertyGovernanceRevisionCoordinatesRecord other) =>
        string.Equals(
            this.OperatingCountryCode,
            other.OperatingCountryCode,
            StringComparison.Ordinal) &&
        string.Equals(this.PolicyId, other.PolicyId, StringComparison.Ordinal) &&
        this.PolicyVersion == other.PolicyVersion &&
        string.Equals(
            this.DataRegionId,
            other.DataRegionId,
            StringComparison.Ordinal) &&
        string.Equals(
            this.TransferProfileId,
            other.TransferProfileId,
            StringComparison.Ordinal) &&
        string.Equals(
            this.RetentionPolicyId,
            other.RetentionPolicyId,
            StringComparison.Ordinal) &&
        this.RetentionPolicyVersion == other.RetentionPolicyVersion &&
        string.Equals(
            this.ContentSha256,
            other.ContentSha256,
            StringComparison.Ordinal) &&
        string.Equals(
            this.AcknowledgementSetSha256,
            other.AcknowledgementSetSha256,
            StringComparison.Ordinal);

    private static string NormalizeCountryCode(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length != Property.CountryCodeLength ||
            normalized.Any(character => character is < 'A' or > 'Z'))
        {
            throw new ArgumentException(
                "Property governance evidence requires an uppercase country code.",
                nameof(value));
        }

        return normalized;
    }

    private static string NormalizeKey(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is 0 ||
            normalized.Length > Property.PolicyKeyMaxLength ||
            normalized[0] is < 'a' or > 'z' ||
            normalized.Any(character =>
                character is not ((>= 'a' and <= 'z') or
                    (>= '0' and <= '9') or '.' or '-' or '_')))
        {
            throw new ArgumentException(
                "Property governance evidence requires a canonical policy key.",
                nameof(value));
        }

        return normalized;
    }

    private static string NormalizeDigest(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length != Property.ContentSha256Length ||
            normalized.Any(character =>
                character is not ((>= '0' and <= '9') or
                    (>= 'a' and <= 'f'))))
        {
            throw new ArgumentException(
                "Property governance evidence requires a lowercase SHA-256 value.",
                nameof(value));
        }

        return normalized;
    }
}
