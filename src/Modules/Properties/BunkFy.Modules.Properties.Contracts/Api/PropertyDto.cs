namespace BunkFy.Modules.Properties.Contracts;

public sealed record PropertyDto(
    Guid PropertyId,
    string Name,
    string Code,
    string TimeZoneId,
    PropertyTimeZoneStatus TimeZoneStatus,
    string? CanonicalTimeZoneId,
    string TimeZoneCatalogVersion,
    DateTimeOffset TimeZoneObservedAtUtc,
    bool TimeZoneCorrectionAllowed,
    PropertyStatus Status,
    PropertyProcessingStatus ProcessingStatus,
    PropertyGovernancePolicyBindingDto? GovernancePolicy,
    long Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? UpdatedAtUtc,
    DateTimeOffset? RetiredAtUtc);

public sealed record PropertyListItemDto(
    Guid PropertyId,
    string Name,
    string Code,
    string TimeZoneId,
    PropertyTimeZoneStatus TimeZoneStatus,
    string? CanonicalTimeZoneId,
    string TimeZoneCatalogVersion,
    DateTimeOffset TimeZoneObservedAtUtc,
    bool TimeZoneCorrectionAllowed,
    PropertyStatus Status,
    PropertyProcessingStatus ProcessingStatus,
    long Version);
