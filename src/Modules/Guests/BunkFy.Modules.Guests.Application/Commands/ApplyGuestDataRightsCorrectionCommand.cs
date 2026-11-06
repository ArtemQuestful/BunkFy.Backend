namespace BunkFy.Modules.Guests.Application.Commands;

using BunkFy.Modules.Guests.Contracts;
using Gma.Framework.Cqrs;

public sealed record ApplyGuestDataRightsCorrectionCommand(
    Guid IdempotencyKey,
    Guid PropertyId,
    Guid CaseId,
    long ApprovalRevision,
    Guid GuestId,
    long ExpectedVersion,
    string DisplayName,
    string? LegalName,
    string? Email,
    string? Phone,
    DateOnly? DateOfBirth,
    string? NationalityCountryCode,
    string? PreferredLanguageTag,
    string? Notes,
    string ActorId,
    string[]? LanguageTags = null) : ITransactionalCommand<GuestDataRightsCorrectionReceiptDto>;
