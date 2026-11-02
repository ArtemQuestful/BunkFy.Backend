namespace BunkFy.Modules.Inventory.Persistence;

using Gma.Framework.Domain.Models;
using BunkFy.Modules.Properties.Contracts;

public sealed class InventoryPropertyTopology : ScopedEntity<Guid>
{
    private InventoryPropertyTopology() { }

    private InventoryPropertyTopology(Guid propertyId, string scopeId)
        : base(propertyId, scopeId)
    {
    }

    public string Name { get; private set; } = string.Empty;
    public string Code { get; private set; } = string.Empty;
    public string TimeZoneId { get; private set; } = string.Empty;
    public PropertyStatus Status { get; private set; } = PropertyStatus.Unknown;
    public long SourceVersion { get; private set; }
    public long DetailsVersion { get; private set; }
    public bool IsKnown { get; private set; }
    public long AvailabilitySelectionVersion { get; private set; } = 1;
    public long ProjectionOrdinal { get; private set; }

    public static InventoryPropertyTopology Create(Guid propertyId, string scopeId) => new(propertyId, scopeId);

    public void Apply(
        string? name,
        string? code,
        string? timeZoneId,
        PropertyStatus status,
        long sourceVersion)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceVersion);

        if (sourceVersion > this.SourceVersion)
        {
            this.SourceVersion = sourceVersion;
            this.Status = status;
        }

        if (name is not null && code is not null && timeZoneId is not null && sourceVersion > this.DetailsVersion)
        {
            this.Name = name;
            this.Code = code;
            this.TimeZoneId = timeZoneId;
            this.DetailsVersion = sourceVersion;
            this.IsKnown = true;
        }
    }

    public void AdvanceAvailabilitySelection()
    {
        if (this.AvailabilitySelectionVersion == long.MaxValue)
        {
            throw new InvalidOperationException(
                "The Inventory availability-selection version is exhausted.");
        }

        this.AvailabilitySelectionVersion++;
    }
}
