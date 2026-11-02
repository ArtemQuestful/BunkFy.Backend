namespace BunkFy.Modules.Inventory.Application.Handlers;

using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;

internal sealed class ManualInventoryBlockCreator(
    IInventoryReadRepository inventory,
    IManualInventoryBlockRepository blocks,
    IManualInventoryBlockGroupRepository groups,
    IInventoryAvailabilityRepository availability,
    IScopeContext scopeContext,
    ISystemClock clock,
    IIdGenerator idGenerator)
{
    public async Task<Result<ManualInventoryBlockCreationResult>> CreateAsync(
        Guid propertyId,
        InventoryBlockTarget target,
        DateOnly arrival,
        DateOnly departure,
        string reason,
        string? actorId,
        CancellationToken cancellationToken)
    {
        Result<ManualInventoryBlockSelection> selection = await this.ResolveForMutationAsync(
            propertyId,
            target,
            arrival,
            departure,
            expectedSelectionDigest: null,
            expectedAffectedBlockCount: null,
            excludedBlockIds: [],
            cancellationToken).ConfigureAwait(false);
        return selection.IsFailure
            ? Result.Failure<ManualInventoryBlockCreationResult>(selection.Error)
            : await this.MaterializeAsync(
                propertyId,
                target,
                arrival,
                departure,
                reason,
                selection.Value,
                replacesGroupId: null,
                actorId,
                cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<ManualInventoryBlockCreationResult>> CreateConfirmedAsync(
        Guid propertyId,
        InventoryBlockTarget target,
        DateOnly arrival,
        DateOnly departure,
        string reason,
        string expectedSelectionDigest,
        int expectedAffectedBlockCount,
        Guid? replacesGroupId,
        IReadOnlyCollection<Guid> excludedBlockIds,
        string? actorId,
        CancellationToken cancellationToken)
    {
        Result<ManualInventoryBlockSelection> selection = await this.ConfirmAsync(
            propertyId,
            target,
            arrival,
            departure,
            expectedSelectionDigest,
            expectedAffectedBlockCount,
            excludedBlockIds,
            cancellationToken).ConfigureAwait(false);
        return selection.IsFailure
            ? Result.Failure<ManualInventoryBlockCreationResult>(selection.Error)
            : await this.MaterializeConfirmedAsync(
                propertyId,
                target,
                arrival,
                departure,
                reason,
                selection.Value,
                replacesGroupId,
                actorId,
                cancellationToken).ConfigureAwait(false);
    }

    public Task<Result<ManualInventoryBlockSelection>> ConfirmAsync(
        Guid propertyId,
        InventoryBlockTarget target,
        DateOnly arrival,
        DateOnly departure,
        string expectedSelectionDigest,
        int expectedAffectedBlockCount,
        IReadOnlyCollection<Guid> excludedBlockIds,
        CancellationToken cancellationToken) => this.ResolveForMutationAsync(
            propertyId,
            target,
            arrival,
            departure,
            expectedSelectionDigest,
            expectedAffectedBlockCount,
            excludedBlockIds,
            cancellationToken);

    public Task<Result<ManualInventoryBlockCreationResult>> MaterializeConfirmedAsync(
        Guid propertyId,
        InventoryBlockTarget target,
        DateOnly arrival,
        DateOnly departure,
        string reason,
        ManualInventoryBlockSelection selection,
        Guid? replacesGroupId,
        string? actorId,
        CancellationToken cancellationToken) => this.MaterializeAsync(
            propertyId,
            target,
            arrival,
            departure,
            reason,
            selection,
            replacesGroupId,
            actorId,
            cancellationToken);

    public async Task<Result<ManualInventoryBlockSelection>> PreviewAsync(
        Guid propertyId,
        InventoryBlockTarget target,
        DateOnly arrival,
        DateOnly departure,
        IReadOnlyCollection<Guid> excludedBlockIds,
        CancellationToken cancellationToken)
    {
        Result<InventoryBlockTargetResolution> resolved = await this.ResolveBoundedAsync(
            propertyId,
            target,
            cancellationToken).ConfigureAwait(false);
        if (resolved.IsFailure)
        {
            return Result.Failure<ManualInventoryBlockSelection>(resolved.Error);
        }

        return await this.BuildSelectionAsync(
            propertyId,
            target,
            arrival,
            departure,
            resolved.Value,
            excludedBlockIds,
            evaluateConflicts: resolved.Value.Units.Count <= InventoryContractLimits.MaximumManualInventoryBlockGroupMembers,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<ManualInventoryBlockSelection>> ResolveForMutationAsync(
        Guid propertyId,
        InventoryBlockTarget target,
        DateOnly arrival,
        DateOnly departure,
        string? expectedSelectionDigest,
        int? expectedAffectedBlockCount,
        IReadOnlyCollection<Guid> excludedBlockIds,
        CancellationToken cancellationToken)
    {
        Result<ManualInventoryBlockSelection> initial = await this.ResolveConfirmedSelectionAsync(
            propertyId,
            target,
            arrival,
            departure,
            expectedSelectionDigest,
            expectedAffectedBlockCount,
            excludedBlockIds,
            cancellationToken).ConfigureAwait(false);
        if (initial.IsFailure)
        {
            return initial;
        }

        return await this.FenceConfirmedSelectionAsync(
            propertyId,
            target,
            arrival,
            departure,
            initial.Value,
            excludedBlockIds,
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<Result<ManualInventoryBlockSelection>> ResolveConfirmedSelectionAsync(
        Guid propertyId,
        InventoryBlockTarget target,
        DateOnly arrival,
        DateOnly departure,
        string? expectedSelectionDigest,
        int? expectedAffectedBlockCount,
        IReadOnlyCollection<Guid> excludedBlockIds,
        CancellationToken cancellationToken)
    {
        Result<InventoryBlockTargetResolution> resolved = await this.ResolveBoundedAsync(
            propertyId,
            target,
            cancellationToken).ConfigureAwait(false);
        if (resolved.IsFailure)
        {
            return Result.Failure<ManualInventoryBlockSelection>(resolved.Error);
        }

        Result<ManualInventoryBlockSelection> initial = await this.BuildSelectionAsync(
            propertyId,
            target,
            arrival,
            departure,
            resolved.Value,
            excludedBlockIds,
            evaluateConflicts: false,
            cancellationToken).ConfigureAwait(false);
        if (initial.IsFailure)
        {
            return initial;
        }

        if (initial.Value.ExceedsLimit)
        {
            return Result.Failure<ManualInventoryBlockSelection>(
                InventoryApplicationErrors.BlockGroupTargetTooLarge);
        }

        if (initial.Value.InventoryUnitIds.Count == 0)
        {
            return Result.Failure<ManualInventoryBlockSelection>(
                InventoryApplicationErrors.BlockTargetEmpty);
        }

        if (expectedSelectionDigest is not null &&
            (!string.Equals(
                initial.Value.SelectionDigest,
                expectedSelectionDigest,
                StringComparison.Ordinal) ||
             initial.Value.InventoryUnitIds.Count != expectedAffectedBlockCount))
        {
            return Result.Failure<ManualInventoryBlockSelection>(
                InventoryApplicationErrors.BlockGroupSelectionMismatch);
        }

        return initial;
    }

    internal async Task<Result<ManualInventoryBlockSelection>> FenceConfirmedSelectionAsync(
        Guid propertyId,
        InventoryBlockTarget target,
        DateOnly arrival,
        DateOnly departure,
        ManualInventoryBlockSelection initial,
        IReadOnlyCollection<Guid> excludedBlockIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initial);

        await availability.TouchUnitsAsync(
            propertyId,
            initial.InventoryUnitIds,
            cancellationToken).ConfigureAwait(false);

        Result<InventoryBlockTargetResolution> afterFence = await this.ResolveBoundedAsync(
            propertyId,
            target,
            cancellationToken).ConfigureAwait(false);
        if (afterFence.IsFailure)
        {
            return Result.Failure<ManualInventoryBlockSelection>(afterFence.Error);
        }

        Result<ManualInventoryBlockSelection> current = await this.BuildSelectionAsync(
            propertyId,
            target,
            arrival,
            departure,
            afterFence.Value,
            excludedBlockIds,
            evaluateConflicts: true,
            cancellationToken).ConfigureAwait(false);
        if (current.IsFailure)
        {
            return current;
        }

        if (current.Value.ExceedsLimit ||
            !string.Equals(
                current.Value.SelectionDigest,
                initial.SelectionDigest,
                StringComparison.Ordinal) ||
            !current.Value.InventoryUnitIds.SequenceEqual(initial.InventoryUnitIds))
        {
            return Result.Failure<ManualInventoryBlockSelection>(
                InventoryApplicationErrors.BlockGroupSelectionMismatch);
        }

        if (current.Value.Conflicts.HasManualBlockConflict)
        {
            return Result.Failure<ManualInventoryBlockSelection>(
                InventoryApplicationErrors.BlockOverlap);
        }

        if (current.Value.Conflicts.HasActiveAllocationConflict)
        {
            return Result.Failure<ManualInventoryBlockSelection>(
                InventoryApplicationErrors.BlockAllocationConflict);
        }

        return Result.Success(current.Value with
        {
            SelectionDigest = initial.SelectionDigest
        });
    }

    private async Task<Result<InventoryBlockTargetResolution>> ResolveBoundedAsync(
        Guid propertyId,
        InventoryBlockTarget target,
        CancellationToken cancellationToken)
    {
        if (!await inventory.PropertyExistsAsync(propertyId, cancellationToken).ConfigureAwait(false))
        {
            return Result.Failure<InventoryBlockTargetResolution>(
                InventoryApplicationErrors.PropertyNotFound);
        }

        if (target.Kind == InventoryBlockTargetKind.Unit)
        {
            InventoryUnitSnapshot? unit = target.InventoryUnitId.HasValue
                ? await inventory.GetUnitAsync(
                    propertyId,
                    target.InventoryUnitId.Value,
                    cancellationToken).ConfigureAwait(false)
                : null;
            if (unit is null)
            {
                return Result.Failure<InventoryBlockTargetResolution>(
                    InventoryApplicationErrors.InventoryUnitNotFound);
            }

            if (!unit.Unit.IsTopologyActive)
            {
                return Result.Failure<InventoryBlockTargetResolution>(
                    InventoryApplicationErrors.InventoryUnitInactive);
            }

            if (!unit.IsSellable)
            {
                return Result.Failure<InventoryBlockTargetResolution>(
                    InventoryApplicationErrors.InventoryUnitNotSellable);
            }
        }

        InventoryBlockTargetResolution resolved = await inventory
            .ResolveBlockTargetUnitsBoundedAsync(
                propertyId,
                target,
                InventoryContractLimits.MaximumManualInventoryBlockGroupMembers + 1,
                cancellationToken)
            .ConfigureAwait(false);
        Guid[] resolvedUnitIds = resolved.Units
            .Select(unit => unit.Unit.InventoryUnitId)
            .ToArray();
        Guid[] coordinateUnitIds = resolved.SelectionCoordinates
            .Select(coordinate => coordinate.InventoryUnitId)
            .ToArray();
        if (resolved.Units.Count != resolved.SelectionCoordinates.Count ||
            resolved.Units.Count > InventoryContractLimits.MaximumManualInventoryBlockGroupMembers + 1 ||
            resolved.Units.Any(unit => !unit.IsSellable) ||
            resolvedUnitIds.Any(id => id == Guid.Empty) ||
            coordinateUnitIds.Any(id => id == Guid.Empty) ||
            resolved.SelectionCoordinates.Any(coordinate =>
                coordinate.PropertyAvailabilitySelectionVersion <= 0) ||
            (resolved.SelectionCoordinates.Count > 0 &&
             resolved.SelectionCoordinates
                 .Select(coordinate => coordinate.PropertyAvailabilitySelectionVersion)
                 .Distinct()
                 .Count() != 1) ||
            resolvedUnitIds.Distinct().Count() != resolvedUnitIds.Length ||
            coordinateUnitIds.Distinct().Count() != coordinateUnitIds.Length ||
            !resolvedUnitIds.OrderBy(id => id.ToString("N"), StringComparer.Ordinal).SequenceEqual(
                coordinateUnitIds.OrderBy(id => id.ToString("N"), StringComparer.Ordinal)))
        {
            throw new InvalidDataException(
                "The bounded Inventory block resolver returned inconsistent or unbounded selection evidence.");
        }

        return Result.Success(resolved);
    }

    private async Task<Result<ManualInventoryBlockSelection>> BuildSelectionAsync(
        Guid propertyId,
        InventoryBlockTarget target,
        DateOnly arrival,
        DateOnly departure,
        InventoryBlockTargetResolution resolved,
        IReadOnlyCollection<Guid> excludedBlockIds,
        bool evaluateConflicts,
        CancellationToken cancellationToken)
    {
        Guid[] unitIds = resolved.Units
            .Select(unit => unit.Unit.InventoryUnitId)
            .Distinct()
            .OrderBy(id => id.ToString("N"), StringComparer.Ordinal)
            .ToArray();
        bool exceedsLimit = resolved.IsTruncated ||
            unitIds.Length > InventoryContractLimits.MaximumManualInventoryBlockGroupMembers;
        string? selectionDigest = unitIds.Length == 0 || exceedsLimit
            ? null
            : ManualInventoryBlockGroupDigest.ComputeSelection(
                target,
                arrival,
                departure,
                resolved.SelectionCoordinates);
        string? membershipDigest = unitIds.Length == 0 || exceedsLimit
            ? null
            : ManualInventoryBlockGroupDigest.ComputeMembership(
                target,
                arrival,
                departure,
                unitIds);
        InventoryAvailabilityConflictSnapshot conflicts = new(false, false);
        if (evaluateConflicts && unitIds.Length > 0 && !exceedsLimit)
        {
            InventoryAvailabilityContextSnapshot context = await availability
                .GetContextAsync(propertyId, unitIds, cancellationToken)
                .ConfigureAwait(false);
            conflicts = await availability.GetConflictsAsync(
                propertyId,
                context.ConflictUnitIds,
                arrival,
                departure,
                excludedAllocationId: null,
                excludedBlockIds,
                cancellationToken).ConfigureAwait(false);
        }

        return Result.Success(new ManualInventoryBlockSelection(
            unitIds,
            resolved.Units,
            resolved.SelectionCoordinates,
            selectionDigest,
            membershipDigest,
            exceedsLimit,
            conflicts));
    }

    private async Task<Result<ManualInventoryBlockCreationResult>> MaterializeAsync(
        Guid propertyId,
        InventoryBlockTarget target,
        DateOnly arrival,
        DateOnly departure,
        string reason,
        ManualInventoryBlockSelection selection,
        Guid? replacesGroupId,
        string? actorId,
        CancellationToken cancellationToken)
    {
        string? scopeId = scopeContext.ScopeId;
        if (!scopeContext.IsEnabled || string.IsNullOrWhiteSpace(scopeId))
        {
            return Result.Failure<ManualInventoryBlockCreationResult>(
                InventoryApplicationErrors.TenantRequired);
        }

        string normalizedReason = InventoryManagementMutationFingerprint.NormalizeReason(reason);
        Guid blockGroupId = idGenerator.NewId();
        DateTimeOffset nowUtc = clock.UtcNow;
        Result<ManualInventoryBlockGroup> groupResult = ManualInventoryBlockGroup.Create(
            blockGroupId,
            scopeId,
            propertyId,
            ToDomainTargetKind(target.Kind),
            target.BuildingLabel,
            target.FloorLabel,
            target.RoomId,
            target.InventoryUnitId,
            arrival,
            departure,
            normalizedReason,
            selection.SelectionDigest,
            selection.MembershipDigest ?? string.Empty,
            ManualInventoryBlockGroup.CurrentMembershipDigestVersion,
            selection.InventoryUnitIds.Count,
            replacesGroupId,
            nowUtc,
            actorId);
        if (groupResult.IsFailure)
        {
            return Result.Failure<ManualInventoryBlockCreationResult>(groupResult.Error);
        }

        List<ManualInventoryBlock> created = new(selection.InventoryUnitIds.Count);
        foreach (Guid inventoryUnitId in selection.InventoryUnitIds)
        {
            Result<ManualInventoryBlock> result = ManualInventoryBlock.Create(
                idGenerator.NewId(),
                blockGroupId,
                scopeId,
                propertyId,
                inventoryUnitId,
                arrival,
                departure,
                normalizedReason,
                idGenerator.NewId(),
                nowUtc,
                actorId);
            if (result.IsFailure)
            {
                return Result.Failure<ManualInventoryBlockCreationResult>(result.Error);
            }

            created.Add(result.Value);
        }

        await groups.AddAsync(groupResult.Value, cancellationToken).ConfigureAwait(false);
        await blocks.AddRangeAsync(created, cancellationToken).ConfigureAwait(false);
        return Result.Success(new ManualInventoryBlockCreationResult(
            groupResult.Value,
            created));
    }

    internal static ManualInventoryBlockGroupTargetKind ToDomainTargetKind(
        InventoryBlockTargetKind kind) => kind switch
        {
            InventoryBlockTargetKind.Property => ManualInventoryBlockGroupTargetKind.Property,
            InventoryBlockTargetKind.Building => ManualInventoryBlockGroupTargetKind.Building,
            InventoryBlockTargetKind.Floor => ManualInventoryBlockGroupTargetKind.Floor,
            InventoryBlockTargetKind.Room => ManualInventoryBlockGroupTargetKind.Room,
            InventoryBlockTargetKind.Unit => ManualInventoryBlockGroupTargetKind.Unit,
            _ => ManualInventoryBlockGroupTargetKind.LegacyUnknown
        };
}

internal sealed record ManualInventoryBlockSelection(
    IReadOnlyCollection<Guid> InventoryUnitIds,
    IReadOnlyCollection<InventoryUnitSnapshot> Units,
    IReadOnlyCollection<InventoryBlockSelectionCoordinate> SelectionCoordinates,
    string? SelectionDigest,
    string? MembershipDigest,
    bool ExceedsLimit,
    InventoryAvailabilityConflictSnapshot Conflicts);

internal sealed record ManualInventoryBlockCreationResult(
    ManualInventoryBlockGroup Group,
    IReadOnlyCollection<ManualInventoryBlock> Blocks)
{
    public Guid BlockGroupId => this.Group.Id;
}
