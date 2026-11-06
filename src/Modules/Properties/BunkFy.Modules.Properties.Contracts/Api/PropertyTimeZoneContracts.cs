namespace BunkFy.Modules.Properties.Contracts;

using System.Text.Json.Serialization;

[JsonConverter(typeof(PropertyTimeZoneChangeKindJsonConverter))]
public enum PropertyTimeZoneChangeKind
{
    Unknown = 0,
    Created = 1,
    Unchanged = 2,
    Canonicalized = 3,
    Changed = 4
}

public static class PropertyTimeZoneChangeKindNames
{
    public static string ToWireName(PropertyTimeZoneChangeKind kind) =>
        kind switch
        {
            PropertyTimeZoneChangeKind.Created => "created",
            PropertyTimeZoneChangeKind.Unchanged => "unchanged",
            PropertyTimeZoneChangeKind.Canonicalized => "canonicalized",
            PropertyTimeZoneChangeKind.Changed => "changed",
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "Property time-zone change kind is invalid.")
        };

    public static bool TryParse(
        string? value,
        out PropertyTimeZoneChangeKind kind)
    {
        kind = value switch
        {
            "created" => PropertyTimeZoneChangeKind.Created,
            "unchanged" => PropertyTimeZoneChangeKind.Unchanged,
            "canonicalized" => PropertyTimeZoneChangeKind.Canonicalized,
            "changed" => PropertyTimeZoneChangeKind.Changed,
            _ => PropertyTimeZoneChangeKind.Unknown
        };
        return kind is not PropertyTimeZoneChangeKind.Unknown;
    }
}

public sealed record PropertyTimeZoneCountryDto(string Code, string Name);

public sealed record PropertyTimeZoneCatalogItemDto(
    string TimeZoneId,
    IReadOnlyCollection<PropertyTimeZoneCountryDto> Countries,
    string? Comment,
    int UtcOffsetMinutes,
    bool RuntimeAvailable);

public sealed record PropertyTimeZoneCatalogPageDto(
    string CatalogVersion,
    DateTimeOffset ObservedAtUtc,
    IReadOnlyCollection<PropertyTimeZoneCatalogItemDto> TimeZones,
    string? NextCursor,
    bool HasMore);

public sealed record PropertyTimeZoneComplianceItemDto(
    Guid PropertyId,
    string Name,
    string Code,
    string TimeZoneId,
    PropertyTimeZoneStatus TimeZoneStatus,
    string? CanonicalTimeZoneId,
    PropertyStatus Status,
    PropertyProcessingStatus ProcessingStatus,
    string? OperatingCountryCode,
    long Version,
    bool CorrectionAllowed);

public sealed record PropertyTimeZoneCompliancePageDto(
    string CatalogVersion,
    DateTimeOffset ObservedAtUtc,
    IReadOnlyCollection<PropertyTimeZoneComplianceItemDto> Properties,
    string? NextCursor,
    bool HasMore);

public sealed record SetPropertyTimeZoneReceiptDto(
    Guid PropertyId,
    Guid OperationId,
    PropertyTimeZoneChangeKind ChangeKind,
    string RequestedTimeZoneId,
    string? PreviousTimeZoneId,
    string TimeZoneId,
    PropertyTimeZoneStatus TimeZoneStatus,
    string CatalogVersion,
    long ExpectedVersion,
    long Version,
    string ActorId,
    DateTimeOffset CompletedAtUtc);

public sealed record PropertyTimeZoneRecoveryDto(
    SetPropertyTimeZoneReceiptDto Receipt,
    string CurrentTimeZoneId,
    PropertyTimeZoneStatus CurrentTimeZoneStatus,
    string? CurrentCanonicalTimeZoneId,
    DateTimeOffset CurrentTimeZoneObservedAtUtc,
    long CurrentVersion);
