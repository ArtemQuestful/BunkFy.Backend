namespace BunkFy.Extensions.ReservationGuestRecords;

using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Reservations.Contracts;
using Gma.Framework.Results;

internal sealed class ReservationGuestRecordWorkflow(
    IGuestProfileCreationCapability guests,
    IReservationGuestRecordLinkCapability reservations)
{
    public async Task<Result<ReservationGuestRecordLinkProcessDto>> ExecuteAsync(
        ReservationGuestRecordWorkflowRequest request,
        string actorId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Result<ReservationGuestRecordLinkPreparationDto> prepared =
            await reservations.PrepareAsync(
                new(
                    request.OperationId,
                    request.PropertyId,
                    request.ReservationId,
                    request.ExpectedReservationVersion,
                    actorId),
                cancellationToken).ConfigureAwait(false);
        if (prepared.IsFailure)
        {
            return Result.Failure<ReservationGuestRecordLinkProcessDto>(
                prepared.Error);
        }

        ReservationGuestRecordLinkProcessDto process = prepared.Value.Process;
        Guid operationId = process.OperationId;
        if (process.Status == ReservationGuestRecordLinkStatus.Completed)
        {
            return Result.Success(process);
        }

        if (process.Status == ReservationGuestRecordLinkStatus.NeedsReview)
        {
            return await reservations.RetryAsync(
                new(
                    operationId,
                    request.PropertyId,
                    request.ReservationId),
                cancellationToken).ConfigureAwait(false);
        }

        if (process.Status == ReservationGuestRecordLinkStatus.Ready)
        {
            return Result.Success(process);
        }

        if (process.Status != ReservationGuestRecordLinkStatus.Prepared)
        {
            return Result.Failure<ReservationGuestRecordLinkProcessDto>(
                ReservationGuestRecordWorkflowErrors.ProcessStateInvalid);
        }

        if (string.IsNullOrWhiteSpace(prepared.Value.GuestCreationActorId))
        {
            return Result.Failure<ReservationGuestRecordLinkProcessDto>(
                ReservationGuestRecordWorkflowErrors.ProcessStateInvalid);
        }

        Result<GuestMutationReceiptDto> guest = await guests.EnsureCreatedAsync(
            new(
                operationId,
                request.PropertyId,
                request.DisplayName,
                request.LegalName,
                request.Email,
                request.Phone,
                request.DateOfBirth,
                request.NationalityCountryCode,
                request.PreferredLanguageTag,
                request.Notes,
                prepared.Value.GuestCreationActorId,
                prepared.Value.CreationConfirmationId,
                request.LanguageTags),
            cancellationToken).ConfigureAwait(false);
        if (guest.IsFailure)
        {
            return Result.Failure<ReservationGuestRecordLinkProcessDto>(
                guest.Error);
        }

        if (guest.Value.GuestId != operationId)
        {
            return Result.Failure<ReservationGuestRecordLinkProcessDto>(
                ReservationGuestRecordWorkflowErrors.GuestIdentityMismatch);
        }

        return await reservations.ConfirmGuestAsync(
            new(
                operationId,
                request.PropertyId,
                request.ReservationId,
                prepared.Value.CreationConfirmationId),
            cancellationToken).ConfigureAwait(false);
    }

    public Task<Result<ReservationGuestRecordLinkProcessDto>> GetAsync(
        Guid operationId,
        Guid propertyId,
        Guid reservationId,
        CancellationToken cancellationToken) =>
        reservations.GetAsync(
            new(operationId, propertyId, reservationId),
            cancellationToken);
}

internal sealed record ReservationGuestRecordWorkflowRequest(
    Guid OperationId,
    Guid PropertyId,
    Guid ReservationId,
    long ExpectedReservationVersion,
    string DisplayName,
    string? LegalName,
    string? Email,
    string? Phone,
    DateOnly? DateOfBirth,
    string? NationalityCountryCode,
    string? PreferredLanguageTag,
    string? Notes,
    string[]? LanguageTags = null);

internal static class ReservationGuestRecordWorkflowErrors
{
    public static readonly Error ProcessStateInvalid = new(
        "ReservationGuestRecords.ProcessStateInvalid",
        "The Reservation Guest Record workflow state is invalid.");

    public static readonly Error GuestIdentityMismatch = new(
        "ReservationGuestRecords.GuestIdentityMismatch",
        "The Guests capability returned a different operation identity.");
}
