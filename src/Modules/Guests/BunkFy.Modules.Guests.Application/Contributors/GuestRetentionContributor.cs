namespace BunkFy.Modules.Guests.Application.Contributors;

using BunkFy.Modules.Guests.Application.Commands;
using BunkFy.Modules.Guests.Application.Handlers;
using BunkFy.Modules.Guests.Application.Policies;
using BunkFy.Modules.Guests.Application.Ports;
using BunkFy.Modules.Guests.Domain.Retention;
using BunkFy.Modules.Retention.Contracts;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Microsoft.Extensions.Options;

internal sealed class GuestRetentionContributor
    : IRetentionExecutionContributor
{
    private readonly IRequestDispatcher dispatcher;
    private readonly IGuestRetentionCandidateRepository candidates;
    private readonly GuestRetentionEligibilityEvaluator eligibility;
    private readonly GuestRetentionOptions options;
    private readonly ISystemClock clock;

    public GuestRetentionContributor(
        IRequestDispatcher dispatcher,
        IGuestRetentionCandidateRepository candidates,
        GuestRetentionEligibilityEvaluator eligibility,
        IOptions<GuestRetentionOptions> options,
        ISystemClock clock)
    {
        this.dispatcher = dispatcher;
        this.candidates = candidates;
        this.eligibility = eligibility;
        this.options = options.Value;
        this.clock = clock;
        this.Schedule = new(
            GuestRetentionCoordinates.OwnerKey,
            GuestRetentionCoordinates.DataClassKey,
            RetentionTargetScopeKind.Tenant,
            GuestRetentionCoordinates.ExecutionPolicyVersion,
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
                0,
                0,
                1,
                GuestRetentionCoordinates.CoordinateInvalidOutcome,
                this.clock.UtcNow);
        }

        Result<GuestRetentionExecutionStart> started =
            await this.dispatcher.SendAsync(
                new BeginGuestRetentionExecutionCommand(request),
                cancellationToken).ConfigureAwait(false);
        GuestRetentionExecutionStart start = Require(started);
        if (!start.DispatchRequired)
        {
            return start.CompletedResult!;
        }

        GuestRetentionScanPage page = await this.candidates.ScanAsync(
            start.StartingProjectionOrdinal,
            this.options.ScanSize,
            cancellationToken).ConfigureAwait(false);
        int mutationCount = 0;
        int pageScannedCount = 0;
        bool blocked = false;
        bool projectionFailed = false;
        bool policyFailed = false;
        bool timeZoneFailed = false;
        bool mutationFailed = false;
        DateTimeOffset? holdReviewDueAtUtc = null;
        DateTimeOffset evaluatedAtUtc = this.clock.UtcNow;
        long nextAfterProjectionOrdinal =
            start.StartingProjectionOrdinal;

        foreach (GuestRetentionCandidateSnapshot candidate in
                 page.Candidates)
        {
            GuestRetentionEligibilityResult decision =
                this.eligibility.Evaluate(candidate, evaluatedAtUtc);
            if (decision.Status == GuestRetentionEligibilityStatus.NotDue)
            {
                RecordScanned(
                    candidate,
                    ref pageScannedCount,
                    ref nextAfterProjectionOrdinal);
                continue;
            }

            if (decision.Status == GuestRetentionEligibilityStatus.Blocked)
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

            if (decision.Status == GuestRetentionEligibilityStatus.Failed)
            {
                RecordFailure(
                    decision.Code,
                    ref projectionFailed,
                    ref policyFailed,
                    ref timeZoneFailed,
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

            Result<GuestRetentionMutationResult> mutation =
                await this.dispatcher.SendAsync(
                    new ApplyGuestRetentionCommand(
                        request.ExecutionId,
                        request.Attempt,
                        candidate.GuestId,
                        candidate.GuestVersion),
                    cancellationToken).ConfigureAwait(false);
            GuestRetentionMutationResult outcome = Require(mutation);
            switch (outcome.Status)
            {
                case GuestRetentionMutationStatus.Applied:
                    mutationCount++;
                    break;
                case GuestRetentionMutationStatus.AlreadyApplied:
                case GuestRetentionMutationStatus.NoLongerEligible:
                    break;
                case GuestRetentionMutationStatus.Blocked:
                    blocked = true;
                    holdReviewDueAtUtc = Earlier(
                        holdReviewDueAtUtc,
                        outcome.HoldReviewDueAtUtc);
                    break;
                case GuestRetentionMutationStatus.Failed:
                    RecordFailure(
                        outcome.Failure,
                        ref projectionFailed,
                        ref policyFailed,
                        ref timeZoneFailed,
                        ref mutationFailed);
                    break;
                default:
                    throw new InvalidOperationException(
                        "Guests.RetentionMutationStatusUnsupported");
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
            mutationFailed || projectionFailed || policyFailed ||
            timeZoneFailed;
        int remainingCount =
            !reachedEnd || blocked || failed
                ? 1
                : 0;
        GuestRetentionExecutionState state;
        string outcomeCode;
        if (failed)
        {
            state = GuestRetentionExecutionState.Failed;
            outcomeCode = mutationFailed
                ? GuestRetentionCoordinates.MutationFailedOutcome
                : timeZoneFailed
                    ? GuestRetentionCoordinates
                        .TimeZoneUnavailableOutcome
                    : projectionFailed
                    ? GuestRetentionCoordinates
                        .ProjectionUnavailableOutcome
                    : GuestRetentionCoordinates
                        .PolicyUnavailableOutcome;
            holdReviewDueAtUtc = null;
        }
        else if (blocked)
        {
            state = GuestRetentionExecutionState.Blocked;
            outcomeCode = GuestRetentionCoordinates.LegalHoldOutcome;
        }
        else
        {
            state = GuestRetentionExecutionState.Completed;
            outcomeCode = remainingCount > 0
                ? GuestRetentionCoordinates.BacklogOutcome
                : GuestRetentionCoordinates.CompletedOutcome;
        }

        Result<RetentionContributionResult> completed =
            await this.dispatcher.SendAsync(
                new CompleteGuestRetentionExecutionCommand(
                    request.ExecutionId,
                    request.Attempt,
                    state,
                    scannedCount,
                    remainingCount,
                    outcomeCode,
                    GuestMutationTime.Normalize(this.clock.UtcNow),
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
            GuestRetentionCoordinates.ExecutionPolicyVersion &&
        string.Equals(
            request.OwnerKey,
            GuestRetentionCoordinates.OwnerKey,
            StringComparison.Ordinal) &&
        string.Equals(
            request.DataClassKey,
            GuestRetentionCoordinates.DataClassKey,
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
        GuestRetentionCandidateSnapshot candidate,
        ref int pageScannedCount,
        ref long nextAfterProjectionOrdinal)
    {
        pageScannedCount = checked(pageScannedCount + 1);
        nextAfterProjectionOrdinal = candidate.ProjectionOrdinal;
    }

    private static void RecordFailure(
        GuestRetentionEligibilityCode code,
        ref bool projectionFailed,
        ref bool policyFailed,
        ref bool timeZoneFailed,
        ref bool mutationFailed)
    {
        if (code == GuestRetentionEligibilityCode.ProjectionUnavailable)
        {
            projectionFailed = true;
        }
        else if (code == GuestRetentionEligibilityCode.PolicyUnavailable)
        {
            policyFailed = true;
        }
        else if (code == GuestRetentionEligibilityCode.TimeZoneUnavailable)
        {
            timeZoneFailed = true;
        }
        else
        {
            mutationFailed = true;
        }
    }

    private static void RecordFailure(
        GuestRetentionMutationFailure failure,
        ref bool projectionFailed,
        ref bool policyFailed,
        ref bool timeZoneFailed,
        ref bool mutationFailed)
    {
        switch (failure)
        {
            case GuestRetentionMutationFailure.ProjectionUnavailable:
                projectionFailed = true;
                break;
            case GuestRetentionMutationFailure.PolicyUnavailable:
                policyFailed = true;
                break;
            case GuestRetentionMutationFailure.TimeZoneUnavailable:
                timeZoneFailed = true;
                break;
            case GuestRetentionMutationFailure.MutationFailed:
            case GuestRetentionMutationFailure.None:
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
