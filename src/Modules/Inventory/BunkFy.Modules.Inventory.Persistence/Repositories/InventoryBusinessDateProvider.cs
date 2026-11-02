namespace BunkFy.Modules.Inventory.Persistence.Repositories;

using BunkFy.Modules.Inventory.Application.Ports;
using Microsoft.EntityFrameworkCore;

internal sealed class InventoryBusinessDateProvider(InventoryDbContext dbContext)
    : IInventoryBusinessDateProvider
{
    public async Task<DateOnly?> GetAsync(
        Guid propertyId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        string? timeZoneId = await dbContext.PropertyTopology
            .AsNoTracking()
            .Where(property =>
                property.Id == propertyId &&
                property.IsKnown)
            .Select(property => property.TimeZoneId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return null;
        }

        try
        {
            TimeZoneInfo timeZone = TimeZoneInfo.FindSystemTimeZoneById(
                timeZoneId);
            DateTimeOffset local = TimeZoneInfo.ConvertTime(nowUtc, timeZone);
            return DateOnly.FromDateTime(local.DateTime);
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }
}
