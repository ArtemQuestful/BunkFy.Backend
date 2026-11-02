namespace BunkFy.Modules.Inventory.Application.Ports;

public interface IInventoryBusinessDateProvider
{
    Task<DateOnly?> GetAsync(
        Guid propertyId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);
}
