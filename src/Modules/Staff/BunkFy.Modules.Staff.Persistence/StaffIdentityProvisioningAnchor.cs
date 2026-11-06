namespace BunkFy.Modules.Staff.Persistence;

using BunkFy.Modules.Staff.Application.Ports;
using Gma.Framework.Domain;

internal sealed class StaffIdentityProvisioningAnchor : IScopedEntity
{
    private StaffIdentityProvisioningAnchor() { }

    internal StaffIdentityProvisioningAnchor(
        StaffIdentityProvisioningAnchorRecord record)
    {
        this.ScopeId = record.ScopeId;
        this.SourceKind = record.SourceKind;
        this.SourceId = record.SourceId;
        this.StaffMemberId = record.StaffMemberId;
        this.AnchoredAtUtc = record.AnchoredAtUtc;
        this.ResolutionEventId = record.ResolutionEventId;
    }

    public string ScopeId { get; private set; } = string.Empty;
    public StaffIdentityProvisioningSourceKind SourceKind { get; private set; }
    public Guid SourceId { get; private set; }
    public Guid StaffMemberId { get; private set; }
    public DateTimeOffset AnchoredAtUtc { get; private set; }
    public Guid? ResolutionEventId { get; private set; }

    internal StaffIdentityProvisioningAnchorRecord ToRecord() => new(
        this.ScopeId,
        this.SourceKind,
        this.SourceId,
        this.StaffMemberId,
        this.AnchoredAtUtc,
        this.ResolutionEventId);
}
