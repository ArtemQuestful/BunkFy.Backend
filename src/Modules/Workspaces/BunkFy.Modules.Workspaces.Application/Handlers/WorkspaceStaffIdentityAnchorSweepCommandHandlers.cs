namespace BunkFy.Modules.Workspaces.Application.Handlers;

using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Application.Models;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Application.Queries;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;

internal sealed class PrepareWorkspaceStaffIdentityAnchorSweepPageCommandHandler(
    IWorkspaceStaffIdentityAnchorSweepRepository repository,
    IWorkspaceCrossGraphMutationLock crossGraphLock,
    IScopeContext scopeContext,
    ISystemClock clock)
    : ICommandHandler<
        PrepareWorkspaceStaffIdentityAnchorSweepPageCommand,
        WorkspaceStaffIdentityAnchorSweepPage>
{
    public async Task<Result<WorkspaceStaffIdentityAnchorSweepPage>> HandleAsync(
        PrepareWorkspaceStaffIdentityAnchorSweepPageCommand command,
        CancellationToken cancellationToken)
    {
        if (!WorkspaceStaffIdentityAnchorTenantScope.TryGetCanonicalTenantId(
                scopeContext,
                out string tenantId))
        {
            return Result.Failure<WorkspaceStaffIdentityAnchorSweepPage>(
                WorkspaceStaffOnboardingApplicationErrors.ScopeRequired);
        }

        // Identity values are allocated before transaction commit. Draining
        // tenant writers before the high-water read prevents a late commit
        // from appearing behind an already captured cycle upper bound.
        await crossGraphLock.AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        return await repository.PreparePageAsync(
                tenantId,
                command.CheckpointId,
                command.CycleId,
                command.EmptyAdvanceId,
                command.RunId,
                command.BatchSize,
                clock.UtcNow,
                cancellationToken)
            .ConfigureAwait(false);
    }

}

internal sealed class AdvanceWorkspaceStaffIdentityAnchorSweepCommandHandler(
    IWorkspaceStaffIdentityAnchorSweepRepository repository,
    IScopeContext scopeContext,
    ISystemClock clock)
    : ICommandHandler<AdvanceWorkspaceStaffIdentityAnchorSweepCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(
        AdvanceWorkspaceStaffIdentityAnchorSweepCommand command,
        CancellationToken cancellationToken)
    {
        if (!WorkspaceStaffIdentityAnchorTenantScope.TryGetCanonicalTenantId(
                scopeContext,
                out _))
        {
            return Result.Failure<Unit>(
                WorkspaceStaffOnboardingApplicationErrors.ScopeRequired);
        }

        Result advanced = await repository.AdvanceAsync(
                command.Advance,
                clock.UtcNow,
                cancellationToken)
            .ConfigureAwait(false);
        return advanced.IsSuccess
            ? Result.Success(Unit.Value)
            : Result.Failure<Unit>(advanced.Error);
    }
}

internal sealed class GetWorkspaceStaffIdentityAnchorSweepStatusQueryHandler(
    IWorkspaceStaffIdentityAnchorSweepRepository repository,
    IScopeContext scopeContext)
    : IQueryHandler<
        GetWorkspaceStaffIdentityAnchorSweepStatusQuery,
        WorkspaceStaffIdentityAnchorSweepStatus>
{
    public async Task<Result<WorkspaceStaffIdentityAnchorSweepStatus>>
        HandleAsync(
            GetWorkspaceStaffIdentityAnchorSweepStatusQuery query,
            CancellationToken cancellationToken)
    {
        if (!WorkspaceStaffIdentityAnchorTenantScope.TryGetCanonicalTenantId(
                scopeContext,
                out string tenantId))
        {
            return Result.Failure<WorkspaceStaffIdentityAnchorSweepStatus>(
                WorkspaceStaffOnboardingApplicationErrors.ScopeRequired);
        }

        WorkspaceStaffIdentityAnchorSweepStatus status =
            await repository.GetStatusAsync(
                    tenantId,
                    cancellationToken)
                .ConfigureAwait(false);
        return Result.Success(status);
    }
}

internal sealed class
    ReconcileWorkspaceStaffIdentityAnchorSweepCandidateCommandHandler(
        WorkspaceStaffOnboardingMutationCoordinator mutations,
        WorkspaceStaffOnboardingIdentityAnchorConvergence convergence,
        WorkspaceStaffOnboardingProcessor processor)
    : ICommandHandler<
        ReconcileWorkspaceStaffIdentityAnchorSweepCandidateCommand,
        WorkspaceStaffIdentityAnchorSweepCandidateResult>
{
    public async Task<Result<WorkspaceStaffIdentityAnchorSweepCandidateResult>>
        HandleAsync(
            ReconcileWorkspaceStaffIdentityAnchorSweepCandidateCommand command,
            CancellationToken cancellationToken)
    {
        if (command.ApplicationId == Guid.Empty)
        {
            return Failure();
        }

        WorkspaceStaffOnboardingMutationLease lease =
            await mutations.AcquireExistingAsync(
                    command.ApplicationId,
                    WorkspaceStaffOnboardingSourceLockMode.Write,
                    requireOperational: false,
                    cancellationToken)
                .ConfigureAwait(false);
        if (lease.Application is null)
        {
            return Result.Success(
                new WorkspaceStaffIdentityAnchorSweepCandidateResult(
                    command.ApplicationId,
                    WorkspaceStaffIdentityAnchorSweepCandidateOutcome
                        .Removed));
        }

        WorkspaceStaffOnboarding application = lease.Application;
        bool wasObserved =
            application.IdentityAnchorResolutionObservedAtUtc.HasValue;
        Result<WorkspaceStaffOnboardingIdentityAnchorConvergenceResult>
            converged = await convergence.ConvergeAcquiredAsync(
                    application,
                    cancellationToken)
                .ConfigureAwait(false);
        if (converged.IsFailure)
        {
            return Result.Failure<
                WorkspaceStaffIdentityAnchorSweepCandidateResult>(
                    converged.Error);
        }

        if (converged.Value.Outcome ==
            WorkspaceStaffOnboardingIdentityAnchorConvergenceOutcome.Active)
        {
            Result processed = await processor.ProcessAcquiredAsync(
                    application,
                    cancellationToken)
                .ConfigureAwait(false);
            if (processed.IsFailure)
            {
                return Result.Failure<
                    WorkspaceStaffIdentityAnchorSweepCandidateResult>(
                        processed.Error);
            }
        }

        return Describe(
            application,
            wasObserved,
            converged.Value.Outcome);
    }

    private static Result<WorkspaceStaffIdentityAnchorSweepCandidateResult>
        Describe(
            WorkspaceStaffOnboarding application,
            bool wasObserved,
            WorkspaceStaffOnboardingIdentityAnchorConvergenceOutcome outcome)
    {
        if (application.IdentityAnchorResolutionObservedAtUtc.HasValue)
        {
            return Success(
                application.Id,
                wasObserved
                    ? WorkspaceStaffIdentityAnchorSweepCandidateOutcome
                        .AlreadyObserved
                    : WorkspaceStaffIdentityAnchorSweepCandidateOutcome
                        .ObservedNow);
        }

        bool hasAnyResolutionCoordinate =
            application.IdentityAnchorResolutionEventId.HasValue ||
            application.IdentityAnchorResolutionStaffMemberId.HasValue ||
            application.IdentityAnchorResolutionApplicationVersion.HasValue ||
            application.IdentityAnchorResolutionDisposition.HasValue ||
            application.IdentityAnchorResolutionIntentAtUtc.HasValue;
        if (hasAnyResolutionCoordinate)
        {
            if (!TryCreateResolutionRequest(
                    application,
                    out StaffWorkspaceOnboardingIdentityAnchorResolutionRequest?
                        request))
            {
                return Failure();
            }

            return Result.Success(
                new WorkspaceStaffIdentityAnchorSweepCandidateResult(
                    application.Id,
                    WorkspaceStaffIdentityAnchorSweepCandidateOutcome
                        .ResolutionReadyToRecord,
                    request));
        }

        return outcome switch
        {
            WorkspaceStaffOnboardingIdentityAnchorConvergenceOutcome.Absent =>
                Success(
                    application.Id,
                    WorkspaceStaffIdentityAnchorSweepCandidateOutcome
                        .NoAnchor),
            WorkspaceStaffOnboardingIdentityAnchorConvergenceOutcome
                .ConvergedNow => Success(
                    application.Id,
                    WorkspaceStaffIdentityAnchorSweepCandidateOutcome
                        .PassOneCommitted),
            _ => Failure()
        };
    }

    private static bool TryCreateResolutionRequest(
        WorkspaceStaffOnboarding application,
        out StaffWorkspaceOnboardingIdentityAnchorResolutionRequest? request)
    {
        request = null;
        if (!application.IdentityAnchorResolutionEventId.HasValue ||
            !application.IdentityAnchorResolutionStaffMemberId.HasValue ||
            !application.IdentityAnchorResolutionApplicationVersion.HasValue ||
            !application.IdentityAnchorResolutionDisposition.HasValue ||
            !application.IdentityAnchorResolutionIntentAtUtc.HasValue ||
            application.IdentityAnchorResolutionEventId.Value == Guid.Empty ||
            application.IdentityAnchorResolutionStaffMemberId.Value ==
                Guid.Empty ||
            application.IdentityAnchorResolutionApplicationVersion.Value <=
                0 ||
            application.IdentityAnchorExpectedResolutionEventId !=
                application.IdentityAnchorResolutionEventId ||
            application.StaffMemberId !=
                application.IdentityAnchorResolutionStaffMemberId ||
            !TryMapDisposition(
                application.IdentityAnchorResolutionDisposition.Value,
                out StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition
                    disposition))
        {
            return false;
        }

        request = new(
            application.IdentityAnchorResolutionEventId.Value,
            application.Id,
            application.IdentityAnchorResolutionStaffMemberId.Value,
            application.IdentityAnchorResolutionApplicationVersion.Value,
            disposition,
            application.IdentityAnchorResolutionIntentAtUtc.Value);
        return true;
    }

    private static bool TryMapDisposition(
        WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition source,
        out StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition target)
    {
        target = source switch
        {
            WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                .CompletedRedacted =>
                StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition
                    .CompletedRedacted,
            WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                .RejectedRedacted =>
                StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition
                    .RejectedRedacted,
            WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                .SupersededRedacted =>
                StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition
                    .SupersededRedacted,
            WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                .ExpiredRedacted =>
                StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition
                    .ExpiredRedacted,
            WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                .WithdrawnRedacted =>
                StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition
                    .WithdrawnRedacted,
            _ => StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition
                .Unknown
        };
        return target !=
            StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition
                .Unknown;
    }

    private static Result<WorkspaceStaffIdentityAnchorSweepCandidateResult>
        Success(
            Guid applicationId,
            WorkspaceStaffIdentityAnchorSweepCandidateOutcome outcome) =>
        Result.Success(
            new WorkspaceStaffIdentityAnchorSweepCandidateResult(
                applicationId,
                outcome));

    private static Result<WorkspaceStaffIdentityAnchorSweepCandidateResult>
        Failure() =>
        Result.Failure<WorkspaceStaffIdentityAnchorSweepCandidateResult>(
            WorkspaceStaffOnboardingApplicationErrors.IdentityAnchorConflict);
}
