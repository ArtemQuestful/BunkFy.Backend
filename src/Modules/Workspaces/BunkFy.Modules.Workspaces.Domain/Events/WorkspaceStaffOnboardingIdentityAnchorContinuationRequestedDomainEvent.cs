namespace BunkFy.Modules.Workspaces.Domain.Events;

using Gma.Framework.Domain;

public sealed record
    WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedDomainEvent(
        Guid EventId,
        DateTimeOffset OccurredAtUtc,
        string ScopeId,
        Guid ApplicationId,
        Guid StaffMemberId)
    : ScopedDomainEvent(EventId, OccurredAtUtc, ScopeId);
