namespace BunkFy.Modules.Guests.Domain.Aggregates;

using BunkFy.Modules.Guests.Domain.Errors;
using BunkFy.Modules.Guests.Domain.ValueObjects;
using Gma.Framework.Results;

public sealed class GuestProfileCreationSnapshot
{
    private GuestProfileCreationSnapshot(
        Guid originPropertyId,
        Guid? creationConfirmationId,
        GuestProfileChange values)
    {
        this.OriginPropertyId = originPropertyId;
        this.CreationConfirmationId = creationConfirmationId;
        this.Values = values;
    }

    public Guid OriginPropertyId { get; }
    public Guid? CreationConfirmationId { get; }

    internal GuestProfileChange Values { get; }

    public static Result<GuestProfileCreationSnapshot> Capture(
        Guid originPropertyId,
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
        Guid? creationConfirmationId = null,
        string[]? languageTags = null)
    {
        if (creationConfirmationId == Guid.Empty)
        {
            return Result.Failure<GuestProfileCreationSnapshot>(
                GuestsDomainErrors.CreationConfirmationInvalid);
        }

        Result<GuestProfileChange> values = GuestProfileChange.Create(
            displayName,
            legalName,
            email,
            phone,
            dateOfBirth,
            nationalityCountryCode,
            preferredLanguageTag,
            notes,
            actorId,
            nowUtc,
            languageTags);
        return values.IsSuccess
            ? Result.Success(new GuestProfileCreationSnapshot(
                originPropertyId,
                creationConfirmationId,
                values.Value with
                {
                    LanguageTags = GuestLanguageTags.ForCreation(
                        values.Value.LanguageTags, values.Value.PreferredLanguageTag)
                }))
            : Result.Failure<GuestProfileCreationSnapshot>(values.Error);
    }
}
