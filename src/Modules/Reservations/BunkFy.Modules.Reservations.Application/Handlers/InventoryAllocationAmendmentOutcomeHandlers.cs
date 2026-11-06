namespace BunkFy.Modules.Reservations.Application.Handlers;

using Gma.Framework.Messaging;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Reservations.Application.External;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Domain.StayAmendments;

[IntegrationEventHandler(ReservationsModuleMetadata.AllocationAmendmentConfirmedHandlerName)]
internal sealed class InventoryAllocationAmendmentConfirmedHandler(
    ReservationMutationCoordinator mutations,
    IInventoryProjectionRepository projection,
    IReservationStayAmendmentOperationRepository stayOperations,
    ExternalReservationOperationCoordinator coordinator,
    ReservationInboxDomainEventDispatcher domainEvents,
    ISystemClock clock,
    IIdGenerator idGenerator)
    : IIntegrationEventHandler<InventoryAllocationAmendmentConfirmedIntegrationEvent>
{
    public async Task HandleAsync(
        InventoryAllocationAmendmentConfirmedIntegrationEvent outcome,
        CancellationToken cancellationToken)
    {
        Reservation? reservation = await mutations.AcquireRequiredContinuationAsync(
            outcome.PropertyId,
            outcome.ReservationId,
            cancellationToken).ConfigureAwait(false);
        if (reservation is null)
        {
            throw new InvalidOperationException(
                $"Reservation '{outcome.ReservationId}' was not found for allocation amendment confirmation.");
        }

        if (reservation.PendingInventoryAmendmentRequestId != outcome.AmendmentRequestId)
        {
            ReservationStayAmendmentOperation? completedOperation =
                await stayOperations.GetByInventoryRequestIdAsync(
                    outcome.AmendmentRequestId,
                    cancellationToken).ConfigureAwait(false);
            if (completedOperation is null)
            {
                return;
            }

            if (completedOperation.Outcome == ReservationStayAmendmentOperationOutcome.Applied &&
                completedOperation.ReservationId == outcome.ReservationId &&
                reservation.AllocationId == outcome.AllocationId &&
                completedOperation.ResultingAllocationVersion == outcome.AllocationVersion &&
                completedOperation.TargetArrival == outcome.Arrival &&
                completedOperation.TargetDeparture == outcome.Departure &&
                completedOperation.GetTargetInventoryUnitIds().Order().SequenceEqual(
                    outcome.InventoryUnitIds.Order()))
            {
                return;
            }

            throw new InvalidOperationException(
                "The confirmed allocation amendment contradicts the durable stay outcome.");
        }

        Guid? externalOperationId = reservation.PendingDetailsExternalOperationId;
        Guid? connectionId = reservation.PendingDetailsAdapterConnectionId;
        Guid? receiptId = reservation.PendingDetailsCorrelationId;
        string? fingerprint = reservation.PendingAllocationAmendmentRequestFingerprint;
        ReservationStayAmendmentOperation? stayOperation = null;
        if (reservation.PendingDetailsChangeOrigin == ReservationDetailsChangeOrigin.Staff)
        {
            if (!reservation.PendingAllocationAmendmentId.HasValue)
            {
                throw new InvalidOperationException(
                    "The confirmed allocation amendment has no local stay coordinate.");
            }

            stayOperation = await stayOperations.GetAsync(
                outcome.PropertyId,
                outcome.ReservationId,
                reservation.PendingAllocationAmendmentId.Value,
                cancellationToken).ConfigureAwait(false);
            if (stayOperation is null ||
                stayOperation.InventoryRequestId != outcome.AmendmentRequestId)
            {
                throw new InvalidOperationException(
                    "The confirmed allocation amendment has no durable stay evidence.");
            }
        }
        DateTimeOffset nowUtc = clock.UtcNow;
        if (stayOperation is not null &&
            (!stayOperation.TargetArrival.HasValue ||
                !stayOperation.TargetDeparture.HasValue ||
                stayOperation.TargetInventoryUnitIds is null ||
                stayOperation.TargetArrival != outcome.Arrival ||
                stayOperation.TargetDeparture != outcome.Departure ||
                !stayOperation.GetTargetInventoryUnitIds().Order().SequenceEqual(
                    outcome.InventoryUnitIds.Order())))
        {
            throw new InvalidOperationException(
                "The confirmed allocation amendment did not match the durable stay target.");
        }

        if (reservation.CompleteAllocationAmendment(
                outcome.AmendmentRequestId,
                outcome.AllocationId,
                outcome.Arrival,
                outcome.Departure,
                outcome.InventoryUnitIds,
                outcome.AllocationVersion,
                idGenerator.NewId(),
                nowUtc).IsFailure)
        {
            throw new InvalidOperationException("The confirmed allocation amendment did not match Reservations state.");
        }

        await projection.ApplyAllocationAsync(
            new(
                outcome.ScopeId,
                outcome.AllocationId,
                outcome.ReservationId,
                outcome.PropertyId,
                outcome.Arrival,
                outcome.Departure,
                InventoryAllocationStatus.Active,
                outcome.InventoryUnitIds,
                outcome.AllocationVersion),
            cancellationToken).ConfigureAwait(false);

        if (stayOperation is not null &&
            (reservation.ExpectedArrivalTime != stayOperation.TargetExpectedArrivalTime ||
                reservation.ExpectedDepartureTime != stayOperation.TargetExpectedDepartureTime ||
                stayOperation.MarkApplied(
                    outcome.Arrival,
                    outcome.Departure,
                    reservation.ExpectedArrivalTime,
                    reservation.ExpectedDepartureTime,
                    outcome.InventoryUnitIds,
                    outcome.AllocationVersion,
                    reservation.DetailsRevision,
                    reservation.Version,
                    nowUtc).IsFailure))
        {
            throw new InvalidOperationException(
                "The confirmed allocation amendment did not match the durable stay operation.");
        }

        await domainEvents.DispatchAsync(reservation, cancellationToken).ConfigureAwait(false);

        if (externalOperationId.HasValue && connectionId.HasValue && receiptId.HasValue && fingerprint is not null)
        {
            await coordinator.CompleteAsync(
                new ExternalReservationOperationContext(
                    externalOperationId.Value,
                    reservation.ScopeId,
                    receiptId.Value,
                    connectionId.Value,
                    reservation.PropertyId),
                ExternalReservationOperationKind.Amend,
                fingerprint,
                ExternalReservationOperationOutcome.Applied,
                reservation.Id,
                reservation.DetailsRevision,
                reservation.Version,
                errorCode: null,
                cancellationToken).ConfigureAwait(false);
        }
    }
}

[IntegrationEventHandler(ReservationsModuleMetadata.AllocationAmendmentRejectedHandlerName)]
internal sealed class InventoryAllocationAmendmentRejectedHandler(
    ReservationMutationCoordinator mutations,
    IReservationStayAmendmentOperationRepository stayOperations,
    ExternalReservationOperationCoordinator coordinator,
    ISystemClock clock)
    : IIntegrationEventHandler<InventoryAllocationAmendmentRejectedIntegrationEvent>
{
    public async Task HandleAsync(
        InventoryAllocationAmendmentRejectedIntegrationEvent outcome,
        CancellationToken cancellationToken)
    {
        Reservation? reservation = await mutations.AcquireRequiredContinuationAsync(
            outcome.PropertyId,
            outcome.ReservationId,
            cancellationToken).ConfigureAwait(false);
        if (reservation is null)
        {
            throw new InvalidOperationException(
                $"Reservation '{outcome.ReservationId}' was not found for allocation amendment rejection.");
        }

        if (reservation.PendingInventoryAmendmentRequestId != outcome.AmendmentRequestId)
        {
            ReservationStayAmendmentOperation? completedOperation =
                await stayOperations.GetByInventoryRequestIdAsync(
                    outcome.AmendmentRequestId,
                    cancellationToken).ConfigureAwait(false);
            if (completedOperation is null)
            {
                return;
            }

            if (completedOperation.Outcome == ReservationStayAmendmentOperationOutcome.Rejected &&
                completedOperation.ReservationId == outcome.ReservationId &&
                reservation.AllocationId == outcome.AllocationId &&
                completedOperation.RejectionCode == (int)outcome.Reason)
            {
                return;
            }

            throw new InvalidOperationException(
                "The rejected allocation amendment contradicts the durable stay outcome.");
        }

        Guid? externalOperationId = reservation.PendingDetailsExternalOperationId;
        Guid? connectionId = reservation.PendingDetailsAdapterConnectionId;
        Guid? receiptId = reservation.PendingDetailsCorrelationId;
        string? fingerprint = reservation.PendingAllocationAmendmentRequestFingerprint;
        ReservationStayAmendmentOperation? stayOperation = null;
        if (reservation.PendingDetailsChangeOrigin == ReservationDetailsChangeOrigin.Staff)
        {
            if (!reservation.PendingAllocationAmendmentId.HasValue)
            {
                throw new InvalidOperationException(
                    "The rejected allocation amendment has no local stay coordinate.");
            }

            stayOperation = await stayOperations.GetAsync(
                outcome.PropertyId,
                outcome.ReservationId,
                reservation.PendingAllocationAmendmentId.Value,
                cancellationToken).ConfigureAwait(false);
            if (stayOperation is null ||
                stayOperation.InventoryRequestId != outcome.AmendmentRequestId)
            {
                throw new InvalidOperationException(
                    "The rejected allocation amendment has no durable stay evidence.");
            }
        }
        DateTimeOffset nowUtc = clock.UtcNow;
        if (reservation.RejectAllocationAmendment(
                outcome.AmendmentRequestId,
                outcome.AllocationId,
                (int)outcome.Reason,
                nowUtc).IsFailure)
        {
            throw new InvalidOperationException(
                "The rejected allocation amendment did not match Reservations state.");
        }

        if (stayOperation is not null && stayOperation.MarkRejected(
                (int)outcome.Reason,
                reservation.DetailsRevision,
                reservation.Version,
                nowUtc).IsFailure)
        {
            throw new InvalidOperationException(
                "The rejected allocation amendment did not match the durable stay operation.");
        }

        if (externalOperationId.HasValue && connectionId.HasValue && receiptId.HasValue && fingerprint is not null)
        {
            await coordinator.CompleteAsync(
                new ExternalReservationOperationContext(
                    externalOperationId.Value,
                    reservation.ScopeId,
                    receiptId.Value,
                    connectionId.Value,
                    reservation.PropertyId),
                ExternalReservationOperationKind.Amend,
                fingerprint,
                ExternalReservationOperationOutcome.ValidationRejected,
                reservation.Id,
                reservation.DetailsRevision,
                reservation.Version,
                $"Inventory.AllocationAmendment.{outcome.Reason}",
                cancellationToken).ConfigureAwait(false);
        }
    }
}
