namespace BunkFy.Modules.Retention.Persistence;

using Gma.Framework.Domain;
using Gma.Framework.Naming;

public sealed class RetentionTenantProjection : IScopedEntity
{
    private RetentionTenantProjection() { }

    public RetentionTenantProjection(
        string scopeId,
        Guid organizationId,
        bool isActive,
        long sourceVersion)
    {
        this.ScopeId = TenantIds.Normalize(scopeId);
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException(
                "A Retention tenant projection requires an organization id.",
                nameof(organizationId));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceVersion);
        this.OrganizationId = organizationId;
        this.IsActive = isActive;
        this.SourceVersion = sourceVersion;
    }

    public string ScopeId { get; private set; } = string.Empty;
    public Guid OrganizationId { get; private set; }
    public bool IsActive { get; private set; }
    public long SourceVersion { get; private set; }

    public void Apply(Guid organizationId, bool isActive, long sourceVersion)
    {
        if (organizationId == Guid.Empty ||
            organizationId != this.OrganizationId)
        {
            throw new InvalidOperationException(
                "Retention.OrganizationCoordinateConflict");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceVersion);
        if (sourceVersion < this.SourceVersion)
        {
            return;
        }

        if (sourceVersion == this.SourceVersion)
        {
            if (this.IsActive != isActive)
            {
                throw new InvalidOperationException(
                    "Retention.OrganizationProjectionConflict");
            }

            return;
        }

        this.IsActive = isActive;
        this.SourceVersion = sourceVersion;
    }
}
