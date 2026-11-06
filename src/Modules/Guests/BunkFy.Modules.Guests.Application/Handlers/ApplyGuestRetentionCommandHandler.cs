namespace BunkFy.Modules.Guests.Application.Handlers;

using BunkFy.Modules.Guests.Application.Commands;
using BunkFy.Modules.Guests.Application.Contributors;
using BunkFy.Modules.Guests.Application.Policies;
using BunkFy.Modules.Guests.Application.Ports;
using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.DataRights;
using BunkFy.Modules.Guests.Domain.Models;
using BunkFy.Modules.Guests.Domain.Retention;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;

internal sealed class ApplyGuestRetentionCommandHandler(
    IGuestRetentionExecutionRepository executions,
    IGuestRetentionCandidateRepository candidates,
    IGuestManagementOperationRepository managementOperations,
    IGuestAnonymisationExecutionBoundary executionBoundary,
    GuestRetentionEligibilityEvaluator eligibility,
    IScopeContext scopeContext,
    ISystemClock clock,
    IIdGenerator ids)
    : ICommandHandler<
        ApplyGuestRetentionCommand,
        GuestRetentionMutationResult>
{
    public async Task<Result<GuestRetentionMutationResult>> HandleAsync(
        ApplyGuestRetentionCommand command,
        CancellationToken cancellationToken)
    {
        string? tenantId =
            scopeContext.IsEnabled ? scopeContext.ScopeId : null;
        if (string.IsNullOrWhiteSpace(tenantId) ||
            command.ExecutionId == Guid.Empty ||
            command.Attempt < 1 ||
            command.GuestId == Guid.Empty ||
            command.ExpectedGuestVersion < 1)
        {
            return Result.Failure<GuestRetentionMutationResult>(
                GuestsApplicationErrors.RetentionMutationInvalid);
        }

        GuestRetentionExecution? execution =
            await executions.GetExecutionAsync(
                command.ExecutionId,
                cancellationToken).ConfigureAwait(false);
        if (execution is null ||
            !string.Equals(
                execution.ScopeId,
                tenantId,
                StringComparison.Ordinal) ||
            execution.Attempt != command.Attempt)
        {
            return Result.Failure<GuestRetentionMutationResult>(
                GuestsApplicationErrors.RetentionExecutionNotFound);
        }

        GuestRetentionAnonymisationReceipt? existing =
            await executions.GetReceiptAsync(
                command.GuestId,
                cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!string.Equals(
                    existing.ScopeId,
                    tenantId,
                    StringComparison.Ordinal) ||
                existing.GuestId != command.GuestId)
            {
                return Result.Failure<GuestRetentionMutationResult>(
                    GuestsApplicationErrors.RetentionProofConflict);
            }

            GuestAnonymisationTombstone? existingTombstone =
                await executions.GetTombstoneAsync(
                    command.GuestId,
                    cancellationToken).ConfigureAwait(false);
            if (existingTombstone is null ||
                !existingTombstone.MatchesRetention(existing))
            {
                return Result.Failure<GuestRetentionMutationResult>(
                    GuestsApplicationErrors.RetentionProofConflict);
            }

            return Result.Success(new GuestRetentionMutationResult(
                existing.Matches(
                    command.ExecutionId,
                    command.GuestId,
                    command.ExpectedGuestVersion)
                    ? GuestRetentionMutationStatus.AlreadyApplied
                    : GuestRetentionMutationStatus.NoLongerEligible));
        }

        if (execution.State != GuestRetentionExecutionState.Running ||
            !execution.MatchesCoordinate(
                GuestRetentionCoordinates.DataClassKey,
                GuestRetentionCoordinates.ExecutionPolicyVersion))
        {
            return Result.Failure<GuestRetentionMutationResult>(
                GuestsApplicationErrors.RetentionExecutionNotFound);
        }

        await executionBoundary.AcquireAsync(
            tenantId,
            command.GuestId,
            cancellationToken).ConfigureAwait(false);

        GuestRetentionCandidateSnapshot? snapshot =
            await candidates.LoadAsync(
                command.GuestId,
                cancellationToken).ConfigureAwait(false);
        if (snapshot is null ||
            snapshot.GuestVersion != command.ExpectedGuestVersion)
        {
            return Result.Success(new GuestRetentionMutationResult(
                GuestRetentionMutationStatus.NoLongerEligible));
        }

        DateTimeOffset nowUtc = ToPersistencePrecision(clock.UtcNow);
        GuestRetentionEligibilityResult decision =
            eligibility.Evaluate(snapshot, nowUtc);
        if (decision.Status == GuestRetentionEligibilityStatus.NotDue)
        {
            return Result.Success(new GuestRetentionMutationResult(
                GuestRetentionMutationStatus.NoLongerEligible));
        }

        if (decision.Status == GuestRetentionEligibilityStatus.Blocked)
        {
            return Result.Success(new GuestRetentionMutationResult(
                GuestRetentionMutationStatus.Blocked,
                decision.HoldReviewDueAtUtc));
        }

        if (decision.Status != GuestRetentionEligibilityStatus.Eligible ||
            decision.RetentionDeadlineUtc is null ||
            string.IsNullOrWhiteSpace(decision.PolicySetSha256) ||
            string.IsNullOrWhiteSpace(
                decision.TimeZoneCatalogVersion))
        {
            return Result.Success(new GuestRetentionMutationResult(
                GuestRetentionMutationStatus.Failed,
                Failure: decision.Code switch
                {
                    GuestRetentionEligibilityCode.ProjectionUnavailable =>
                        GuestRetentionMutationFailure
                            .ProjectionUnavailable,
                    GuestRetentionEligibilityCode.PolicyUnavailable =>
                        GuestRetentionMutationFailure.PolicyUnavailable,
                    GuestRetentionEligibilityCode.TimeZoneUnavailable =>
                        GuestRetentionMutationFailure.TimeZoneUnavailable,
                    _ => GuestRetentionMutationFailure.MutationFailed
                }));
        }

        GuestProfile? profile = await executions.GetProfileAsync(
            command.GuestId,
            cancellationToken).ConfigureAwait(false);
        if (profile is null ||
            profile.Version != command.ExpectedGuestVersion)
        {
            return Result.Success(new GuestRetentionMutationResult(
                GuestRetentionMutationStatus.NoLongerEligible));
        }

        Guid eventId = ids.NewId();
        Result<GuestProfileAnonymisationOutcome> mutated =
            profile.AnonymiseForRetention(
                command.ExpectedGuestVersion,
                GuestRetentionCoordinates.SystemActor,
                eventId,
                nowUtc);
        if (mutated.IsFailure)
        {
            return Result.Failure<GuestRetentionMutationResult>(
                mutated.Error);
        }

        Result<GuestRetentionAnonymisationReceipt> receipt =
            GuestRetentionAnonymisationReceipt.Create(
                ids.NewId(),
                tenantId,
                command.ExecutionId,
                command.GuestId,
                mutated.Value.PreviousVersion,
                mutated.Value.CurrentVersion,
                decision.AffectedPropertyCount,
                decision.RetentionDeadlineUtc.Value,
                decision.PolicySetSha256,
                decision.TimeZoneCatalogVersion,
                mutated.Value.EventId,
                GuestRetentionCoordinates.SystemActor,
                mutated.Value.OccurredAtUtc);
        if (receipt.IsFailure)
        {
            return Result.Failure<GuestRetentionMutationResult>(
                receipt.Error);
        }

        Result<GuestAnonymisationTombstone> tombstone =
            GuestAnonymisationTombstone.CreateForRetention(
                tenantId,
                receipt.Value);
        if (tombstone.IsFailure)
        {
            return Result.Failure<GuestRetentionMutationResult>(
                tombstone.Error);
        }

        Result affected = execution.RecordAffected();
        if (affected.IsFailure)
        {
            return Result.Failure<GuestRetentionMutationResult>(
                affected.Error);
        }

        await managementOperations.DeleteForGuestAsync(
            command.GuestId,
            cancellationToken).ConfigureAwait(false);
        await executions.AddAnonymisationProofAsync(
            receipt.Value,
            tombstone.Value,
            cancellationToken).ConfigureAwait(false);
        return Result.Success(new GuestRetentionMutationResult(
            GuestRetentionMutationStatus.Applied));
    }

    private static DateTimeOffset ToPersistencePrecision(
        DateTimeOffset value)
    {
        const long ticksPerMicrosecond =
            TimeSpan.TicksPerMillisecond / 1000;
        return new(
            value.Ticks - (value.Ticks % ticksPerMicrosecond),
            value.Offset);
    }
}
