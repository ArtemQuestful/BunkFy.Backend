namespace BunkFy.Modules.Stations.Domain;

using Gma.Framework.Domain;

/// <summary>Property roster prerequisite, not a permission or a PIN credential.</summary>
public sealed class StationStaffRegistration : IScopedEntity
{
    private StationStaffRegistration() { }
    public StationStaffRegistration(string scopeId, Guid propertyId, Guid staffId, long reference)
    {
        StationRules.Coordinates(scopeId, propertyId, staffId);
        ArgumentOutOfRangeException.ThrowIfLessThan(reference, 1);
        this.ScopeId = scopeId;
        this.PropertyId = propertyId;
        this.StaffMemberId = staffId;
        this.RosterReference = reference;
    }
    public string ScopeId { get; private set; } = "";
    public Guid PropertyId { get; private set; }
    public Guid StaffMemberId { get; private set; }
    public long RosterReference { get; private set; }
    public long Version { get; private set; } = 1;
    public bool Active { get; private set; } = true;
    public void SetActive(bool active)
    {
        this.Active = active;
        this.Version = checked(this.Version + 1);
    }
}
