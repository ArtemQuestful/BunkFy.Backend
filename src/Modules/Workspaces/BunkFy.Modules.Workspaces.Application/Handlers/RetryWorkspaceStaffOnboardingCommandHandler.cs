namespace BunkFy.Modules.Workspaces.Application.Handlers;

using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Application.Mapping;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;

internal sealed class RetryWorkspaceStaffOnboardingCommandHandler(
    IWorkspaceStaffOnboardingRepository applications,
    IWorkspaceStaffAccessPlanRepository plans,
    IWorkspaceStaffDeferredClaimWithdrawalRepository deferredWithdrawals,
    WorkspaceStaffOnboardingProcessor processor,
    ISystemClock clock)
    : ICommandHandler<RetryWorkspaceStaffOnboardingCommand, WorkspaceStaffOnboardingDto>
{
    public async Task<Result<WorkspaceStaffOnboardingDto>> HandleAsync(
        RetryWorkspaceStaffOnboardingCommand command,
        CancellationToken cancellationToken)
    {
        WorkspaceStaffOnboarding? application = await applications.GetAsync(
            command.ApplicationId,
            cancellationToken).ConfigureAwait(false);
        if (application is null)
        {
            return Result.Failure<WorkspaceStaffOnboardingDto>(
                WorkspaceStaffOnboardingApplicationErrors.ApplicationNotFound);
        }

        if (application.Status == WorkspaceStaffOnboardingState.Failed &&
            application.StaffMemberId.HasValue)
        {
            return Result.Failure<WorkspaceStaffOnboardingDto>(
                WorkspaceStaffOnboardingApplicationErrors
                    .RecoveryAttemptRequired);
        }

        Result processed = await processor.ProcessForSourceFinalizationAsync(
            application,
            cancellationToken).ConfigureAwait(false);
        if (processed.IsSuccess &&
            application.SourceKind == WorkspaceStaffOnboardingSource.EnrollmentLink)
        {
            await OrganizationEnrollmentClaimExpiredStaffOnboardingHandler
                .ExpirePlanWhenUnusedUnderSourceLockAsync(
                    applications,
                    plans,
                    deferredWithdrawals,
                    application.SourceId,
                    clock.UtcNow,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return processed.IsSuccess
            ? Result.Success(application.ToDto())
            : Result.Failure<WorkspaceStaffOnboardingDto>(processed.Error);
    }
}
