namespace BunkFy.Modules.Guests.Application.Capabilities;

using BunkFy.Modules.Guests.Application.Commands;
using BunkFy.Modules.Guests.Contracts;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;

internal sealed class GuestProfileCreationCapability(IRequestDispatcher dispatcher)
    : IGuestProfileCreationCapability
{
    public Task<Result<GuestMutationReceiptDto>> EnsureCreatedAsync(
        GuestProfileCreationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return dispatcher.SendAsync(
            new CreateGuestProfileCommand(
                request.OperationId,
                request.PropertyId,
                request.DisplayName,
                request.LegalName,
                request.Email,
                request.Phone,
                request.DateOfBirth,
                request.NationalityCountryCode,
                request.PreferredLanguageTag,
                request.Notes,
                request.ActorId,
                request.CreationConfirmationId,
                request.LanguageTags),
            cancellationToken);
    }
}
