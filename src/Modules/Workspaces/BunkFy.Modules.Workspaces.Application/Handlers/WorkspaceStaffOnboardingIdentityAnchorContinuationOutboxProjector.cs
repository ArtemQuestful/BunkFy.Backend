namespace BunkFy.Modules.Workspaces.Application.Handlers;

using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain.Events;
using Gma.Framework.Application.Events;
using Gma.Framework.Messaging;

internal sealed class
    WorkspaceStaffOnboardingIdentityAnchorContinuationOutboxProjector(
        IOutboxWriterRegistry outboxWriters)
    : IDomainEventHandler<
        WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedDomainEvent>
{
    public Task HandleAsync(
        WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedDomainEvent
            domainEvent,
        CancellationToken cancellationToken) =>
        outboxWriters.GetRequired(WorkspacesModuleMetadata.Name).EnqueueAsync(
            new
                WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedIntegrationEvent(
                    domainEvent.EventId,
                    domainEvent.ScopeId,
                    domainEvent.OccurredAtUtc,
                    domainEvent.ApplicationId,
                    domainEvent.StaffMemberId),
            cancellationToken);
}
