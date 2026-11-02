namespace BunkFy.Modules.Inventory.Application.Handlers;

using BunkFy.Modules.Inventory.Application.Commands;
using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using BunkFy.Modules.Inventory.Domain.Errors;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;

internal sealed class ReplaceManualInventoryBlockGroupCommandHandler(
    InventoryManagementMutationCoordinator mutations,
    InventoryManagementOperationJournal journal,
    ManualInventoryBlockCreator creator,
    IInventoryAvailabilitySelectionFence selectionFence,
    IManualInventoryBlockGroupRepository groups,
    IManualInventoryBlockRepository blocks,
    IInventoryAvailabilityRepository availability,
    InventoryRetirementCoordinator retirements,
    ISystemClock clock,
    IIdGenerator idGenerator)
    : ICommandHandler<ReplaceManualInventoryBlockGroupCommand, ManualInventoryBlockGroupMutationReceiptDto>
{
    public async Task<Result<ManualInventoryBlockGroupMutationReceiptDto>> HandleAsync(
        ReplaceManualInventoryBlockGroupCommand command,
        CancellationToken cancellationToken)
    {
        if (command.OperationId == Guid.Empty)
        {
            return Result.Failure<ManualInventoryBlockGroupMutationReceiptDto>(
                InventoryApplicationErrors.ManagementOperationInvalid);
        }

        if (!command.Confirmed)
        {
            return Result.Failure<ManualInventoryBlockGroupMutationReceiptDto>(
                InventoryApplicationErrors.BlockGroupConfirmationRequired);
        }

        if (!InventoryBlockTargetNormalizer.TryNormalize(command.Target, out InventoryBlockTarget target))
        {
            return Result.Failure<ManualInventoryBlockGroupMutationReceiptDto>(
                InventoryApplicationErrors.BlockTargetInvalid);
        }

        string fingerprint = InventoryManagementMutationFingerprint.ComputeManualBlockGroupReplace(
            command.PropertyId,
            command.BlockGroupId,
            command.ExpectedVersion,
            target,
            command.Arrival,
            command.Departure,
            command.Reason,
            command.ExpectedSelectionDigest,
            command.ExpectedAffectedBlockCount);
        await mutations.AcquireBlockGroupAsync(command.BlockGroupId, cancellationToken)
            .ConfigureAwait(false);
        InventoryManagementReplayDecision<ManualInventoryBlockGroupMutationReceiptDto> replay =
            await journal.InspectBlockGroupReplaceAsync(
                command.PropertyId,
                command.BlockGroupId,
                command.OperationId,
                command.ExpectedVersion,
                fingerprint,
                cancellationToken).ConfigureAwait(false);
        if (replay.Exists)
        {
            return replay.ToResult();
        }

        if (!ManualInventoryBlockGroup.IsValidActorId(command.ActorId))
        {
            return Result.Failure<ManualInventoryBlockGroupMutationReceiptDto>(
                InventoryDomainErrors.BlockGroupActorInvalid);
        }

        ManualInventoryBlockGroup? predecessor = await groups.GetAsync(
            command.PropertyId,
            command.BlockGroupId,
            cancellationToken).ConfigureAwait(false);
        if (predecessor is null)
        {
            return Result.Failure<ManualInventoryBlockGroupMutationReceiptDto>(
                InventoryApplicationErrors.BlockGroupNotFound);
        }

        if (predecessor.Version != command.ExpectedVersion)
        {
            return Result.Failure<ManualInventoryBlockGroupMutationReceiptDto>(
                InventoryApplicationErrors.VersionConflict);
        }

        if (predecessor.State is ManualInventoryBlockGroupState.Released or
            ManualInventoryBlockGroupState.Replaced)
        {
            return Result.Failure<ManualInventoryBlockGroupMutationReceiptDto>(
                InventoryApplicationErrors.BlockAlreadyReleased);
        }

        IReadOnlyCollection<ManualInventoryBlock> activeBlocks = await blocks.GetActiveGroupAsync(
            command.PropertyId,
            command.BlockGroupId,
            cancellationToken).ConfigureAwait(false);
        if (activeBlocks.Count != predecessor.ActiveBlockCount || activeBlocks.Count == 0)
        {
            throw new InvalidDataException(
                "A manual Inventory block group disagrees with its active child rows.");
        }

        await selectionFence.AcquireAsync(command.PropertyId, cancellationToken)
            .ConfigureAwait(false);
        Result<ManualInventoryBlockSelection> candidate = await creator.ResolveConfirmedSelectionAsync(
            command.PropertyId,
            target,
            command.Arrival,
            command.Departure,
            command.ExpectedSelectionDigest,
            command.ExpectedAffectedBlockCount,
            activeBlocks.Select(block => block.Id).ToArray(),
            cancellationToken).ConfigureAwait(false);
        if (candidate.IsFailure)
        {
            return Result.Failure<ManualInventoryBlockGroupMutationReceiptDto>(candidate.Error);
        }

        if (predecessor.HasSameDefinition(
                ManualInventoryBlockCreator.ToDomainTargetKind(target.Kind),
                target.BuildingLabel,
                target.FloorLabel,
                target.RoomId,
                target.InventoryUnitId,
                command.Arrival,
                command.Departure,
                command.Reason,
                candidate.Value.MembershipDigest ?? string.Empty,
                candidate.Value.InventoryUnitIds.Count))
        {
            ManualInventoryBlockGroupMutationReceiptDto noOp = new(
                predecessor.Id,
                predecessor.PropertyId,
                AffectedBlockCount: 0,
                predecessor.State.ToContractStatus(),
                predecessor.Version,
                PreviousBlockGroupId: predecessor.Id,
                ReleasedBlockCount: 0,
                CreatedBlockCount: 0,
                TotalBlockCount: predecessor.InitialBlockCount,
                ActiveBlockCount: predecessor.ActiveBlockCount,
                AlreadyReleasedBlockCount: predecessor.InitialBlockCount - predecessor.ActiveBlockCount,
                MembershipDigest: predecessor.MembershipDigest);
            return Result.Success(await journal.RecordBlockGroupReplaceAsync(
                predecessor.ScopeId,
                predecessor.Id,
                command.ExpectedVersion,
                noOp,
                command.OperationId,
                fingerprint,
                clock.UtcNow,
                cancellationToken).ConfigureAwait(false));
        }

        Result<ManualInventoryBlockSelection> confirmed = await creator.FenceConfirmedSelectionAsync(
            command.PropertyId,
            target,
            command.Arrival,
            command.Departure,
            candidate.Value,
            activeBlocks.Select(block => block.Id).ToArray(),
            cancellationToken).ConfigureAwait(false);
        if (confirmed.IsFailure)
        {
            return Result.Failure<ManualInventoryBlockGroupMutationReceiptDto>(confirmed.Error);
        }

        Result<ManualInventoryBlockCreationResult> created = await creator.MaterializeConfirmedAsync(
            command.PropertyId,
            target,
            command.Arrival,
            command.Departure,
            command.Reason,
            confirmed.Value,
            predecessor.Id,
            command.ActorId,
            cancellationToken).ConfigureAwait(false);
        if (created.IsFailure)
        {
            return Result.Failure<ManualInventoryBlockGroupMutationReceiptDto>(created.Error);
        }

        DateTimeOffset nowUtc = created.Value.Group.CreatedAtUtc;
        foreach (ManualInventoryBlock block in activeBlocks)
        {
            Result released = block.Release(block.Version, idGenerator.NewId(), nowUtc, command.ActorId);
            if (released.IsFailure)
            {
                return Result.Failure<ManualInventoryBlockGroupMutationReceiptDto>(released.Error);
            }
        }

        Result predecessorReplaced = predecessor.ReplaceWith(
            command.ExpectedVersion,
            created.Value.Group.Id,
            activeBlocks.Count,
            nowUtc,
            command.ActorId);
        if (predecessorReplaced.IsFailure)
        {
            return Result.Failure<ManualInventoryBlockGroupMutationReceiptDto>(predecessorReplaced.Error);
        }

        Guid[] releasedUnitIds = activeBlocks.Select(block => block.InventoryUnitId).Distinct().ToArray();
        HashSet<Guid> successorUnitIds = confirmed.Value.InventoryUnitIds.ToHashSet();
        Guid[] predecessorOnlyUnitIds = releasedUnitIds
            .Where(unitId => !successorUnitIds.Contains(unitId))
            .ToArray();
        if (predecessorOnlyUnitIds.Length > 0)
        {
            await availability.TouchUnitsAsync(
                command.PropertyId,
                predecessorOnlyUnitIds,
                cancellationToken).ConfigureAwait(false);
        }

        await retirements.TryAdvanceForUnitsAsync(
            command.PropertyId,
            releasedUnitIds,
            excludedAllocationId: null,
            excludedBlockIds: activeBlocks.Select(block => block.Id).ToArray(),
            cancellationToken).ConfigureAwait(false);
        ManualInventoryBlockGroupMutationReceiptDto receipt = new(
            created.Value.Group.Id,
            command.PropertyId,
            AffectedBlockCount: activeBlocks.Count + created.Value.Blocks.Count,
            ManualInventoryBlockGroupStatus.Active,
            created.Value.Group.Version,
            PreviousBlockGroupId: predecessor.Id,
            ReleasedBlockCount: activeBlocks.Count,
            CreatedBlockCount: created.Value.Blocks.Count,
            TotalBlockCount: created.Value.Group.InitialBlockCount,
            ActiveBlockCount: created.Value.Group.ActiveBlockCount,
            AlreadyReleasedBlockCount: predecessor.InitialBlockCount - activeBlocks.Count,
            MembershipDigest: created.Value.Group.MembershipDigest);
        return Result.Success(await journal.RecordBlockGroupReplaceAsync(
            predecessor.ScopeId,
            predecessor.Id,
            command.ExpectedVersion,
            receipt,
            command.OperationId,
            fingerprint,
            nowUtc,
            cancellationToken).ConfigureAwait(false));
    }
}
