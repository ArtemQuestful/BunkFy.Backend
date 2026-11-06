namespace BunkFy.Modules.Workspaces.Application.Tasks;

using System.Text.Json;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Application.Handlers;
using BunkFy.Modules.Workspaces.Application.Models;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Scoping;
using Gma.Framework.Tasks;
using SweepApplicationErrors =
    BunkFy.Modules.Workspaces.Application
        .WorkspaceStaffIdentityAnchorSweepErrors;

internal sealed class WorkspaceStaffIdentityAnchorSweepTaskHandler(
    IWorkspaceStaffIdentityAnchorSweepTransactionDispatcher
        transactionDispatcher,
    IStaffWorkspaceOnboardingIdentityAnchorOutcomeReader outcomes,
    IStaffWorkspaceOnboardingIdentityAnchorResolutionRecorder recorder,
    IIdGenerator ids,
    IScopeContext scopeContext)
    : ITaskHandler<ReconcileWorkspaceStaffIdentityAnchorsPayload>
{
    private const int MaximumCandidateTransitions = 4;
    private readonly IWorkspaceStaffIdentityAnchorSweepTransactionDispatcher
        transactionDispatcher = transactionDispatcher;
    private readonly IStaffWorkspaceOnboardingIdentityAnchorOutcomeReader
        outcomes = outcomes;
    private readonly IStaffWorkspaceOnboardingIdentityAnchorResolutionRecorder
        recorder = recorder;
    private readonly IIdGenerator ids = ids;
    private readonly IScopeContext scopeContext = scopeContext;

    public async Task HandleAsync(
        ReconcileWorkspaceStaffIdentityAnchorsPayload payload,
        TaskExecutionContext context,
        CancellationToken cancellationToken)
    {
        this.Validate(payload, context);
        for (int batch = 0; batch < payload.MaxBatches; batch++)
        {
            Result<WorkspaceStaffIdentityAnchorSweepPage> prepared =
                await this.transactionDispatcher.PreparePageAsync(
                        context,
                        new(
                            this.NewId(),
                            this.NewId(),
                            this.NewId(),
                            context.RunId,
                            payload.BatchSize),
                        cancellationToken)
                    .ConfigureAwait(false);
            if (prepared.IsFailure)
            {
                throw Failure(prepared.Error);
            }

            WorkspaceStaffIdentityAnchorSweepPage page = prepared.Value;
            ValidatePage(page, payload.BatchSize);
            if (!page.AdvanceRequired)
            {
                return;
            }

            IReadOnlyDictionary<Guid,
                StaffWorkspaceOnboardingIdentityAnchorOutcome> staffOutcomes =
                await this.ReadOutcomesAsync(
                        page.Candidates,
                        cancellationToken)
                    .ConfigureAwait(false);
            WorkspaceStaffIdentityAnchorSweepPageCounts counts =
                await this.ProcessPageAsync(
                        page.Candidates,
                        staffOutcomes,
                        context,
                        cancellationToken)
                    .ConfigureAwait(false);

            Result<Unit> advanced = await this.transactionDispatcher
                .AdvanceAsync(
                        context,
                        new(
                            new WorkspaceStaffIdentityAnchorSweepAdvance(
                                page.CheckpointId,
                                page.CheckpointVersion,
                                page.CycleId,
                                page.ExpectedAfterOrdinal,
                                page.NextAfterOrdinal ??
                                    throw InvalidTask(),
                                page.ReachedEnd,
                                this.NewId(),
                                context.RunId,
                                counts)),
                        cancellationToken)
                .ConfigureAwait(false);
            if (advanced.IsFailure)
            {
                throw Failure(advanced.Error);
            }

            if (page.ReachedEnd)
            {
                return;
            }
        }
    }

    private async Task<IReadOnlyDictionary<Guid,
        StaffWorkspaceOnboardingIdentityAnchorOutcome>> ReadOutcomesAsync(
            IReadOnlyList<WorkspaceStaffIdentityAnchorSweepCandidate>
                candidates,
            CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return new Dictionary<Guid,
                StaffWorkspaceOnboardingIdentityAnchorOutcome>();
        }

        StaffWorkspaceOnboardingIdentityAnchorOutcomeRequest[] requests =
            candidates.Select(candidate =>
                    new StaffWorkspaceOnboardingIdentityAnchorOutcomeRequest(
                        candidate.ApplicationId,
                        candidate.SubjectId))
                .ToArray();
        IReadOnlyList<StaffWorkspaceOnboardingIdentityAnchorOutcome> read =
            await this.outcomes.ReadAsync(requests, cancellationToken)
                .ConfigureAwait(false);
        if (read.Count != requests.Length ||
            read.Count >
                StaffWorkspaceOnboardingIdentityAnchorLifecycleLimits
                    .MaximumBatchSize ||
            read.Any(outcome => !IsDefinedOutcome(outcome)) ||
            read.Select(outcome => outcome.ApplicationId).Distinct().Count() !=
                read.Count)
        {
            throw Failure(
                SweepApplicationErrors.StaffBatchInvalid);
        }

        Dictionary<Guid, StaffWorkspaceOnboardingIdentityAnchorOutcome> byId =
            read.ToDictionary(outcome => outcome.ApplicationId);
        if (requests.Any(request => !byId.ContainsKey(request.ApplicationId)))
        {
            throw Failure(
                SweepApplicationErrors.StaffBatchInvalid);
        }

        return byId;
    }

    private async Task<WorkspaceStaffIdentityAnchorSweepPageCounts>
        ProcessPageAsync(
            IReadOnlyList<WorkspaceStaffIdentityAnchorSweepCandidate>
                candidates,
            IReadOnlyDictionary<Guid,
                StaffWorkspaceOnboardingIdentityAnchorOutcome> staffOutcomes,
            TaskExecutionContext context,
            CancellationToken cancellationToken)
    {
        MutableCounts counts = new(candidates.Count);
        foreach (WorkspaceStaffIdentityAnchorSweepCandidate candidate in
            candidates)
        {
            StaffWorkspaceOnboardingIdentityAnchorOutcome staffOutcome =
                staffOutcomes[candidate.ApplicationId];
            if (staffOutcome.Status ==
                    StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Absent &&
                !candidate.HasLocalAnchorState)
            {
                counts.NoAnchorCount++;
                continue;
            }

            if (staffOutcome.Status is
                StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Unknown or
                StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Corrupt)
            {
                counts.ConflictCount++;
                continue;
            }

            CandidateFinalOutcome final = await this.ProcessCandidateAsync(
                    candidate,
                    staffOutcome.Status ==
                        StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus
                            .Absent &&
                        candidate.HasLocalAnchorState,
                    counts,
                    context,
                    cancellationToken)
                .ConfigureAwait(false);
            counts.Record(final);
        }

        return counts.ToImmutable();
    }

    private async Task<CandidateFinalOutcome> ProcessCandidateAsync(
        WorkspaceStaffIdentityAnchorSweepCandidate candidate,
        bool absentLocalContradiction,
        MutableCounts counts,
        TaskExecutionContext context,
        CancellationToken cancellationToken)
    {
        bool passOneCounted = false;
        bool resolutionRecordCounted = false;
        for (int transition = 0;
            transition < MaximumCandidateTransitions;
            transition++)
        {
            Result<WorkspaceStaffIdentityAnchorSweepCandidateResult>
                reconciled = await this.transactionDispatcher
                    .ReconcileCandidateAsync(
                        context,
                        new(candidate.ApplicationId),
                        cancellationToken)
                    .ConfigureAwait(false);
            if (reconciled.IsFailure)
            {
                return IsDeferred(reconciled.Error)
                    ? CandidateFinalOutcome.Deferred
                    : CandidateFinalOutcome.Conflict;
            }

            WorkspaceStaffIdentityAnchorSweepCandidateResult result =
                reconciled.Value;
            if (result.ApplicationId != candidate.ApplicationId)
            {
                return CandidateFinalOutcome.Conflict;
            }

            switch (result.Outcome)
            {
                case WorkspaceStaffIdentityAnchorSweepCandidateOutcome
                    .NoAnchor:
                    return absentLocalContradiction
                        ? CandidateFinalOutcome.Conflict
                        : CandidateFinalOutcome.NoAnchor;
                case WorkspaceStaffIdentityAnchorSweepCandidateOutcome.Removed:
                    return CandidateFinalOutcome.Removed;
                case WorkspaceStaffIdentityAnchorSweepCandidateOutcome
                    .ObservedNow:
                    return CandidateFinalOutcome.Observed;
                case WorkspaceStaffIdentityAnchorSweepCandidateOutcome
                    .AlreadyObserved:
                    return CandidateFinalOutcome.AlreadyObserved;
                case WorkspaceStaffIdentityAnchorSweepCandidateOutcome
                    .PassOneCommitted:
                    if (!passOneCounted)
                    {
                        counts.PassOneCommittedCount++;
                        passOneCounted = true;
                    }

                    continue;
                case WorkspaceStaffIdentityAnchorSweepCandidateOutcome
                    .ResolutionReadyToRecord:
                    if (!IsValidRequest(
                            candidate.ApplicationId,
                            result.ResolutionRequest))
                    {
                        return CandidateFinalOutcome.Conflict;
                    }

                    StaffWorkspaceOnboardingIdentityAnchorResolutionResult
                        recorded = await this.recorder.RecordAsync(
                                result.ResolutionRequest!,
                                cancellationToken)
                            .ConfigureAwait(false);
                    if (recorded.Status is not (
                        StaffWorkspaceOnboardingIdentityAnchorResolutionStatus
                            .Recorded or
                        StaffWorkspaceOnboardingIdentityAnchorResolutionStatus
                            .AlreadyRecorded))
                    {
                        return CandidateFinalOutcome.Conflict;
                    }

                    if (!resolutionRecordCounted)
                    {
                        counts.ResolutionRecordConfirmedCount++;
                        resolutionRecordCounted = true;
                    }

                    continue;
                case WorkspaceStaffIdentityAnchorSweepCandidateOutcome.Unknown:
                    return CandidateFinalOutcome.Conflict;
                default:
                    return CandidateFinalOutcome.Conflict;
            }
        }

        return CandidateFinalOutcome.Deferred;
    }

    private static bool IsValidRequest(
        Guid expectedApplicationId,
        StaffWorkspaceOnboardingIdentityAnchorResolutionRequest? request) =>
        request is not null &&
        request.ApplicationId == expectedApplicationId &&
        request.ApplicationId != Guid.Empty &&
        request.StaffMemberId != Guid.Empty &&
        request.ResolutionEventId != Guid.Empty &&
        request.ResolutionEventId != request.ApplicationId &&
        request.WorkspaceApplicationVersion > 0 &&
        request.ResolvedAtUtc != default &&
        request.Disposition !=
            StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition
                .Unknown;

    private static bool IsDefinedOutcome(
        StaffWorkspaceOnboardingIdentityAnchorOutcome outcome) =>
        outcome.ApplicationId != Guid.Empty &&
        Enum.IsDefined(outcome.Status) &&
        Enum.IsDefined(outcome.TargetLifecycle) &&
        Enum.IsDefined(outcome.SubjectMatch) &&
        (!outcome.ResolutionDisposition.HasValue ||
            Enum.IsDefined(outcome.ResolutionDisposition.Value)) &&
        outcome.Status switch
        {
            StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Absent =>
                !outcome.StaffMemberId.HasValue &&
                outcome.TargetLifecycle ==
                    StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle
                        .Unknown &&
                outcome.SubjectMatch ==
                    StaffWorkspaceOnboardingIdentityAnchorSubjectMatch
                        .Unknown &&
                !outcome.WorkspaceApplicationVersion.HasValue &&
                !outcome.ResolutionDisposition.HasValue &&
                !outcome.ResolutionEventId.HasValue,
            StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Unresolved =>
                IsConcreteAnchor(outcome) &&
                !outcome.WorkspaceApplicationVersion.HasValue &&
                !outcome.ResolutionDisposition.HasValue,
            StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Resolved =>
                IsConcreteAnchor(outcome) &&
                outcome.WorkspaceApplicationVersion is > 0 &&
                outcome.ResolutionDisposition is not null and not
                    StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition
                        .Unknown,
            StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Unknown or
            StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Corrupt =>
                true,
            _ => false
        };

    private static bool IsConcreteAnchor(
        StaffWorkspaceOnboardingIdentityAnchorOutcome outcome) =>
        outcome.StaffMemberId is { } staffMemberId &&
        staffMemberId != Guid.Empty &&
        outcome.ResolutionEventId is { } resolutionEventId &&
        resolutionEventId != Guid.Empty &&
        outcome.TargetLifecycle !=
            StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Unknown &&
        outcome.SubjectMatch !=
            StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Unknown;

    private static bool IsDeferred(Error error) =>
        string.Equals(
            error.Code,
            WorkspaceStaffOnboardingApplicationErrors
                .IdentityAnchorLifecycleTransitionPending.Code,
            StringComparison.Ordinal) ||
        string.Equals(
            error.Code,
            WorkspaceStaffOnboardingApplicationErrors
                .ProcessingRestricted.Code,
            StringComparison.Ordinal) ||
        string.Equals(
            error.Code,
            WorkspaceStaffOnboardingApplicationErrors
                .RestrictionProjectionUnavailable.Code,
            StringComparison.Ordinal) ||
        string.Equals(
            error.Code,
            WorkspaceOperationalAdmissionErrors.ProcessingRestricted.Code,
            StringComparison.Ordinal) ||
        string.Equals(
            error.Code,
            WorkspaceOperationalAdmissionErrors.AdmissionUnavailable.Code,
            StringComparison.Ordinal);

    private Guid NewId()
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            Guid id = this.ids.NewId();
            if (id != Guid.Empty)
            {
                return id;
            }
        }

        throw InvalidTask();
    }

    private void Validate(
        ReconcileWorkspaceStaffIdentityAnchorsPayload payload,
        TaskExecutionContext context)
    {
        if (payload.BatchSize is <= 0 or >
                ReconcileWorkspaceStaffIdentityAnchorsPayload
                    .MaximumBatchSize ||
            payload.BatchSize >
                StaffWorkspaceOnboardingIdentityAnchorLifecycleLimits
                    .MaximumBatchSize ||
            payload.MaxBatches is <= 0 or >
                ReconcileWorkspaceStaffIdentityAnchorsPayload
                    .MaximumBatches ||
            !WorkspaceStaffIdentityAnchorTenantScope.TryGetCanonicalTenantId(
                this.scopeContext,
                out string tenantId) ||
            !string.Equals(
                context.ScopeId,
                tenantId,
                StringComparison.Ordinal))
        {
            throw InvalidTask();
        }
    }

    private static void ValidatePage(
        WorkspaceStaffIdentityAnchorSweepPage page,
        int batchSize)
    {
        bool candidatesOrdered = AreStrictlyOrdered(
            page.Candidates.Select(candidate =>
                candidate.IdentityAnchorSweepOrdinal));
        bool emptyCompletedCycle =
            !page.AdvanceRequired &&
            page.ReachedEnd &&
            page.Candidates.Count == 0 &&
            !page.UpperOrdinal.HasValue &&
            !page.ExpectedAfterOrdinal.HasValue &&
            !page.NextAfterOrdinal.HasValue;
        bool advanceablePage =
            page.AdvanceRequired &&
            page.CheckpointId != Guid.Empty &&
            page.CheckpointVersion > 0 &&
            page.CycleId != Guid.Empty &&
            page.UpperOrdinal is > 0 &&
            page.NextAfterOrdinal is > 0 &&
            page.NextAfterOrdinal <= page.UpperOrdinal &&
            (!page.ExpectedAfterOrdinal.HasValue ||
                (page.ExpectedAfterOrdinal > 0 &&
                    page.ExpectedAfterOrdinal < page.NextAfterOrdinal)) &&
            page.Candidates.Count <= batchSize &&
            page.Candidates.All(candidate =>
                candidate.ApplicationId != Guid.Empty &&
                candidate.IdentityAnchorSweepOrdinal > 0 &&
                candidate.IdentityAnchorSweepOrdinal <= page.UpperOrdinal &&
                (!page.ExpectedAfterOrdinal.HasValue ||
                    candidate.IdentityAnchorSweepOrdinal >
                        page.ExpectedAfterOrdinal) &&
                !string.IsNullOrWhiteSpace(candidate.SubjectId)) &&
            candidatesOrdered &&
            (page.Candidates.Count == 0
                ? page.ReachedEnd &&
                    page.NextAfterOrdinal == page.UpperOrdinal
                : page.NextAfterOrdinal == page.Candidates[^1]
                    .IdentityAnchorSweepOrdinal) &&
            page.Candidates.Select(candidate => candidate.ApplicationId)
                .Distinct().Count() == page.Candidates.Count;
        if (!emptyCompletedCycle && !advanceablePage)
        {
            throw InvalidTask();
        }
    }

    private static bool AreStrictlyOrdered(IEnumerable<long> ordinals)
    {
        long? previous = null;
        foreach (long ordinal in ordinals)
        {
            if (previous.HasValue && ordinal <= previous.Value)
            {
                return false;
            }

            previous = ordinal;
        }

        return true;
    }

    private static InvalidOperationException Failure(Error error) =>
        new($"{error.Code}: {error.Message}");

    private static InvalidOperationException InvalidTask() =>
        Failure(SweepApplicationErrors.InvalidTask);

    private enum CandidateFinalOutcome
    {
        NoAnchor,
        Removed,
        Observed,
        AlreadyObserved,
        Deferred,
        Conflict
    }

    private sealed class MutableCounts(int scannedCount)
    {
        public long ScannedCount { get; } = scannedCount;
        public long NoAnchorCount { get; set; }
        public long RemovedCount { get; set; }
        public long ObservedCount { get; set; }
        public long AlreadyObservedCount { get; set; }
        public long DeferredCount { get; set; }
        public long ConflictCount { get; set; }
        public long PassOneCommittedCount { get; set; }
        public long ResolutionRecordConfirmedCount { get; set; }

        public void Record(CandidateFinalOutcome outcome)
        {
            switch (outcome)
            {
                case CandidateFinalOutcome.NoAnchor:
                    this.NoAnchorCount++;
                    break;
                case CandidateFinalOutcome.Removed:
                    this.RemovedCount++;
                    break;
                case CandidateFinalOutcome.Observed:
                    this.ObservedCount++;
                    break;
                case CandidateFinalOutcome.AlreadyObserved:
                    this.AlreadyObservedCount++;
                    break;
                case CandidateFinalOutcome.Deferred:
                    this.DeferredCount++;
                    break;
                case CandidateFinalOutcome.Conflict:
                    this.ConflictCount++;
                    break;
                default:
                    throw InvalidTask();
            }
        }

        public WorkspaceStaffIdentityAnchorSweepPageCounts ToImmutable() =>
            new(
                this.ScannedCount,
                this.NoAnchorCount,
                this.RemovedCount,
                this.ObservedCount,
                this.AlreadyObservedCount,
                this.DeferredCount,
                this.ConflictCount,
                this.PassOneCommittedCount,
                this.ResolutionRecordConfirmedCount);
    }
}

internal sealed class WorkspaceStaffIdentityAnchorSweepScheduleProvider(
    IWorkspaceStaffIdentityAnchorSweepRepository repository)
    : ITaskScheduleProvider
{
    public async IAsyncEnumerable<ScheduledTaskDefinition> GetSchedulesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
    {
        await foreach (string scopeId in repository
            .StreamScheduleScopeIdsAsync(cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            yield return new(
                "staff-identity-anchor-sweep",
                WorkspacesModuleMetadata.Name,
                ReconcileWorkspaceStaffIdentityAnchorsPayload.TaskName,
                JsonSerializer.Serialize(
                    new ReconcileWorkspaceStaffIdentityAnchorsPayload()),
                TimeSpan.FromMinutes(5),
                ReconcileWorkspaceStaffIdentityAnchorsPayload.WorkerGroup,
                scopeId,
                maxAttempts: 5,
                ReconcileWorkspaceStaffIdentityAnchorsPayload.PayloadVersion,
                runOnStart: true);
        }
    }
}
