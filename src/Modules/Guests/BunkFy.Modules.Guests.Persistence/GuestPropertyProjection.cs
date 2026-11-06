namespace BunkFy.Modules.Guests.Persistence;

using BunkFy.Modules.Guests.Application.Ports;
using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Domain;

public sealed class GuestPropertyProjection : IScopedEntity
{
    private GuestPropertyProjection() { }

    public GuestPropertyProjection(
        string scopeId,
        Guid id,
        string? name,
        PropertyStatus status,
        long version)
        : this(
            scopeId,
            id,
            name,
            timeZoneId: null,
            status,
            version)
    {
    }

    public GuestPropertyProjection(
        string scopeId,
        Guid id,
        string? name,
        string? timeZoneId,
        PropertyStatus status,
        long version)
    {
        this.ScopeId = scopeId;
        this.Id = id;
        this.Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        this.TimeZoneId = string.IsNullOrWhiteSpace(timeZoneId)
            ? null
            : timeZoneId.Trim();
        if (this.TimeZoneId is not null)
        {
            this.TimeZoneEvidenceSource =
                GuestPropertyTimeZoneEvidenceSource.Legacy;
            this.TimeZoneEvidenceSourceVersion = Math.Max(0, version);
        }

        this.Status = status;
        this.IsKnown = version > 0 && status != PropertyStatus.Unknown;
        this.TopologySourceVersion = version;
    }

    public string ScopeId { get; private set; } = string.Empty;
    public Guid Id { get; private set; }
    public string? Name { get; private set; }
    public string? TimeZoneId { get; private set; }
    public string? CanonicalTimeZoneId { get; private set; }
    public PropertyTimeZoneStatus TimeZoneStatus { get; private set; }
    public string? TimeZoneCatalogVersion { get; private set; }
    public GuestPropertyTimeZoneEvidenceSource TimeZoneEvidenceSource { get; private set; }
    public long TimeZoneEvidenceSourceVersion { get; private set; }
    public PropertyStatus Status { get; private set; }
    public bool IsKnown { get; private set; }
    public PropertyProcessingStatus ProcessingStatus { get; private set; } = PropertyProcessingStatus.Unconfigured;
    public GuestPropertyPolicyBinding? GovernancePolicy { get; private set; }
    public long TopologySourceVersion { get; private set; }
    public long PolicySourceVersion { get; private set; }

    public void ApplyTopology(string? name, PropertyStatus status, long sourceVersion)
    {
        this.ApplyTopology(name, timeZoneId: null, status, sourceVersion);
    }

    public void ApplyTopology(
        string? name,
        string? timeZoneId,
        PropertyStatus status,
        long sourceVersion)
    {
        if (sourceVersion <= this.TopologySourceVersion)
        {
            return;
        }

        string? rawTimeZoneId = string.IsNullOrWhiteSpace(timeZoneId)
            ? null
            : timeZoneId.Trim();
        bool hasProjectedEvidence =
            EvidencePrecedence(this.TimeZoneEvidenceSource) > 0;
        if (rawTimeZoneId is not null &&
            hasProjectedEvidence &&
            sourceVersion == this.TimeZoneEvidenceSourceVersion &&
            !string.Equals(
                this.TimeZoneId,
                rawTimeZoneId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Equal-version property time-zone evidence conflicts with the topology projection.");
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            this.Name = name.Trim();
        }

        if (rawTimeZoneId is not null)
        {
            if (!hasProjectedEvidence ||
                sourceVersion >= this.TimeZoneEvidenceSourceVersion)
            {
                this.TimeZoneId = rawTimeZoneId;
                if (sourceVersion > this.TimeZoneEvidenceSourceVersion)
                {
                    this.CanonicalTimeZoneId = null;
                    this.TimeZoneStatus = PropertyTimeZoneStatus.Unknown;
                    this.TimeZoneCatalogVersion = null;
                    this.TimeZoneEvidenceSource =
                        GuestPropertyTimeZoneEvidenceSource.Unknown;
                    this.TimeZoneEvidenceSourceVersion = 0;
                }
            }
        }

        this.Status = status;
        this.IsKnown = true;
        this.TopologySourceVersion = sourceVersion;
    }

    public void ApplyTimeZone(
        string timeZoneId,
        string? canonicalTimeZoneId,
        PropertyTimeZoneStatus timeZoneStatus,
        string? timeZoneCatalogVersion,
        GuestPropertyTimeZoneEvidenceSource evidenceSource,
        long sourceVersion)
    {
        if (sourceVersion < this.TopologySourceVersion ||
            sourceVersion < this.TimeZoneEvidenceSourceVersion)
        {
            return;
        }

        string raw = timeZoneId?.Trim() ?? string.Empty;
        string? canonical = string.IsNullOrWhiteSpace(canonicalTimeZoneId)
            ? null
            : canonicalTimeZoneId.Trim();
        string? catalog = string.IsNullOrWhiteSpace(timeZoneCatalogVersion)
            ? null
            : timeZoneCatalogVersion.Trim();
        bool sourceSupported = evidenceSource is
            GuestPropertyTimeZoneEvidenceSource.Generic or
            GuestPropertyTimeZoneEvidenceSource.Dedicated or
            GuestPropertyTimeZoneEvidenceSource.Rebuild;
        bool canonicalProof = timeZoneStatus ==
                PropertyTimeZoneStatus.Canonical &&
            string.Equals(raw, canonical, StringComparison.Ordinal) &&
            catalog is not null;
        bool aliasProof = timeZoneStatus == PropertyTimeZoneStatus.Alias &&
            canonical is not null &&
            !string.Equals(raw, canonical, StringComparison.Ordinal) &&
            catalog is not null;
        bool unresolvedClassification = timeZoneStatus is
                PropertyTimeZoneStatus.Legacy or
                PropertyTimeZoneStatus.Unrecognized or
                PropertyTimeZoneStatus.RuntimeUnavailable &&
            canonical is null &&
            catalog is null;
        bool boundedClassification = timeZoneStatus is
            PropertyTimeZoneStatus.Canonical or
            PropertyTimeZoneStatus.Alias or
            PropertyTimeZoneStatus.Legacy or
            PropertyTimeZoneStatus.Unrecognized or
            PropertyTimeZoneStatus.RuntimeUnavailable;
        if (raw.Length == 0 ||
            raw.Length > PropertiesContractLimits.TimeZoneIdMaxLength ||
            canonical?.Length > PropertiesContractLimits.TimeZoneIdMaxLength ||
            catalog?.Length >
                PropertiesContractLimits.TimeZoneCatalogVersionMaxLength ||
            !sourceSupported ||
            sourceVersion < 1 ||
            !boundedClassification ||
            HasControlCharacter(raw) ||
            (canonical is not null && HasControlCharacter(canonical)) ||
            (catalog is not null && HasControlCharacter(catalog)) ||
            !(canonicalProof || aliasProof || unresolvedClassification) ||
            (evidenceSource ==
                GuestPropertyTimeZoneEvidenceSource.Dedicated &&
             !canonicalProof))
        {
            throw new ArgumentException(
                "The projected property time-zone evidence is inconsistent.",
                nameof(timeZoneId));
        }

        if (sourceVersion == this.TimeZoneEvidenceSourceVersion)
        {
            if (this.HasSameTimeZoneEvidence(
                    raw,
                    canonical,
                    timeZoneStatus,
                    catalog,
                    evidenceSource))
            {
                return;
            }

            int incomingPrecedence = EvidencePrecedence(evidenceSource);
            int currentPrecedence =
                EvidencePrecedence(this.TimeZoneEvidenceSource);
            if (!string.Equals(
                    this.TimeZoneId,
                    raw,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Equal-version property time-zone evidence conflicts with the existing projection.");
            }

            if (incomingPrecedence < currentPrecedence)
            {
                return;
            }

            bool rebuildRefresh = evidenceSource ==
                    GuestPropertyTimeZoneEvidenceSource.Rebuild &&
                this.TimeZoneEvidenceSource ==
                    GuestPropertyTimeZoneEvidenceSource.Rebuild;
            if (incomingPrecedence == currentPrecedence && !rebuildRefresh)
            {
                throw new InvalidOperationException(
                    "Equal-version property time-zone evidence conflicts with the existing projection.");
            }
        }

        this.TimeZoneId = raw;
        this.CanonicalTimeZoneId = canonical;
        this.TimeZoneStatus = timeZoneStatus;
        this.TimeZoneCatalogVersion = catalog;
        this.TimeZoneEvidenceSource = evidenceSource;
        this.TimeZoneEvidenceSourceVersion = sourceVersion;
        this.IsKnown = true;
    }

    public void ApplyPolicy(
        PropertyProcessingStatus processingStatus,
        PropertyGovernancePolicyBinding? governancePolicy,
        long sourceVersion)
    {
        if (sourceVersion <= this.PolicySourceVersion)
        {
            return;
        }

        bool isConfigured = processingStatus is PropertyProcessingStatus.Enabled or PropertyProcessingStatus.Suspended;
        if (processingStatus == PropertyProcessingStatus.Unknown || isConfigured != (governancePolicy is not null))
        {
            throw new ArgumentException("The projected property policy is inconsistent.", nameof(governancePolicy));
        }

        this.ProcessingStatus = processingStatus;
        this.GovernancePolicy = governancePolicy is null ? null : GuestPropertyPolicyBinding.From(governancePolicy);
        this.IsKnown = true;
        this.PolicySourceVersion = sourceVersion;
    }

    private static int EvidencePrecedence(
        GuestPropertyTimeZoneEvidenceSource source) => source switch
        {
            GuestPropertyTimeZoneEvidenceSource.Rebuild => 3,
            GuestPropertyTimeZoneEvidenceSource.Dedicated => 2,
            GuestPropertyTimeZoneEvidenceSource.Generic => 1,
            _ => 0
        };

    private static bool HasControlCharacter(string value) =>
        value.Any(char.IsControl);

    private bool HasSameTimeZoneEvidence(
        string rawTimeZoneId,
        string? canonicalTimeZoneId,
        PropertyTimeZoneStatus timeZoneStatus,
        string? catalogVersion,
        GuestPropertyTimeZoneEvidenceSource evidenceSource) =>
        string.Equals(
            this.TimeZoneId,
            rawTimeZoneId,
            StringComparison.Ordinal) &&
        string.Equals(
            this.CanonicalTimeZoneId,
            canonicalTimeZoneId,
            StringComparison.Ordinal) &&
        this.TimeZoneStatus == timeZoneStatus &&
        string.Equals(
            this.TimeZoneCatalogVersion,
            catalogVersion,
            StringComparison.Ordinal) &&
        this.TimeZoneEvidenceSource == evidenceSource;
}

public sealed class GuestPropertyPolicyBinding
{
    private readonly List<GuestPropertyPolicyAcknowledgement> acknowledgements = [];

    private GuestPropertyPolicyBinding() { }

    private GuestPropertyPolicyBinding(PropertyGovernancePolicyBinding policy)
    {
        this.OperatingCountryCode = policy.OperatingCountryCode;
        this.PolicyId = policy.PolicyId;
        this.PolicyVersion = policy.PolicyVersion;
        this.DataRegionId = policy.DataRegionId;
        this.TransferProfileId = policy.TransferProfileId;
        this.RetentionPolicyId = policy.RetentionPolicyId;
        this.RetentionPolicyVersion = policy.RetentionPolicyVersion;
        this.ContentSha256 = policy.ContentSha256;
        this.PolicyEffectiveAtUtc = policy.PolicyEffectiveAtUtc;
        this.PolicyExpiresAtUtc = policy.PolicyExpiresAtUtc;
        this.ActivatedAtUtc = policy.ActivatedAtUtc;
        this.acknowledgements.AddRange(policy.Acknowledgements.Select(acknowledgement =>
            new GuestPropertyPolicyAcknowledgement(
                acknowledgement.AcknowledgementId,
                acknowledgement.AcknowledgementVersion)));
    }

    public string OperatingCountryCode { get; private set; } = string.Empty;
    public string PolicyId { get; private set; } = string.Empty;
    public int PolicyVersion { get; private set; }
    public string DataRegionId { get; private set; } = string.Empty;
    public string TransferProfileId { get; private set; } = string.Empty;
    public string RetentionPolicyId { get; private set; } = string.Empty;
    public int RetentionPolicyVersion { get; private set; }
    public string ContentSha256 { get; private set; } = string.Empty;
    public DateTimeOffset PolicyEffectiveAtUtc { get; private set; }
    public DateTimeOffset PolicyExpiresAtUtc { get; private set; }
    public DateTimeOffset ActivatedAtUtc { get; private set; }
    public IReadOnlyCollection<GuestPropertyPolicyAcknowledgement> Acknowledgements =>
        this.acknowledgements.AsReadOnly();

    internal static GuestPropertyPolicyBinding From(PropertyGovernancePolicyBinding policy) => new(policy);
}

public sealed class GuestPropertyPolicyAcknowledgement
{
    private GuestPropertyPolicyAcknowledgement() { }

    internal GuestPropertyPolicyAcknowledgement(string acknowledgementId, int acknowledgementVersion)
    {
        this.AcknowledgementId = acknowledgementId;
        this.AcknowledgementVersion = acknowledgementVersion;
    }

    public string AcknowledgementId { get; private set; } = string.Empty;
    public int AcknowledgementVersion { get; private set; }
}
