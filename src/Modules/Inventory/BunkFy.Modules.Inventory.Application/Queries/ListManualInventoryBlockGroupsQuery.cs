namespace BunkFy.Modules.Inventory.Application.Queries;

using BunkFy.Modules.Inventory.Contracts;
using Gma.Framework.Cqrs;

public sealed record ListManualInventoryBlockGroupsQuery(
    Guid PropertyId,
    ManualInventoryBlockGroupStatus? Status,
    string? Cursor,
    int PageSize)
    : IQuery<ManualInventoryBlockGroupListResponse>;
