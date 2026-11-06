namespace BunkFy.Modules.Reservations.Application.Handlers;

using BunkFy.Modules.Reservations.Application.Commands;
using BunkFy.Modules.Reservations.Application.Contributors;
using BunkFy.Modules.Reservations.Application.Policies;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Domain.DataRights;
using BunkFy.Modules.Reservations.Domain.Retention;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;

internal sealed class ApplyReservationRetentionCommandHandler(
    IReservationRetentionExecutionRepository executions,
    IReservationRetentionCandidateRepository candidates,
    IReservationRepository reservations,
    IReservationAnonymisationRepository anonymisation,
    ReservationMutationCoordinator mutations,
    ReservationRetentionEligibilityEvaluator eligibility,
    IScopeContext scopeContext,
    ISystemClock clock,
    IIdGenerator ids)
    : ICommandHandler<
        ApplyReservationRetentionCommand,
        ReservationRetentionMutationResult>
{
    public async Task<Result<ReservationRetentionMutationResult>>
        HandleAsync(
            ApplyReservationRetentionCommand command,
            CancellationToken cancellationToken)
    {
        string? tenantId =
            scopeContext.IsEnabled ? scopeContext.ScopeId : null;
        if (string.IsNullOrWhiteSpace(tenantId) ||
            command.ExecutionId == Guid.Empty ||
            command.Attempt < 1 ||
            command.PropertyId == Guid.Empty ||
            command.ReservationId == Guid.Empty ||
            command.ExpectedReservationVersion < 1 ||
            command.ExpectedDetailsRevision < 1)
        {
            return Result.Failure<
                ReservationRetentionMutationResult>(
                ReservationsApplicationErrors
                    .RetentionMutationInvalid);
        }

        ReservationRetentionExecution? execution =
            await executions.GetExecutionAsync(
                command.ExecutionId,
                cancellationToken).ConfigureAwait(false);
        if (execution is null ||
            !string.Equals(
                execution.ScopeId,
                tenantId,
                StringComparison.Ordinal) ||
            execution.State !=
                ReservationRetentionExecutionState.Running ||
            execution.Attempt != command.Attempt ||
            !execution.MatchesCoordinate(
                ReservationRetentionCoordinates.DataClassKey,
                ReservationRetentionCoordinates
                    .ExecutionPolicyVersion))
        {
            return Result.Failure<
                ReservationRetentionMutationResult>(
                ReservationsApplicationErrors
                    .RetentionExecutionNotFound);
        }

        ReservationRetentionAnonymisationReceipt? existing =
            await executions.GetReceiptAsync(
                command.ReservationId,
                cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            ReservationAnonymisationTombstone? existingTombstone =
                await executions.GetTombstoneAsync(
                    command.ReservationId,
                    cancellationToken).ConfigureAwait(false);
            if (existingTombstone is null ||
                !existingTombstone.MatchesRetention(existing))
            {
                return Result.Failure<
                    ReservationRetentionMutationResult>(
                    ReservationsApplicationErrors
                        .RetentionProofConflict);
            }

            return Result.Success(
                new ReservationRetentionMutationResult(
                    existing.Matches(
                        command.ExecutionId,
                        command.ReservationId,
                        command.ExpectedReservationVersion,
                        command.ExpectedDetailsRevision)
                        ? ReservationRetentionMutationStatus
                            .AlreadyApplied
                        : ReservationRetentionMutationStatus
                            .NoLongerEligible));
        }

        _ = await mutations.AcquireExistingAsync(
            command.ReservationId,
            cancellationToken).ConfigureAwait(false);

        ReservationRetentionCandidateSnapshot? snapshot =
            await candidates.LoadAsync(
                command.PropertyId,
                command.ReservationId,
                cancellationToken).ConfigureAwait(false);
        if (snapshot is null ||
            snapshot.ReservationVersion !=
                command.ExpectedReservationVersion ||
            snapshot.DetailsRevision !=
                command.ExpectedDetailsRevision)
        {
            return Result.Success(
                new ReservationRetentionMutationResult(
                    ReservationRetentionMutationStatus
                        .NoLongerEligible));
        }

        DateTimeOffset nowUtc =
            ReservationMutationTime.Normalize(clock.UtcNow);
        ReservationRetentionEligibilityResult decision =
            eligibility.Evaluate(snapshot, nowUtc);
        if (decision.Status ==
            ReservationRetentionEligibilityStatus.NotDue)
        {
            return Result.Success(
                new ReservationRetentionMutationResult(
                    ReservationRetentionMutationStatus
                        .NoLongerEligible));
        }

        if (decision.Status ==
            ReservationRetentionEligibilityStatus.Blocked)
        {
            return Result.Success(
                new ReservationRetentionMutationResult(
                    ReservationRetentionMutationStatus.Blocked,
                    decision.HoldReviewDueAtUtc));
        }

        if (decision.Status !=
                ReservationRetentionEligibilityStatus.Eligible ||
            !decision.TerminalAtUtc.HasValue ||
            !decision.RetentionDeadlineUtc.HasValue ||
            string.IsNullOrWhiteSpace(
                decision.PolicyEvidenceSha256))
        {
            return Result.Success(
                new ReservationRetentionMutationResult(
                    ReservationRetentionMutationStatus.Failed,
                    Failure: decision.Code switch
                    {
                        ReservationRetentionEligibilityCode
                                .ProjectionUnavailable =>
                            ReservationRetentionMutationFailure
                                .ProjectionUnavailable,
                        ReservationRetentionEligibilityCode
                                .PolicyUnavailable =>
                            ReservationRetentionMutationFailure
                                .PolicyUnavailable,
                        _ => ReservationRetentionMutationFailure
                            .MutationFailed
                    }));
        }

        Reservation? reservation =
            await reservations.GetForDataRightsAsync(
                command.PropertyId,
                command.ReservationId,
                cancellationToken).ConfigureAwait(false);
        if (reservation is null ||
            reservation.Version !=
                command.ExpectedReservationVersion ||
            reservation.DetailsRevision !=
                command.ExpectedDetailsRevision ||
            reservation.TerminalAtUtc != decision.TerminalAtUtc)
        {
            return Result.Success(
                new ReservationRetentionMutationResult(
                    ReservationRetentionMutationStatus
                        .NoLongerEligible));
        }

        Result<ReservationAnonymisationOutcome> mutated =
            reservation.Anonymise(
                command.ExpectedReservationVersion,
                command.ExpectedDetailsRevision,
                ReservationRetentionCoordinates.SystemActor,
                ids.NewId(),
                nowUtc);
        if (mutated.IsFailure)
        {
            return Result.Failure<
                ReservationRetentionMutationResult>(
                mutated.Error);
        }

        ReservationAnonymisationAffectedRecords affected =
            await anonymisation.RedactOwnedRecordsAsync(
                reservation,
                mutated.Value,
                cancellationToken).ConfigureAwait(false);
        Result<ReservationRetentionAnonymisationReceipt> receipt =
            ReservationRetentionAnonymisationReceipt.Create(
                ids.NewId(),
                tenantId,
                command.ExecutionId,
                command.PropertyId,
                command.ReservationId,
                mutated.Value,
                decision.TerminalAtUtc.Value,
                decision.RetentionDeadlineUtc.Value,
                decision.PolicyEvidenceSha256,
                affected.RedactedHistoryCount,
                affected.ReducedExternalOperationCount,
                affected.SuppressedReminderCount);
        if (receipt.IsFailure)
        {
            return Result.Failure<
                ReservationRetentionMutationResult>(
                receipt.Error);
        }

        Result<ReservationAnonymisationTombstone> tombstone =
            ReservationAnonymisationTombstone
                .CreateForRetention(receipt.Value);
        if (tombstone.IsFailure)
        {
            return Result.Failure<
                ReservationRetentionMutationResult>(
                tombstone.Error);
        }

        Result recorded = execution.RecordAffected();
        if (recorded.IsFailure)
        {
            return Result.Failure<
                ReservationRetentionMutationResult>(
                recorded.Error);
        }

        await executions.AddAnonymisationProofAsync(
            receipt.Value,
            tombstone.Value,
            cancellationToken).ConfigureAwait(false);
        return Result.Success(
            new ReservationRetentionMutationResult(
                ReservationRetentionMutationStatus.Applied));
    }
}
