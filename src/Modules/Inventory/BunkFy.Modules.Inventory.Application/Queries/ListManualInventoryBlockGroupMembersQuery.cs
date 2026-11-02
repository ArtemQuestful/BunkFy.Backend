namespace BunkFy.Modules.Inventory.Application.Queries;

using BunkFy.Modules.Inventory.Contracts;
using Gma.Framework.Cqrs;

public sealed record ListManualInventoryBlockGroupMembersQuery(
    Guid PropertyId,
    Guid BlockGroupId,
    ManualInventoryBlockStatus? Status,
    string? Cursor,
    int PageSize)
    : IQuery<ManualInventoryBlockGroupMemberListResponse>;
