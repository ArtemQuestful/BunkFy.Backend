namespace BunkFy.Modules.Staff.Contracts;

using Gma.Framework.Messaging;
using Gma.Framework.Naming;
using Gma.Framework.Scoping;

[IntegrationEventName(EventType)]
[IntegrationEventVersion(EventVersion)]
[ScopeAware]
public sealed record StaffIdentityProvisioningAnchorCreatedIntegrationEvent
    : IntegrationEvent, IScopedIntegrationEvent
{
    public const string EventType =
        "workspace-onboarding-identity-anchor-created";
    public const int EventVersion = 1;

    public StaffIdentityProvisioningAnchorCreatedIntegrationEvent(
        Guid eventId,
        string scopeId,
        DateTimeOffset occurredAtUtc,
        Guid applicationId,
        Guid staffMemberId,
        Guid resolutionEventId)
        : base(eventId, occurredAtUtc, EventType, EventVersion)
    {
        this.ScopeId = ScopeIds.Normalize(scopeId, nameof(scopeId));
        this.ApplicationId = IntegrationEventContractGuards.RequireId(
            applicationId,
            nameof(applicationId));
        this.StaffMemberId = IntegrationEventContractGuards.RequireId(
            staffMemberId,
            nameof(staffMemberId));
        this.ResolutionEventId = IntegrationEventContractGuards.RequireId(
            resolutionEventId,
            nameof(resolutionEventId));
        if (eventId != this.ApplicationId)
        {
            throw new ArgumentException(
                "The anchor-created event id does not match its application coordinate.",
                nameof(eventId));
        }

        if (this.ResolutionEventId == this.ApplicationId)
        {
            throw new ArgumentException(
                "The resolution event id must be distinct from its application coordinate.",
                nameof(resolutionEventId));
        }
    }

    public string ScopeId { get; }
    public Guid ApplicationId { get; }
    public Guid StaffMemberId { get; }
    public Guid ResolutionEventId { get; }
    string IScopedIntegrationEvent.ScopeId => this.ScopeId;
}
