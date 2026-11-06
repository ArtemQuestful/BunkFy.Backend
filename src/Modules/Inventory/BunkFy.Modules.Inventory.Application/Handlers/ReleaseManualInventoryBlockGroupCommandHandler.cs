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

internal sealed class ReleaseManualInventoryBlockGroupCommandHandler(
    InventoryManagementMutationCoordinator mutations,
    InventoryManagementOperationJournal journal,
    IManualInventoryBlockGroupRepository groups,
    IManualInventoryBlockRepository blocks,
    IInventoryAvailabilityRepository availability,
    InventoryRetirementCoordinator retirements,
    ISystemClock clock,
    IIdGenerator idGenerator)
    : ICommandHandler<ReleaseManualInventoryBlockGroupCommand, ManualInventoryBlockGroupMutationReceiptDto>
{
    public async Task<Result<ManualInventoryBlockGroupMutationReceiptDto>> HandleAsync(
        ReleaseManualInventoryBlockGroupCommand command,
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

        string fingerprint = InventoryManagementMutationFingerprint
            .ComputeManualBlockGroupReleaseV2(
                command.PropertyId,
                command.BlockGroupId,
                command.ExpectedVersion);
        await mutations.AcquireBlockGroupAsync(command.BlockGroupId, cancellationToken)
            .ConfigureAwait(false);
        InventoryManagementReplayDecision<ManualInventoryBlockGroupMutationReceiptDto> replay =
            await journal.InspectBlockGroupReleaseV2Async(
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

        ManualInventoryBlockGroup? group = await groups.GetAsync(
            command.PropertyId,
            command.BlockGroupId,
            cancellationToken).ConfigureAwait(false);
        if (group is null)
        {
            return Result.Failure<ManualInventoryBlockGroupMutationReceiptDto>(
                InventoryApplicationErrors.BlockGroupNotFound);
        }

        if (group.Version != command.ExpectedVersion)
        {
            return Result.Failure<ManualInventoryBlockGroupMutationReceiptDto>(
                InventoryApplicationErrors.VersionConflict);
        }

        DateTimeOffset nowUtc = clock.UtcNow;
        int alreadyReleased = group.InitialBlockCount - group.ActiveBlockCount;
        if (group.State is ManualInventoryBlockGroupState.Released or
            ManualInventoryBlockGroupState.Replaced)
        {
            ManualInventoryBlockGroupMutationReceiptDto noOp = group.ToReleaseReceipt(
                releasedNowBlockCount: 0,
                alreadyReleasedBlockCount: group.InitialBlockCount);
            return Result.Success(await journal.RecordBlockGroupReleaseV2Async(
                group.ScopeId,
                command.ExpectedVersion,
                noOp,
                command.OperationId,
                fingerprint,
                nowUtc,
                cancellationToken).ConfigureAwait(false));
        }

        IReadOnlyCollection<ManualInventoryBlock> activeBlocks = await blocks.GetActiveGroupAsync(
            command.PropertyId,
            command.BlockGroupId,
            cancellationToken).ConfigureAwait(false);
        if (activeBlocks.Count != group.ActiveBlockCount || activeBlocks.Count == 0)
        {
            throw new InvalidDataException(
                "A manual Inventory block group disagrees with its active child rows.");
        }

        foreach (ManualInventoryBlock block in activeBlocks)
        {
            Result released = block.Release(
                block.Version,
                idGenerator.NewId(),
                nowUtc,
                command.ActorId);
            if (released.IsFailure)
            {
                return Result.Failure<ManualInventoryBlockGroupMutationReceiptDto>(released.Error);
            }
        }

        Result groupReleased = group.Release(
            command.ExpectedVersion,
            activeBlocks.Count,
            nowUtc,
            command.ActorId);
        if (groupReleased.IsFailure)
        {
            return Result.Failure<ManualInventoryBlockGroupMutationReceiptDto>(groupReleased.Error);
        }

        Guid[] unitIds = activeBlocks.Select(block => block.InventoryUnitId).Distinct().ToArray();
        await availability.TouchUnitsAsync(command.PropertyId, unitIds, cancellationToken)
            .ConfigureAwait(false);
        await retirements.TryAdvanceForUnitsAsync(
            command.PropertyId,
            unitIds,
            excludedAllocationId: null,
            excludedBlockIds: activeBlocks.Select(block => block.Id).ToArray(),
            cancellationToken).ConfigureAwait(false);
        ManualInventoryBlockGroupMutationReceiptDto receipt = group.ToReleaseReceipt(
            activeBlocks.Count,
            alreadyReleased);
        return Result.Success(await journal.RecordBlockGroupReleaseV2Async(
            group.ScopeId,
            command.ExpectedVersion,
            receipt,
            command.OperationId,
            fingerprint,
            nowUtc,
            cancellationToken).ConfigureAwait(false));
    }
}
