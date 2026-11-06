namespace BunkFy.Modules.Workspaces.Application.Handlers;

using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Domain.Events;
using Gma.Framework.Application.Events;
using Gma.Framework.Messaging;
using Contracts = BunkFy.Modules.Workspaces.Contracts;
using Domain = BunkFy.Modules.Workspaces.Domain;

internal sealed class
    WorkspaceStaffOnboardingIdentityAnchorResolutionOutboxProjector(
        IOutboxWriterRegistry outboxWriters)
    : IDomainEventHandler<
        WorkspaceStaffOnboardingIdentityAnchorResolvedDomainEvent>
{
    public Task HandleAsync(
        WorkspaceStaffOnboardingIdentityAnchorResolvedDomainEvent domainEvent,
        CancellationToken cancellationToken) =>
        outboxWriters.GetRequired(WorkspacesModuleMetadata.Name).EnqueueAsync(
            new WorkspaceStaffOnboardingIdentityAnchorResolvedIntegrationEvent(
                domainEvent.EventId,
                domainEvent.ScopeId,
                domainEvent.OccurredAtUtc,
                domainEvent.ApplicationId,
                domainEvent.StaffMemberId,
                domainEvent.WorkspaceApplicationVersion,
                ToContractDisposition(domainEvent.Disposition)),
            cancellationToken);

    private static Contracts.WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
        ToContractDisposition(
            Domain.WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                disposition) => disposition switch
                {
                    Domain.WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                        .CompletedRedacted =>
                        Contracts.WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                            .CompletedRedacted,
                    Domain.WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                        .RejectedRedacted =>
                        Contracts.WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                            .RejectedRedacted,
                    Domain.WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                        .SupersededRedacted =>
                        Contracts.WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                            .SupersededRedacted,
                    Domain.WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                        .ExpiredRedacted =>
                        Contracts.WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                            .ExpiredRedacted,
                    Domain.WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                        .WithdrawnRedacted =>
                        Contracts.WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                            .WithdrawnRedacted,
                    _ => throw new InvalidOperationException(
                        "The Workspaces identity-anchor resolution disposition is invalid.")
                };
}
