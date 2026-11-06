namespace BunkFy.Modules.Guests.Application.Ports;

using BunkFy.Modules.Properties.Contracts;
using BunkFy.TimeZones;

public interface IGuestPropertyProjectionRepository
{
    Task ApplyTopologyAsync(GuestPropertyTopologyWriteModel property, CancellationToken cancellationToken);
    Task ApplyTimeZoneAsync(GuestPropertyTimeZoneWriteModel property, CancellationToken cancellationToken);
    Task ApplyPolicyAsync(GuestPropertyPolicyWriteModel property, CancellationToken cancellationToken);
    Task<GuestPropertyPolicySnapshot?> GetPolicyAsync(Guid propertyId, CancellationToken cancellationToken);
}

public sealed record GuestPropertyTimeZoneWriteModel(
    string ScopeId,
    Guid PropertyId,
    string TimeZoneId,
    string? CanonicalTimeZoneId,
    PropertyTimeZoneStatus TimeZoneStatus,
    string? TimeZoneCatalogVersion,
    GuestPropertyTimeZoneEvidenceSource TimeZoneEvidenceSource,
    long TimeZoneEvidenceSourceVersion);

public enum GuestPropertyTimeZoneEvidenceSource
{
    Unknown = 0,
    Legacy = 1,
    Generic = 2,
    Dedicated = 3,
    Rebuild = 4
}

public sealed record GuestPropertyTopologyWriteModel(
    string ScopeId,
    Guid PropertyId,
    string Name,
    string? TimeZoneId,
    PropertyStatus Status,
    long SourceVersion)
{
    public GuestPropertyTopologyWriteModel(
        string scopeId,
        Guid propertyId,
        string name,
        PropertyStatus status,
        long sourceVersion)
        : this(
            scopeId,
            propertyId,
            name,
            TimeZoneCatalog.UniversalTimeZoneId,
            status,
            sourceVersion)
    {
    }
}

public sealed record GuestPropertyPolicyWriteModel(
    string ScopeId,
    Guid PropertyId,
    PropertyProcessingStatus ProcessingStatus,
    PropertyGovernancePolicyBinding? GovernancePolicy,
    long SourceVersion);

public sealed record GuestPropertyPolicySnapshot(
    bool IsKnown,
    bool IsActive,
    PropertyProcessingStatus ProcessingStatus,
    PropertyGovernancePolicyBinding? GovernancePolicy,
    long TopologySourceVersion,
    long PolicySourceVersion);
