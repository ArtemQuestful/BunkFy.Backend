namespace BunkFy.Modules.Stations.Persistence;

using Gma.Framework.Domain;
/// <summary>Future termination owner closes this under the same mutation lock; no termination service is registered in P1.</summary>
internal sealed class StationsTenantLifecycleState : IScopedEntity
{
    private StationsTenantLifecycleState() { }
    public StationsTenantLifecycleState(string scopeId) => this.ScopeId = scopeId;
    public string ScopeId { get; private set; } = "";
    public bool Closed { get; private set; }
    // Workspace owners can turn the shared station/PIN experience off without
    // changing the deployment-wide station capability.
    public bool SharedStationPinEnabled { get; private set; } = true;
    public long Revision { get; private set; } = 1;

    public bool TrySetSharedStationPinEnabled(bool enabled, long expectedRevision)
    {
        if (expectedRevision != this.Revision || this.Closed)
        {
            return false;
        }

        if (this.SharedStationPinEnabled != enabled)
        {
            this.SharedStationPinEnabled = enabled;
            this.Revision++;
        }

        return true;
    }
}
