namespace BunkFy.Modules.Workspaces.Application.Handlers;

using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Messaging;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;

[IntegrationEventHandler(
    WorkspacesModuleMetadata.StaffOnboardingIdentityAnchorContinuationHandlerName)]
internal sealed class WorkspaceStaffOnboardingIdentityAnchorContinuationHandler(
    IWorkspaceStaffOnboardingRepository applications,
    IWorkspaceStaffAccessPlanRepository plans,
    IWorkspaceStaffDeferredClaimWithdrawalRepository deferredWithdrawals,
    WorkspaceStaffOnboardingProcessor processor,
    ISystemClock clock)
    : IIntegrationEventHandler<
        WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedIntegrationEvent>
{
    public async Task HandleAsync(
        WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedIntegrationEvent
            integrationEvent,
        CancellationToken cancellationToken)
    {
        WorkspaceStaffOnboarding? application = await applications.GetAsync(
            integrationEvent.ApplicationId,
            cancellationToken).ConfigureAwait(false);
        if (application is null ||
            !string.Equals(
                application.ScopeId,
                integrationEvent.ScopeId,
                StringComparison.Ordinal) ||
            application.StaffMemberId != integrationEvent.StaffMemberId ||
            application.IdentityAnchorContinuationEventId !=
                integrationEvent.EventId)
        {
            throw new InvalidOperationException(
                "The identity-anchor continuation coordinates are unavailable.");
        }

        Result processed = await processor.ProcessIdentityAnchorContinuationAsync(
            application,
            integrationEvent.StaffMemberId,
            cancellationToken).ConfigureAwait(false);
        if (processed.IsFailure &&
            !WorkspaceStaffOnboardingProcessor.RequiresOwnerRecovery(
                application, processed))
        {
            throw new InvalidOperationException(
                $"Identity-anchor continuation failed with '{processed.Error.Code}'.");
        }

        if (application.Status == WorkspaceStaffOnboardingState.Completed &&
            application.SourceKind ==
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
    }
}
