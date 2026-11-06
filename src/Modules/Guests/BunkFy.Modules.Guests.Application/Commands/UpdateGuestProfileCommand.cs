namespace BunkFy.Modules.Guests.Application.Commands;

using BunkFy.Modules.Guests.Application.Ports;
using Gma.Framework.Cqrs;
using BunkFy.Modules.Guests.Contracts;

public sealed record UpdateGuestProfileCommand(
    Guid OperationId,
    Guid PropertyId,
    Guid GuestId,
    string DisplayName,
    string? LegalName,
    string? Email,
    string? Phone,
    DateOnly? DateOfBirth,
    string? NationalityCountryCode,
    string? PreferredLanguageTag,
    string? Notes,
    long ExpectedVersion,
    string ActorId,
    string[]? LanguageTags = null) :
    ITransactionalCommand<GuestMutationReceiptDto>,
    IGuestsPersistenceRetryableCommand;
