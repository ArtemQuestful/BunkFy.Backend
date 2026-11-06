namespace BunkFy.Modules.Workspaces.Application.Handlers;

using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;

internal enum WorkspaceStaffOnboardingIdentityAnchorConvergenceOutcome
{
    Absent = 1,
    ConvergedNow = 2,
    Active = 3,
    ResolutionPending = 4,
    ResolutionObserved = 5
}

internal readonly record struct
    WorkspaceStaffOnboardingIdentityAnchorConvergenceResult(
        WorkspaceStaffOnboardingIdentityAnchorConvergenceOutcome Outcome);

internal sealed class WorkspaceStaffOnboardingIdentityAnchorConvergence(
    IStaffWorkspaceOnboardingIdentityAnchorOutcomeReader outcomes,
    WorkspaceStaffAccessMutationCoordinator accessMutations,
    IWorkspaceStaffAccessProcessRepository accessProcesses,
    WorkspaceAccessProvisioner access,
    ISystemClock clock,
    IIdGenerator ids)
{
    public async Task<Result<
        WorkspaceStaffOnboardingIdentityAnchorConvergenceResult>>
        ConvergeAcquiredAsync(
            WorkspaceStaffOnboarding application,
            CancellationToken cancellationToken,
            Guid? expectedStaffMemberId = null,
            Guid? expectedResolutionEventId = null)
    {
        ArgumentNullException.ThrowIfNull(application);
        StaffWorkspaceOnboardingIdentityAnchorOutcome outcome =
            await outcomes.ReadAsync(
                new StaffWorkspaceOnboardingIdentityAnchorOutcomeRequest(
                    application.Id,
                    application.SubjectId),
                cancellationToken).ConfigureAwait(false);

        if (outcome.ApplicationId != application.Id)
        {
            return Conflict();
        }

        if (expectedStaffMemberId.HasValue &&
            (expectedStaffMemberId.Value == Guid.Empty ||
                outcome.Status ==
                    StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Absent ||
                outcome.StaffMemberId != expectedStaffMemberId))
        {
            return Conflict();
        }

        if (expectedResolutionEventId.HasValue &&
            (expectedResolutionEventId.Value == Guid.Empty ||
                outcome.ResolutionEventId != expectedResolutionEventId))
        {
            return Conflict();
        }

        return outcome.Status switch
        {
            StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Absent =>
                HandleAbsent(application, outcome),
            StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Unresolved =>
                await this.HandleUnresolvedAsync(
                    application,
                    outcome,
                    cancellationToken).ConfigureAwait(false),
            StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Resolved =>
                this.HandleResolved(application, outcome),
            _ => Conflict()
        };
    }

    private static Result<WorkspaceStaffOnboardingIdentityAnchorConvergenceResult>
        HandleAbsent(
            WorkspaceStaffOnboarding application,
            StaffWorkspaceOnboardingIdentityAnchorOutcome outcome)
    {
        if (application.StaffMemberId.HasValue ||
            application.IdentityAnchorResolutionEventId.HasValue ||
            outcome.StaffMemberId.HasValue ||
            outcome.TargetLifecycle !=
                StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Unknown ||
            outcome.SubjectMatch !=
                StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Unknown ||
            outcome.WorkspaceApplicationVersion.HasValue ||
            outcome.ResolutionDisposition.HasValue ||
            outcome.ResolutionEventId.HasValue)
        {
            return Conflict();
        }

        return Success(
            WorkspaceStaffOnboardingIdentityAnchorConvergenceOutcome.Absent);
    }

    private async Task<Result<
        WorkspaceStaffOnboardingIdentityAnchorConvergenceResult>>
        HandleUnresolvedAsync(
            WorkspaceStaffOnboarding application,
            StaffWorkspaceOnboardingIdentityAnchorOutcome outcome,
            CancellationToken cancellationToken)
    {
        if (!outcome.StaffMemberId.HasValue ||
            outcome.StaffMemberId.Value == Guid.Empty ||
            !outcome.ResolutionEventId.HasValue ||
            outcome.ResolutionEventId.Value == Guid.Empty ||
            outcome.ResolutionEventId.Value == application.Id ||
            outcome.WorkspaceApplicationVersion.HasValue ||
            outcome.ResolutionDisposition.HasValue ||
            outcome.TargetLifecycle is
                StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Unknown or
                StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Missing)
        {
            return Conflict();
        }

        Guid staffMemberId = outcome.StaffMemberId.Value;
        if (!HasSupportedSubjectRelationship(outcome))
        {
            return Conflict();
        }

        if (outcome.SubjectMatch ==
            StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Mismatch)
        {
            Result<StaffWorkspaceOnboardingIdentityAnchorOutcome>
                historicalFenced =
                await this.ReadUnderLifecycleFenceAsync(
                    application,
                    staffMemberId,
                    cancellationToken).ConfigureAwait(false);
            if (historicalFenced.IsFailure)
            {
                return Result.Failure<
                    WorkspaceStaffOnboardingIdentityAnchorConvergenceResult>(
                        historicalFenced.Error);
            }

            return await this.ApplyUnresolvedUnderFenceAsync(
                application,
                historicalFenced.Value,
                cancellationToken).ConfigureAwait(false);
        }

        if (outcome.TargetLifecycle ==
                StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Active &&
            !IsTerminal(application.Status))
        {
            return await this.ApplyUnresolvedUnderFenceAsync(
                application,
                outcome,
                cancellationToken).ConfigureAwait(false);
        }

        Result<StaffWorkspaceOnboardingIdentityAnchorOutcome> fenced =
            await this.ReadUnderLifecycleFenceAsync(
                application,
                staffMemberId,
                cancellationToken).ConfigureAwait(false);
        if (fenced.IsFailure)
        {
            return Result.Failure<
                WorkspaceStaffOnboardingIdentityAnchorConvergenceResult>(
                    fenced.Error);
        }

        return await this.ApplyUnresolvedUnderFenceAsync(
            application,
            fenced.Value,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<
        WorkspaceStaffOnboardingIdentityAnchorConvergenceResult>>
        FenceActiveGrantAcquiredAsync(
            WorkspaceStaffOnboarding application,
            Guid expectedStaffMemberId,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(application);
        if (expectedStaffMemberId == Guid.Empty ||
            application.StaffMemberId != expectedStaffMemberId ||
            application.IdentityAnchorResolutionEventId.HasValue)
        {
            return Conflict();
        }

        Result<StaffWorkspaceOnboardingIdentityAnchorOutcome> fenced =
            await this.ReadUnderLifecycleFenceAsync(
                application,
                expectedStaffMemberId,
                cancellationToken).ConfigureAwait(false);
        if (fenced.IsFailure)
        {
            return Result.Failure<
                WorkspaceStaffOnboardingIdentityAnchorConvergenceResult>(
                    fenced.Error);
        }

        if (fenced.Value.Status ==
            StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Unresolved)
        {
            return await this.ApplyUnresolvedUnderFenceAsync(
                application,
                fenced.Value,
                cancellationToken).ConfigureAwait(false);
        }

        return fenced.Value.Status ==
            StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Resolved
                ? this.HandleResolved(application, fenced.Value)
                : Conflict();
    }

    private async Task<Result<
        WorkspaceStaffOnboardingIdentityAnchorConvergenceResult>>
        ApplyUnresolvedUnderFenceAsync(
            WorkspaceStaffOnboarding application,
            StaffWorkspaceOnboardingIdentityAnchorOutcome outcome,
            CancellationToken cancellationToken)
    {
        if (!IsValidUnresolved(outcome) ||
            !HasSupportedSubjectRelationship(outcome) ||
            outcome.ResolutionEventId == application.Id)
        {
            return Conflict();
        }

        Guid staffMemberId = outcome.StaffMemberId!.Value;
        Guid resolutionEventId = outcome.ResolutionEventId!.Value;
        if (outcome.SubjectMatch ==
            StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Mismatch)
        {
            await access.DenyMemberAsync(
                    application.ScopeId,
                    application.SubjectId,
                    cancellationToken).ConfigureAwait(false);
            Result historicalTarget =
                application.ConvergeMismatchedStaffAnchor(
                    staffMemberId,
                    resolutionEventId,
                    clock.UtcNow);
            return historicalTarget.IsSuccess
                ? Success(
                    WorkspaceStaffOnboardingIdentityAnchorConvergenceOutcome
                        .ResolutionPending)
                : Conflict();
        }

        if (IsTerminal(application.Status))
        {
            bool completedActiveTarget =
                application.Status == WorkspaceStaffOnboardingState.Completed &&
                outcome.SubjectMatch ==
                    StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Exact &&
                outcome.TargetLifecycle ==
                    StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Active;
            if (!completedActiveTarget &&
                !application.IdentityAnchorResolutionEventId.HasValue)
            {
                await access.DenyMemberAsync(
                        application.ScopeId,
                        application.SubjectId,
                        cancellationToken).ConfigureAwait(false);
            }

            Result localTerminal = application.ConvergeTerminalStaffAnchor(
                staffMemberId,
                resolutionEventId,
                clock.UtcNow);
            return localTerminal.IsSuccess
                ? Success(
                    WorkspaceStaffOnboardingIdentityAnchorConvergenceOutcome
                        .ResolutionPending)
                : Conflict();
        }

        if (outcome.TargetLifecycle ==
            StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Active)
        {
            if (outcome.SubjectMatch !=
                    StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Exact ||
                application.IdentityAnchorResolutionEventId.HasValue)
            {
                return Conflict();
            }

            if (application.Status == WorkspaceStaffOnboardingState.Failed &&
                application.StaffMemberId.HasValue)
            {
                if (application.StaffMemberId.Value != staffMemberId ||
                    (application.IdentityAnchorExpectedResolutionEventId.HasValue &&
                        application.IdentityAnchorExpectedResolutionEventId.Value !=
                            resolutionEventId))
                {
                    return Conflict();
                }

                // A durable Staff anchor does not authorize retrying a failed
                // access mutation. Preserve the receipted recovery boundary.
                return Success(
                    WorkspaceStaffOnboardingIdentityAnchorConvergenceOutcome.Active);
            }

            long version = application.Version;
            Guid continuationEventId =
                application.IdentityAnchorContinuationEventId ??
                this.CreateContinuationEventId(
                    application.Id,
                    resolutionEventId);
            if (continuationEventId == Guid.Empty)
            {
                return Conflict();
            }

            Result converged = application.ConvergeCommittedStaffAnchor(
                staffMemberId,
                resolutionEventId,
                continuationEventId,
                clock.UtcNow);
            if (converged.IsFailure)
            {
                return Conflict();
            }

            if (application.Version != version)
            {
                return Success(
                    WorkspaceStaffOnboardingIdentityAnchorConvergenceOutcome
                        .ConvergedNow);
            }

            return Success(
                WorkspaceStaffOnboardingIdentityAnchorConvergenceOutcome.Active);
        }

        if (outcome.TargetLifecycle ==
            StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Suspended)
        {
            if (outcome.SubjectMatch !=
                    StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Exact ||
                application.IdentityAnchorResolutionEventId.HasValue)
            {
                return Conflict();
            }

            await access.DenyMemberAsync(
                    application.ScopeId,
                    application.SubjectId,
                    cancellationToken).ConfigureAwait(false);
            Result suspendedTerminal = application.ConvergeTerminalStaffAnchor(
                staffMemberId,
                resolutionEventId,
                clock.UtcNow);
            return suspendedTerminal.IsSuccess
                ? Success(
                    WorkspaceStaffOnboardingIdentityAnchorConvergenceOutcome
                        .ResolutionPending)
                : Conflict();
        }

        if (outcome.TargetLifecycle is not (
                StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Departed or
                StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Anonymised))
        {
            return Conflict();
        }

        await access.DenyMemberAsync(
                application.ScopeId,
                application.SubjectId,
                cancellationToken).ConfigureAwait(false);
        Result targetTerminal = application.ConvergeTerminalStaffAnchor(
            staffMemberId,
            resolutionEventId,
            clock.UtcNow);
        return targetTerminal.IsSuccess
            ? Success(
                WorkspaceStaffOnboardingIdentityAnchorConvergenceOutcome
                    .ResolutionPending)
            : Conflict();
    }

    private Result<WorkspaceStaffOnboardingIdentityAnchorConvergenceResult>
        HandleResolved(
            WorkspaceStaffOnboarding application,
            StaffWorkspaceOnboardingIdentityAnchorOutcome outcome)
    {
        if (!outcome.StaffMemberId.HasValue ||
            !outcome.ResolutionEventId.HasValue ||
            !outcome.WorkspaceApplicationVersion.HasValue ||
            !outcome.ResolutionDisposition.HasValue ||
            outcome.StaffMemberId.Value == Guid.Empty ||
            outcome.ResolutionEventId.Value == Guid.Empty ||
            application.IdentityAnchorExpectedResolutionEventId !=
                outcome.ResolutionEventId ||
            outcome.WorkspaceApplicationVersion.Value <= 0 ||
            !TryMapDisposition(
                outcome.ResolutionDisposition.Value,
                out WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                    disposition))
        {
            return Conflict();
        }

        bool wasObserved =
            application.IdentityAnchorResolutionObservedAtUtc.HasValue;
        Result observed = application.ObserveResolution(
            application.IdentityAnchorResolutionEventId ?? Guid.Empty,
            outcome.StaffMemberId.Value,
            outcome.WorkspaceApplicationVersion.Value,
            disposition,
            clock.UtcNow);
        if (observed.IsFailure)
        {
            return Conflict();
        }

        return Success(wasObserved
            ? WorkspaceStaffOnboardingIdentityAnchorConvergenceOutcome
                .ResolutionPending
            : WorkspaceStaffOnboardingIdentityAnchorConvergenceOutcome
                .ResolutionObserved);
    }

    private async Task<Result<
        StaffWorkspaceOnboardingIdentityAnchorOutcome>>
        ReadUnderLifecycleFenceAsync(
            WorkspaceStaffOnboarding application,
            Guid staffMemberId,
            CancellationToken cancellationToken)
    {
        await accessMutations.AcquireStaffAsync(
                staffMemberId,
                cancellationToken).ConfigureAwait(false);
        WorkspaceStaffAccessProcess? open =
            await accessProcesses.GetOpenByStaffAsync(
                staffMemberId,
                cancellationToken).ConfigureAwait(false);
        StaffWorkspaceOnboardingIdentityAnchorOutcome outcome =
            await outcomes.ReadAsync(
                new StaffWorkspaceOnboardingIdentityAnchorOutcomeRequest(
                    application.Id,
                    application.SubjectId),
                cancellationToken).ConfigureAwait(false);
        if (outcome.ApplicationId != application.Id ||
            outcome.Status ==
                StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Absent ||
            outcome.StaffMemberId != staffMemberId)
        {
            return Result.Failure<
                StaffWorkspaceOnboardingIdentityAnchorOutcome>(
                    WorkspaceStaffOnboardingApplicationErrors
                        .IdentityAnchorConflict);
        }

        if (open is null)
        {
            return Result.Success(outcome);
        }

        bool exactSubject = outcome.SubjectMatch ==
            StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Exact;
        if (open.StaffMemberId != staffMemberId ||
            !string.Equals(
                open.ScopeId,
                application.ScopeId,
                StringComparison.Ordinal) ||
            (exactSubject && !string.Equals(
                open.SubjectId,
                application.SubjectId,
                StringComparison.Ordinal)))
        {
            return Result.Failure<
                StaffWorkspaceOnboardingIdentityAnchorOutcome>(
                    WorkspaceStaffOnboardingApplicationErrors
                        .IdentityAnchorConflict);
        }

        if (open.TargetState is
            WorkspaceStaffAccessTargetState.Active or
            WorkspaceStaffAccessTargetState.Suspended or
            WorkspaceStaffAccessTargetState.Departed)
        {
            return Result.Failure<
                StaffWorkspaceOnboardingIdentityAnchorOutcome>(
                    WorkspaceStaffOnboardingApplicationErrors
                        .IdentityAnchorLifecycleTransitionPending);
        }

        return Result.Failure<StaffWorkspaceOnboardingIdentityAnchorOutcome>(
            WorkspaceStaffOnboardingApplicationErrors.IdentityAnchorConflict);
    }

    private static bool IsValidUnresolved(
        StaffWorkspaceOnboardingIdentityAnchorOutcome outcome) =>
        outcome.Status ==
            StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Unresolved &&
        outcome.StaffMemberId.HasValue &&
        outcome.StaffMemberId.Value != Guid.Empty &&
        outcome.ResolutionEventId.HasValue &&
        outcome.ResolutionEventId.Value != Guid.Empty &&
        !outcome.WorkspaceApplicationVersion.HasValue &&
        !outcome.ResolutionDisposition.HasValue &&
        outcome.TargetLifecycle is not (
            StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Unknown or
            StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Missing);

    private static bool TryMapDisposition(
        StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition source,
        out WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition target)
    {
        target = source switch
        {
            StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition
                .CompletedRedacted =>
                WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                    .CompletedRedacted,
            StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition
                .RejectedRedacted =>
                WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                    .RejectedRedacted,
            StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition
                .SupersededRedacted =>
                WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                    .SupersededRedacted,
            StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition
                .ExpiredRedacted =>
                WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                    .ExpiredRedacted,
            StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition
                .WithdrawnRedacted =>
                WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                    .WithdrawnRedacted,
            _ => WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                .Unknown
        };
        return target !=
            WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition.Unknown;
    }

    private static bool IsTerminal(WorkspaceStaffOnboardingState status) =>
        status is WorkspaceStaffOnboardingState.Completed or
            WorkspaceStaffOnboardingState.Rejected or
            WorkspaceStaffOnboardingState.Superseded or
            WorkspaceStaffOnboardingState.Expired or
            WorkspaceStaffOnboardingState.Withdrawn;

    private static bool HasSupportedSubjectRelationship(
        StaffWorkspaceOnboardingIdentityAnchorOutcome outcome) =>
        outcome.TargetLifecycle switch
        {
            StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Active or
            StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Suspended or
            StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Departed =>
                outcome.SubjectMatch is
                    StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Exact or
                    StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Mismatch,
            StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Anonymised =>
                outcome.SubjectMatch ==
                    StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Missing,
            _ => false
        };

    private Guid CreateContinuationEventId(
        Guid applicationId,
        Guid resolutionEventId)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            Guid candidate = ids.NewId();
            if (candidate != Guid.Empty &&
                candidate != applicationId &&
                candidate != resolutionEventId)
            {
                return candidate;
            }
        }

        return Guid.Empty;
    }

    private static Result<
        WorkspaceStaffOnboardingIdentityAnchorConvergenceResult> Success(
            WorkspaceStaffOnboardingIdentityAnchorConvergenceOutcome outcome) =>
        Result.Success(
            new WorkspaceStaffOnboardingIdentityAnchorConvergenceResult(outcome));

    private static Result<
        WorkspaceStaffOnboardingIdentityAnchorConvergenceResult> Conflict() =>
        Result.Failure<
            WorkspaceStaffOnboardingIdentityAnchorConvergenceResult>(
                WorkspaceStaffOnboardingApplicationErrors
                    .IdentityAnchorConflict);
}
