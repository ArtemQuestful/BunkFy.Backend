namespace BunkFy.Modules.Workspaces.Application.Handlers;

using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Application.Mapping;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Microsoft.Extensions.Logging;
using DomainRestorationDisposition =
    BunkFy.Modules.Workspaces.Domain.WorkspaceStaffAccessRestorationDisposition;

internal sealed class PrepareWorkspaceStaffAccessCommandHandler(
    IWorkspaceStaffAccessProcessRepository processes,
    IWorkspaceStaffOnboardingRestorationSuppressionReader suppressions,
    WorkspaceStaffAccessMutationCoordinator mutations,
    WorkspaceAccessProvisioner access,
    WorkspaceOperationalAdmissionEvaluator operationalAdmission,
    ISystemClock clock,
    ILogger<PrepareWorkspaceStaffAccessCommandHandler> logger)
    : ICommandHandler<PrepareWorkspaceStaffAccessCommand, WorkspaceStaffAccessPreparation>
{
    public async Task<Result<WorkspaceStaffAccessPreparation>> HandleAsync(
        PrepareWorkspaceStaffAccessCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command.Context);
        StaffLifecyclePolicyContext context = command.Context;
        if (context.StaffMemberId == Guid.Empty ||
            string.IsNullOrWhiteSpace(context.AuthSubjectId))
        {
            return Result.Failure<WorkspaceStaffAccessPreparation>(
                WorkspaceStaffAccessApplicationErrors.ProcessConflict);
        }

        await mutations.AcquireCoordinatesAsync(
                context.StaffMemberId,
                context.AuthSubjectId,
                cancellationToken).ConfigureAwait(false);

        WorkspaceStaffAccessTargetState targetState = ToTargetState(context.TargetStatus);
        WorkspaceStaffAccessProcess? replay = await processes.GetByStaffVersionAsync(
            context.StaffMemberId,
            context.TargetVersion,
            cancellationToken).ConfigureAwait(false);
        if (replay is not null)
        {
            return replay.Matches(context.AuthSubjectId, targetState, context.EffectiveOn)
                ? Result.Success(ToPreparation(replay))
                : Result.Failure<WorkspaceStaffAccessPreparation>(
                    WorkspaceStaffAccessApplicationErrors.ProcessConflict);
        }

        if (targetState == WorkspaceStaffAccessTargetState.Active)
        {
            Result admitted = WorkspaceOperationalAdmissionGuard.RequireAllowed(
                await operationalAdmission.EvaluateAsync(
                    context.ScopeId,
                    cancellationToken).ConfigureAwait(false));
            if (admitted.IsFailure)
            {
                return Result.Failure<WorkspaceStaffAccessPreparation>(
                    admitted.Error);
            }
        }

        DateTimeOffset nowUtc = clock.UtcNow;
        WorkspaceStaffAccessProcess? observedPriorCommit = null;
        WorkspaceStaffAccessProcess? open = await processes.GetOpenByStaffAsync(
            context.StaffMemberId,
            cancellationToken).ConfigureAwait(false);
        if (open is not null)
        {
            WorkspaceStaffAccessTargetState previousState = ToTargetState(context.PreviousStatus);
            bool provesPriorCommit = open.State == WorkspaceStaffAccessProcessState.AwaitingStaffCommit &&
                open.TargetState != WorkspaceStaffAccessTargetState.Active &&
                open.TargetState == previousState &&
                open.TargetStaffVersion == context.ExpectedVersion;
            if (!provesPriorCommit || open.ObserveStaffCommit(nowUtc).IsFailure)
            {
                return Result.Failure<WorkspaceStaffAccessPreparation>(
                    WorkspaceStaffAccessApplicationErrors.ProcessConflict);
            }

            observedPriorCommit = open;
        }

        IReadOnlyCollection<WorkspaceStaffAccessProfileTarget> profileTargets;
        DomainRestorationDisposition restorationDisposition;
        if (targetState == WorkspaceStaffAccessTargetState.Active)
        {
            WorkspaceStaffOnboardingRestorationSuppressionState suppression =
                await suppressions.ReadAsync(
                    context.ScopeId,
                    context.StaffMemberId,
                    context.AuthSubjectId,
                    cancellationToken).ConfigureAwait(false);
            if (suppression ==
                WorkspaceStaffOnboardingRestorationSuppressionState.Conflict)
            {
                return Result.Failure<WorkspaceStaffAccessPreparation>(
                    WorkspaceStaffAccessApplicationErrors.ProcessConflict);
            }

            if (suppression ==
                WorkspaceStaffOnboardingRestorationSuppressionState.Suppressed)
            {
                restorationDisposition =
                    DomainRestorationDisposition.Suppressed;
                profileTargets = [];
                logger.LogInformation(
                    "Automatic workspace access restoration was suppressed by a terminal Staff onboarding resolution.");
            }
            else if (suppression ==
                WorkspaceStaffOnboardingRestorationSuppressionState.None)
            {
                WorkspaceStaffAccessProcess? suspension = observedPriorCommit ??
                    await processes.GetLatestCompletedSuspensionAsync(
                            context.StaffMemberId,
                            context.AuthSubjectId,
                            cancellationToken)
                        .ConfigureAwait(false);
                if (suspension is null)
                {
                    return Result.Failure<WorkspaceStaffAccessPreparation>(
                        WorkspaceStaffAccessApplicationErrors
                            .ResumeSnapshotUnavailable);
                }

                restorationDisposition =
                    DomainRestorationDisposition.RestoreSnapshot;
                profileTargets = suspension.ProfileSnapshots
                    .Select(snapshot => new WorkspaceStaffAccessProfileTarget(
                        snapshot.ProfileId,
                        snapshot.AssignmentScope))
                    .ToArray();
            }
            else
            {
                return Result.Failure<WorkspaceStaffAccessPreparation>(
                    WorkspaceStaffAccessApplicationErrors.ProcessConflict);
            }
        }
        else
        {
            restorationDisposition =
                DomainRestorationDisposition.NotApplicable;
            profileTargets = await access.CaptureRestorableProfilesAsync(
                context.ScopeId,
                context.AuthSubjectId,
                cancellationToken).ConfigureAwait(false);
        }

        Result<WorkspaceStaffAccessProcess> created = WorkspaceStaffAccessProcess.Create(
            context.TransitionId,
            context.ScopeId,
            context.StaffMemberId,
            context.AuthSubjectId,
            targetState,
            restorationDisposition,
            context.TargetVersion,
            context.EffectiveOn,
            context.ActorId,
            profileTargets,
            nowUtc);
        if (created.IsFailure)
        {
            return Result.Failure<WorkspaceStaffAccessPreparation>(created.Error);
        }

        if (targetState == WorkspaceStaffAccessTargetState.Active)
        {
            Result awaiting = created.Value.MarkAwaitingStaffCommit(nowUtc);
            if (awaiting.IsFailure)
            {
                return Result.Failure<WorkspaceStaffAccessPreparation>(awaiting.Error);
            }
        }

        await processes.AddAsync(created.Value, cancellationToken).ConfigureAwait(false);
        return Result.Success(ToPreparation(created.Value));
    }

    private static WorkspaceStaffAccessPreparation ToPreparation(WorkspaceStaffAccessProcess process) =>
        new(
            process.Id,
            process.State == WorkspaceStaffAccessProcessState.Prepared,
            WorkspaceStaffAccessMappings.MapRestorationDisposition(
                process.RestorationDisposition));

    private static WorkspaceStaffAccessTargetState ToTargetState(StaffStatus status) => status switch
    {
        StaffStatus.Active => WorkspaceStaffAccessTargetState.Active,
        StaffStatus.Suspended => WorkspaceStaffAccessTargetState.Suspended,
        StaffStatus.Departed => WorkspaceStaffAccessTargetState.Departed,
        _ => WorkspaceStaffAccessTargetState.Unknown
    };
}
