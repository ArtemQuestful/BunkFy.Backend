namespace BunkFy.Modules.Inventory.Application.Handlers;

using BunkFy.Modules.Inventory.Application.Commands;
using BunkFy.Modules.Inventory.Contracts;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using BunkFy.Modules.Inventory.Domain.Errors;

internal sealed class CreateManualInventoryBlockGroupCommandHandler(
    InventoryManagementMutationCoordinator mutations,
    InventoryManagementOperationJournal journal,
    ManualInventoryBlockCreator creator,
    IInventoryAvailabilitySelectionFence selectionFence)
    : ICommandHandler<CreateManualInventoryBlockGroupCommand, ManualInventoryBlockGroupMutationReceiptDto>
{
    public async Task<Result<ManualInventoryBlockGroupMutationReceiptDto>> HandleAsync(
        CreateManualInventoryBlockGroupCommand command,
        CancellationToken cancellationToken)
    {
        if (command.OperationId == Guid.Empty)
        {
            return Result.Failure<
                ManualInventoryBlockGroupMutationReceiptDto>(
                InventoryApplicationErrors.ManagementOperationInvalid);
        }

        if (!command.Confirmed)
        {
            return Result.Failure<ManualInventoryBlockGroupMutationReceiptDto>(
                InventoryApplicationErrors.BlockGroupConfirmationRequired);
        }

        if (!InventoryBlockTargetNormalizer.TryNormalize(
                command.Target,
                out InventoryBlockTarget target))
        {
            return Result.Failure<
                ManualInventoryBlockGroupMutationReceiptDto>(
                InventoryApplicationErrors.BlockTargetInvalid);
        }

        string fingerprint = InventoryManagementMutationFingerprint
            .ComputeManualBlockGroupCreateV2(
                command.PropertyId,
                target,
                command.Arrival,
                command.Departure,
                command.Reason,
                command.ExpectedSelectionDigest,
                command.ExpectedAffectedBlockCount);
        await mutations.AcquirePropertyOperationAsync(
                command.PropertyId,
                command.OperationId,
                cancellationToken)
            .ConfigureAwait(false);
        InventoryManagementReplayDecision<
            ManualInventoryBlockGroupMutationReceiptDto> replay =
            await journal.InspectBlockGroupCreateV2Async(
                command.PropertyId,
                command.OperationId,
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

        await selectionFence.AcquireAsync(command.PropertyId, cancellationToken)
            .ConfigureAwait(false);
        Result<ManualInventoryBlockCreationResult> result = await creator.CreateConfirmedAsync(
            command.PropertyId,
            target,
            command.Arrival,
            command.Departure,
            command.Reason,
            command.ExpectedSelectionDigest,
            command.ExpectedAffectedBlockCount,
            replacesGroupId: null,
            excludedBlockIds: [],
            command.ActorId,
            cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return Result.Failure<
                ManualInventoryBlockGroupMutationReceiptDto>(result.Error);
        }

        ManualInventoryBlockGroupMutationReceiptDto receipt = await journal
            .RecordBlockGroupCreateV2Async(
                result.Value,
                command.OperationId,
                fingerprint,
                result.Value.Group.CreatedAtUtc,
                cancellationToken)
            .ConfigureAwait(false);
        return Result.Success(receipt);
    }
}
