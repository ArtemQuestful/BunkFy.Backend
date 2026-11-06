namespace BunkFy.Modules.Guests.Contracts;

public sealed record GuestListItemDto(
    Guid GuestId,
    string DisplayName,
    string? LegalName,
    string? Email,
    string? Phone,
    string? NationalityCountryCode,
    string? PreferredLanguageTag,
    GuestStatus Status,
    string LastChangedBy,
    DateTimeOffset LastChangedAtUtc)
{
    public string[] LanguageTags { get; init; } = [];
}
