namespace BunkFy.Modules.Guests.Domain.ValueObjects;

using System.Net.Mail;
using Gma.Framework.Results;
using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.Errors;

public sealed record GuestProfileChange(
    string DisplayName,
    string? LegalName,
    string? Email,
    string? Phone,
    DateOnly? DateOfBirth,
    string? NationalityCountryCode,
    string? PreferredLanguageTag,
    string? Notes,
    string ActorId,
    string[]? LanguageTags = null)
{
    public static Result<GuestProfileChange> Create(
        string? displayName,
        string? legalName,
        string? email,
        string? phone,
        DateOnly? dateOfBirth,
        string? nationalityCountryCode,
        string? preferredLanguageTag,
        string? notes,
        string? actorId,
        DateTimeOffset nowUtc,
        string[]? languageTags = null)
    {
        string normalizedDisplayName = displayName?.Trim() ?? string.Empty;
        if (normalizedDisplayName.Length is 0 or > GuestProfile.DisplayNameMaxLength)
        {
            return Result.Failure<GuestProfileChange>(GuestsDomainErrors.DisplayNameInvalid);
        }

        string normalizedActor = actorId?.Trim() ?? string.Empty;
        if (normalizedActor.Length is 0 or > GuestProfile.ActorIdMaxLength)
        {
            return Result.Failure<GuestProfileChange>(GuestsDomainErrors.ActorInvalid);
        }

        string? normalizedLegalName = NormalizeOptional(legalName);
        if (normalizedLegalName?.Length > GuestProfile.LegalNameMaxLength)
        {
            return Result.Failure<GuestProfileChange>(GuestsDomainErrors.LegalNameInvalid);
        }

        string? normalizedEmail = NormalizeOptional(email)?.ToLowerInvariant();
        if (normalizedEmail is not null &&
            (normalizedEmail.Length > GuestProfile.EmailMaxLength || !MailAddress.TryCreate(normalizedEmail, out _)))
        {
            return Result.Failure<GuestProfileChange>(GuestsDomainErrors.EmailInvalid);
        }

        string? normalizedPhone = NormalizeOptional(phone);
        if (normalizedPhone?.Length > GuestProfile.PhoneMaxLength)
        {
            return Result.Failure<GuestProfileChange>(GuestsDomainErrors.PhoneInvalid);
        }

        if (dateOfBirth > DateOnly.FromDateTime(nowUtc.UtcDateTime))
        {
            return Result.Failure<GuestProfileChange>(GuestsDomainErrors.DateOfBirthInvalid);
        }

        string? normalizedCountry = NormalizeOptional(nationalityCountryCode)?.ToUpperInvariant();
        if (normalizedCountry is not null &&
            (normalizedCountry.Length != GuestProfile.CountryCodeLength || !normalizedCountry.All(IsAsciiLetter)))
        {
            return Result.Failure<GuestProfileChange>(GuestsDomainErrors.NationalityInvalid);
        }

        string? normalizedLanguage = NormalizeOptional(preferredLanguageTag);
        if (normalizedLanguage is not null &&
            (normalizedLanguage.Length > GuestProfile.LanguageTagMaxLength ||
             !normalizedLanguage.All(character => IsAsciiLetter(character) ||
                 char.IsDigit(character) || character == '-')))
        {
            return Result.Failure<GuestProfileChange>(GuestsDomainErrors.LanguageTagInvalid);
        }

        string[]? normalizedTags = null;
        if (languageTags is not null)
        {
            Result<string[]> tags = GuestLanguageTags.Normalize(languageTags, normalizedLanguage);
            if (tags.IsFailure)
            {
                return Result.Failure<GuestProfileChange>(tags.Error);
            }

            normalizedTags = tags.Value;
        }

        string? normalizedNotes = NormalizeOptional(notes);
        if (normalizedNotes?.Length > GuestProfile.NotesMaxLength)
        {
            return Result.Failure<GuestProfileChange>(GuestsDomainErrors.NotesInvalid);
        }

        return Result.Success(new GuestProfileChange(
            normalizedDisplayName,
            normalizedLegalName,
            normalizedEmail,
            normalizedPhone,
            dateOfBirth,
            normalizedCountry,
            normalizedLanguage,
            normalizedNotes,
            normalizedActor,
            normalizedTags));
    }

    private static string? NormalizeOptional(string? value)
    {
        string normalized = value?.Trim() ?? string.Empty;
        return normalized.Length == 0 ? null : normalized;
    }

    private static bool IsAsciiLetter(char character) =>
        character is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z');
}
