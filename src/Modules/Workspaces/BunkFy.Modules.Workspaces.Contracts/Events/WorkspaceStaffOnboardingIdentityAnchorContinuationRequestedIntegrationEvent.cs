namespace BunkFy.Modules.Workspaces.Contracts;

using Gma.Framework.Messaging;
using Gma.Framework.Naming;
using Gma.Framework.Scoping;

[IntegrationEventName(EventType)]
[IntegrationEventVersion(EventVersion)]
[ScopeAware]
public sealed record
    WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedIntegrationEvent
    : IntegrationEvent, IScopedIntegrationEvent
{
    public const string EventType =
        "workspace-staff-onboarding-identity-anchor-continuation-requested";
    public const int EventVersion = 1;

    public WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedIntegrationEvent(
        Guid eventId,
        string scopeId,
        DateTimeOffset occurredAtUtc,
        Guid applicationId,
        Guid staffMemberId)
        : base(eventId, occurredAtUtc, EventType, EventVersion)
    {
        this.ScopeId = ScopeIds.Normalize(scopeId, nameof(scopeId));
        this.ApplicationId = IntegrationEventContractGuards.RequireId(
            applicationId,
            nameof(applicationId));
        this.StaffMemberId = IntegrationEventContractGuards.RequireId(
            staffMemberId,
            nameof(staffMemberId));
        if (eventId == this.ApplicationId)
        {
            throw new ArgumentException(
                "The continuation event id must be distinct from its application coordinate.",
                nameof(eventId));
        }
    }

    public string ScopeId { get; }
    public Guid ApplicationId { get; }
    public Guid StaffMemberId { get; }
    string IScopedIntegrationEvent.ScopeId => this.ScopeId;

}
