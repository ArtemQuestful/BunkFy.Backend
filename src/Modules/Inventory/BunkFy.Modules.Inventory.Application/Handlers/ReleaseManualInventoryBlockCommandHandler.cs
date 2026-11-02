namespace BunkFy.Modules.Inventory.Application.Handlers;

using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using BunkFy.Modules.Inventory.Application.Commands;
using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using BunkFy.Modules.Inventory.Domain.Errors;

internal sealed class ReleaseManualInventoryBlockCommandHandler(
    InventoryManagementMutationCoordinator mutations,
    InventoryManagementOperationJournal journal,
    IManualInventoryBlockRepository blocks,
    IManualInventoryBlockGroupRepository groups,
    IInventoryAvailabilityRepository availability,
    InventoryRetirementCoordinator retirements,
    ISystemClock clock,
    IIdGenerator idGenerator)
    : ICommandHandler<ReleaseManualInventoryBlockCommand, ManualInventoryBlockMutationReceiptDto>
{
    public async Task<Result<ManualInventoryBlockMutationReceiptDto>> HandleAsync(
        ReleaseManualInventoryBlockCommand command,
        CancellationToken cancellationToken)
    {
        if (command.OperationId == Guid.Empty)
        {
            return Result.Failure<ManualInventoryBlockMutationReceiptDto>(
                InventoryApplicationErrors.ManagementOperationInvalid);
        }

        string fingerprint = InventoryManagementMutationFingerprint
            .ComputeManualBlockRelease(
                command.PropertyId,
                command.BlockId,
                command.ExpectedVersion);
        await mutations.AcquireBlockAsync(
                command.BlockId,
                cancellationToken)
            .ConfigureAwait(false);
        InventoryManagementReplayDecision<
            ManualInventoryBlockMutationReceiptDto> replay = await journal
                .InspectBlockReleaseAsync(
                    command.PropertyId,
                    command.BlockId,
                    command.OperationId,
                    command.ExpectedVersion,
                    fingerprint,
                    cancellationToken)
                .ConfigureAwait(false);
        if (replay.Exists)
        {
            return replay.ToResult();
        }

        if (!ManualInventoryBlockGroup.IsValidActorId(command.ActorId))
        {
            return Result.Failure<ManualInventoryBlockMutationReceiptDto>(
                InventoryDomainErrors.BlockGroupActorInvalid);
        }

        ManualInventoryBlockIdentity? identity = await blocks
            .GetIdentityAsync(
                command.PropertyId,
                command.BlockId,
                cancellationToken)
            .ConfigureAwait(false);
        if (identity is null)
        {
            return Result.Failure<ManualInventoryBlockMutationReceiptDto>(
                InventoryApplicationErrors.BlockNotFound);
        }

        await mutations.AcquireBlockGroupAsync(
                identity.BlockGroupId,
                cancellationToken)
            .ConfigureAwait(false);
        ManualInventoryBlock? block = await blocks
            .GetAsync(command.PropertyId, command.BlockId, cancellationToken)
            .ConfigureAwait(false);
        if (block is null)
        {
            return Result.Failure<ManualInventoryBlockMutationReceiptDto>(InventoryApplicationErrors.BlockNotFound);
        }

        ManualInventoryBlockGroup? group = await groups.GetAsync(
            command.PropertyId,
            identity.BlockGroupId,
            cancellationToken).ConfigureAwait(false);
        if (group is null || group.Id != block.BlockGroupId ||
            group.PropertyId != block.PropertyId)
        {
            throw new InvalidDataException(
                "A manual Inventory block references a missing or mismatched parent group.");
        }

        if (block.Status != ManualInventoryBlockState.Active ||
            group.State is ManualInventoryBlockGroupState.Released or ManualInventoryBlockGroupState.Replaced ||
            group.ActiveBlockCount <= 0)
        {
            return Result.Failure<ManualInventoryBlockMutationReceiptDto>(
                InventoryApplicationErrors.BlockAlreadyReleased);
        }


        IReadOnlyCollection<ManualInventoryBlock> activeGroupBlocks = await blocks.GetActiveGroupAsync(
            command.PropertyId,
            group.Id,
            cancellationToken).ConfigureAwait(false);
        if (activeGroupBlocks.Count != group.ActiveBlockCount ||
            activeGroupBlocks.Count == 0 ||
            !activeGroupBlocks.Any(active => active.Id == block.Id))
        {
            throw new InvalidDataException(
                "A manual Inventory block group disagrees with its active child rows.");
        }

        DateTimeOffset nowUtc = clock.UtcNow;
        Result released = block.Release(
            command.ExpectedVersion,
            idGenerator.NewId(),
            nowUtc,
            command.ActorId);
        if (released.IsFailure)
        {
            return Result.Failure<ManualInventoryBlockMutationReceiptDto>(released.Error);
        }

        Result parentReleased = group.RecordMemberRelease(nowUtc, command.ActorId);
        if (parentReleased.IsFailure)
        {
            return Result.Failure<ManualInventoryBlockMutationReceiptDto>(
                parentReleased.Error);
        }

        await availability.TouchUnitsAsync(
            block.PropertyId,
            [block.InventoryUnitId],
            cancellationToken).ConfigureAwait(false);
        await retirements.TryAdvanceForUnitsAsync(
            block.PropertyId,
            [block.InventoryUnitId],
            excludedAllocationId: null,
            excludedBlockIds: [block.Id],
            cancellationToken).ConfigureAwait(false);
        ManualInventoryBlockMutationReceiptDto receipt = await journal
            .RecordBlockReleaseAsync(
                block,
                command.OperationId,
                command.ExpectedVersion,
                fingerprint,
                nowUtc,
                cancellationToken)
            .ConfigureAwait(false);
        return Result.Success(receipt);
    }
}
