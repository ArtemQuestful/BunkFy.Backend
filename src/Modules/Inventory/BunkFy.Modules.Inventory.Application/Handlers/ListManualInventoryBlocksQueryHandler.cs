namespace BunkFy.Modules.Inventory.Application.Handlers;

using Gma.Framework.Cqrs;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Application.Queries;
using BunkFy.Modules.Inventory.Contracts;

internal sealed class ListManualInventoryBlocksQueryHandler(
    IInventoryReadRepository inventory,
    IManualInventoryBlockRepository blocks)
    : IQueryHandler<ListManualInventoryBlocksQuery, ManualInventoryBlockListResponse>
{
    public async Task<Result<ManualInventoryBlockListResponse>> HandleAsync(
        ListManualInventoryBlocksQuery query,
        CancellationToken cancellationToken)
    {
        if (!await inventory.PropertyExistsAsync(query.PropertyId, cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure<ManualInventoryBlockListResponse>(InventoryApplicationErrors.PropertyNotFound);
        }

        PageRequest pageRequest = PageRequest.Normalize(query.Page, query.PageSize);
        return Result.Success(await blocks.ListAsync(
            query.PropertyId,
            query.InventoryUnitId,
            query.IncludeReleased,
            query.OverlapsFrom,
            query.OverlapsTo,
            pageRequest,
            cancellationToken).ConfigureAwait(false));
    }
}
