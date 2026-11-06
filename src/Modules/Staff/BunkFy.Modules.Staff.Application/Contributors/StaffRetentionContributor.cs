namespace BunkFy.Modules.Staff.Application.Contributors;

using BunkFy.Modules.Retention.Contracts;
using BunkFy.Modules.Staff.Application.Commands;
using BunkFy.Modules.Staff.Application.Policies;
using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.Retention;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Microsoft.Extensions.Options;

internal sealed class StaffRetentionContributor
    : IRetentionExecutionContributor
{
    private readonly IRequestDispatcher dispatcher;
    private readonly IStaffRetentionCandidateRepository candidates;
    private readonly StaffRetentionEligibilityEvaluator eligibility;
    private readonly StaffRetentionPrerequisiteEvaluator prerequisites;
    private readonly StaffRetentionOptions options;
    private readonly ISystemClock clock;

    public StaffRetentionContributor(
        IRequestDispatcher dispatcher,
        IStaffRetentionCandidateRepository candidates,
        StaffRetentionEligibilityEvaluator eligibility,
        StaffRetentionPrerequisiteEvaluator prerequisites,
        IOptions<StaffRetentionOptions> options,
        ISystemClock clock)
    {
        this.dispatcher = dispatcher;
        this.candidates = candidates;
        this.eligibility = eligibility;
        this.prerequisites = prerequisites;
        this.options = options.Value;
        this.clock = clock;
        this.Schedule = new(
            StaffRetentionCoordinates.OwnerKey,
            StaffRetentionCoordinates.DataClassKey,
            RetentionTargetScopeKind.Tenant,
            StaffRetentionCoordinates.ExecutionPolicyVersion,
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
                StaffRetentionCoordinates.CoordinateInvalidOutcome,
                this.clock.UtcNow);
        }

        Result<StaffRetentionExecutionStart> started =
            await this.dispatcher.SendAsync(
                new BeginStaffRetentionExecutionCommand(request),
                cancellationToken).ConfigureAwait(false);
        StaffRetentionExecutionStart start = Require(started);
        if (!start.DispatchRequired)
        {
            return start.CompletedResult!;
        }

        StaffRetentionScanPage page = await this.candidates.ScanAsync(
            start.StartingProjectionOrdinal,
            this.options.ScanSize,
            cancellationToken).ConfigureAwait(false);
        int mutationCount = 0;
        int pageScannedCount = 0;
        bool blocked = false;
        bool projectionFailed = false;
        bool policyFailed = false;
        bool prerequisiteBlocked = false;
        bool prerequisiteUnavailable = false;
        bool mutationFailed = false;
        DateTimeOffset? holdReviewDueAtUtc = null;
        DateTimeOffset evaluatedAtUtc = this.clock.UtcNow;
        long nextAfterProjectionOrdinal =
            start.StartingProjectionOrdinal;

        foreach (StaffRetentionCandidateSnapshot candidate in
                 page.Candidates)
        {
            StaffRetentionEligibilityResult decision =
                this.eligibility.Evaluate(candidate, evaluatedAtUtc);
            if (decision.Status ==
                StaffRetentionEligibilityStatus.NotDue)
            {
                RecordScanned(
                    candidate,
                    ref pageScannedCount,
                    ref nextAfterProjectionOrdinal);
                continue;
            }

            if (decision.Status ==
                StaffRetentionEligibilityStatus.Blocked)
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
                StaffRetentionEligibilityStatus.Failed)
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

            StaffRetentionPrerequisiteEvaluation preparation =
                await this.prerequisites.PrepareAsync(
                    new(
                        StaffRetentionAnonymisationPrerequisiteContract
                            .CurrentVersion,
                        request.ExecutionId,
                        request.TenantId,
                        candidate.StaffMemberId,
                        candidate.StaffVersion),
                    cancellationToken).ConfigureAwait(false);
            if (preparation !=
                StaffRetentionPrerequisiteEvaluation.Completed)
            {
                RecordFailure(
                    preparation ==
                        StaffRetentionPrerequisiteEvaluation.Blocked
                        ? StaffRetentionMutationFailure
                            .PrerequisiteBlocked
                        : StaffRetentionMutationFailure
                            .PrerequisiteUnavailable,
                    ref projectionFailed,
                    ref policyFailed,
                    ref prerequisiteBlocked,
                    ref prerequisiteUnavailable,
                    ref mutationFailed);
                RecordScanned(
                    candidate,
                    ref pageScannedCount,
                    ref nextAfterProjectionOrdinal);
                continue;
            }

            Result<StaffRetentionMutationResult> mutation =
                await this.dispatcher.SendAsync(
                    new ApplyStaffRetentionCommand(
                        request.ExecutionId,
                        request.Attempt,
                        candidate.StaffMemberId,
                        candidate.StaffVersion),
                    cancellationToken).ConfigureAwait(false);
            StaffRetentionMutationResult outcome = Require(mutation);
            switch (outcome.Status)
            {
                case StaffRetentionMutationStatus.Applied:
                    mutationCount++;
                    break;
                case StaffRetentionMutationStatus.AlreadyApplied:
                case StaffRetentionMutationStatus.NoLongerEligible:
                    break;
                case StaffRetentionMutationStatus.Blocked:
                    blocked = true;
                    holdReviewDueAtUtc = Earlier(
                        holdReviewDueAtUtc,
                        outcome.HoldReviewDueAtUtc);
                    break;
                case StaffRetentionMutationStatus.Failed:
                    RecordFailure(
                        outcome.Failure,
                        ref projectionFailed,
                        ref policyFailed,
                        ref prerequisiteBlocked,
                        ref prerequisiteUnavailable,
                        ref mutationFailed);
                    break;
                default:
                    throw new InvalidOperationException(
                        "Staff.RetentionMutationStatusUnsupported");
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
            mutationFailed ||
            prerequisiteBlocked ||
            prerequisiteUnavailable ||
            projectionFailed ||
            policyFailed;
        int remainingCount =
            !reachedEnd || blocked || failed
                ? 1
                : 0;
        StaffRetentionExecutionState state;
        string outcomeCode;
        if (failed)
        {
            state = StaffRetentionExecutionState.Failed;
            outcomeCode = mutationFailed
                ? StaffRetentionCoordinates.MutationFailedOutcome
                : prerequisiteBlocked
                    ? StaffRetentionCoordinates
                        .PrerequisiteBlockedOutcome
                    : prerequisiteUnavailable
                        ? StaffRetentionCoordinates
                            .PrerequisiteUnavailableOutcome
                        : projectionFailed
                            ? StaffRetentionCoordinates
                                .ProjectionUnavailableOutcome
                            : StaffRetentionCoordinates
                                .PolicyUnavailableOutcome;
            holdReviewDueAtUtc = null;
        }
        else if (blocked)
        {
            state = StaffRetentionExecutionState.Blocked;
            outcomeCode =
                StaffRetentionCoordinates.LegalHoldOutcome;
        }
        else
        {
            state = StaffRetentionExecutionState.Completed;
            outcomeCode = remainingCount > 0
                ? StaffRetentionCoordinates.BacklogOutcome
                : StaffRetentionCoordinates.CompletedOutcome;
        }

        Result<RetentionContributionResult> completed =
            await this.dispatcher.SendAsync(
                new CompleteStaffRetentionExecutionCommand(
                    request.ExecutionId,
                    request.Attempt,
                    state,
                    scannedCount,
                    remainingCount,
                    outcomeCode,
                    this.clock.UtcNow,
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
            StaffRetentionCoordinates.ExecutionPolicyVersion &&
        string.Equals(
            request.OwnerKey,
            StaffRetentionCoordinates.OwnerKey,
            StringComparison.Ordinal) &&
        string.Equals(
            request.DataClassKey,
            StaffRetentionCoordinates.DataClassKey,
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
        StaffRetentionCandidateSnapshot candidate,
        ref int pageScannedCount,
        ref long nextAfterProjectionOrdinal)
    {
        pageScannedCount = checked(pageScannedCount + 1);
        nextAfterProjectionOrdinal = candidate.ProjectionOrdinal;
    }

    private static void RecordFailure(
        StaffRetentionEligibilityCode code,
        ref bool projectionFailed,
        ref bool policyFailed,
        ref bool mutationFailed)
    {
        if (code ==
            StaffRetentionEligibilityCode.ProjectionUnavailable)
        {
            projectionFailed = true;
        }
        else if (code ==
                 StaffRetentionEligibilityCode.PolicyUnavailable)
        {
            policyFailed = true;
        }
        else
        {
            mutationFailed = true;
        }
    }

    private static void RecordFailure(
        StaffRetentionMutationFailure failure,
        ref bool projectionFailed,
        ref bool policyFailed,
        ref bool prerequisiteBlocked,
        ref bool prerequisiteUnavailable,
        ref bool mutationFailed)
    {
        switch (failure)
        {
            case StaffRetentionMutationFailure.ProjectionUnavailable:
                projectionFailed = true;
                break;
            case StaffRetentionMutationFailure.PolicyUnavailable:
                policyFailed = true;
                break;
            case StaffRetentionMutationFailure.PrerequisiteBlocked:
                prerequisiteBlocked = true;
                break;
            case StaffRetentionMutationFailure
                    .PrerequisiteUnavailable:
                prerequisiteUnavailable = true;
                break;
            case StaffRetentionMutationFailure
                    .IdentityAnchorResolutionRequired:
                mutationFailed = true;
                break;
            case StaffRetentionMutationFailure.MutationFailed:
            case StaffRetentionMutationFailure.None:
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
