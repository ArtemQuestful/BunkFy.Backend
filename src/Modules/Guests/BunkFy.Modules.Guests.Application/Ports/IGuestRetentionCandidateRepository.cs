namespace BunkFy.Modules.Guests.Application.Ports;

using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Properties.Contracts;

internal interface IGuestRetentionCandidateRepository
{
    Task<GuestRetentionScanPage> ScanAsync(
        long afterProjectionOrdinal,
        int limit,
        CancellationToken cancellationToken);

    Task<GuestRetentionCandidateSnapshot?> LoadAsync(
        Guid guestId,
        CancellationToken cancellationToken);
}

internal sealed record GuestRetentionScanPage(
    IReadOnlyList<GuestRetentionCandidateSnapshot> Candidates,
    bool ReachedEnd);

internal sealed record GuestRetentionCandidateSnapshot(
    Guid GuestId,
    long GuestVersion,
    long ProjectionOrdinal,
    GuestProfileState GuestState,
    Guid OriginPropertyId,
    IReadOnlyList<GuestRetentionStaySnapshot> Stays,
    IReadOnlyList<GuestRetentionHoldSnapshot> ActiveHolds,
    IReadOnlyList<GuestRetentionPropertySnapshot> Properties,
    bool AssociationOverflowed);

internal sealed record GuestRetentionStaySnapshot(
    Guid PropertyId,
    bool ProjectionSupported,
    bool HasOperationalStay,
    DateOnly? LatestTerminalBusinessDate);

internal sealed record GuestRetentionHoldSnapshot(
    Guid PropertyId,
    DateTimeOffset PlacedAtUtc);

internal sealed record GuestRetentionPropertySnapshot(
    Guid PropertyId,
    bool IsKnown,
    PropertyStatus Status,
    PropertyProcessingStatus ProcessingStatus,
    string? TimeZoneId,
    string? CanonicalTimeZoneId,
    PropertyTimeZoneStatus TimeZoneStatus,
    string? TimeZoneCatalogVersion,
    GuestPropertyTimeZoneEvidenceSource TimeZoneEvidenceSource,
    long TimeZoneEvidenceSourceVersion,
    long TopologySourceVersion,
    long PolicySourceVersion,
    PropertyGovernancePolicyBinding? GovernancePolicy);
