namespace BunkFy.Modules.Workspaces.Domain.Events;

using Gma.Framework.Domain;

public sealed record WorkspaceStaffOnboardingIdentityAnchorResolvedDomainEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    string ScopeId,
    Guid ApplicationId,
    Guid StaffMemberId,
    long WorkspaceApplicationVersion,
    WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition Disposition)
    : ScopedDomainEvent(EventId, OccurredAtUtc, ScopeId);
