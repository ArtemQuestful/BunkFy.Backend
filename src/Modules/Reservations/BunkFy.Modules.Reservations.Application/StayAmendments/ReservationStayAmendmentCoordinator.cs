namespace BunkFy.Modules.Reservations.Application.StayAmendments;

using BunkFy.Modules.Reservations.Application.Commands;
using BunkFy.Modules.Reservations.Application.Handlers;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Domain.StayAmendments;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;

internal sealed class ReservationStayAmendmentCoordinator(
    ReservationMutationCoordinator mutations,
    IInventoryProjectionRepository inventoryProjection,
    IReservationManagementOperationRepository managementOperations,
    IReservationStayAmendmentOperationRepository stayOperations,
    ISystemClock clock,
    IIdGenerator idGenerator)
{
    public async Task<Result<ReservationStayAmendmentReceiptDto>> AmendAsync(
        AmendReservationStayCommand command,
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

        string actorId = command.ActorId?.Trim() ?? string.Empty;
        if (command.OperationId == Guid.Empty ||
            !ReservationStayAmendmentCoordinationSupport.IsValidActor(actorId))
        {
            return Result.Failure<ReservationStayAmendmentReceiptDto>(
                ReservationsApplicationErrors.StayAmendmentRequestInvalid);
        }

        string fingerprint = ReservationStayAmendmentFingerprint.ComputeV2(command);
        ReservationManagementOperationRecord? management = await managementOperations
            .GetAsync(command.ReservationId, command.OperationId, cancellationToken)
            .ConfigureAwait(false);
        ReservationStayAmendmentOperation? existing = await stayOperations
            .GetAsync(command.PropertyId, command.ReservationId, command.OperationId, cancellationToken)
            .ConfigureAwait(false);
        if (management is not null || existing is not null)
        {
            return management?.MatchesStayAmendment(
                    command.ExpectedDetailsRevision,
                    fingerprint) == true &&
                existing?.MatchesRequest(
                    ReservationStayAmendmentOperation.CurrentRequestSchemaVersion,
                    fingerprint) == true
                ? Result.Success(existing.ToReceipt(clock.UtcNow))
                : Result.Failure<ReservationStayAmendmentReceiptDto>(
                    ReservationsApplicationErrors.StayAmendmentOperationConflict);
        }

        if (reservation.PendingAllocationAmendmentId == command.OperationId &&
            (reservation.PendingDetailsChangeOrigin != ReservationDetailsChangeOrigin.Staff ||
                !string.Equals(
                    reservation.PendingAllocationAmendmentRequestFingerprint,
                    fingerprint,
                    StringComparison.Ordinal)))
        {
            return Result.Failure<ReservationStayAmendmentReceiptDto>(
                ReservationsApplicationErrors.AllocationAmendmentInProgress);
        }

        if (reservation.PendingAllocationAmendmentId == command.OperationId)
        {
            return Result.Failure<ReservationStayAmendmentReceiptDto>(
                ReservationsApplicationErrors.StayAmendmentOperationConflict);
        }

        Result selection = await ReservationStayAmendmentCoordinationSupport.ValidateSelectionAsync(
            inventoryProjection,
            command.PropertyId,
            command.InventoryUnitIds,
            cancellationToken).ConfigureAwait(false);
        if (selection.IsFailure)
        {
            return Result.Failure<ReservationStayAmendmentReceiptDto>(selection.Error);
        }

        DateTimeOffset nowUtc = clock.UtcNow;
        Guid inventoryRequestId = idGenerator.NewId();
        Result<ReservationDetailsChangeOutcome> begun = reservation.BeginAllocationAmendment(
            command.OperationId,
            inventoryRequestId,
            fingerprint,
            command.Arrival,
            command.Departure,
            command.InventoryUnitIds,
            reservation.PrimaryGuestName,
            reservation.Email,
            reservation.Phone,
            reservation.GuestCount,
            reservation.Notes,
            command.ExpectedDetailsRevision,
            ReservationDetailsChangeOrigin.Staff,
            actorId,
            adapterConnectionId: null,
            externalOperationId: null,
            command.OperationId,
            idGenerator.NewId(),
            nowUtc,
            command.ExpectedArrivalTime,
            command.ExpectedDepartureTime);
        if (begun.IsFailure)
        {
            return Result.Failure<ReservationStayAmendmentReceiptDto>(begun.Error);
        }

        if (begun.Value == ReservationDetailsChangeOutcome.Unchanged)
        {
            Result advanced = reservation.AdvanceStayAmendmentEvidenceCoordinate(
                nowUtc);
            if (advanced.IsFailure)
            {
                return Result.Failure<ReservationStayAmendmentReceiptDto>(
                    ReservationsApplicationErrors.StayAmendmentRequestInvalid);
            }
        }

        Result<ReservationStayAmendmentOperation> created = begun.Value == ReservationDetailsChangeOutcome.Changed
            ? ReservationStayAmendmentOperation.CreatePending(
                command.OperationId,
                reservation.ScopeId,
                command.PropertyId,
                command.ReservationId,
                inventoryRequestId,
                ReservationStayAmendmentOperation.CurrentRequestSchemaVersion,
                fingerprint,
                command.Arrival,
                command.Departure,
                command.ExpectedArrivalTime,
                command.ExpectedDepartureTime,
                command.InventoryUnitIds,
                command.ExpectedDetailsRevision,
                actorId,
                nowUtc)
            : ReservationStayAmendmentOperation.CreateAppliedNoOp(
                command.OperationId,
                reservation.ScopeId,
                command.PropertyId,
                command.ReservationId,
                ReservationStayAmendmentOperation.CurrentRequestSchemaVersion,
                fingerprint,
                command.Arrival,
                command.Departure,
                command.ExpectedArrivalTime,
                command.ExpectedDepartureTime,
                command.InventoryUnitIds,
                command.ExpectedDetailsRevision,
                actorId,
                reservation.DetailsRevision,
                reservation.Version,
                reservation.AllocationVersion!.Value,
                nowUtc);
        if (created.IsFailure)
        {
            return Result.Failure<ReservationStayAmendmentReceiptDto>(
                ReservationsApplicationErrors.StayAmendmentRequestInvalid);
        }

        await managementOperations.AddAsync(
            ReservationStayAmendmentCoordinationSupport.CreateManagementOperation(
                reservation,
                command.OperationId,
                ReservationManagementOperationKind.StayAmendment,
                command.ExpectedDetailsRevision,
                fingerprint,
                nowUtc),
            cancellationToken).ConfigureAwait(false);
        await stayOperations.AddAsync(created.Value, cancellationToken).ConfigureAwait(false);
        return Result.Success(created.Value.ToReceipt(nowUtc));
    }

    public async Task<Result<ReservationMutationReceiptDto>> ReassignInventoryAsync(
        ReassignReservationInventoryCommand command,
        CancellationToken cancellationToken)
    {
        Reservation? reservation = await mutations.AcquireOperationalAsync(
            command.PropertyId,
            command.ReservationId,
            cancellationToken).ConfigureAwait(false);
        if (reservation is null)
        {
            return Result.Failure<ReservationMutationReceiptDto>(
                ReservationsApplicationErrors.ReservationNotFound);
        }

        string actorId = command.ActorId?.Trim() ?? string.Empty;
        if (command.AmendmentRequestId == Guid.Empty ||
            !ReservationStayAmendmentCoordinationSupport.IsValidActor(actorId))
        {
            return Result.Failure<ReservationMutationReceiptDto>(
                command.AmendmentRequestId == Guid.Empty
                    ? ReservationsApplicationErrors.ManagementOperationInvalid
                    : ReservationsApplicationErrors.DetailsChangeProvenanceInvalid);
        }

        string fingerprint = ReservationStayAmendmentFingerprint.ComputeLegacyV1(command);
        ReservationManagementOperationRecord? management = await managementOperations
            .GetAsync(command.ReservationId, command.AmendmentRequestId, cancellationToken)
            .ConfigureAwait(false);
        if (management is not null)
        {
            ReservationStayAmendmentOperation? durableOperation = await stayOperations
                .GetAsync(
                    command.PropertyId,
                    command.ReservationId,
                    command.AmendmentRequestId,
                    cancellationToken).ConfigureAwait(false);
            return management.MatchesInventoryAmendment(
                       command.ExpectedDetailsRevision,
                       fingerprint) &&
                   durableOperation?.MatchesRequest(
                       ReservationStayAmendmentOperation.LegacyRequestSchemaVersion,
                       fingerprint) == true
                ? Result.Success(reservation.ToMutationReceipt())
                : Result.Failure<ReservationMutationReceiptDto>(
                    ReservationsApplicationErrors.ManagementOperationConflict);
        }

        ReservationStayAmendmentOperation? existing = await stayOperations
            .GetAsync(
                command.PropertyId,
                command.ReservationId,
                command.AmendmentRequestId,
                cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return Result.Failure<ReservationMutationReceiptDto>(
                ReservationsApplicationErrors.ManagementOperationConflict);
        }

        if (reservation.PendingAllocationAmendmentId == command.AmendmentRequestId &&
            (reservation.PendingDetailsChangeOrigin != ReservationDetailsChangeOrigin.Staff ||
                !string.Equals(
                    reservation.PendingAllocationAmendmentRequestFingerprint,
                    fingerprint,
                    StringComparison.Ordinal)))
        {
            return Result.Failure<ReservationMutationReceiptDto>(
                ReservationsApplicationErrors.AllocationAmendmentInProgress);
        }

        if (reservation.PendingAllocationAmendmentId == command.AmendmentRequestId)
        {
            // The migration backfills every exact predecessor row, and current
            // writes persist the reservation, parent, and child atomically.
            // A pending reservation without its evidence is therefore a
            // corrupt graph; do not manufacture attribution on a retry.
            return Result.Failure<ReservationMutationReceiptDto>(
                ReservationsApplicationErrors.ManagementOperationConflict);
        }

        Result selection = await ReservationStayAmendmentCoordinationSupport.ValidateSelectionAsync(
            inventoryProjection,
            command.PropertyId,
            command.InventoryUnitIds,
            cancellationToken).ConfigureAwait(false);
        if (selection.IsFailure)
        {
            return Result.Failure<ReservationMutationReceiptDto>(selection.Error);
        }

        DateTimeOffset nowUtc = clock.UtcNow;
        Guid inventoryRequestId = idGenerator.NewId();
        Result<ReservationDetailsChangeOutcome> begun = reservation.BeginAllocationAmendment(
            command.AmendmentRequestId,
            inventoryRequestId,
            fingerprint,
            reservation.Arrival,
            reservation.Departure,
            command.InventoryUnitIds,
            reservation.PrimaryGuestName,
            reservation.Email,
            reservation.Phone,
            reservation.GuestCount,
            reservation.Notes,
            command.ExpectedDetailsRevision,
            ReservationDetailsChangeOrigin.Staff,
            actorId,
            adapterConnectionId: null,
            externalOperationId: null,
            command.AmendmentRequestId,
            idGenerator.NewId(),
            nowUtc,
            reservation.ExpectedArrivalTime,
            reservation.ExpectedDepartureTime);
        if (begun.IsFailure)
        {
            return Result.Failure<ReservationMutationReceiptDto>(begun.Error);
        }

        if (begun.Value == ReservationDetailsChangeOutcome.Unchanged)
        {
            return Result.Success(reservation.ToMutationReceipt());
        }

        Result<ReservationStayAmendmentOperation> created =
            ReservationStayAmendmentOperation.CreatePending(
                command.AmendmentRequestId,
                reservation.ScopeId,
                command.PropertyId,
                command.ReservationId,
                inventoryRequestId,
                ReservationStayAmendmentOperation.LegacyRequestSchemaVersion,
                fingerprint,
                reservation.PendingArrival!.Value,
                reservation.PendingDeparture!.Value,
                reservation.PendingExpectedArrivalTime,
                reservation.PendingExpectedDepartureTime,
                command.InventoryUnitIds,
                command.ExpectedDetailsRevision,
                actorId,
                nowUtc);
        if (created.IsFailure)
        {
            return Result.Failure<ReservationMutationReceiptDto>(
                ReservationsApplicationErrors.ManagementOperationInvalid);
        }

        await managementOperations.AddAsync(
            ReservationStayAmendmentCoordinationSupport.CreateManagementOperation(
                reservation,
                command.AmendmentRequestId,
                ReservationManagementOperationKind.InventoryAmendment,
                command.ExpectedDetailsRevision,
                fingerprint,
                nowUtc),
            cancellationToken).ConfigureAwait(false);
        await stayOperations.AddAsync(created.Value, cancellationToken).ConfigureAwait(false);
        return Result.Success(reservation.ToMutationReceipt());
    }

}
