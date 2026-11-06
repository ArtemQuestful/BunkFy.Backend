namespace BunkFy.Modules.Guests.Domain.Aggregates;

using BunkFy.Modules.Guests.Domain.Models;
using BunkFy.Modules.Guests.Domain.ValueObjects;

public sealed partial class GuestProfile
{
    private GuestProfileField[] GetChangedFields(GuestProfileChange values, string[] resolvedTags)
    {
        List<GuestProfileField> changed = [];
        AddIfChanged(changed, GuestProfileField.DisplayName, this.DisplayName, values.DisplayName);
        AddIfChanged(changed, GuestProfileField.LegalName, this.LegalName, values.LegalName);
        AddIfChanged(changed, GuestProfileField.Email, this.Email, values.Email);
        AddIfChanged(changed, GuestProfileField.Phone, this.Phone, values.Phone);
        if (this.DateOfBirth != values.DateOfBirth)
        {
            changed.Add(GuestProfileField.DateOfBirth);
        }

        AddIfChanged(
            changed,
            GuestProfileField.NationalityCountryCode,
            this.NationalityCountryCode,
            values.NationalityCountryCode);
        AddIfChanged(
            changed,
            GuestProfileField.PreferredLanguageTag,
            this.PreferredLanguageTag,
            values.PreferredLanguageTag);
        AddIfChanged(changed, GuestProfileField.Notes, this.Notes, values.Notes);
        if (!GuestLanguageTags.Equivalent(this.languageTags, resolvedTags))
        {
            changed.Add(GuestProfileField.LanguageTags);
        }

        return changed.ToArray();
    }

    private static void AddIfChanged(
        List<GuestProfileField> changed,
        GuestProfileField field,
        string? current,
        string? requested)
    {
        if (!string.Equals(current, requested, StringComparison.Ordinal))
        {
            changed.Add(field);
        }
    }
}
