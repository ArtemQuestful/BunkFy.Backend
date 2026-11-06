namespace BunkFy.Modules.Retention.Persistence;

using Gma.Framework.Domain;
using Gma.Framework.Naming;

public sealed class RetentionPropertyProjection : IScopedEntity
{
    private RetentionPropertyProjection() { }

    public RetentionPropertyProjection(
        string scopeId,
        Guid id,
        bool isActive,
        long topologySourceVersion)
    {
        this.ScopeId = TenantIds.Normalize(scopeId);
        if (id == Guid.Empty)
        {
            throw new ArgumentException(
                "A Retention property projection requires a property id.",
                nameof(id));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(topologySourceVersion);
        if (topologySourceVersion == 0 && isActive)
        {
            throw new ArgumentException(
                "An unknown Retention property projection cannot be active.",
                nameof(isActive));
        }

        this.Id = id;
        this.IsKnown = topologySourceVersion > 0;
        this.IsActive = isActive;
        this.TopologySourceVersion = topologySourceVersion;
    }

    public string ScopeId { get; private set; } = string.Empty;
    public Guid Id { get; private set; }
    public bool IsKnown { get; private set; }
    public bool IsActive { get; private set; }
    public bool IsProcessingEnabled { get; private set; }
    public int? RetentionPolicyVersion { get; private set; }
    public long TopologySourceVersion { get; private set; }
    public long PolicySourceVersion { get; private set; }
    public bool IsSchedulable =>
        this.IsKnown &&
        this.IsActive &&
        this.IsProcessingEnabled &&
        this.RetentionPolicyVersion > 0;

    public void ApplyTopology(bool isActive, long sourceVersion)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceVersion);
        if (sourceVersion < this.TopologySourceVersion)
        {
            return;
        }

        if (sourceVersion == this.TopologySourceVersion)
        {
            if (!this.IsKnown || this.IsActive != isActive)
            {
                throw new InvalidOperationException(
                    "Retention.PropertyTopologyProjectionConflict");
            }

            return;
        }

        this.IsKnown = true;
        this.IsActive = isActive;
        this.TopologySourceVersion = sourceVersion;
    }

    public void ApplyPolicy(
        bool isProcessingEnabled,
        int retentionPolicyVersion,
        long sourceVersion)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retentionPolicyVersion);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceVersion);
        if (sourceVersion < this.PolicySourceVersion)
        {
            return;
        }

        if (sourceVersion == this.PolicySourceVersion)
        {
            if (!this.IsKnown ||
                this.IsProcessingEnabled != isProcessingEnabled ||
                this.RetentionPolicyVersion != retentionPolicyVersion)
            {
                throw new InvalidOperationException(
                    "Retention.PropertyPolicyProjectionConflict");
            }

            return;
        }

        this.IsKnown = true;
        this.IsProcessingEnabled = isProcessingEnabled;
        this.RetentionPolicyVersion = retentionPolicyVersion;
        this.PolicySourceVersion = sourceVersion;
    }
}
