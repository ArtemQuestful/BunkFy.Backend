namespace BunkFy.Modules.Inventory.Persistence.Repositories;

using Gma.Framework.Pagination;
using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using Microsoft.EntityFrameworkCore;

internal sealed class ManualInventoryBlockRepository(InventoryDbContext dbContext)
    : IManualInventoryBlockRepository
{
    public Task<ManualInventoryBlockIdentity?> GetIdentityAsync(
        Guid propertyId,
        Guid blockId,
        CancellationToken cancellationToken) => dbContext.ManualBlocks
            .AsNoTracking()
            .Where(block =>
                block.Id == blockId && block.PropertyId == propertyId)
            .Select(block => new ManualInventoryBlockIdentity(
                block.Id,
                block.BlockGroupId))
            .SingleOrDefaultAsync(cancellationToken);

    public Task AddAsync(ManualInventoryBlock block, CancellationToken cancellationToken)
    {
        dbContext.ManualBlocks.Add(block);
        return Task.CompletedTask;
    }

    public Task AddRangeAsync(IReadOnlyCollection<ManualInventoryBlock> blocks, CancellationToken cancellationToken)
    {
        dbContext.ManualBlocks.AddRange(blocks);
        return Task.CompletedTask;
    }

    public Task<ManualInventoryBlock?> GetAsync(
        Guid propertyId,
        Guid blockId,
        CancellationToken cancellationToken) =>
        dbContext.ManualBlocks.FirstOrDefaultAsync(
            block => block.Id == blockId && block.PropertyId == propertyId,
            cancellationToken);

    public async Task<IReadOnlyCollection<ManualInventoryBlock>> GetActiveGroupAsync(
        Guid propertyId,
        Guid blockGroupId,
        CancellationToken cancellationToken) =>
        await dbContext.ManualBlocks
            .Where(block =>
                block.PropertyId == propertyId &&
                block.BlockGroupId == blockGroupId &&
                block.Status == ManualInventoryBlockState.Active)
            .OrderBy(block => block.InventoryUnitId)
            .ThenBy(block => block.Id)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyCollection<Guid>> GetActiveGroupIdsAsync(
        Guid propertyId,
        Guid blockGroupId,
        CancellationToken cancellationToken) =>
        await dbContext.ManualBlocks
            .AsNoTracking()
            .Where(block =>
                block.PropertyId == propertyId &&
                block.BlockGroupId == blockGroupId &&
                block.Status == ManualInventoryBlockState.Active)
            .OrderBy(block => block.InventoryUnitId)
            .ThenBy(block => block.Id)
            .Select(block => block.Id)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<ManualInventoryBlockGroupMemberListResponse>
        ListGroupMembersAsync(
            Guid propertyId,
            Guid blockGroupId,
            ManualInventoryBlockStatus? status,
            string? cursor,
            int pageSize,
            CancellationToken cancellationToken)
    {
        int normalizedPageSize = Math.Clamp(
            pageSize,
            1,
            PageRequest.MaxPageSize);
        IQueryable<ManualInventoryBlock> query = dbContext.ManualBlocks
            .AsNoTracking()
            .Where(block =>
                block.PropertyId == propertyId &&
                block.BlockGroupId == blockGroupId);
        if (status.HasValue)
        {
            ManualInventoryBlockState state = status.Value switch
            {
                ManualInventoryBlockStatus.Active =>
                    ManualInventoryBlockState.Active,
                ManualInventoryBlockStatus.Released =>
                    ManualInventoryBlockState.Released,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(status),
                    "The manual block status is invalid.")
            };
            query = query.Where(block => block.Status == state);
        }

        if (cursor is not null)
        {
            Guid afterBlockId = ManualInventoryBlockGroupCursor.DecodeMember(
                cursor,
                propertyId,
                blockGroupId,
                status);
            query = query.Where(block => block.Id.CompareTo(afterBlockId) > 0);
        }

        ManualInventoryBlockDto[] page = await query
            .OrderBy(block => block.Id)
            .Take(normalizedPageSize + 1)
            .Select(block => new ManualInventoryBlockDto(
                block.Id,
                block.BlockGroupId,
                block.PropertyId,
                block.InventoryUnitId,
                block.Arrival,
                block.Departure,
                block.Reason,
                block.Status == ManualInventoryBlockState.Active
                    ? ManualInventoryBlockStatus.Active
                    : ManualInventoryBlockStatus.Released,
                block.Version,
                block.CreatedAtUtc,
                block.ReleasedAtUtc))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        bool hasMore = page.Length > normalizedPageSize;
        if (hasMore)
        {
            page = page[..normalizedPageSize];
        }

        string? nextCursor = hasMore && page.Length > 0
            ? ManualInventoryBlockGroupCursor.EncodeMember(
                propertyId,
                blockGroupId,
                status,
                page[^1].BlockId)
            : null;
        return new(page, nextCursor, normalizedPageSize);
    }

    public async Task<ManualInventoryBlockListResponse> ListAsync(
        Guid propertyId,
        Guid? inventoryUnitId,
        bool includeReleased,
        DateOnly? overlapsFrom,
        DateOnly? overlapsTo,
        PageRequest pageRequest,
        CancellationToken cancellationToken)
    {
        IQueryable<ManualInventoryBlock> query = dbContext.ManualBlocks
            .AsNoTracking()
            .Where(block => block.PropertyId == propertyId);
        if (inventoryUnitId.HasValue)
        {
            query = query.Where(block => block.InventoryUnitId == inventoryUnitId.Value);
        }

        if (!includeReleased)
        {
            query = query.Where(block => block.Status == ManualInventoryBlockState.Active);
        }

        if (overlapsFrom.HasValue && overlapsTo.HasValue)
        {
            DateOnly from = overlapsFrom.Value;
            DateOnly to = overlapsTo.Value;
            query = query.Where(block => block.Arrival < to && block.Departure > from);
        }

        ManualInventoryBlockDto[] page = await query
            .OrderBy(block => block.Arrival)
            .ThenBy(block => block.InventoryUnitId)
            .ThenBy(block => block.Id)
            .Skip(pageRequest.SkipCount)
            .Take(pageRequest.PageSize + 1)
            .Select(block => new ManualInventoryBlockDto(
                block.Id,
                block.BlockGroupId,
                block.PropertyId,
                block.InventoryUnitId,
                block.Arrival,
                block.Departure,
                block.Reason,
                block.Status == ManualInventoryBlockState.Active
                    ? ManualInventoryBlockStatus.Active
                    : ManualInventoryBlockStatus.Released,
                block.Version,
                block.CreatedAtUtc,
                block.ReleasedAtUtc))
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

        bool hasMore = page.Length > pageRequest.PageSize;
        ManualInventoryBlockDto[] blocks = hasMore ? page[..pageRequest.PageSize] : page;
        return new(blocks, pageRequest.Page, pageRequest.PageSize, hasMore);
    }
}
