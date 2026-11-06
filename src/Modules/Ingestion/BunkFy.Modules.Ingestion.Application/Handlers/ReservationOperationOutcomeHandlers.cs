namespace BunkFy.Modules.Ingestion.Application.Handlers;

using System.Text.Json;
using Gma.Framework.Messaging;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Runtime.Identity;
using BunkFy.Modules.Ingestion.Application.Ports;
using BunkFy.Modules.Ingestion.Application.Reservations;
using BunkFy.Modules.Ingestion.Contracts;
using BunkFy.Modules.Ingestion.Domain.Receipts;
using BunkFy.Modules.Ingestion.Domain.Proposals;
using BunkFy.Modules.Ingestion.Domain.Reservations;
using global::BunkFy.Modules.Reservations.Contracts;

[IntegrationEventHandler(IngestionModuleMetadata.ReservationOperationOutcomeHandlerName)]
internal sealed class ReservationOperationOutcomeHandler(
    IngestionSourceMutationCoordinator sourceMutations,
    IReservationDispatchRepository dispatches,
    IReservationSourceLinkRepository sourceLinks,
    IObservationReceiptRepository receipts,
    IChangeProposalRepository proposals,
    PendingChangeProposalSuperseder proposalSuperseder,
    IIngestionRetentionPolicy retentionPolicy,
    IOutboxWriterRegistry outboxWriters,
    ISystemClock clock,
    IIdGenerator idGenerator)
    : IIntegrationEventHandler<ExternalReservationOperationCompletedIntegrationEvent>
{
    public async Task HandleAsync(
        ExternalReservationOperationCompletedIntegrationEvent outcome,
        CancellationToken cancellationToken)
    {
        IngestionSourceMutationLease? lease =
            await sourceMutations.AcquireDispatchAsync(
                    outcome.OperationId,
                    cancellationToken)
                .ConfigureAwait(false);
        if (lease is null)
        {
            return;
        }

        ReservationDispatch? dispatch = await dispatches.GetAsync(outcome.OperationId, cancellationToken)
            .ConfigureAwait(false);
        if (dispatch is null || dispatch.State != ReservationDispatchState.Pending)
        {
            return;
        }

        if (dispatch.ReceiptId != outcome.ReceiptId || dispatch.ConnectionId != outcome.ConnectionId ||
            dispatch.PropertyId != outcome.PropertyId || !KindMatches(dispatch.Kind, outcome.OperationKind))
        {
            throw new InvalidOperationException("The reservation operation outcome does not match its dispatch.");
        }

        ReservationSourceLink link = await sourceLinks.GetAsync(dispatch.SourceLinkId, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("The reservation dispatch source link was not found.");
        DateTimeOffset nowUtc = clock.UtcNow;
        ReservationDispatchState state = Map(outcome.Outcome);
        DateTimeOffset? sensitiveDataRetainUntilUtc = state == ReservationDispatchState.Accepted
            ? null
            : retentionPolicy.GetSensitiveHistoryRetainUntilUtc(
                dispatch.PropertyId,
                dispatch.ConnectionId,
                nowUtc);
        if (dispatch.Complete(
            state,
            outcome.ReservationId,
            outcome.DetailsRevision,
            outcome.ReservationVersion,
            outcome.ErrorCode,
            sensitiveDataRetainUntilUtc,
            nowUtc).IsFailure)
        {
            throw new InvalidOperationException("The reservation dispatch could not accept its outcome.");
        }

        bool acceptedCancellation = dispatch.Kind == ReservationDispatchKind.Cancel &&
                                    state == ReservationDispatchState.Accepted;
        bool applied = state is ReservationDispatchState.Applied or ReservationDispatchState.Unchanged or ReservationDispatchState.Accepted;
        string? operationalBaseline = dispatch.Kind == ReservationDispatchKind.Cancel
            ? null
            : ReservationOperationalBaseline.FromNormalizedSnapshot(dispatch.NormalizedSnapshot!);
        if (link.CompleteDispatch(
            dispatch.Id,
            dispatch.ReceiptId,
            dispatch.SourceRevision,
            dispatch.SourceSequence,
            operationalBaseline,
            outcome.ReservationId,
            outcome.DetailsRevision,
            keepActive: acceptedCancellation,
            applied,
            cancellationPending: acceptedCancellation,
            cancelled: dispatch.Kind == ReservationDispatchKind.Cancel && state == ReservationDispatchState.Applied,
            nowUtc).IsFailure)
        {
            throw new InvalidOperationException("The reservation source link could not accept its operation outcome.");
        }

        ObservationReceipt? receipt = await receipts.GetAsync(dispatch.ReceiptId, cancellationToken).ConfigureAwait(false);
        if (dispatch.TriggerKind == ReservationDispatchTriggerKind.Proposal)
        {
            await this.CompleteProposalAttemptAsync(dispatch, state, nowUtc, cancellationToken).ConfigureAwait(false);
        }
        else if (receipt?.State == ObservationReceiptState.Pending && state == ReservationDispatchState.ProposalRequired)
        {
            await this.CreateProposalAsync(receipt, link, dispatch, cancellationToken).ConfigureAwait(false);
        }
        else if (receipt?.State == ObservationReceiptState.Pending)
        {
            _ = receipt.MarkProcessed(nowUtc);
        }

        if (!acceptedCancellation && link.DeferredReceiptId.HasValue)
        {
            await this.PublishDeferredAsync(link, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CompleteProposalAttemptAsync(
        ReservationDispatch dispatch,
        ReservationDispatchState state,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        ChangeProposal proposal = await proposals.GetAsync(dispatch.TriggerId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The reservation dispatch proposal was not found.");
        if (proposal.ProductOperationId != dispatch.Id || proposal.State != ChangeProposalState.Applying)
        {
            throw new InvalidOperationException("The reservation dispatch does not match the applying proposal.");
        }

        Gma.Framework.Results.Result transition = state switch
        {
            ReservationDispatchState.Applied or ReservationDispatchState.Unchanged =>
                proposal.MarkApplied(
                    dispatch.Id,
                    proposal.Version,
                    retentionPolicy.GetSensitiveHistoryRetainUntilUtc(
                        proposal.PropertyId,
                        proposal.ConnectionId,
                        nowUtc),
                    nowUtc),
            ReservationDispatchState.ProposalRequired =>
                proposal.MarkStale(
                    "Reservation details changed while the proposal was being applied.",
                    proposal.Version,
                    retentionPolicy.GetSensitiveHistoryRetainUntilUtc(
                        proposal.PropertyId,
                        proposal.ConnectionId,
                        nowUtc),
                    nowUtc),
            ReservationDispatchState.Rejected or ReservationDispatchState.Conflict =>
                proposal.MarkFailed(
                    dispatch.ErrorCode ?? "The reservation operation was rejected.",
                    proposal.Version,
                    retentionPolicy.GetSensitiveHistoryRetainUntilUtc(
                        proposal.PropertyId,
                        proposal.ConnectionId,
                        nowUtc),
                    nowUtc),
            ReservationDispatchState.Accepted when dispatch.Kind == ReservationDispatchKind.Cancel =>
                Gma.Framework.Results.Result.Success(),
            _ => Gma.Framework.Results.Result.Failure(BunkFy.Modules.Ingestion.Domain.Errors.IngestionDomainErrors.ReservationDispatchInvalid)
        };
        if (transition.IsFailure)
        {
            throw new InvalidOperationException($"The proposal outcome transition failed: {transition.Error.Code}");
        }
    }

    private async Task CreateProposalAsync(
        ObservationReceipt receipt,
        ReservationSourceLink link,
        ReservationDispatch dispatch,
        CancellationToken cancellationToken)
    {
        if (!link.ReservationId.HasValue || !dispatch.ExpectedDetailsRevision.HasValue)
        {
            throw new InvalidOperationException("A reservation conflict outcome requires an adapter baseline.");
        }

        ChangeProposal? existing = await proposals.FindByReceiptAsync(receipt.Id, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            await proposalSuperseder.SupersedeAsync(
                receipt.ConnectionId,
                receipt.ExternalId,
                link.ReservationId.Value,
                receipt.Id,
                cancellationToken).ConfigureAwait(false);
            string diff = JsonSerializer.Serialize(new ReservationProposalDiff(
                link.LastAppliedOperationalBaseline,
                dispatch.NormalizedSnapshot!));
            Gma.Framework.Results.Result<ChangeProposal> created = ChangeProposal.Create(
                ReservationOperationIdentity.CreateProposalId(receipt.Id),
                receipt.ScopeId,
                receipt.PropertyId,
                receipt.ConnectionId,
                receipt.Id,
                link.ReservationId.Value,
                receipt.RawPayloadFileId,
                dispatch.ExpectedDetailsRevision.Value,
                "reservation-details-revision-conflict",
                diff,
                clock.UtcNow);
            if (created.IsFailure)
            {
                throw new InvalidOperationException($"The reservation conflict proposal is invalid: {created.Error.Code}");
            }

            await proposals.AddAsync(created.Value, cancellationToken).ConfigureAwait(false);
        }

        _ = receipt.MarkProcessed(clock.UtcNow);
    }

    private Task PublishDeferredAsync(ReservationSourceLink link, CancellationToken cancellationToken) =>
        outboxWriters.GetRequired(IngestionModuleMetadata.Name).EnqueueAsync(
            new ObservationReceiptAcceptedIntegrationEvent(
                idGenerator.NewId(),
                link.ScopeId,
                clock.UtcNow,
                link.DeferredReceiptId!.Value,
                link.ConnectionId,
                link.PropertyId),
            cancellationToken);

    private static bool KindMatches(ReservationDispatchKind dispatch, ExternalReservationOperationKind outcome) =>
        (dispatch, outcome) is
            (ReservationDispatchKind.Create, ExternalReservationOperationKind.Create) or
            (ReservationDispatchKind.ChangeGuestDetails, ExternalReservationOperationKind.ChangeGuestDetails) or
            (ReservationDispatchKind.Amend, ExternalReservationOperationKind.Amend) or
            (ReservationDispatchKind.Cancel, ExternalReservationOperationKind.Cancel);

    private static ReservationDispatchState Map(ExternalReservationOperationOutcome outcome) => outcome switch
    {
        ExternalReservationOperationOutcome.Applied => ReservationDispatchState.Applied,
        ExternalReservationOperationOutcome.Accepted => ReservationDispatchState.Accepted,
        ExternalReservationOperationOutcome.Unchanged => ReservationDispatchState.Unchanged,
        ExternalReservationOperationOutcome.DetailsRevisionConflict => ReservationDispatchState.ProposalRequired,
        ExternalReservationOperationOutcome.OperationConflict => ReservationDispatchState.Conflict,
        _ => ReservationDispatchState.Rejected
    };

    private sealed record ReservationProposalDiff(
        string? PreviousOperationalBaseline,
        string IncomingSnapshot);
}

[IntegrationEventHandler(IngestionModuleMetadata.ReservationCancelledHandlerName)]
internal sealed class ReservationCancelledForIngestionHandler(
    IngestionSourceMutationCoordinator sourceMutations,
    IReservationDispatchRepository dispatches,
    IReservationSourceLinkRepository sourceLinks,
    IObservationReceiptRepository receipts,
    IChangeProposalRepository proposals,
    IIngestionRetentionPolicy retentionPolicy,
    IOutboxWriterRegistry outboxWriters,
    ISystemClock clock,
    IIdGenerator idGenerator)
    : IIntegrationEventHandler<ReservationCancelledIntegrationEvent>
{
    public async Task HandleAsync(ReservationCancelledIntegrationEvent cancelled, CancellationToken cancellationToken)
    {
        IngestionSourceMutationLease? lease =
            await sourceMutations.AcquireAcceptedCancellationAsync(
                    cancelled.ReservationId,
                    cancellationToken)
                .ConfigureAwait(false);
        ReservationDispatch? dispatch = lease is null
            ? null
            : await dispatches.GetAsync(
                    lease.Coordinate.RecordId,
                    cancellationToken)
                .ConfigureAwait(false);
        if (dispatch is null || dispatch.State != ReservationDispatchState.Accepted)
        {
            return;
        }

        ReservationSourceLink? link = await sourceLinks.GetAsync(dispatch.SourceLinkId, cancellationToken)
            .ConfigureAwait(false);
        if (link is null || link.PropertyId != cancelled.PropertyId)
        {
            return;
        }

        DateTimeOffset nowUtc = clock.UtcNow;
        DateTimeOffset sensitiveDataRetainUntilUtc = retentionPolicy.GetSensitiveHistoryRetainUntilUtc(
            dispatch.PropertyId,
            dispatch.ConnectionId,
            nowUtc);
        if (dispatch.ConfirmAcceptedCancellation(
                cancelled.ReservationVersion,
                sensitiveDataRetainUntilUtc,
                nowUtc).IsFailure ||
            link.CompleteAcceptedCancellation(cancelled.ReservationId, nowUtc).IsFailure)
        {
            throw new InvalidOperationException("The accepted external cancellation could not be completed.");
        }
        if (dispatch.TriggerKind == ReservationDispatchTriggerKind.Proposal)
        {
            ChangeProposal proposal = await proposals.GetAsync(dispatch.TriggerId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The accepted cancellation proposal was not found.");
            if (proposal.MarkApplied(
                    dispatch.Id,
                    proposal.Version,
                    retentionPolicy.GetSensitiveHistoryRetainUntilUtc(
                        proposal.PropertyId,
                        proposal.ConnectionId,
                        nowUtc),
                    nowUtc).IsFailure)
            {
                throw new InvalidOperationException("The accepted cancellation proposal could not be completed.");
            }
        }

        ObservationReceipt? receipt = await receipts.GetAsync(dispatch.ReceiptId, cancellationToken).ConfigureAwait(false);
        if (receipt?.State == ObservationReceiptState.Pending)
        {
            _ = receipt.MarkProcessed(nowUtc);
        }

        if (link.DeferredReceiptId.HasValue)
        {
            await outboxWriters.GetRequired(IngestionModuleMetadata.Name).EnqueueAsync(
                new ObservationReceiptAcceptedIntegrationEvent(
                    idGenerator.NewId(), link.ScopeId, clock.UtcNow, link.DeferredReceiptId.Value,
                    link.ConnectionId, link.PropertyId), cancellationToken).ConfigureAwait(false);
        }
    }
}
