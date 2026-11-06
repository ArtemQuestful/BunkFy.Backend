namespace BunkFy.Modules.Inventory.Application.Queries;

using Gma.Framework.Cqrs;
using BunkFy.Modules.Inventory.Contracts;

public sealed record ListManualInventoryBlocksQuery(
    Guid PropertyId,
    Guid? InventoryUnitId,
    bool IncludeReleased,
    int Page,
    int PageSize,
    DateOnly? OverlapsFrom = null,
    DateOnly? OverlapsTo = null)
    : IQuery<ManualInventoryBlockListResponse>;
