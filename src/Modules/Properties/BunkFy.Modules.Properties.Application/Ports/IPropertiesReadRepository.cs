namespace BunkFy.Modules.Properties.Application.Ports;

using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Aggregates;
using Gma.Framework.Pagination;

public interface IPropertiesReadRepository
{
    Task<Property?> GetPropertyAsync(Guid propertyId, CancellationToken cancellationToken);
    Task<PropertyReadPage> ListPropertiesAsync(PageRequest pageRequest, CancellationToken cancellationToken);
    Task<PropertyReadPage> ListVisiblePropertiesAsync(
        PageRequest pageRequest,
        PropertiesVisibilityScope visibility,
        CancellationToken cancellationToken);
    Task<RoomDto?> GetRoomAsync(Guid propertyId, Guid roomId, CancellationToken cancellationToken);
    Task<RoomListResponse> ListRoomsAsync(Guid propertyId, PageRequest pageRequest, CancellationToken cancellationToken);
    Task<BedListResponse> ListBedsAsync(Guid propertyId, Guid roomId, PageRequest pageRequest, CancellationToken cancellationToken);
}

public sealed record PropertyReadPage(
    IReadOnlyCollection<PropertyListReadModel> Properties,
    int Page,
    int PageSize,
    bool HasMore);

public sealed record PropertyListReadModel(
    Guid PropertyId,
    string Name,
    string Code,
    string TimeZoneId,
    PropertyState Status,
    PropertyProcessingState ProcessingState,
    long Version);
