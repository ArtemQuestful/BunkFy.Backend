namespace BunkFy.Modules.Staff.Persistence;

using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Contracts;
using Gma.Framework.Domain;

internal sealed class StaffIdentityProvisioningAnchorResolution
    : IScopedEntity
{
    private StaffIdentityProvisioningAnchorResolution() { }

    internal StaffIdentityProvisioningAnchorResolution(
        StaffIdentityProvisioningAnchorResolutionRecord record)
    {
        this.ScopeId = record.ScopeId;
        this.SourceKind = record.SourceKind;
        this.SourceId = record.SourceId;
        this.StaffMemberId = record.StaffMemberId;
        this.WorkspaceApplicationVersion =
            record.WorkspaceApplicationVersion;
        this.Disposition = record.Disposition;
        this.ResolutionEventId = record.ResolutionEventId;
        this.ResolvedAtUtc = record.ResolvedAtUtc;
    }

    public string ScopeId { get; private set; } = string.Empty;
    public StaffIdentityProvisioningSourceKind SourceKind { get; private set; }
    public Guid SourceId { get; private set; }
    public Guid StaffMemberId { get; private set; }
    public long WorkspaceApplicationVersion { get; private set; }
    public StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition Disposition
    {
        get;
        private set;
    }
    public Guid ResolutionEventId { get; private set; }
    public DateTimeOffset ResolvedAtUtc { get; private set; }

    internal StaffIdentityProvisioningAnchorResolutionRecord ToRecord() =>
        new(
            this.ScopeId,
            this.SourceKind,
            this.SourceId,
            this.StaffMemberId,
            this.WorkspaceApplicationVersion,
            this.Disposition,
            this.ResolutionEventId,
            this.ResolvedAtUtc);
}
