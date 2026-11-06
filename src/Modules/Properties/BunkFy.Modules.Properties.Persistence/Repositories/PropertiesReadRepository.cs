namespace BunkFy.Modules.Properties.Persistence.Repositories;

using BunkFy.Modules.Properties.Application.Mapping;
using BunkFy.Modules.Properties.Application.Ports;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Aggregates;
using Microsoft.EntityFrameworkCore;
using Gma.Framework.Pagination;

internal sealed class PropertiesReadRepository(PropertiesDbContext dbContext)
    : IPropertiesReadRepository
{
    public Task<Property?> GetPropertyAsync(
        Guid propertyId,
        CancellationToken cancellationToken) =>
        dbContext.Properties
            .AsNoTracking()
            .FirstOrDefaultAsync(property => property.Id == propertyId, cancellationToken);

    public async Task<PropertyReadPage> ListPropertiesAsync(PageRequest pageRequest, CancellationToken cancellationToken)
        => await this.ListVisiblePropertiesAsync(
            pageRequest,
            PropertiesVisibilityScope.All,
            cancellationToken).ConfigureAwait(false);

    public async Task<PropertyReadPage> ListVisiblePropertiesAsync(
        PageRequest pageRequest,
        PropertiesVisibilityScope visibility,
        CancellationToken cancellationToken)
    {
        IQueryable<Property> query = dbContext.Properties.AsNoTracking();
        if (!visibility.IncludesAllProperties)
        {
            query = query.Where(property => visibility.PropertyIds.Contains(property.Id));
        }

        var fetched = await query
            .OrderBy(property => property.Code)
            .ThenBy(property => property.Id)
            .Skip(pageRequest.SkipCount)
            .Take(pageRequest.PageSize + 1)
            .Select(property => new
            {
                property.Id,
                property.Name,
                property.Code,
                property.TimeZoneId,
                property.Status,
                property.ProcessingState,
                property.Version
            })
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        bool hasMore = fetched.Length > pageRequest.PageSize;
        PropertyListReadModel[] properties = fetched
            .Take(pageRequest.PageSize)
            .Select(property => new PropertyListReadModel(
                property.Id,
                property.Name.Value,
                property.Code.Value,
                property.TimeZoneId.Value,
                property.Status,
                property.ProcessingState,
                property.Version))
            .ToArray();

        return new PropertyReadPage(
            Array.AsReadOnly(properties),
            pageRequest.Page,
            pageRequest.PageSize,
            hasMore);
    }

    public async Task<RoomDto?> GetRoomAsync(Guid propertyId, Guid roomId, CancellationToken cancellationToken)
    {
        Room? room = await dbContext.Rooms
            .AsNoTracking()
            .FirstOrDefaultAsync(room => room.PropertyId == propertyId && room.Id == roomId, cancellationToken)
            .ConfigureAwait(false);

        return room is null ? null : PropertiesMapper.ToDto(room);
    }

    public async Task<RoomListResponse> ListRoomsAsync(Guid propertyId, PageRequest pageRequest, CancellationToken cancellationToken)
    {
        var fetched = await dbContext.Rooms
            .AsNoTracking()
            .Where(room => room.PropertyId == propertyId)
            .OrderBy(room => room.Name)
            .ThenBy(room => room.Id)
            .Skip(pageRequest.SkipCount)
            .Take(pageRequest.PageSize + 1)
            .Select(room => new
            {
                room.Id,
                room.PropertyId,
                room.Name,
                room.BuildingLabel,
                room.FloorLabel,
                room.Status,
                room.Version
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        bool hasMore = fetched.Count > pageRequest.PageSize;
        RoomListItemDto[] rooms = fetched
            .Take(pageRequest.PageSize)
            .Select(room => new RoomListItemDto(
                room.Id,
                room.PropertyId,
                room.Name.Value,
                room.BuildingLabel?.Value,
                room.FloorLabel?.Value,
                PropertiesMapper.MapStatus(room.Status),
                room.Version))
            .ToArray();

        return new RoomListResponse(rooms, pageRequest.Page, pageRequest.PageSize, hasMore);
    }

    public async Task<BedListResponse> ListBedsAsync(
        Guid propertyId,
        Guid roomId,
        PageRequest pageRequest,
        CancellationToken cancellationToken)
    {
        var fetched = await dbContext.Rooms
            .AsNoTracking()
            .Where(room => room.PropertyId == propertyId && room.Id == roomId)
            .SelectMany(room => room.Beds.Select(bed => new
            {
                bed.Id,
                bed.RoomId,
                bed.PropertyId,
                bed.Label,
                bed.Status,
                bed.Version,
                RoomVersion = room.Version
            }))
            .OrderBy(item => item.Label)
            .ThenBy(item => item.Id)
            .Skip(pageRequest.SkipCount)
            .Take(pageRequest.PageSize + 1)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        bool hasMore = fetched.Count > pageRequest.PageSize;
        BedListItemDto[] beds = fetched
            .Take(pageRequest.PageSize)
            .Select(item => new BedListItemDto(
                item.Id,
                item.RoomId,
                item.PropertyId,
                item.Label.Value,
                PropertiesMapper.MapStatus(item.Status),
                item.Version,
                item.RoomVersion))
            .ToArray();

        return new BedListResponse(beds, pageRequest.Page, pageRequest.PageSize, hasMore);
    }
}
