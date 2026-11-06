namespace BunkFy.Modules.Guests.Contracts;

using Gma.Framework.Results;

public interface IGuestProfileCreationCapability
{
    Task<Result<GuestMutationReceiptDto>> EnsureCreatedAsync(
        GuestProfileCreationRequest request,
        CancellationToken cancellationToken);
}

public sealed record GuestProfileCreationRequest(
    Guid OperationId,
    Guid PropertyId,
    string DisplayName,
    string? LegalName,
    string? Email,
    string? Phone,
    DateOnly? DateOfBirth,
    string? NationalityCountryCode,
    string? PreferredLanguageTag,
    string? Notes,
    string ActorId,
    Guid? CreationConfirmationId = null,
    string[]? LanguageTags = null);
