namespace BunkFy.Modules.Reservations.Application.Contributors;

using BunkFy.Modules.Reservations.Application.Commands;
using BunkFy.Modules.Reservations.Application.Handlers;
using BunkFy.Modules.Reservations.Application.Policies;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Domain.Retention;
using BunkFy.Modules.Retention.Contracts;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Microsoft.Extensions.Options;

internal sealed class ReservationRetentionContributor
    : IRetentionExecutionContributor
{
    private readonly IRequestDispatcher dispatcher;
    private readonly IReservationRetentionCandidateRepository
        candidates;
    private readonly ReservationRetentionEligibilityEvaluator
        eligibility;
    private readonly ReservationRetentionOptions options;
    private readonly ISystemClock clock;

    public ReservationRetentionContributor(
        IRequestDispatcher dispatcher,
        IReservationRetentionCandidateRepository candidates,
        ReservationRetentionEligibilityEvaluator eligibility,
        IOptions<ReservationRetentionOptions> options,
        ISystemClock clock)
    {
        this.dispatcher = dispatcher;
        this.candidates = candidates;
        this.eligibility = eligibility;
        this.options = options.Value;
        this.clock = clock;
        this.Schedule = new(
            ReservationRetentionCoordinates.OwnerKey,
            ReservationRetentionCoordinates.DataClassKey,
            RetentionTargetScopeKind.Tenant,
            ReservationRetentionCoordinates.ExecutionPolicyVersion,
            this.options.Interval,
            maxAttempts: 3,
            executionTimeout: TimeSpan.FromMinutes(15));
    }

    public RetentionScheduleDescriptor Schedule { get; }

    public async Task<RetentionContributionResult> ExecuteAsync(
        RetentionContributionRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsExpectedRequest(request))
        {
            return Result(
                RetentionContributionStatus.Failed,
                scannedCount: 0,
                affectedCount: 0,
                remainingCount: 1,
                ReservationRetentionCoordinates
                    .CoordinateInvalidOutcome,
                ReservationMutationTime.Normalize(
                    this.clock.UtcNow));
        }

        Result<ReservationRetentionExecutionStart> started =
            await this.dispatcher.SendAsync(
                new BeginReservationRetentionExecutionCommand(
                    request),
                cancellationToken).ConfigureAwait(false);
        ReservationRetentionExecutionStart start = Require(started);
        if (!start.DispatchRequired)
        {
            return start.CompletedResult!;
        }

        ReservationRetentionScanPage page =
            await this.candidates.ScanAsync(
                start.StartingProjectionOrdinal,
                this.options.ScanSize,
                cancellationToken).ConfigureAwait(false);
        int mutationCount = 0;
        int pageScannedCount = 0;
        bool blocked = false;
        bool projectionFailed = false;
        bool policyFailed = false;
        bool mutationFailed = false;
        DateTimeOffset? holdReviewDueAtUtc = null;
        DateTimeOffset evaluatedAtUtc = this.clock.UtcNow;
        long nextAfterProjectionOrdinal =
            start.StartingProjectionOrdinal;

        foreach (ReservationRetentionCandidateSnapshot candidate in
                 page.Candidates)
        {
            ReservationRetentionEligibilityResult decision =
                this.eligibility.Evaluate(
                    candidate,
                    evaluatedAtUtc);
            if (decision.Status ==
                ReservationRetentionEligibilityStatus.NotDue)
            {
                RecordScanned(
                    candidate,
                    ref pageScannedCount,
                    ref nextAfterProjectionOrdinal);
                continue;
            }

            if (decision.Status ==
                ReservationRetentionEligibilityStatus.Blocked)
            {
                blocked = true;
                holdReviewDueAtUtc = Earlier(
                    holdReviewDueAtUtc,
                    decision.HoldReviewDueAtUtc);
                RecordScanned(
                    candidate,
                    ref pageScannedCount,
                    ref nextAfterProjectionOrdinal);
                continue;
            }

            if (decision.Status ==
                ReservationRetentionEligibilityStatus.Failed)
            {
                RecordFailure(
                    decision.Code,
                    ref projectionFailed,
                    ref policyFailed,
                    ref mutationFailed);
                RecordScanned(
                    candidate,
                    ref pageScannedCount,
                    ref nextAfterProjectionOrdinal);
                continue;
            }

            if (mutationCount >= this.options.MutationBatchSize)
            {
                break;
            }

            Result<ReservationRetentionMutationResult> mutation =
                await this.dispatcher.SendAsync(
                    new ApplyReservationRetentionCommand(
                        request.ExecutionId,
                        request.Attempt,
                        candidate.PropertyId,
                        candidate.ReservationId,
                        candidate.ReservationVersion,
                        candidate.DetailsRevision),
                    cancellationToken).ConfigureAwait(false);
            ReservationRetentionMutationResult outcome =
                Require(mutation);
            switch (outcome.Status)
            {
                case ReservationRetentionMutationStatus.Applied:
                    mutationCount++;
                    break;
                case ReservationRetentionMutationStatus.AlreadyApplied:
                case ReservationRetentionMutationStatus
                    .NoLongerEligible:
                    break;
                case ReservationRetentionMutationStatus.Blocked:
                    blocked = true;
                    holdReviewDueAtUtc = Earlier(
                        holdReviewDueAtUtc,
                        outcome.HoldReviewDueAtUtc);
                    break;
                case ReservationRetentionMutationStatus.Failed:
                    RecordFailure(
                        outcome.Failure,
                        ref projectionFailed,
                        ref policyFailed,
                        ref mutationFailed);
                    break;
                default:
                    throw new InvalidOperationException(
                        "Reservations.RetentionMutationStatusUnsupported");
            }

            RecordScanned(
                candidate,
                ref pageScannedCount,
                ref nextAfterProjectionOrdinal);
        }

        bool reachedEnd =
            page.ReachedEnd &&
            pageScannedCount == page.Candidates.Count;
        if (reachedEnd)
        {
            nextAfterProjectionOrdinal = 0;
        }

        int scannedCount = checked(
            start.AffectedCount + pageScannedCount);
        bool failed =
            mutationFailed || projectionFailed || policyFailed;
        int remainingCount =
            !reachedEnd || blocked || failed
                ? 1
                : 0;
        ReservationRetentionExecutionState state;
        string outcomeCode;
        if (failed)
        {
            state = ReservationRetentionExecutionState.Failed;
            outcomeCode = mutationFailed
                ? ReservationRetentionCoordinates
                    .MutationFailedOutcome
                : projectionFailed
                    ? ReservationRetentionCoordinates
                        .ProjectionUnavailableOutcome
                    : ReservationRetentionCoordinates
                        .PolicyUnavailableOutcome;
            holdReviewDueAtUtc = null;
        }
        else if (blocked)
        {
            state = ReservationRetentionExecutionState.Blocked;
            outcomeCode = ReservationRetentionCoordinates
                .LegalHoldOutcome;
        }
        else
        {
            state = ReservationRetentionExecutionState.Completed;
            outcomeCode = remainingCount > 0
                ? ReservationRetentionCoordinates.BacklogOutcome
                : ReservationRetentionCoordinates.CompletedOutcome;
        }

        Result<RetentionContributionResult> completed =
            await this.dispatcher.SendAsync(
                new CompleteReservationRetentionExecutionCommand(
                    request.ExecutionId,
                    request.Attempt,
                    state,
                    scannedCount,
                    remainingCount,
                    outcomeCode,
                    ReservationMutationTime.Normalize(
                        this.clock.UtcNow),
                    holdReviewDueAtUtc,
                    start.StartingProjectionOrdinal,
                    nextAfterProjectionOrdinal),
                cancellationToken).ConfigureAwait(false);
        return Require(completed);
    }

    private static bool IsExpectedRequest(
        RetentionContributionRequest request) =>
        request.ContractVersion ==
            RetentionExecutionContract.CurrentVersion &&
        request.PropertyId is null &&
        request.ExecutionId != Guid.Empty &&
        request.ExecutionPolicyVersion ==
            ReservationRetentionCoordinates.ExecutionPolicyVersion &&
        string.Equals(
            request.OwnerKey,
            ReservationRetentionCoordinates.OwnerKey,
            StringComparison.Ordinal) &&
        string.Equals(
            request.DataClassKey,
            ReservationRetentionCoordinates.DataClassKey,
            StringComparison.Ordinal) &&
        Guid.TryParse(request.TenantId, out _);

    private static DateTimeOffset? Earlier(
        DateTimeOffset? current,
        DateTimeOffset? candidate) =>
        candidate is null
            ? current
            : current is null || candidate < current
                ? candidate
                : current;

    private static void RecordScanned(
        ReservationRetentionCandidateSnapshot candidate,
        ref int pageScannedCount,
        ref long nextAfterProjectionOrdinal)
    {
        pageScannedCount = checked(pageScannedCount + 1);
        nextAfterProjectionOrdinal = candidate.ProjectionOrdinal;
    }

    private static void RecordFailure(
        ReservationRetentionEligibilityCode code,
        ref bool projectionFailed,
        ref bool policyFailed,
        ref bool mutationFailed)
    {
        if (code ==
            ReservationRetentionEligibilityCode
                .ProjectionUnavailable)
        {
            projectionFailed = true;
        }
        else if (code ==
                 ReservationRetentionEligibilityCode
                     .PolicyUnavailable)
        {
            policyFailed = true;
        }
        else
        {
            mutationFailed = true;
        }
    }

    private static void RecordFailure(
        ReservationRetentionMutationFailure failure,
        ref bool projectionFailed,
        ref bool policyFailed,
        ref bool mutationFailed)
    {
        switch (failure)
        {
            case ReservationRetentionMutationFailure
                .ProjectionUnavailable:
                projectionFailed = true;
                break;
            case ReservationRetentionMutationFailure
                .PolicyUnavailable:
                policyFailed = true;
                break;
            case ReservationRetentionMutationFailure.MutationFailed:
            case ReservationRetentionMutationFailure.None:
            default:
                mutationFailed = true;
                break;
        }
    }

    private static RetentionContributionResult Result(
        RetentionContributionStatus status,
        int scannedCount,
        int affectedCount,
        int remainingCount,
        string outcomeCode,
        DateTimeOffset completedAtUtc) =>
        new(
            RetentionExecutionContract.CurrentVersion,
            status,
            scannedCount,
            affectedCount,
            remainingCount,
            outcomeCode,
            completedAtUtc);

    private static T Require<T>(Result<T> result) =>
        result.IsSuccess
            ? result.Value
            : throw new InvalidOperationException(
                $"{result.Error.Code}: {result.Error.Message}");
}
