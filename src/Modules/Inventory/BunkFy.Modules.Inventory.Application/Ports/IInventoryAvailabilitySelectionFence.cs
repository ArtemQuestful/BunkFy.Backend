namespace BunkFy.Modules.Inventory.Application.Ports;

/// <summary>
/// Serializes property-scoped changes that can alter the membership selected by
/// a manual Inventory block target.
/// </summary>
public interface IInventoryAvailabilitySelectionFence
{
    /// <summary>
    /// Acquires a non-mutating, transaction-scoped lock for the property. The
    /// lock must cover the durable property selection-version row.
    /// </summary>
    Task AcquireAsync(
        Guid propertyId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Advances the durable property selection version exactly once for an
    /// actual selection-affecting change. The property lock must remain held.
    /// </summary>
    Task AdvanceAsync(
        Guid propertyId,
        CancellationToken cancellationToken);
}
