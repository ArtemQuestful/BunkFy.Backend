namespace BunkFy.Modules.Properties.Application.Handlers;

using BunkFy.Modules.Properties.Application.Ports;
using BunkFy.Modules.Properties.Application.Queries;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Aggregates;
using Gma.Framework.Cqrs;
using Gma.Framework.Pagination;
using Gma.Framework.Results;

internal sealed class ListRoomsQueryHandler(IPropertiesReadRepository repository)
    : IQueryHandler<ListRoomsQuery, RoomListResponse>
{
    public async Task<Result<RoomListResponse>> HandleAsync(ListRoomsQuery query, CancellationToken cancellationToken)
    {
        PageRequest pageRequest = PageRequest.Normalize(query.Page, query.PageSize);
        Property? property = await repository.GetPropertyAsync(query.PropertyId, cancellationToken).ConfigureAwait(false);
        if (property is null)
        {
            return Result.Failure<RoomListResponse>(PropertiesApplicationErrors.PropertyNotFound);
        }

        return Result.Success(await repository.ListRoomsAsync(query.PropertyId, pageRequest, cancellationToken).ConfigureAwait(false));
    }
}
