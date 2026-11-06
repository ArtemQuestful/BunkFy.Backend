namespace BunkFy.Modules.Reservations.Application.Handlers;

using BunkFy.Modules.Reservations.Application.Commands;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Application.StayAmendments;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Domain.Errors;
using BunkFy.Modules.Reservations.Domain.StayAmendments;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;

internal sealed class ReconcileReservationStayAmendmentCommandHandler(
    ReservationMutationCoordinator mutations,
    IReservationStayAmendmentOperationRepository operations,
    ISystemClock clock,
    IIdGenerator idGenerator)
    : ICommandHandler<
        ReconcileReservationStayAmendmentCommand,
        ReservationStayAmendmentReceiptDto>
{
    public async Task<Result<ReservationStayAmendmentReceiptDto>> HandleAsync(
        ReconcileReservationStayAmendmentCommand command,
        CancellationToken cancellationToken)
    {
        Reservation? reservation = await mutations.AcquireOperationalAsync(
            command.PropertyId,
            command.ReservationId,
            cancellationToken).ConfigureAwait(false);
        if (reservation is null)
        {
            return Result.Failure<ReservationStayAmendmentReceiptDto>(
                ReservationsApplicationErrors.ReservationNotFound);
        }

        ReservationStayAmendmentOperation? operation = await operations.GetAsync(
            command.PropertyId,
            command.ReservationId,
            command.OperationId,
            cancellationToken).ConfigureAwait(false);
        if (operation is null)
        {
            return Result.Failure<ReservationStayAmendmentReceiptDto>(
                ReservationsApplicationErrors.StayAmendmentOperationNotFound);
        }

        DateTimeOffset nowUtc = clock.UtcNow;
        Result valid = operation.CanReconcile(
            command.ExpectedOperationVersion,
            command.ActorId,
            nowUtc);
        if (valid.IsFailure)
        {
            return Result.Failure<ReservationStayAmendmentReceiptDto>(MapReconcileError(valid.Error));
        }

        if (!operation.TargetArrival.HasValue || !operation.TargetDeparture.HasValue ||
            operation.TargetInventoryUnitIds is null ||
            !operation.InventoryRequestId.HasValue ||
            reservation.PendingInventoryAmendmentRequestId != operation.InventoryRequestId)
        {
            return Result.Failure<ReservationStayAmendmentReceiptDto>(
                ReservationsApplicationErrors.StayAmendmentReconcileInvalid);
        }

        Result republished = reservation.RepublishPendingAllocationAmendment(
            operation.Id,
            operation.RequestFingerprint,
            operation.TargetArrival.Value,
            operation.TargetDeparture.Value,
            operation.TargetExpectedArrivalTime,
            operation.TargetExpectedDepartureTime,
            operation.GetTargetInventoryUnitIds(),
            idGenerator.NewId(),
            nowUtc);
        if (republished.IsFailure)
        {
            return Result.Failure<ReservationStayAmendmentReceiptDto>(
                ReservationsApplicationErrors.StayAmendmentReconcileInvalid);
        }

        Result reconciled = operation.Reconcile(
            command.ExpectedOperationVersion,
            command.ActorId,
            nowUtc);
        if (reconciled.IsFailure)
        {
            throw new InvalidOperationException(
                "A validated stay-amendment reconciliation changed before completion.");
        }

        return Result.Success(operation.ToReceipt(nowUtc));
    }

    private static Error MapReconcileError(Error error)
    {
        if (error == ReservationsDomainErrors.StayAmendmentOperationVersionConflict)
        {
            return ReservationsApplicationErrors.StayAmendmentOperationVersionConflict;
        }

        return error == ReservationsDomainErrors.StayAmendmentReconcileTooSoon
            ? ReservationsApplicationErrors.StayAmendmentReconcileTooSoon
            : ReservationsApplicationErrors.StayAmendmentReconcileInvalid;
    }
}
