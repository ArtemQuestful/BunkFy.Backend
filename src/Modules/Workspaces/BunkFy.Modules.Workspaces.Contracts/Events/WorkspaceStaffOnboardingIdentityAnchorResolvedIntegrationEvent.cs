namespace BunkFy.Modules.Workspaces.Contracts;

using BunkFy.Modules.Staff.Contracts;
using Gma.Framework.Messaging;
using Gma.Framework.Naming;
using Gma.Framework.Scoping;

[IntegrationEventName(EventType)]
[IntegrationEventVersion(EventVersion)]
[ScopeAware]
public sealed record
    WorkspaceStaffOnboardingIdentityAnchorResolvedIntegrationEvent
    : IntegrationEvent, IScopedIntegrationEvent
{
    public const string EventType =
        "workspace-staff-onboarding-identity-anchor-resolved";
    public const int EventVersion = 1;

    public WorkspaceStaffOnboardingIdentityAnchorResolvedIntegrationEvent(
        Guid eventId,
        string scopeId,
        DateTimeOffset occurredAtUtc,
        Guid applicationId,
        Guid staffMemberId,
        long workspaceApplicationVersion,
        WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition disposition)
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
                "The resolution event id must be distinct from its application coordinate.",
                nameof(eventId));
        }

        this.WorkspaceApplicationVersion = workspaceApplicationVersion > 0
            ? workspaceApplicationVersion
            : throw new ArgumentOutOfRangeException(
                nameof(workspaceApplicationVersion));
        this.Disposition = Enum.IsDefined(disposition) &&
            disposition !=
                WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                    .Unknown
                ? disposition
                : throw new ArgumentOutOfRangeException(nameof(disposition));
    }

    public string ScopeId { get; }
    public Guid ApplicationId { get; }
    public Guid StaffMemberId { get; }
    public long WorkspaceApplicationVersion { get; }
    public WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition Disposition { get; }
    string IScopedIntegrationEvent.ScopeId => this.ScopeId;

}
