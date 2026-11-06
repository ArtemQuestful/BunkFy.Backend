namespace BunkFy.Modules.Guests.Application.Commands;

using BunkFy.Modules.Guests.Application.Ports;
using Gma.Framework.Cqrs;
using BunkFy.Modules.Guests.Contracts;

public sealed record CreateGuestProfileCommand(
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
    string[]? LanguageTags = null) :
    ITransactionalCommand<GuestMutationReceiptDto>,
    IGuestsPersistenceRetryableCommand;
