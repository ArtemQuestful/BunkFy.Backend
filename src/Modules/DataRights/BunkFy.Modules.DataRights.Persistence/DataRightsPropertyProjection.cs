namespace BunkFy.Modules.DataRights.Persistence;

using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Domain;
using Gma.Framework.Messaging;
using Gma.Framework.Naming;

public sealed class DataRightsPropertyProjection : IScopedEntity
{
    private DataRightsPropertyProjection() { }

    public DataRightsPropertyProjection(
        string scopeId,
        Guid id,
        string? name,
        string? timeZoneId,
        PropertyStatus status,
        long version)
    {
        this.ScopeId = TenantIds.Normalize(scopeId);
        if (id == Guid.Empty)
        {
            throw new ArgumentException(
                "A Data Rights property projection requires a property id.",
                nameof(id));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(version);
        string? normalizedName = NormalizeOptionalText(
            name,
            PropertiesContractLimits.PropertyNameMaxLength,
            nameof(name));
        string? normalizedTimeZoneId = NormalizeOptionalText(
            timeZoneId,
            PropertiesContractLimits.TimeZoneIdMaxLength,
            nameof(timeZoneId));
        ValidateTopology(
            normalizedName,
            normalizedTimeZoneId,
            status,
            version,
            allowUnknown: version == 0);

        this.Id = id;
        this.Name = normalizedName;
        this.TimeZoneId = normalizedTimeZoneId;
        this.Status = status;
        this.IsKnown = version > 0;
        this.TopologySourceVersion = version;
    }

    public string ScopeId { get; private set; } = string.Empty;
    public Guid Id { get; private set; }
    public string? Name { get; private set; }
    public string? TimeZoneId { get; private set; }
    public PropertyStatus Status { get; private set; }
    public bool IsKnown { get; private set; }
    public PropertyProcessingStatus ProcessingStatus { get; private set; } =
        PropertyProcessingStatus.Unconfigured;
    public DataRightsPropertyPolicyBinding? GovernancePolicy { get; private set; }
    public long TopologySourceVersion { get; private set; }
    public long PolicySourceVersion { get; private set; }

    public void ApplyTopology(
        string? name,
        string? timeZoneId,
        PropertyStatus status,
        long sourceVersion)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceVersion);
        string? normalizedName = NormalizeOptionalText(
            name,
            PropertiesContractLimits.PropertyNameMaxLength,
            nameof(name));
        string? normalizedTimeZoneId = NormalizeOptionalText(
            timeZoneId,
            PropertiesContractLimits.TimeZoneIdMaxLength,
            nameof(timeZoneId));
        string? nextName = normalizedName ?? this.Name;
        string? nextTimeZoneId = normalizedTimeZoneId ?? this.TimeZoneId;
        ValidateTopology(
            nextName,
            nextTimeZoneId,
            status,
            sourceVersion,
            allowUnknown: false);

        if (sourceVersion < this.TopologySourceVersion)
        {
            return;
        }

        if (sourceVersion == this.TopologySourceVersion)
        {
            if (this.Status != status ||
                !string.Equals(this.Name, nextName, StringComparison.Ordinal) ||
                !string.Equals(
                    this.TimeZoneId,
                    nextTimeZoneId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "DataRights.PropertyTopologyProjectionConflict");
            }

            return;
        }

        this.Name = nextName;
        this.TimeZoneId = nextTimeZoneId;
        this.Status = status;
        this.IsKnown = true;
        this.TopologySourceVersion = sourceVersion;
    }

    public void ApplyPolicy(
        PropertyProcessingStatus processingStatus,
        PropertyGovernancePolicyBinding? governancePolicy,
        long sourceVersion)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceVersion);
        bool configured = processingStatus is
            PropertyProcessingStatus.Enabled or PropertyProcessingStatus.Suspended;
        if (processingStatus == PropertyProcessingStatus.Unknown ||
            configured != (governancePolicy is not null))
        {
            throw new ArgumentException(
                "The projected property policy is inconsistent.",
                nameof(governancePolicy));
        }

        if (sourceVersion < this.PolicySourceVersion)
        {
            return;
        }

        if (sourceVersion == this.PolicySourceVersion)
        {
            if (this.ProcessingStatus != processingStatus ||
                !DataRightsPropertyPolicyBinding.Equivalent(
                    this.GovernancePolicy,
                    governancePolicy))
            {
                throw new InvalidOperationException(
                    "DataRights.PropertyPolicyProjectionConflict");
            }

            return;
        }

        this.ProcessingStatus = processingStatus;
        this.GovernancePolicy = governancePolicy is null
            ? null
            : DataRightsPropertyPolicyBinding.From(governancePolicy);
        this.IsKnown = true;
        this.PolicySourceVersion = sourceVersion;
    }

    private static string? NormalizeOptionalText(
        string? value,
        int maxLength,
        string parameterName) => string.IsNullOrEmpty(value)
            ? null
            : IntegrationEventContractGuards.NormalizeRequiredText(
                value,
                maxLength,
                parameterName);

    private static void ValidateTopology(
        string? name,
        string? timeZoneId,
        PropertyStatus status,
        long sourceVersion,
        bool allowUnknown)
    {
        if (allowUnknown)
        {
            if (status != PropertyStatus.Unknown || name is not null || timeZoneId is not null)
            {
                throw new ArgumentException(
                    "An unknown Data Rights property projection cannot carry topology facts.",
                    nameof(status));
            }

            return;
        }

        if (status is not (PropertyStatus.Active or PropertyStatus.Retired) ||
            sourceVersion < 1 ||
            (status == PropertyStatus.Active && (name is null || timeZoneId is null)))
        {
            throw new ArgumentException(
                "The projected Data Rights property topology is inconsistent.",
                nameof(status));
        }
    }
}

public sealed class DataRightsPropertyPolicyBinding
{
    private readonly List<DataRightsPropertyPolicyAcknowledgement> acknowledgements = [];

    private DataRightsPropertyPolicyBinding() { }

    private DataRightsPropertyPolicyBinding(PropertyGovernancePolicyBinding policy)
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
        this.acknowledgements.AddRange(policy.Acknowledgements
            .OrderBy(
                acknowledgement => acknowledgement.AcknowledgementId,
                StringComparer.Ordinal)
            .ThenBy(acknowledgement => acknowledgement.AcknowledgementVersion)
            .Select(acknowledgement => new DataRightsPropertyPolicyAcknowledgement(
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
    public IReadOnlyCollection<DataRightsPropertyPolicyAcknowledgement> Acknowledgements =>
        this.acknowledgements.AsReadOnly();

    internal static DataRightsPropertyPolicyBinding From(
        PropertyGovernancePolicyBinding policy) => new(policy);

    internal static bool Equivalent(
        DataRightsPropertyPolicyBinding? current,
        PropertyGovernancePolicyBinding? incoming)
    {
        if (current is null || incoming is null)
        {
            return current is null && incoming is null;
        }

        return string.Equals(
                   current.OperatingCountryCode,
                   incoming.OperatingCountryCode,
                   StringComparison.Ordinal) &&
               string.Equals(current.PolicyId, incoming.PolicyId, StringComparison.Ordinal) &&
               current.PolicyVersion == incoming.PolicyVersion &&
               string.Equals(
                   current.DataRegionId,
                   incoming.DataRegionId,
                   StringComparison.Ordinal) &&
               string.Equals(
                   current.TransferProfileId,
                   incoming.TransferProfileId,
                   StringComparison.Ordinal) &&
               string.Equals(
                   current.RetentionPolicyId,
                   incoming.RetentionPolicyId,
                   StringComparison.Ordinal) &&
               current.RetentionPolicyVersion == incoming.RetentionPolicyVersion &&
               string.Equals(
                   current.ContentSha256,
                   incoming.ContentSha256,
                   StringComparison.Ordinal) &&
               current.PolicyEffectiveAtUtc == incoming.PolicyEffectiveAtUtc &&
               current.PolicyExpiresAtUtc == incoming.PolicyExpiresAtUtc &&
               current.ActivatedAtUtc == incoming.ActivatedAtUtc &&
               current.Acknowledgements
                   .OrderBy(
                       acknowledgement => acknowledgement.AcknowledgementId,
                       StringComparer.Ordinal)
                   .ThenBy(acknowledgement => acknowledgement.AcknowledgementVersion)
                   .Select(acknowledgement => (
                       acknowledgement.AcknowledgementId,
                       acknowledgement.AcknowledgementVersion))
                   .SequenceEqual(incoming.Acknowledgements
                       .OrderBy(
                           acknowledgement => acknowledgement.AcknowledgementId,
                           StringComparer.Ordinal)
                       .ThenBy(acknowledgement => acknowledgement.AcknowledgementVersion)
                       .Select(acknowledgement => (
                           acknowledgement.AcknowledgementId,
                           acknowledgement.AcknowledgementVersion)));
    }
}

public sealed class DataRightsPropertyPolicyAcknowledgement
{
    private DataRightsPropertyPolicyAcknowledgement() { }

    internal DataRightsPropertyPolicyAcknowledgement(
        string acknowledgementId,
        int acknowledgementVersion)
    {
        this.AcknowledgementId = acknowledgementId;
        this.AcknowledgementVersion = acknowledgementVersion;
    }

    public string AcknowledgementId { get; private set; } = string.Empty;
    public int AcknowledgementVersion { get; private set; }
}
