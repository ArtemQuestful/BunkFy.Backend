namespace BunkFy.Modules.Inventory.Application.Handlers;

using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Application.Queries;
using BunkFy.Modules.Inventory.Application.Validation;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using Gma.Framework.Cqrs;
using Gma.Framework.Pagination;
using Gma.Framework.Results;

internal sealed class PreviewManualInventoryBlockGroupQueryHandler(
    IManualInventoryBlockGroupRepository groups,
    IManualInventoryBlockRepository blocks,
    ManualInventoryBlockCreator creator)
    : IQueryHandler<PreviewManualInventoryBlockGroupQuery, ManualInventoryBlockGroupSelectionPreviewDto>
{
    private const int PreviewMemberLimit = 25;

    public async Task<Result<ManualInventoryBlockGroupSelectionPreviewDto>> HandleAsync(
        PreviewManualInventoryBlockGroupQuery query,
        CancellationToken cancellationToken)
    {
        if (!InventoryBlockTargetNormalizer.TryNormalize(query.Target, out InventoryBlockTarget target))
        {
            return Result.Failure<ManualInventoryBlockGroupSelectionPreviewDto>(
                InventoryApplicationErrors.BlockTargetInvalid);
        }

        if (query.Arrival >= query.Departure)
        {
            return Result.Failure<ManualInventoryBlockGroupSelectionPreviewDto>(
                InventoryApplicationErrors.StayRangeInvalid);
        }

        if (!CreateManualInventoryBlockGroupCommandValidator.IsValidReason(query.Reason))
        {
            return Result.Failure<ManualInventoryBlockGroupSelectionPreviewDto>(
                InventoryApplicationErrors.BlockReasonInvalid);
        }

        ManualInventoryBlockGroupDto? existingDto = null;
        IReadOnlyCollection<Guid> excludedBlockIds = [];
        if (query.BlockGroupId.HasValue)
        {
            existingDto = await groups.GetDtoAsync(
                query.PropertyId,
                query.BlockGroupId.Value,
                cancellationToken).ConfigureAwait(false);
            if (existingDto is null)
            {
                return Result.Failure<ManualInventoryBlockGroupSelectionPreviewDto>(
                    InventoryApplicationErrors.BlockGroupNotFound);
            }

            if (query.ExpectedVersion.HasValue && existingDto.Version != query.ExpectedVersion.Value)
            {
                return Result.Failure<ManualInventoryBlockGroupSelectionPreviewDto>(
                    InventoryApplicationErrors.VersionConflict);
            }

            if (existingDto.Status is ManualInventoryBlockGroupStatus.Released or
                ManualInventoryBlockGroupStatus.Replaced)
            {
                return Result.Failure<ManualInventoryBlockGroupSelectionPreviewDto>(
                    InventoryApplicationErrors.BlockAlreadyReleased);
            }

            excludedBlockIds = await blocks.GetActiveGroupIdsAsync(
                query.PropertyId,
                query.BlockGroupId.Value,
                cancellationToken).ConfigureAwait(false);
        }

        Result<ManualInventoryBlockSelection> selection = await creator.PreviewAsync(
            query.PropertyId,
            target,
            query.Arrival,
            query.Departure,
            excludedBlockIds,
            cancellationToken).ConfigureAwait(false);
        if (selection.IsFailure)
        {
            return Result.Failure<ManualInventoryBlockGroupSelectionPreviewDto>(selection.Error);
        }

        Dictionary<Guid, InventoryBlockSelectionCoordinate> coordinates = selection.Value.SelectionCoordinates
            .ToDictionary(item => item.InventoryUnitId);
        ManualInventoryBlockGroupPreviewMemberDto[] members = selection.Value.Units
            .OrderBy(item => item.Unit.InventoryUnitId.ToString("N"), StringComparer.Ordinal)
            .Take(PreviewMemberLimit)
            .Select(item => new ManualInventoryBlockGroupPreviewMemberDto(
                item.Unit.InventoryUnitId,
                item.Unit.RoomId,
                coordinates[item.Unit.InventoryUnitId].RoomName,
                item.Unit.Label))
            .ToArray();
        bool conflicted = selection.Value.Conflicts.HasManualBlockConflict ||
            selection.Value.Conflicts.HasActiveAllocationConflict;
        ManualInventoryBlockGroupPreviewStatus status = selection.Value.ExceedsLimit
            ? ManualInventoryBlockGroupPreviewStatus.TooLarge
            : selection.Value.InventoryUnitIds.Count == 0
                ? ManualInventoryBlockGroupPreviewStatus.Empty
                : conflicted
                    ? ManualInventoryBlockGroupPreviewStatus.Conflicted
                    : ManualInventoryBlockGroupPreviewStatus.Ready;
        bool isNoOp = existingDto is not null &&
            existingDto.Target == target &&
            existingDto.Arrival == query.Arrival &&
            existingDto.Departure == query.Departure &&
            string.Equals(existingDto.Reason, query.Reason.Trim(), StringComparison.Ordinal) &&
            string.Equals(existingDto.MembershipDigest, selection.Value.MembershipDigest, StringComparison.Ordinal) &&
            existingDto.ActiveBlockCount == selection.Value.InventoryUnitIds.Count;
        return Result.Success(new ManualInventoryBlockGroupSelectionPreviewDto(
            query.PropertyId,
            target,
            query.Arrival,
            query.Departure,
            status,
            InventoryContractLimits.MaximumManualInventoryBlockGroupMembers,
            selection.Value.ExceedsLimit ? null : selection.Value.InventoryUnitIds.Count,
            selection.Value.InventoryUnitIds.Count,
            selection.Value.ExceedsLimit,
            selection.Value.SelectionDigest,
            selection.Value.MembershipDigest,
            ManualInventoryBlockGroup.CurrentMembershipDigestVersion,
            selection.Value.Conflicts.HasManualBlockConflict,
            selection.Value.Conflicts.HasActiveAllocationConflict,
            members,
            selection.Value.InventoryUnitIds.Count > members.Length || selection.Value.ExceedsLimit,
            query.BlockGroupId,
            existingDto?.Version,
            isNoOp));
    }
}

internal sealed class GetManualInventoryBlockGroupQueryHandler(
    IManualInventoryBlockGroupRepository groups)
    : IQueryHandler<GetManualInventoryBlockGroupQuery, ManualInventoryBlockGroupDto>
{
    public async Task<Result<ManualInventoryBlockGroupDto>> HandleAsync(
        GetManualInventoryBlockGroupQuery query,
        CancellationToken cancellationToken) =>
        await groups.GetDtoAsync(query.PropertyId, query.BlockGroupId, cancellationToken)
            .ConfigureAwait(false) is { } group
            ? Result.Success(group)
            : Result.Failure<ManualInventoryBlockGroupDto>(InventoryApplicationErrors.BlockGroupNotFound);
}

internal sealed class ListManualInventoryBlockGroupsQueryHandler(
    IInventoryReadRepository inventory,
    IManualInventoryBlockGroupRepository groups)
    : IQueryHandler<ListManualInventoryBlockGroupsQuery, ManualInventoryBlockGroupListResponse>
{
    public async Task<Result<ManualInventoryBlockGroupListResponse>> HandleAsync(
        ListManualInventoryBlockGroupsQuery query,
        CancellationToken cancellationToken)
    {
        if (!await inventory.PropertyExistsAsync(query.PropertyId, cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure<ManualInventoryBlockGroupListResponse>(InventoryApplicationErrors.PropertyNotFound);
        }

        try
        {
            return Result.Success(await groups.ListAsync(
                query.PropertyId,
                query.Status,
                query.Cursor,
                PageRequest.Normalize(1, query.PageSize).PageSize,
                cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            return Result.Failure<ManualInventoryBlockGroupListResponse>(
                InventoryApplicationErrors.BlockGroupCursorInvalid);
        }
    }
}

internal sealed class ListManualInventoryBlockGroupMembersQueryHandler(
    IManualInventoryBlockGroupRepository groups,
    IManualInventoryBlockRepository blocks)
    : IQueryHandler<ListManualInventoryBlockGroupMembersQuery, ManualInventoryBlockGroupMemberListResponse>
{
    public async Task<Result<ManualInventoryBlockGroupMemberListResponse>> HandleAsync(
        ListManualInventoryBlockGroupMembersQuery query,
        CancellationToken cancellationToken)
    {
        if (await groups.GetAsync(query.PropertyId, query.BlockGroupId, cancellationToken)
                .ConfigureAwait(false) is null)
        {
            return Result.Failure<ManualInventoryBlockGroupMemberListResponse>(
                InventoryApplicationErrors.BlockGroupNotFound);
        }

        try
        {
            return Result.Success(await blocks.ListGroupMembersAsync(
                query.PropertyId,
                query.BlockGroupId,
                query.Status,
                query.Cursor,
                PageRequest.Normalize(1, query.PageSize).PageSize,
                cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            return Result.Failure<ManualInventoryBlockGroupMemberListResponse>(
                InventoryApplicationErrors.BlockGroupCursorInvalid);
        }
    }
}

internal sealed class GetManualInventoryBlockGroupCreateOperationQueryHandler(
    IInventoryManagementOperationRepository operations)
    : IQueryHandler<GetManualInventoryBlockGroupCreateOperationQuery, ManualInventoryBlockGroupOperationDto>
{
    public async Task<Result<ManualInventoryBlockGroupOperationDto>> HandleAsync(
        GetManualInventoryBlockGroupCreateOperationQuery query,
        CancellationToken cancellationToken) => await operations.GetAsync(
            InventoryManagementResourceKind.Property,
            query.PropertyId,
            query.OperationId,
            cancellationToken).ConfigureAwait(false) is { } operation &&
            operation.PropertyId == query.PropertyId &&
            operation.Kind is InventoryManagementMutationKind.ManualBlockGroupCreate or
                InventoryManagementMutationKind.ManualBlockGroupCreateV2
            ? Result.Success(operation.ToBlockGroupOperationDto())
            : Result.Failure<ManualInventoryBlockGroupOperationDto>(
                InventoryApplicationErrors.BlockGroupOperationNotFound);
}

internal sealed class GetManualInventoryBlockGroupOperationQueryHandler(
    IInventoryManagementOperationRepository operations)
    : IQueryHandler<GetManualInventoryBlockGroupOperationQuery, ManualInventoryBlockGroupOperationDto>
{
    public async Task<Result<ManualInventoryBlockGroupOperationDto>> HandleAsync(
        GetManualInventoryBlockGroupOperationQuery query,
        CancellationToken cancellationToken) => await operations.GetAsync(
            InventoryManagementResourceKind.BlockGroup,
            query.BlockGroupId,
            query.OperationId,
            cancellationToken).ConfigureAwait(false) is { } operation &&
            operation.PropertyId == query.PropertyId &&
            operation.Kind is InventoryManagementMutationKind.ManualBlockGroupReplace or
                InventoryManagementMutationKind.ManualBlockGroupRelease or
                InventoryManagementMutationKind.ManualBlockGroupReleaseV2
            ? Result.Success(operation.ToBlockGroupOperationDto())
            : Result.Failure<ManualInventoryBlockGroupOperationDto>(
                InventoryApplicationErrors.BlockGroupOperationNotFound);
}
