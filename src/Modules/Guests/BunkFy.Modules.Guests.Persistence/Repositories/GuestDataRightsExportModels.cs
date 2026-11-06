namespace BunkFy.Modules.Guests.Persistence.Repositories;

using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Guests.Application.Ports;
using BunkFy.Modules.Guests.Domain.Aggregates;

internal sealed record GuestProfileDataRightsExport(
    Guid GuestId,
    Guid OriginPropertyId,
    string DisplayName,
    string? LegalName,
    string? Email,
    string? Phone,
    DateOnly? DateOfBirth,
    string? NationalityCountryCode,
    string? PreferredLanguageTag,
    string? Notes,
    GuestProfileState Status,
    long Version,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastChangedAtUtc,
    DateTimeOffset? ArchivedAtUtc)
{
    public string[] LanguageTags { get; init; } = [];
}

internal sealed record GuestStayDataRightsExport(
    Guid ReservationId,
    Guid PropertyId,
    GuestStayRole Role,
    DateOnly Arrival,
    DateOnly Departure,
    GuestStayStatus Status,
    DateOnly? CheckedInBusinessDate,
    DateOnly? NoShowBusinessDate,
    DateOnly? CheckedOutBusinessDate,
    bool IsCurrentParticipant,
    long ReservationVersion);

internal sealed record GuestManagementOperationDataRightsExport(
    Guid OperationId,
    string ScopeId,
    Guid PropertyId,
    Guid GuestId,
    GuestManagementOperationKind Kind,
    long ExpectedVersion,
    string? RequestFingerprint,
    GuestStatus ResultStatus,
    long ResultVersion,
    DateTimeOffset CompletedAtUtc);
