namespace BunkFy.Extensions.Workspaces;

using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Messaging;

[IntegrationEventHandler(HandlerName, RequiresExplicitProducerBinding = true)]
internal sealed class WorkspaceStaffOnboardingIdentityAnchorResolutionHandler(
    IStaffWorkspaceOnboardingIdentityAnchorResolutionRecorder recorder)
    : IIntegrationEventHandler<
        WorkspaceStaffOnboardingIdentityAnchorResolvedIntegrationEvent>
{
    public const string HandlerName =
        "bunkfy-workspace-staff-identity-anchor-resolution";

    public async Task HandleAsync(
        WorkspaceStaffOnboardingIdentityAnchorResolvedIntegrationEvent
            integrationEvent,
        CancellationToken cancellationToken)
    {
        StaffWorkspaceOnboardingIdentityAnchorResolutionResult recorded =
            await recorder.RecordAsync(
                new StaffWorkspaceOnboardingIdentityAnchorResolutionRequest(
                    integrationEvent.EventId,
                    integrationEvent.ApplicationId,
                    integrationEvent.StaffMemberId,
                    integrationEvent.WorkspaceApplicationVersion,
                    ToStaffDisposition(integrationEvent.Disposition),
                    integrationEvent.OccurredAtUtc),
                cancellationToken).ConfigureAwait(false);
        if (recorded.Status is not (
                StaffWorkspaceOnboardingIdentityAnchorResolutionStatus.Recorded or
                StaffWorkspaceOnboardingIdentityAnchorResolutionStatus
                    .AlreadyRecorded))
        {
            throw new InvalidOperationException(
                $"Staff identity-anchor resolution failed with '{recorded.Status}'.");
        }
    }

    private static
        StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition
        ToStaffDisposition(
            WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                disposition) => disposition switch
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
                    _ => throw new InvalidOperationException(
                        "The Workspaces identity-anchor resolution disposition is invalid.")
                };
}
