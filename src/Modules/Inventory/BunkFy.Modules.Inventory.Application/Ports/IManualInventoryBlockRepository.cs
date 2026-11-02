namespace BunkFy.Modules.Inventory.Application.Ports;

using Gma.Framework.Pagination;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;

public interface IManualInventoryBlockRepository
{
    Task<ManualInventoryBlockIdentity?> GetIdentityAsync(
        Guid propertyId,
        Guid blockId,
        CancellationToken cancellationToken);

    Task AddAsync(ManualInventoryBlock block, CancellationToken cancellationToken);
    Task AddRangeAsync(IReadOnlyCollection<ManualInventoryBlock> blocks, CancellationToken cancellationToken);
    Task<ManualInventoryBlock?> GetAsync(Guid propertyId, Guid blockId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<ManualInventoryBlock>> GetActiveGroupAsync(
        Guid propertyId,
        Guid blockGroupId,
        CancellationToken cancellationToken);
    async Task<IReadOnlyCollection<Guid>> GetActiveGroupIdsAsync(
        Guid propertyId,
        Guid blockGroupId,
        CancellationToken cancellationToken) =>
        (await this.GetActiveGroupAsync(
                propertyId,
                blockGroupId,
                cancellationToken)
            .ConfigureAwait(false))
        .Select(block => block.Id)
        .ToArray();
    Task<ManualInventoryBlockGroupMemberListResponse> ListGroupMembersAsync(
        Guid propertyId,
        Guid blockGroupId,
        ManualInventoryBlockStatus? status,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken);
    Task<ManualInventoryBlockListResponse> ListAsync(
        Guid propertyId,
        Guid? inventoryUnitId,
        bool includeReleased,
        DateOnly? overlapsFrom,
        DateOnly? overlapsTo,
        PageRequest pageRequest,
        CancellationToken cancellationToken);
}

public sealed record ManualInventoryBlockIdentity(
    Guid BlockId,
    Guid BlockGroupId);
