namespace BunkFy.Modules.Properties.Contracts;

/// <summary>Authoritative property facts only; no legal, processing, or station admission.</summary>
public interface IPropertyStationEligibilitySource
{
    Task<PropertyStationEligibilitySnapshot?> FindAsync(
        string scopeId,
        Guid propertyId,
        CancellationToken cancellationToken = default);
}

public sealed record PropertyStationEligibilitySnapshot(
    string ScopeId,
    Guid PropertyId,
    PropertyStatus Status,
    long Version,
    PropertyProcessingStatus ConfiguredProcessingStatus,
    string TimeZoneId,
    string? Name = null);
