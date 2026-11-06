namespace BunkFy.Modules.Guests.Application.Mapping;

using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Guests.Domain.DataRights;
using BunkFy.Modules.Guests.Domain.Models;

internal static class GuestDataRightsCorrectionMappings
{
    public static GuestDataRightsCorrectionReceiptDto ToDto(
        this GuestDataRightsCorrectionReceipt receipt) => new(
        receipt.Id,
        receipt.CaseId,
        receipt.ApprovalRevision,
        receipt.GuestId,
        receipt.SelectedRecordVersion,
        receipt.CurrentRecordVersion,
        receipt.ChangedFields.Select(ToFieldKey).ToArray(),
        receipt.EventId,
        receipt.CompletedAtUtc);

    public static string ToFieldKey(this GuestProfileField field) => field switch
    {
        GuestProfileField.DisplayName => "guest.profile.display-name",
        GuestProfileField.LegalName => "guest.profile.legal-name",
        GuestProfileField.Email => "guest.profile.email",
        GuestProfileField.Phone => "guest.profile.phone",
        GuestProfileField.DateOfBirth => "guest.profile.date-of-birth",
        GuestProfileField.NationalityCountryCode => "guest.profile.nationality-country-code",
        GuestProfileField.PreferredLanguageTag => "guest.profile.preferred-language-tag",
        GuestProfileField.LanguageTags => "guest.profile.language-tags",
        GuestProfileField.Notes => "guest.profile.notes",
        _ => throw new InvalidOperationException("The correction receipt contains an unknown field.")
    };
}
