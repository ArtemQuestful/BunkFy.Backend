namespace BunkFy.Modules.Guests.Contracts;

public sealed record GuestProfileDto(
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
    GuestStatus Status,
    long Version,
    string CreatedBy,
    DateTimeOffset CreatedAtUtc,
    string LastChangedBy,
    DateTimeOffset LastChangedAtUtc,
    DateTimeOffset? ArchivedAtUtc)
{
    public string[] LanguageTags { get; init; } = [];
}
