namespace BunkFy.Modules.Ingestion.Application.Handlers;

using System.Text.Json;
using BunkFy.DataGovernance;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using BunkFy.Modules.Ingestion.Application.Commands;
using BunkFy.Modules.Ingestion.Application.Ports;
using BunkFy.Modules.Ingestion.Application.Policies;
using BunkFy.Modules.Ingestion.Application.Reservations;
using BunkFy.Modules.Ingestion.Domain.Connections;
using BunkFy.Modules.Ingestion.Domain.Proposals;
using BunkFy.Modules.Ingestion.Domain.Receipts;
using BunkFy.Modules.Ingestion.Domain.Reservations;
using BunkFy.Modules.Ingestion.Application.DataRights;

internal sealed class DispatchNormalizedReservationObservationCommandHandler(
    IngestionSourceMutationCoordinator sourceMutations,
    IObservationReceiptRepository receipts,
    IReservationSourceLinkRepository sourceLinks,
    IReservationDispatchRepository dispatches,
    IChangeProposalRepository proposals,
    PendingChangeProposalSuperseder proposalSuperseder,
    IIngestionCountryPolicyAdmission countryPolicy,
    ReservationExternalRequestPublisher requestPublisher,
    ISystemClock clock,
    IngestionAnonymisationBarrier anonymisationBarrier)
    : ICommandHandler<DispatchNormalizedReservationObservationCommand, ReservationObservationDispatchResult>
{
    public async Task<Result<ReservationObservationDispatchResult>> HandleAsync(
        DispatchNormalizedReservationObservationCommand command,
        CancellationToken cancellationToken)
    {
        IngestionSourceMutationLease? lease =
            await sourceMutations.AcquireReceiptAsync(
                    command.ReceiptId,
                    cancellationToken)
                .ConfigureAwait(false);
        if (lease is null)
        {
            return Result.Failure<ReservationObservationDispatchResult>(IngestionApplicationErrors.ReceiptNotFound);
        }

        ObservationReceipt? receipt = await receipts.GetAsync(command.ReceiptId, cancellationToken).ConfigureAwait(false);
        if (receipt is null)
        {
            return Result.Failure<ReservationObservationDispatchResult>(IngestionApplicationErrors.ReceiptNotFound);
        }

        if (receipt.State != ObservationReceiptState.Pending)
        {
            return Result.Failure<ReservationObservationDispatchResult>(IngestionApplicationErrors.ReceiptNotPending);
        }

        AdapterConnection connection = lease.Connection;
        if (connection.Id != receipt.ConnectionId ||
            connection.PropertyId != receipt.PropertyId)
        {
            return Result.Failure<ReservationObservationDispatchResult>(IngestionApplicationErrors.ConnectionNotFound);
        }

        Result<Guid> barrier = await anonymisationBarrier
            .CheckUnderLockAsync(
                receipt.ScopeId,
                connection.Id,
                receipt.SourceRecordType,
                receipt.ExternalId,
                cancellationToken)
            .ConfigureAwait(false);
        if (barrier.IsFailure)
        {
            return Result.Failure<ReservationObservationDispatchResult>(
                barrier.Error);
        }

        if (barrier.Value != lease.Coordinate.SourceLinkId)
        {
            throw new InvalidOperationException(
                "The observation resolved to a different source graph after locking.");
        }

        CountryPolicyDecision countryPolicyDecision = await countryPolicy.EvaluateAsync(
            receipt.PropertyId,
            IngestionCountryPolicyAdmission.ReservationIngestionPurpose,
            CountryPolicySurface.Import,
            IngestionCountryPolicyAdmission.ApprovedParserProvenance,
            cancellationToken).ConfigureAwait(false);
        if (!countryPolicyDecision.IsAllowed)
        {
            return Result.Failure<ReservationObservationDispatchResult>(
                IngestionApplicationErrors.CountryPolicyDenied(countryPolicyDecision.Reason));
        }

        ReservationSourceLink? link = await sourceLinks.FindBySourceAsync(
            connection.Id,
            receipt.ExternalId,
            cancellationToken).ConfigureAwait(false);
        if (link is null)
        {
            if (command.Observation.Kind == NormalizedReservationObservationKind.Cancel)
            {
                return Result.Failure<ReservationObservationDispatchResult>(IngestionApplicationErrors.ReservationSourceNotLinked);
            }

            Result<ReservationSourceLink> created = ReservationSourceLink.Create(
                barrier.Value,
                receipt.ScopeId,
                receipt.PropertyId,
                connection.Id,
                CreateSourceSystem(connection),
                receipt.ExternalId,
                clock.UtcNow);
            if (created.IsFailure)
            {
                return Result.Failure<ReservationObservationDispatchResult>(created.Error);
            }

            link = created.Value;
            await sourceLinks.AddAsync(link, cancellationToken).ConfigureAwait(false);
        }

        ReservationDispatch? existing = await dispatches.FindByTriggerAsync(
            ReservationDispatchTriggerKind.Observation,
            receipt.Id,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return Result.Success(new ReservationObservationDispatchResult(
                receipt.Id,
                link.Id,
                existing.Id,
                existing.State == ReservationDispatchState.Pending
                    ? ReservationObservationDispatchDisposition.Dispatched
                    : ReservationObservationDispatchDisposition.Replay));
        }

        bool resumingDeferred = link.DeferredReceiptId == receipt.Id && !link.ActiveProductOperationId.HasValue;
        Result<ReservationObservationResult> observed = resumingDeferred
            ? Result.Success(new ReservationObservationResult(ReservationObservationDisposition.Ready, null))
            : link.Observe(
                receipt.Id,
                receipt.SourceRevision,
                command.Observation.SourceSequence,
                receipt.SourceUpdatedAtUtc,
                receipt.ContentHash,
                clock.UtcNow);
        if (observed.IsFailure)
        {
            return Result.Failure<ReservationObservationDispatchResult>(observed.Error);
        }

        if (observed.Value.Disposition is ReservationObservationDisposition.Replay or ReservationObservationDisposition.Stale)
        {
            _ = receipt.MarkProcessed(clock.UtcNow);
            return Result.Success(new ReservationObservationDispatchResult(
                receipt.Id,
                link.Id,
                OperationId: null,
                observed.Value.Disposition == ReservationObservationDisposition.Replay
                    ? ReservationObservationDispatchDisposition.Replay
                    : ReservationObservationDispatchDisposition.Stale));
        }

        if (observed.Value.SupersededReceiptId.HasValue && observed.Value.SupersededReceiptId != receipt.Id)
        {
            ObservationReceipt? superseded = await receipts.GetAsync(observed.Value.SupersededReceiptId.Value, cancellationToken)
                .ConfigureAwait(false);
            if (superseded?.State == ObservationReceiptState.Pending)
            {
                _ = superseded.MarkProcessed(clock.UtcNow);
            }
        }

        if (observed.Value.Disposition == ReservationObservationDisposition.Deferred)
        {
            return Result.Success(new ReservationObservationDispatchResult(
                receipt.Id,
                link.Id,
                OperationId: null,
                ReservationObservationDispatchDisposition.Deferred));
        }

        bool observationRequiresReview = observed.Value.Disposition == ReservationObservationDisposition.RequiresReview;

        ReservationDispatchKind kind = ReservationObservationDispatchClassifier.Classify(link, command.Observation);
        Result validation = Validate(command.Observation, kind);
        if (validation.IsFailure)
        {
            return Result.Failure<ReservationObservationDispatchResult>(validation.Error);
        }

        string normalizedSnapshot = JsonSerializer.Serialize(command.Observation);
        if (observationRequiresReview ||
            (connection.ConflictPolicy == IngestionConflictPolicy.SuggestionsOnly && kind != ReservationDispatchKind.Create))
        {
            return await this.CreateProposalAsync(
                receipt,
                link,
                normalizedSnapshot,
                observationRequiresReview ? "source-order-unverifiable" : "connection-suggestions-only",
                cancellationToken).ConfigureAwait(false);
        }

        long? expectedRevision = kind == ReservationDispatchKind.Create
            ? null
            : link.LastAppliedReservationDetailsRevision;
        if (kind != ReservationDispatchKind.Create && !expectedRevision.HasValue)
        {
            return await this.CreateProposalAsync(
                receipt,
                link,
                normalizedSnapshot,
                "adapter-baseline-unavailable",
                cancellationToken).ConfigureAwait(false);
        }

        Guid operationId = ReservationOperationIdentity.CreateOperationId(receipt.Id, kind);
        Result<ReservationDispatch> dispatch = ReservationDispatch.Create(
            operationId,
            receipt.ScopeId,
            link.Id,
            ReservationDispatchTriggerKind.Observation,
            receipt.Id,
            receipt.Id,
            connection.Id,
            receipt.PropertyId,
            link.ReservationId,
            kind,
            receipt.SourceRevision,
            command.Observation.SourceSequence,
            normalizedSnapshot,
            expectedRevision,
            clock.UtcNow);
        if (dispatch.IsFailure)
        {
            return Result.Failure<ReservationObservationDispatchResult>(dispatch.Error);
        }

        Result begun = link.BeginDispatch(operationId, clock.UtcNow);
        if (begun.IsFailure)
        {
            return Result.Failure<ReservationObservationDispatchResult>(begun.Error);
        }

        await dispatches.AddAsync(dispatch.Value, cancellationToken).ConfigureAwait(false);
        await requestPublisher.PublishAsync(receipt, link, operationId, kind, expectedRevision, command.Observation, cancellationToken)
            .ConfigureAwait(false);
        return Result.Success(new ReservationObservationDispatchResult(
            receipt.Id,
            link.Id,
            operationId,
            ReservationObservationDispatchDisposition.Dispatched));
    }

    private async Task<Result<ReservationObservationDispatchResult>> CreateProposalAsync(
        ObservationReceipt receipt,
        ReservationSourceLink link,
        string normalizedSnapshot,
        string reason,
        CancellationToken cancellationToken)
    {
        if (!link.ReservationId.HasValue || !link.LastAppliedReservationDetailsRevision.HasValue)
        {
            return Result.Failure<ReservationObservationDispatchResult>(IngestionApplicationErrors.ReservationBaselineUnavailable);
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
                normalizedSnapshot));
            Result<ChangeProposal> created = ChangeProposal.Create(
                ReservationOperationIdentity.CreateProposalId(receipt.Id),
                receipt.ScopeId,
                receipt.PropertyId,
                receipt.ConnectionId,
                receipt.Id,
                link.ReservationId.Value,
                receipt.RawPayloadFileId,
                link.LastAppliedReservationDetailsRevision.Value,
                reason,
                diff,
                clock.UtcNow);
            if (created.IsFailure)
            {
                return Result.Failure<ReservationObservationDispatchResult>(created.Error);
            }

            await proposals.AddAsync(created.Value, cancellationToken).ConfigureAwait(false);
        }

        _ = receipt.MarkProcessed(clock.UtcNow);
        return Result.Success(new ReservationObservationDispatchResult(
            receipt.Id,
            link.Id,
            OperationId: null,
            ReservationObservationDispatchDisposition.ProposalRequired));
    }

    private static Result Validate(NormalizedReservationObservation observation, ReservationDispatchKind kind)
    {
        if (observation.Kind is not (NormalizedReservationObservationKind.Upsert or NormalizedReservationObservationKind.Cancel) ||
            observation.SourceSequence < 0)
        {
            return Result.Failure(IngestionApplicationErrors.NormalizedReservationObservationInvalid);
        }

        if (kind == ReservationDispatchKind.Cancel)
        {
            return Result.Success();
        }

        return observation.Arrival.HasValue && observation.Departure > observation.Arrival &&
               HasMinutePrecision(observation.ExpectedArrivalTime) &&
               HasMinutePrecision(observation.ExpectedDepartureTime) &&
               observation.InventoryUnitIds is { Count: > 0 } &&
               observation.InventoryUnitIds.All(id => id != Guid.Empty) &&
               observation.InventoryUnitIds.Distinct().Count() == observation.InventoryUnitIds.Count &&
               !string.IsNullOrWhiteSpace(observation.PrimaryGuestName) && observation.GuestCount > 0
            ? Result.Success()
            : Result.Failure(IngestionApplicationErrors.NormalizedReservationObservationInvalid);
    }

    private static bool HasMinutePrecision(TimeOnly? value) =>
        !value.HasValue || value.Value.Ticks % TimeSpan.TicksPerMinute == 0;

    private static string CreateSourceSystem(AdapterConnection connection) =>
        $"{connection.AdapterType}:{connection.Id:N}";

    private sealed record ReservationProposalDiff(
        string? PreviousOperationalBaseline,
        string IncomingSnapshot);
}
