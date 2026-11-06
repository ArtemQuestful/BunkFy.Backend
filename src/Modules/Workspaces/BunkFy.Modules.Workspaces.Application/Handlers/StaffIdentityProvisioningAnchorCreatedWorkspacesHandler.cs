namespace BunkFy.Modules.Workspaces.Application.Handlers;

using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Messaging;
using Gma.Framework.Results;

[IntegrationEventHandler(
    WorkspacesModuleMetadata.StaffOnboardingIdentityAnchorCreatedHandlerName)]
internal sealed class StaffIdentityProvisioningAnchorCreatedWorkspacesHandler(
    IWorkspaceStaffOnboardingRepository applications,
    WorkspaceStaffOnboardingProcessor processor)
    : IIntegrationEventHandler<
        StaffIdentityProvisioningAnchorCreatedIntegrationEvent>
{
    public async Task HandleAsync(
        StaffIdentityProvisioningAnchorCreatedIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        if (integrationEvent.EventId != integrationEvent.ApplicationId)
        {
            throw new InvalidOperationException(
                "The Staff identity-anchor event coordinate is invalid.");
        }
        if (integrationEvent.ResolutionEventId == Guid.Empty ||
            integrationEvent.ResolutionEventId == integrationEvent.ApplicationId)
        {
            throw new InvalidOperationException(
                "The Staff identity-anchor resolution coordinate is invalid.");
        }

        WorkspaceStaffOnboarding? application = await applications.GetAsync(
            integrationEvent.ApplicationId,
            cancellationToken).ConfigureAwait(false);
        if (application is null ||
            !string.Equals(
                application.ScopeId,
                integrationEvent.ScopeId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The Staff identity anchor has no Workspaces onboarding application.");
        }

        Result processed = await processor.ProcessAnchorCreatedAsync(
            application,
            integrationEvent.StaffMemberId,
            integrationEvent.ResolutionEventId,
            cancellationToken).ConfigureAwait(false);
        if (processed.IsFailure &&
            !WorkspaceStaffOnboardingProcessor.RequiresOwnerRecovery(
                application, processed))
        {
            throw new InvalidOperationException(
                $"Staff identity-anchor convergence failed with '{processed.Error.Code}'.");
        }
    }
}
