namespace BunkFy.Modules.Inventory.Persistence.Repositories;

using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using Microsoft.EntityFrameworkCore;

internal sealed class RoomInventoryConfigurationRepository(InventoryDbContext dbContext)
    : IRoomInventoryConfigurationRepository
{
    public async Task<bool> EnsureAsync(
        string scopeId,
        Guid propertyId,
        Guid roomId,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        if (dbContext.RoomConfigurations.Local.Any(configuration => configuration.Id == roomId) ||
            await dbContext.RoomConfigurations.AnyAsync(configuration => configuration.Id == roomId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        RoomInventoryConfiguration configuration = RoomInventoryConfiguration
            .Create(roomId, scopeId, propertyId, createdAtUtc).Value;
        dbContext.RoomConfigurations.Add(configuration);
        return true;
    }

    public Task<RoomInventoryConfiguration?> GetAsync(
        Guid propertyId,
        Guid roomId,
        CancellationToken cancellationToken) =>
        dbContext.RoomConfigurations.FirstOrDefaultAsync(
            configuration => configuration.Id == roomId && configuration.PropertyId == propertyId,
            cancellationToken);
}
