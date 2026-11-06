namespace BunkFy.Modules.Inventory.Application.Handlers;

using BunkFy.Modules.Inventory.Application.Commands;
using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;

internal sealed class CancelBedRetirementCommandHandler(
    InventoryManagementMutationCoordinator mutations,
    InventoryManagementOperationJournal journal,
    IBedRetirementRepository retirements,
    IInventoryAvailabilitySelectionFence selectionFence,
    BedRetirementCoordinator coordinator,
    InventoryUnitDefinitionPublisher definitions,
    ISystemClock clock)
    : ICommandHandler<CancelBedRetirementCommand, BedRetirementDto>
{
    public async Task<Result<BedRetirementDto>> HandleAsync(
        CancelBedRetirementCommand command,
        CancellationToken cancellationToken)
    {
        if (command.OperationId == Guid.Empty)
        {
            return Result.Failure<BedRetirementDto>(
                InventoryApplicationErrors.ManagementOperationInvalid);
        }

        if (!command.Confirmed)
        {
            return Result.Failure<BedRetirementDto>(
                InventoryApplicationErrors.ConfirmationRequired);
        }

        string normalizedReason = InventoryManagementMutationFingerprint
            .NormalizeReason(command.Reason);
        string fingerprint = InventoryManagementMutationFingerprint
            .ComputeBedRetirementCancellation(
                command.PropertyId,
                command.TopologyChangeId,
                command.ExpectedVersion,
                normalizedReason);
        await selectionFence.AcquireAsync(command.PropertyId, cancellationToken)
            .ConfigureAwait(false);
        await mutations.AcquireBedRetirementAsync(
                command.TopologyChangeId,
                cancellationToken)
            .ConfigureAwait(false);
        InventoryManagementReplayDecision<
            InventoryRetirementOperationPointer> replay = await journal
                .InspectBedRetirementCancellationAsync(
                    command.PropertyId,
                    command.TopologyChangeId,
                    command.OperationId,
                    command.ExpectedVersion,
                    fingerprint,
                    cancellationToken)
                .ConfigureAwait(false);
        if (replay.Exists)
        {
            return await this.ReplayAsync(replay, cancellationToken)
                .ConfigureAwait(false);
        }

        BedRetirementProcess? process = await retirements.GetAsync(
            command.PropertyId,
            command.TopologyChangeId,
            cancellationToken).ConfigureAwait(false);
        if (process is null)
        {
            return Result.Failure<BedRetirementDto>(
                InventoryApplicationErrors.BedRetirementNotFound);
        }

        await mutations.AcquireRoomAsync(process.RoomId, cancellationToken)
            .ConfigureAwait(false);
        DateTimeOffset nowUtc = clock.UtcNow;
        Result canceled = process.Cancel(
            command.ExpectedVersion,
            normalizedReason,
            command.CanceledBy,
            nowUtc);
        if (canceled.IsFailure)
        {
            return Result.Failure<BedRetirementDto>(canceled.Error);
        }

        await selectionFence.AdvanceAsync(command.PropertyId, cancellationToken)
            .ConfigureAwait(false);

        await definitions.PublishRoomAsync(
            process.PropertyId,
            process.RoomId,
            nowUtc,
            cancellationToken).ConfigureAwait(false);
        await journal.RecordBedRetirementCancellationAsync(
                process,
                command.OperationId,
                command.ExpectedVersion,
                fingerprint,
                nowUtc,
                cancellationToken)
            .ConfigureAwait(false);
        return Result.Success(await coordinator.GetDtoAsync(process, cancellationToken)
            .ConfigureAwait(false));
    }

    private async Task<Result<BedRetirementDto>> ReplayAsync(
        InventoryManagementReplayDecision<InventoryRetirementOperationPointer> replay,
        CancellationToken cancellationToken)
    {
        Result<InventoryRetirementOperationPointer> pointer = replay.ToResult();
        if (pointer.IsFailure)
        {
            return Result.Failure<BedRetirementDto>(pointer.Error);
        }

        BedRetirementProcess? process = await retirements.GetAsync(
            pointer.Value.PropertyId,
            pointer.Value.TopologyChangeId,
            cancellationToken).ConfigureAwait(false);
        if (process is null)
        {
            throw new InvalidDataException(
                "A committed bed-retirement cancellation references a missing process.");
        }

        return Result.Success(await coordinator.GetDtoAsync(process, cancellationToken)
            .ConfigureAwait(false));
    }
}
