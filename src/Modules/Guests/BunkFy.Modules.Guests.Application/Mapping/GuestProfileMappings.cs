namespace BunkFy.Modules.Guests.Application.Mapping;

using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Guests.Domain.Aggregates;

public static class GuestProfileMappings
{
    public static GuestProfileDto ToDto(this GuestProfile profile) => new(
        profile.Id,
        profile.OriginPropertyId,
        profile.DisplayName,
        profile.LegalName,
        profile.Email,
        profile.Phone,
        profile.DateOfBirth,
        profile.NationalityCountryCode,
        profile.PreferredLanguageTag,
        profile.Notes,
        MapStatus(profile.Status),
        profile.Version,
        profile.CreatedBy,
        profile.CreatedAtUtc,
        profile.LastChangedBy,
        profile.LastChangedAtUtc,
        profile.ArchivedAtUtc)
    {
        LanguageTags = profile.LanguageTags
    };

    public static GuestMutationReceiptDto ToMutationReceipt(this GuestProfile profile) => new(
        profile.Id,
        MapStatus(profile.Status),
        profile.Version,
        profile.LastChangedAtUtc);

    public static GuestStatus MapStatus(GuestProfileState status) => status switch
    {
        GuestProfileState.Active => GuestStatus.Active,
        GuestProfileState.Archived => GuestStatus.Archived,
        GuestProfileState.Unknown => GuestStatus.Unknown,
        _ => GuestStatus.Unknown
    };
}
