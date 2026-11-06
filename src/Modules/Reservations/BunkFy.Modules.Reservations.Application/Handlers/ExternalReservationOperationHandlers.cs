namespace BunkFy.Modules.Reservations.Application.Handlers;

using BunkFy.DataGovernance;
using Gma.Framework.Messaging;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using BunkFy.Modules.Reservations.Application.External;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Application.Policies;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Domain.Aggregates;

[IntegrationEventHandler(ReservationsModuleMetadata.ExternalCreateHandlerName)]
internal sealed class ExternalReservationCreateRequestedHandler(
    IReservationRepository reservations,
    IInventoryProjectionRepository inventoryProjection,
    IReservationCountryPolicyAdmission countryPolicy,
    ExternalReservationOperationCoordinator coordinator,
    ReservationInboxDomainEventDispatcher domainEvents,
    ISystemClock clock,
    IIdGenerator idGenerator)
    : IIntegrationEventHandler<ExternalReservationCreateRequestedIntegrationEvent>
{
    public async Task HandleAsync(
        ExternalReservationCreateRequestedIntegrationEvent request,
        CancellationToken cancellationToken)
    {
        ExternalReservationOperationContext context = Context(request);
        string fingerprint = ExternalReservationOperationFingerprint.Create(request);
        if (!await coordinator.ShouldProcessAsync(
                context,
                ExternalReservationOperationKind.Create,
                fingerprint,
                cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        CountryPolicyDecision policyDecision = await countryPolicy.EvaluateAsync(
            request.PropertyId,
            ReservationCountryPolicyAdmission.ReservationIngestionPurpose,
            CountryPolicySurface.Import,
            ReservationCountryPolicyAdmission.ApprovedIngestionProvenance,
            cancellationToken).ConfigureAwait(false);
        if (!policyDecision.IsAllowed)
        {
            await CompleteAsync(
                ExternalReservationOperationOutcome.ValidationRejected,
                null,
                ReservationsApplicationErrors.CountryPolicyDenied(policyDecision.Reason).Code).ConfigureAwait(false);
            return;
        }

        Reservation? existing = await reservations.GetByExternalSourceAsync(
            request.SourceSystem,
            request.SourceReference,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            await CompleteAsync(
                ExternalReservationOperationOutcome.SourceAlreadyExists,
                existing,
                ReservationsApplicationErrors.ExternalSourceAlreadyExists.Code).ConfigureAwait(false);
            return;
        }

        InventoryUnitSelectionValidation selection = await inventoryProjection.ValidateSelectionAsync(
            request.PropertyId,
            request.InventoryUnitIds,
            cancellationToken).ConfigureAwait(false);
        if (selection != InventoryUnitSelectionValidation.Valid)
        {
            string error = selection == InventoryUnitSelectionValidation.UnitNotFound
                ? ReservationsApplicationErrors.InventoryUnitNotFound.Code
                : ReservationsApplicationErrors.InventoryUnitPropertyMismatch.Code;
            await CompleteAsync(ExternalReservationOperationOutcome.ValidationRejected, null, error).ConfigureAwait(false);
            return;
        }

        Result<Reservation> created = Reservation.Create(
            idGenerator.NewId(),
            request.ScopeId,
            request.PropertyId,
            idGenerator.NewId(),
            request.Arrival,
            request.Departure,
            request.InventoryUnitIds,
            request.PrimaryGuestName,
            request.Email,
            request.Phone,
            request.GuestCount,
            ReservationSource.External,
            request.SourceSystem,
            request.SourceReference,
            request.Notes,
            idGenerator.NewId(),
            idGenerator.NewId(),
            ReservationDetailsChangeOrigin.Adapter,
            $"adapter:{request.ConnectionId:N}",
            request.ConnectionId,
            request.OperationId,
            request.ReceiptId,
            clock.UtcNow,
            request.ExpectedArrivalTime,
            request.ExpectedDepartureTime);
        if (created.IsFailure)
        {
            await CompleteAsync(
                ExternalReservationOperationOutcome.ValidationRejected,
                null,
                created.Error.Code).ConfigureAwait(false);
            return;
        }

        await reservations.AddAsync(created.Value, cancellationToken).ConfigureAwait(false);
        await domainEvents.DispatchAsync(created.Value, cancellationToken).ConfigureAwait(false);
        await CompleteAsync(ExternalReservationOperationOutcome.Applied, created.Value, errorCode: null)
            .ConfigureAwait(false);

        Task CompleteAsync(
            ExternalReservationOperationOutcome outcome,
            Reservation? reservation,
            string? errorCode) => coordinator.CompleteAsync(
                context,
                ExternalReservationOperationKind.Create,
                fingerprint,
                outcome,
                reservation?.Id,
                reservation?.DetailsRevision,
                reservation?.Version,
                errorCode,
                cancellationToken);
    }

    private static ExternalReservationOperationContext Context(ExternalReservationCreateRequestedIntegrationEvent request) =>
        new(request.OperationId, request.ScopeId, request.ReceiptId, request.ConnectionId, request.PropertyId);
}

[IntegrationEventHandler(ReservationsModuleMetadata.ExternalGuestDetailsHandlerName)]
internal sealed class ExternalReservationGuestDetailsChangeRequestedHandler(
    ReservationMutationCoordinator mutations,
    IReservationCountryPolicyAdmission countryPolicy,
    ExternalReservationOperationCoordinator coordinator,
    ReservationInboxDomainEventDispatcher domainEvents,
    ISystemClock clock,
    IIdGenerator idGenerator)
    : IIntegrationEventHandler<ExternalReservationGuestDetailsChangeRequestedIntegrationEvent>
{
    public async Task HandleAsync(
        ExternalReservationGuestDetailsChangeRequestedIntegrationEvent request,
        CancellationToken cancellationToken)
    {
        ExternalReservationOperationContext context = new(
            request.OperationId,
            request.ScopeId,
            request.ReceiptId,
            request.ConnectionId,
            request.PropertyId);
        string fingerprint = ExternalReservationOperationFingerprint.Change(request);
        if (!await coordinator.ShouldProcessAsync(
                context,
                ExternalReservationOperationKind.ChangeGuestDetails,
                fingerprint,
                cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        CountryPolicyDecision policyDecision = await countryPolicy.EvaluateAsync(
            request.PropertyId,
            ReservationCountryPolicyAdmission.ReservationIngestionPurpose,
            CountryPolicySurface.Import,
            ReservationCountryPolicyAdmission.ApprovedIngestionProvenance,
            cancellationToken).ConfigureAwait(false);
        if (!policyDecision.IsAllowed)
        {
            await CompleteAsync(
                ExternalReservationOperationOutcome.ValidationRejected,
                null,
                ReservationsApplicationErrors.CountryPolicyDenied(policyDecision.Reason).Code).ConfigureAwait(false);
            return;
        }

        Reservation? reservation = await mutations.AcquireOperationalAsync(
            request.PropertyId,
            request.ReservationId,
            cancellationToken).ConfigureAwait(false);
        if (!MatchesSource(reservation, request.SourceSystem, request.SourceReference))
        {
            await CompleteAsync(
                ExternalReservationOperationOutcome.ReservationNotFound,
                value: null,
                ReservationsApplicationErrors.ReservationNotFound.Code).ConfigureAwait(false);
            return;
        }

        Result<ReservationDetailsChangeOutcome> changed = reservation!.UpdateGuestDetails(
            request.PrimaryGuestName,
            request.Email,
            request.Phone,
            request.GuestCount,
            request.Notes,
            request.ExpectedDetailsRevision,
            ReservationDetailsChangeOrigin.Adapter,
            $"adapter:{request.ConnectionId:N}",
            request.ConnectionId,
            request.OperationId,
            request.ReceiptId,
            idGenerator.NewId(),
            clock.UtcNow,
            request.ExpectedArrivalTime,
            request.ExpectedDepartureTime);
        if (changed.IsFailure)
        {
            ExternalReservationOperationOutcome outcome = changed.Error == ReservationsApplicationErrors.DetailsRevisionConflict
                ? ExternalReservationOperationOutcome.DetailsRevisionConflict
                : ExternalReservationOperationOutcome.ValidationRejected;
            await CompleteAsync(outcome, reservation, changed.Error.Code).ConfigureAwait(false);
            return;
        }

        bool wasChanged = changed.Value == ReservationDetailsChangeOutcome.Changed;
        if (wasChanged)
        {
            await domainEvents.DispatchAsync(reservation, cancellationToken).ConfigureAwait(false);
        }

        await CompleteAsync(
            wasChanged
                ? ExternalReservationOperationOutcome.Applied
                : ExternalReservationOperationOutcome.Unchanged,
            reservation,
            errorCode: null).ConfigureAwait(false);

        Task CompleteAsync(
            ExternalReservationOperationOutcome outcome,
            Reservation? value,
            string? errorCode) => coordinator.CompleteAsync(
                context,
                ExternalReservationOperationKind.ChangeGuestDetails,
                fingerprint,
                outcome,
                value?.Id,
                value?.DetailsRevision,
                value?.Version,
                errorCode,
                cancellationToken);
    }

    private static bool MatchesSource(Reservation? reservation, string sourceSystem, string sourceReference) =>
        reservation is not null && reservation.Source == ReservationSource.External &&
        string.Equals(reservation.SourceSystem, sourceSystem, StringComparison.Ordinal) &&
        string.Equals(reservation.SourceReference, sourceReference, StringComparison.Ordinal);
}

[IntegrationEventHandler(ReservationsModuleMetadata.ExternalAmendmentHandlerName)]
internal sealed class ExternalReservationAmendmentRequestedHandler(
    ReservationMutationCoordinator mutations,
    IInventoryProjectionRepository inventoryProjection,
    IReservationCountryPolicyAdmission countryPolicy,
    ExternalReservationOperationCoordinator coordinator,
    ReservationInboxDomainEventDispatcher domainEvents,
    ISystemClock clock,
    IIdGenerator idGenerator)
    : IIntegrationEventHandler<ExternalReservationAmendmentRequestedIntegrationEvent>
{
    public async Task HandleAsync(
        ExternalReservationAmendmentRequestedIntegrationEvent request,
        CancellationToken cancellationToken)
    {
        ExternalReservationOperationContext context = new(
            request.OperationId,
            request.ScopeId,
            request.ReceiptId,
            request.ConnectionId,
            request.PropertyId);
        string fingerprint = ExternalReservationOperationFingerprint.Amend(request);
        if (!await coordinator.ShouldProcessAsync(
                context,
                ExternalReservationOperationKind.Amend,
                fingerprint,
                cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        CountryPolicyDecision policyDecision = await countryPolicy.EvaluateAsync(
            request.PropertyId,
            ReservationCountryPolicyAdmission.ReservationIngestionPurpose,
            CountryPolicySurface.Import,
            ReservationCountryPolicyAdmission.ApprovedIngestionProvenance,
            cancellationToken).ConfigureAwait(false);
        if (!policyDecision.IsAllowed)
        {
            await CompleteAsync(
                ExternalReservationOperationOutcome.ValidationRejected,
                null,
                ReservationsApplicationErrors.CountryPolicyDenied(policyDecision.Reason).Code).ConfigureAwait(false);
            return;
        }

        Reservation? reservation = await mutations.AcquireOperationalAsync(
            request.PropertyId,
            request.ReservationId,
            cancellationToken).ConfigureAwait(false);
        if (!MatchesSource(reservation, request.SourceSystem, request.SourceReference))
        {
            await CompleteAsync(
                ExternalReservationOperationOutcome.ReservationNotFound,
                value: null,
                ReservationsApplicationErrors.ReservationNotFound.Code).ConfigureAwait(false);
            return;
        }

        if (reservation!.PendingAllocationAmendmentId.HasValue)
        {
            if (reservation.PendingAllocationAmendmentId == request.OperationId &&
                string.Equals(
                    reservation.PendingAllocationAmendmentRequestFingerprint,
                    fingerprint,
                    StringComparison.Ordinal))
            {
                return;
            }

            await coordinator.PublishConflictAsync(
                context,
                ExternalReservationOperationKind.Amend,
                fingerprint,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        InventoryUnitSelectionValidation selection = await inventoryProjection.ValidateSelectionAsync(
            request.PropertyId,
            request.InventoryUnitIds,
            cancellationToken).ConfigureAwait(false);
        if (selection != InventoryUnitSelectionValidation.Valid)
        {
            string error = selection == InventoryUnitSelectionValidation.UnitNotFound
                ? ReservationsApplicationErrors.InventoryUnitNotFound.Code
                : ReservationsApplicationErrors.InventoryUnitPropertyMismatch.Code;
            await CompleteAsync(ExternalReservationOperationOutcome.ValidationRejected, reservation, error)
                .ConfigureAwait(false);
            return;
        }

        Result<ReservationDetailsChangeOutcome> begun = reservation.BeginAllocationAmendment(
            request.OperationId,
            idGenerator.NewId(),
            fingerprint,
            request.Arrival,
            request.Departure,
            request.InventoryUnitIds,
            request.PrimaryGuestName,
            request.Email,
            request.Phone,
            request.GuestCount,
            request.Notes,
            request.ExpectedDetailsRevision,
            ReservationDetailsChangeOrigin.Adapter,
            $"adapter:{request.ConnectionId:N}",
            request.ConnectionId,
            request.OperationId,
            request.ReceiptId,
            idGenerator.NewId(),
            clock.UtcNow,
            request.ExpectedArrivalTime,
            request.ExpectedDepartureTime);
        if (begun.IsFailure)
        {
            ExternalReservationOperationOutcome outcome = begun.Error == ReservationsApplicationErrors.DetailsRevisionConflict
                ? ExternalReservationOperationOutcome.DetailsRevisionConflict
                : begun.Error == Domain.Errors.ReservationsDomainErrors.AllocationAmendmentInProgress
                    ? ExternalReservationOperationOutcome.InvalidTransition
                    : ExternalReservationOperationOutcome.ValidationRejected;
            await CompleteAsync(outcome, reservation, begun.Error.Code).ConfigureAwait(false);
            return;
        }

        if (begun.Value == ReservationDetailsChangeOutcome.Unchanged)
        {
            await CompleteAsync(ExternalReservationOperationOutcome.Unchanged, reservation, errorCode: null)
                .ConfigureAwait(false);
            return;
        }

        await domainEvents.DispatchAsync(reservation, cancellationToken).ConfigureAwait(false);

        Task CompleteAsync(
            ExternalReservationOperationOutcome outcome,
            Reservation? value,
            string? errorCode) => coordinator.CompleteAsync(
                context,
                ExternalReservationOperationKind.Amend,
                fingerprint,
                outcome,
                value?.Id,
                value?.DetailsRevision,
                value?.Version,
                errorCode,
                cancellationToken);
    }

    private static bool MatchesSource(Reservation? reservation, string sourceSystem, string sourceReference) =>
        reservation is not null && reservation.Source == ReservationSource.External &&
        string.Equals(reservation.SourceSystem, sourceSystem, StringComparison.Ordinal) &&
        string.Equals(reservation.SourceReference, sourceReference, StringComparison.Ordinal);
}

[IntegrationEventHandler(ReservationsModuleMetadata.ExternalCancellationHandlerName)]
internal sealed class ExternalReservationCancellationRequestedHandler(
    ReservationMutationCoordinator mutations,
    ExternalReservationOperationCoordinator coordinator,
    ReservationInboxDomainEventDispatcher domainEvents,
    ISystemClock clock,
    IIdGenerator idGenerator)
    : IIntegrationEventHandler<ExternalReservationCancellationRequestedIntegrationEvent>
{
    public async Task HandleAsync(
        ExternalReservationCancellationRequestedIntegrationEvent request,
        CancellationToken cancellationToken)
    {
        ExternalReservationOperationContext context = new(
            request.OperationId,
            request.ScopeId,
            request.ReceiptId,
            request.ConnectionId,
            request.PropertyId);
        string fingerprint = ExternalReservationOperationFingerprint.Cancel(request);
        if (!await coordinator.ShouldProcessAsync(
                context,
                ExternalReservationOperationKind.Cancel,
                fingerprint,
                cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        Reservation? reservation = await mutations.AcquireRequiredContinuationAsync(
            request.PropertyId,
            request.ReservationId,
            cancellationToken).ConfigureAwait(false);
        if (!MatchesSource(reservation, request.SourceSystem, request.SourceReference))
        {
            await CompleteAsync(
                ExternalReservationOperationOutcome.ReservationNotFound,
                value: null,
                ReservationsApplicationErrors.ReservationNotFound.Code).ConfigureAwait(false);
            return;
        }

        Result cancelled = reservation!.RequestExternalCancellation(
            request.ExpectedDetailsRevision,
            idGenerator.NewId(),
            idGenerator.NewId(),
            clock.UtcNow);
        if (cancelled.IsFailure)
        {
            ExternalReservationOperationOutcome outcome = cancelled.Error == ReservationsApplicationErrors.DetailsRevisionConflict
                ? ExternalReservationOperationOutcome.DetailsRevisionConflict
                : ExternalReservationOperationOutcome.InvalidTransition;
            await CompleteAsync(outcome, reservation, cancelled.Error.Code).ConfigureAwait(false);
            return;
        }

        await domainEvents.DispatchAsync(reservation, cancellationToken).ConfigureAwait(false);
        await CompleteAsync(
            reservation.Status == ReservationState.Cancelled
                ? ExternalReservationOperationOutcome.Applied
                : ExternalReservationOperationOutcome.Accepted,
            reservation,
            errorCode: null).ConfigureAwait(false);

        Task CompleteAsync(
            ExternalReservationOperationOutcome outcome,
            Reservation? value,
            string? errorCode) => coordinator.CompleteAsync(
                context,
                ExternalReservationOperationKind.Cancel,
                fingerprint,
                outcome,
                value?.Id,
                value?.DetailsRevision,
                value?.Version,
                errorCode,
                cancellationToken);
    }

    private static bool MatchesSource(Reservation? reservation, string sourceSystem, string sourceReference) =>
        reservation is not null && reservation.Source == ReservationSource.External &&
        string.Equals(reservation.SourceSystem, sourceSystem, StringComparison.Ordinal) &&
        string.Equals(reservation.SourceReference, sourceReference, StringComparison.Ordinal);
}
