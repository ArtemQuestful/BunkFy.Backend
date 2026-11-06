namespace BunkFy.Modules.Workspaces.Persistence;

using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Domain;
using Gma.Framework.Messaging;
using Gma.Framework.Naming;

public sealed class WorkspacePropertyProjection : IScopedEntity
{
    private WorkspacePropertyProjection() { }

    public WorkspacePropertyProjection(
        string scopeId,
        Guid id,
        string? name,
        PropertyStatus status,
        long version)
    {
        this.ScopeId = TenantIds.Normalize(scopeId);
        if (id == Guid.Empty)
        {
            throw new ArgumentException(
                "A Workspaces property projection requires a property id.",
                nameof(id));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        string? normalizedName = NormalizeOptionalName(name);
        ValidateIncoming(normalizedName, status);

        this.Id = id;
        this.Name = normalizedName;
        this.Status = status;
        this.Version = version;
    }

    public string ScopeId { get; private set; } = string.Empty;
    public Guid Id { get; private set; }
    public string? Name { get; private set; }
    public PropertyStatus Status { get; private set; }
    public long Version { get; private set; }

    public void Apply(string? name, PropertyStatus status, long version)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(version);
        string? normalizedName = NormalizeOptionalName(name);
        ValidateIncoming(normalizedName, status);
        string? nextName = normalizedName ?? this.Name;

        if (version < this.Version)
        {
            return;
        }

        if (version == this.Version)
        {
            if (this.Status != status ||
                !string.Equals(this.Name, nextName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Workspaces.PropertyProjectionConflict");
            }

            return;
        }

        this.Name = nextName;
        this.Status = status;
        this.Version = version;
    }

    private static string? NormalizeOptionalName(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : IntegrationEventContractGuards.NormalizeRequiredText(
                name,
                PropertiesContractLimits.PropertyNameMaxLength,
                nameof(name));

    private static void ValidateIncoming(string? name, PropertyStatus status)
    {
        if (status is not (PropertyStatus.Active or PropertyStatus.Retired) ||
            (status == PropertyStatus.Active && name is null))
        {
            throw new ArgumentException(
                "The projected Workspaces property lifecycle is inconsistent.",
                nameof(status));
        }
    }
}
