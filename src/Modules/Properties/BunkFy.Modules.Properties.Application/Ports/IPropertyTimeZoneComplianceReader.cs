namespace BunkFy.Modules.Properties.Application.Ports;

using BunkFy.Modules.Properties.Contracts;

public interface IPropertyTimeZoneComplianceReader
{
    Task<PropertyTimeZoneComplianceReadPage> ReadPageAsync(
        string? cursor,
        int pageSize,
        string catalogVersion,
        CancellationToken cancellationToken);
}

public sealed record PropertyTimeZoneComplianceReadModel(
    Guid PropertyId,
    string Name,
    string Code,
    string TimeZoneId,
    PropertyStatus Status,
    PropertyProcessingStatus ProcessingStatus,
    string? OperatingCountryCode,
    long Version);

public sealed record PropertyTimeZoneComplianceReadPage(
    IReadOnlyCollection<PropertyTimeZoneComplianceReadModel> Properties,
    string? NextCursor,
    bool HasMore);
