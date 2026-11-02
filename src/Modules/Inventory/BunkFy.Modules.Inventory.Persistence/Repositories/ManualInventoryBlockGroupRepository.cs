namespace BunkFy.Modules.Inventory.Persistence.Repositories;

using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using Gma.Framework.Pagination;
using Microsoft.EntityFrameworkCore;

internal sealed class ManualInventoryBlockGroupRepository(
    InventoryDbContext dbContext)
    : IManualInventoryBlockGroupRepository
{
    public Task<ManualInventoryBlockGroup?> GetAsync(
        Guid propertyId,
        Guid blockGroupId,
        CancellationToken cancellationToken) => dbContext.ManualBlockGroups
            .SingleOrDefaultAsync(
                group => group.PropertyId == propertyId &&
                    group.Id == blockGroupId,
                cancellationToken);

    public async Task ReloadAsync(
        ManualInventoryBlockGroup blockGroup,
        CancellationToken cancellationToken) => await dbContext.Entry(blockGroup)
            .ReloadAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task AddAsync(
        ManualInventoryBlockGroup blockGroup,
        CancellationToken cancellationToken)
    {
        dbContext.ManualBlockGroups.Add(blockGroup);
        return Task.CompletedTask;
    }

    public async Task<ManualInventoryBlockGroupDto?> GetDtoAsync(
        Guid propertyId,
        Guid blockGroupId,
        CancellationToken cancellationToken)
    {
        ManualInventoryBlockGroup? group = await dbContext.ManualBlockGroups
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.PropertyId == propertyId &&
                    item.Id == blockGroupId,
                cancellationToken)
            .ConfigureAwait(false);
        if (group is null)
        {
            return null;
        }

        ManualInventoryBlockGroupSuccessorReadModel? successor = await this
            .SuccessorReadModels(propertyId, [group.Id])
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return ToReadModel(group, successor?.SuccessorId).ToDto();
    }

    public async Task<ManualInventoryBlockGroupListResponse> ListAsync(
        Guid propertyId,
        ManualInventoryBlockGroupStatus? status,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken)
    {
        int normalizedPageSize = Math.Clamp(
            pageSize,
            1,
            PageRequest.MaxPageSize);
        IQueryable<ManualInventoryBlockGroup> source = dbContext
            .ManualBlockGroups
            .AsNoTracking()
            .Where(group => group.PropertyId == propertyId);
        ManualInventoryBlockGroupState? domainStatus = MapStatus(status);
        if (domainStatus.HasValue)
        {
            source = source.Where(group => group.State == domainStatus.Value);
        }

        if (cursor is not null)
        {
            (DateTimeOffset createdAtUtc, Guid blockGroupId) =
                ManualInventoryBlockGroupCursor.DecodeGroup(
                    cursor,
                    propertyId,
                    status);
            source = source.Where(group =>
                group.CreatedAtUtc < createdAtUtc ||
                (group.CreatedAtUtc == createdAtUtc &&
                 group.Id.CompareTo(blockGroupId) > 0));
        }

        ManualInventoryBlockGroup[] groups = await source
            .OrderByDescending(group => group.CreatedAtUtc)
            .ThenBy(group => group.Id)
            .Take(normalizedPageSize + 1)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        ManualInventoryBlockGroupSuccessorReadModel[] successors =
            await this.SuccessorReadModels(
                    propertyId,
                    groups.Select(item => item.Id).ToArray())
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
        IReadOnlyDictionary<Guid, Guid?> replacedByGroupIds = successors
            .ToDictionary(
                successor => successor.PredecessorId,
                successor => (Guid?)successor.SuccessorId);
        ManualInventoryBlockGroupReadModel[] page = groups
            .Select(item => ToReadModel(
                item,
                replacedByGroupIds.GetValueOrDefault(item.Id)))
            .ToArray();
        bool hasMore = page.Length > normalizedPageSize;
        if (hasMore)
        {
            page = page[..normalizedPageSize];
        }

        string? nextCursor = hasMore && page.Length > 0
            ? ManualInventoryBlockGroupCursor.EncodeGroup(
                propertyId,
                status,
                page[^1].CreatedAtUtc,
                page[^1].BlockGroupId)
            : null;
        return new(
            page.Select(item => item.ToDto()).ToArray(),
            normalizedPageSize,
            nextCursor);
    }

    private IQueryable<ManualInventoryBlockGroupSuccessorReadModel>
        SuccessorReadModels(
            Guid propertyId,
            IReadOnlyCollection<Guid> predecessorIds) => dbContext
                .ManualBlockGroups
                .AsNoTracking()
                .Where(successor =>
                    successor.PropertyId == propertyId &&
                    successor.ReplacesGroupId.HasValue &&
                    predecessorIds.Contains(successor.ReplacesGroupId.Value))
                .Select(successor =>
                    new ManualInventoryBlockGroupSuccessorReadModel(
                        successor.ReplacesGroupId!.Value,
                        successor.Id));

    private static ManualInventoryBlockGroupReadModel ToReadModel(
        ManualInventoryBlockGroup group,
        Guid? replacedByGroupId) => new(
            group.Id,
            group.PropertyId,
            group.TargetKind,
            group.BuildingLabel,
            group.FloorLabel,
            group.RoomId,
            group.InventoryUnitId,
            group.Arrival,
            group.Departure,
            group.Reason,
            group.SelectionDigest,
            group.MembershipDigest,
            group.MembershipDigestVersion,
            group.InitialBlockCount,
            group.ActiveBlockCount,
            group.State,
            group.Version,
            group.ReplacesGroupId,
            replacedByGroupId,
            group.CreatedAtUtc,
            group.UpdatedAtUtc,
            group.ReleasedAtUtc,
            group.CreatedByActorId,
            group.LastModifiedByActorId);

    private static ManualInventoryBlockGroupState? MapStatus(
        ManualInventoryBlockGroupStatus? status) => status switch
        {
            null => null,
            ManualInventoryBlockGroupStatus.Active =>
                ManualInventoryBlockGroupState.Active,
            ManualInventoryBlockGroupStatus.PartiallyReleased =>
                ManualInventoryBlockGroupState.PartiallyReleased,
            ManualInventoryBlockGroupStatus.Released =>
                ManualInventoryBlockGroupState.Released,
            ManualInventoryBlockGroupStatus.Replaced =>
                ManualInventoryBlockGroupState.Replaced,
            _ => throw new ArgumentOutOfRangeException(
                nameof(status),
                "The manual block-group status is invalid.")
        };

    private sealed record ManualInventoryBlockGroupReadModel(
        Guid BlockGroupId,
        Guid PropertyId,
        ManualInventoryBlockGroupTargetKind TargetKind,
        string? BuildingLabel,
        string? FloorLabel,
        Guid? RoomId,
        Guid? InventoryUnitId,
        DateOnly Arrival,
        DateOnly Departure,
        string Reason,
        string? SelectionDigest,
        string MembershipDigest,
        int MembershipDigestVersion,
        int InitialBlockCount,
        int ActiveBlockCount,
        ManualInventoryBlockGroupState State,
        long Version,
        Guid? ReplacesGroupId,
        Guid? ReplacedByGroupId,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset? UpdatedAtUtc,
        DateTimeOffset? ReleasedAtUtc,
        string? CreatedByActorId,
        string? LastModifiedByActorId)
    {
        public ManualInventoryBlockGroupDto ToDto() => new(
            this.BlockGroupId,
            this.PropertyId,
            new InventoryBlockTarget(
                (InventoryBlockTargetKind)(int)this.TargetKind,
                this.BuildingLabel,
                this.FloorLabel,
                this.RoomId,
                this.InventoryUnitId),
            this.Arrival,
            this.Departure,
            this.Reason,
            this.SelectionDigest,
            this.MembershipDigest,
            this.MembershipDigestVersion,
            this.InitialBlockCount,
            this.ActiveBlockCount,
            this.State switch
            {
                ManualInventoryBlockGroupState.Active =>
                    ManualInventoryBlockGroupStatus.Active,
                ManualInventoryBlockGroupState.PartiallyReleased =>
                    ManualInventoryBlockGroupStatus.PartiallyReleased,
                ManualInventoryBlockGroupState.Released =>
                    ManualInventoryBlockGroupStatus.Released,
                ManualInventoryBlockGroupState.Replaced =>
                    ManualInventoryBlockGroupStatus.Replaced,
                _ => ManualInventoryBlockGroupStatus.Unknown
            },
            this.Version,
            this.ReplacesGroupId,
            this.ReplacedByGroupId,
            this.CreatedAtUtc,
            this.UpdatedAtUtc,
            this.ReleasedAtUtc,
            this.CreatedByActorId,
            this.LastModifiedByActorId);
    }

    private sealed record ManualInventoryBlockGroupSuccessorReadModel(
        Guid PredecessorId,
        Guid SuccessorId);
}
