namespace BunkFy.Modules.Workspaces.Application.Handlers;

using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Messaging;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;
using Microsoft.Extensions.Logging;

[IntegrationEventHandler(
    WorkspacesModuleMetadata.StaffOnboardingRestrictionRecoveryHandlerName)]
internal sealed class
    WorkspaceStaffOnboardingProcessingRestrictionRecoveryHandler(
        IWorkspaceStaffOnboardingRepository applications,
        IWorkspaceStaffAccessPlanRepository plans,
        IWorkspaceStaffDeferredClaimWithdrawalRepository deferredWithdrawals,
        WorkspaceStaffOnboardingProcessor processor,
        ISystemClock clock,
        ILogger<
            WorkspaceStaffOnboardingProcessingRestrictionRecoveryHandler>
            logger)
    : IIntegrationEventHandler<
        WorkspaceStaffOnboardingProcessingRestrictionChangedIntegrationEvent>
{
    public async Task HandleAsync(
        WorkspaceStaffOnboardingProcessingRestrictionChangedIntegrationEvent
            integrationEvent,
        CancellationToken cancellationToken)
    {
        if (integrationEvent.IsRestricted ||
            integrationEvent.ContractVersion !=
                WorkspaceStaffOnboardingProcessingRestrictionContract
                    .CurrentVersion)
        {
            return;
        }

        WorkspaceStaffOnboarding? application = await applications.GetAsync(
            integrationEvent.ApplicationId,
            cancellationToken).ConfigureAwait(false);
        if (application is null ||
            application.Status != WorkspaceStaffOnboardingState.Provisioning ||
            application.StaffMemberId.HasValue)
        {
            return;
        }

        Result recovered = await processor.ProcessForSourceFinalizationAsync(
            application,
            cancellationToken).ConfigureAwait(false);
        if (recovered.IsSuccess)
        {
            if (application.SourceKind ==
                WorkspaceStaffOnboardingSource.EnrollmentLink)
            {
                await OrganizationEnrollmentClaimExpiredStaffOnboardingHandler
                    .ExpirePlanWhenUnusedUnderSourceLockAsync(
                        applications,
                        plans,
                        deferredWithdrawals,
                        application.SourceId,
                        clock.UtcNow,
                        cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        if (recovered.Error ==
            WorkspaceStaffOnboardingApplicationErrors.ProcessingRestricted)
        {
            logger.LogInformation(
                "Staff onboarding restriction release recovery stopped because a newer restriction is active.");
            return;
        }

        throw new InvalidOperationException(
            $"Staff onboarding restriction release recovery failed with '{recovered.Error.Code}'.");
    }
}
