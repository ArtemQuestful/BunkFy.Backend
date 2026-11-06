namespace BunkFy.Modules.Properties.Contracts;

using Gma.Framework.Messaging;
using Gma.Framework.Tenancy;
using Gma.Framework.Tenancy.Messaging;

[IntegrationEventName(EventType)]
[IntegrationEventVersion(EventVersion)]
[TenantScoped]
public sealed record PropertyTimeZoneChangedIntegrationEvent
    : TenantIntegrationEvent
{
    public const string EventType = "property-time-zone-changed";
    public const int EventVersion = 1;

    public PropertyTimeZoneChangedIntegrationEvent(
        Guid eventId,
        string tenantId,
        DateTimeOffset occurredAtUtc,
        Guid propertyId,
        string previousTimeZoneId,
        string? previousCanonicalTimeZoneId,
        string timeZoneId,
        PropertyTimeZoneChangeKind changeKind,
        string catalogVersion,
        long propertyVersion)
        : base(
            eventId,
            tenantId,
            occurredAtUtc,
            EventType,
            EventVersion)
    {
        this.PropertyId = IntegrationEventContractGuards.RequireId(
            propertyId,
            nameof(propertyId));
        this.PreviousTimeZoneId =
            IntegrationEventContractGuards.NormalizeRequiredText(
                previousTimeZoneId,
                PropertiesContractLimits.TimeZoneIdMaxLength,
                nameof(previousTimeZoneId));
        this.PreviousCanonicalTimeZoneId =
            string.IsNullOrWhiteSpace(previousCanonicalTimeZoneId)
                ? null
                : IntegrationEventContractGuards.NormalizeRequiredText(
                    previousCanonicalTimeZoneId,
                    PropertiesContractLimits.TimeZoneIdMaxLength,
                    nameof(previousCanonicalTimeZoneId));
        this.TimeZoneId =
            IntegrationEventContractGuards.NormalizeRequiredText(
                timeZoneId,
                PropertiesContractLimits.TimeZoneIdMaxLength,
                nameof(timeZoneId));
        this.ChangeKind = changeKind is
            PropertyTimeZoneChangeKind.Canonicalized or
            PropertyTimeZoneChangeKind.Changed
                ? changeKind
                : throw new ArgumentOutOfRangeException(
                    nameof(changeKind),
                    changeKind,
                    "A material time-zone transition kind is required.");
        bool rawChanged = !string.Equals(
            this.PreviousTimeZoneId,
            this.TimeZoneId,
            StringComparison.Ordinal);
        bool canonicalEquivalent = string.Equals(
            this.PreviousCanonicalTimeZoneId,
            this.TimeZoneId,
            StringComparison.Ordinal);
        bool invalidCanonicalization =
            this.ChangeKind == PropertyTimeZoneChangeKind.Canonicalized &&
            !canonicalEquivalent;
        bool invalidSemanticChange =
            this.ChangeKind == PropertyTimeZoneChangeKind.Changed &&
            canonicalEquivalent;
        if (!rawChanged ||
            invalidCanonicalization ||
            invalidSemanticChange)
        {
            throw new ArgumentException(
                "Time-zone transition provenance is inconsistent with its change kind.",
                nameof(changeKind));
        }

        this.CatalogVersion =
            IntegrationEventContractGuards.NormalizeRequiredText(
                catalogVersion,
                PropertiesContractLimits.TimeZoneCatalogVersionMaxLength,
                nameof(catalogVersion));
        this.PropertyVersion = PropertiesEventContractGuards.RequireVersion(
            propertyVersion,
            nameof(propertyVersion));
    }

    public Guid PropertyId { get; }
    public string PreviousTimeZoneId { get; }
    public string? PreviousCanonicalTimeZoneId { get; }
    public string TimeZoneId { get; }
    public PropertyTimeZoneChangeKind ChangeKind { get; }
    public string CatalogVersion { get; }
    public long PropertyVersion { get; }
}
