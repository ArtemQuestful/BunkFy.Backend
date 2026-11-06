namespace BunkFy.Modules.Stations.Domain;

using Gma.Framework.Domain;

/// <summary>Only reservation check-in; not an Access role or inferred Staff assignment.</summary>
public sealed class StationStaffCheckInGrant : IScopedEntity
{
    private StationStaffCheckInGrant() { }
    public StationStaffCheckInGrant(string scopeId, Guid propertyId, Guid staffId)
    {
        StationRules.Coordinates(scopeId, propertyId, staffId);
        this.ScopeId = scopeId;
        this.PropertyId = propertyId;
        this.StaffMemberId = staffId;
    }
    public string ScopeId { get; private set; } = "";
    public Guid PropertyId { get; private set; }
    public Guid StaffMemberId { get; private set; }
    public long Revision { get; private set; } = 1;
    public bool Revoked { get; private set; }
    public void ReGrant() { this.Revoked = false; this.Revision = checked(this.Revision + 1); }
    public void Revoke() { this.Revoked = true; this.Revision = checked(this.Revision + 1); }
}
