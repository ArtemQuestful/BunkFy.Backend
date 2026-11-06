namespace BunkFy.Modules.Guests.Application.Validation;

using Gma.Framework.Cqrs;
using BunkFy.Modules.Guests.Application.Commands;
using BunkFy.Modules.Guests.Application.Queries;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Guests.Domain.ValueObjects;

internal static class GuestProfileValidation
{
    public static IEnumerable<string> Write(
        Guid propertyId,
        Guid? guestId,
        string displayName,
        string? legalName,
        string? email,
        string? phone,
        string? nationalityCountryCode,
        string? preferredLanguageTag,
        string? notes,
        long? expectedVersion,
        string actorId,
        string[]? languageTags = null)
    {
        if (propertyId == Guid.Empty)
        {
            yield return "PropertyId is required.";
        }

        if (guestId == Guid.Empty)
        {
            yield return "GuestId is required.";
        }

        if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > GuestsContractLimits.DisplayNameMaxLength)
        {
            yield return "DisplayName is required and must be within the supported limit.";
        }

        if (legalName?.Trim().Length > GuestsContractLimits.LegalNameMaxLength ||
            email?.Trim().Length > GuestsContractLimits.EmailMaxLength ||
            phone?.Trim().Length > GuestsContractLimits.PhoneMaxLength ||
            notes?.Trim().Length > GuestsContractLimits.NotesMaxLength)
        {
            yield return "One or more guest profile fields exceed their supported limits.";
        }

        if (nationalityCountryCode is not null &&
            nationalityCountryCode.Trim().Length is not (0 or GuestsContractLimits.CountryCodeLength))
        {
            yield return "NationalityCountryCode must be a two-letter country code.";
        }

        if (preferredLanguageTag?.Trim().Length > GuestsContractLimits.LanguageTagMaxLength)
        {
            yield return "PreferredLanguageTag exceeds the supported limit.";
        }

        if (languageTags is not null && GuestLanguageTags.Normalize(
                languageTags,
                string.IsNullOrWhiteSpace(preferredLanguageTag) ? null : preferredLanguageTag.Trim()).IsFailure)
        {
            yield return "LanguageTags must be at most 12 valid tags and include a supplied preferred language.";
        }

        if (expectedVersion.HasValue && expectedVersion.Value <= 0)
        {
            yield return "ExpectedVersion must be greater than zero.";
        }

        string normalizedActor = actorId?.Trim() ?? string.Empty;
        if (normalizedActor.Length is 0 or > GuestsContractLimits.ActorIdMaxLength)
        {
            yield return "ActorId is required and must be within the supported limit.";
        }
    }
}

internal sealed class CreateGuestProfileCommandValidator : ICommandValidator<CreateGuestProfileCommand>
{
    public IEnumerable<string> Validate(CreateGuestProfileCommand command)
    {
        if (command.CreationConfirmationId == Guid.Empty)
        {
            yield return "CreationConfirmationId must not be empty when provided.";
        }

        foreach (string failure in GuestProfileValidation.Write(
                     command.PropertyId,
                     command.OperationId,
                     command.DisplayName,
                     command.LegalName,
                     command.Email,
                     command.Phone,
                     command.NationalityCountryCode,
                     command.PreferredLanguageTag,
                     command.Notes,
                     expectedVersion: null,
                     command.ActorId,
                     command.LanguageTags))
        {
            yield return failure;
        }
    }
}

internal sealed class UpdateGuestProfileCommandValidator : ICommandValidator<UpdateGuestProfileCommand>
{
    public IEnumerable<string> Validate(UpdateGuestProfileCommand command)
    {
        if (command.OperationId == Guid.Empty)
        {
            yield return "OperationId is required.";
        }

        foreach (string failure in GuestProfileValidation.Write(
                     command.PropertyId,
                     command.GuestId,
                     command.DisplayName,
                     command.LegalName,
                     command.Email,
                     command.Phone,
                     command.NationalityCountryCode,
                     command.PreferredLanguageTag,
                     command.Notes,
                     command.ExpectedVersion,
                     command.ActorId,
                     command.LanguageTags))
        {
            yield return failure;
        }
    }
}

internal sealed class ArchiveGuestProfileCommandValidator : ICommandValidator<ArchiveGuestProfileCommand>
{
    public IEnumerable<string> Validate(ArchiveGuestProfileCommand command)
    {
        if (command.OperationId == Guid.Empty)
        {
            yield return "OperationId is required.";
        }

        if (command.PropertyId == Guid.Empty || command.GuestId == Guid.Empty)
        {
            yield return "PropertyId and GuestId are required.";
        }

        if (command.ExpectedVersion <= 0)
        {
            yield return "ExpectedVersion must be greater than zero.";
        }

        string actor = command.ActorId?.Trim() ?? string.Empty;
        if (actor.Length is 0 or > GuestsContractLimits.ActorIdMaxLength)
        {
            yield return "ActorId is required and must be within the supported limit.";
        }
    }
}

internal sealed class GetGuestProfileQueryValidator : IQueryValidator<GetGuestProfileQuery>
{
    public IEnumerable<string> Validate(GetGuestProfileQuery query)
    {
        if (query.PropertyId == Guid.Empty || query.GuestId == Guid.Empty)
        {
            yield return "PropertyId and GuestId are required.";
        }
    }
}

internal sealed class ListGuestProfilesQueryValidator : IQueryValidator<ListGuestProfilesQuery>
{
    public IEnumerable<string> Validate(ListGuestProfilesQuery query)
    {
        if (query.PropertyId == Guid.Empty)
        {
            yield return "PropertyId is required.";
        }

        if (query.Search?.Trim().Length > GuestsContractLimits.SearchMaxLength)
        {
            yield return "Search exceeds the supported limit.";
        }

        if (query.Status.HasValue &&
            (query.Status.Value == GuestStatus.Unknown || !Enum.IsDefined(query.Status.Value)))
        {
            yield return "Status is invalid.";
        }
    }
}
