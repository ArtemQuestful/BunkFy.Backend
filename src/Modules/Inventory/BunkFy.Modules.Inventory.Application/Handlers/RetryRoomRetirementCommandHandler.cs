namespace BunkFy.Modules.Inventory.Application.Handlers;

using BunkFy.Modules.Inventory.Application.Commands;
using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;

internal sealed class RetryRoomRetirementCommandHandler(
    InventoryManagementMutationCoordinator mutations,
    InventoryManagementOperationJournal journal,
    IRoomRetirementRepository retirements,
    IInventoryAvailabilityRepository availability,
    IInventoryBusinessDateProvider businessDates,
    RoomRetirementCoordinator coordinator,
    ISystemClock clock,
    IIdGenerator idGenerator)
    : ICommandHandler<RetryRoomRetirementCommand, RoomRetirementDto>
{
    public async Task<Result<RoomRetirementDto>> HandleAsync(
        RetryRoomRetirementCommand command,
        CancellationToken cancellationToken)
    {
        if (command.OperationId == Guid.Empty)
        {
            return Result.Failure<RoomRetirementDto>(
                InventoryApplicationErrors.ManagementOperationInvalid);
        }

        string fingerprint = InventoryManagementMutationFingerprint
            .ComputeRoomRetirementRetry(
                command.PropertyId,
                command.TopologyChangeId,
                command.ExpectedVersion);
        await mutations.AcquireRoomRetirementAsync(
                command.TopologyChangeId,
                cancellationToken)
            .ConfigureAwait(false);
        InventoryManagementReplayDecision<
            InventoryRetirementOperationPointer> replay = await journal
                .InspectRoomRetirementRetryAsync(
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

        RoomRetirementProcess? process = await retirements
            .GetAsync(command.PropertyId, command.TopologyChangeId, cancellationToken)
            .ConfigureAwait(false);
        if (process is null)
        {
            return Result.Failure<RoomRetirementDto>(InventoryApplicationErrors.RoomRetirementNotFound);
        }

        await mutations.AcquireRoomAsync(process.RoomId, cancellationToken)
            .ConfigureAwait(false);
        if (process.Version != command.ExpectedVersion)
        {
            return Result.Failure<RoomRetirementDto>(
                InventoryApplicationErrors.VersionConflict);
        }

        if (process.State != InventoryRetirementProcessState.Rejected)
        {
            return Result.Failure<RoomRetirementDto>(InventoryApplicationErrors.RoomRetirementRetryInvalid);
        }

        DateOnly? businessDate = await businessDates.GetAsync(
            command.PropertyId,
            clock.UtcNow,
            cancellationToken).ConfigureAwait(false);
        if (!businessDate.HasValue)
        {
            return Result.Failure<RoomRetirementDto>(InventoryApplicationErrors.PropertyNotFound);
        }

        RoomInventoryImpactSnapshot? impact = await availability.GetRoomImpactAsync(
            process.PropertyId,
            process.RoomId,
            businessDate.Value,
            excludedAllocationId: null,
            excludedBlockIds: [],
            cancellationToken).ConfigureAwait(false);
        if (impact is null)
        {
            return Result.Failure<RoomRetirementDto>(InventoryApplicationErrors.RoomNotFound);
        }

        if (impact.PreventsRoomRetirementFinalization)
        {
            return Result.Failure<RoomRetirementDto>(InventoryApplicationErrors.RoomRetirementStillDraining);
        }

        DateTimeOffset nowUtc = clock.UtcNow;
        Result requested = process.RequestFinalization(
            idGenerator.NewId(),
            nowUtc);
        if (requested.IsFailure)
        {
            return Result.Failure<RoomRetirementDto>(requested.Error);
        }

        await journal.RecordRoomRetirementRetryAsync(
                process,
                command.OperationId,
                command.ExpectedVersion,
                fingerprint,
                nowUtc,
                cancellationToken)
            .ConfigureAwait(false);
        return Result.Success(process.ToDto(impact));
    }

    private async Task<Result<RoomRetirementDto>> ReplayAsync(
        InventoryManagementReplayDecision<
            InventoryRetirementOperationPointer> replay,
        CancellationToken cancellationToken)
    {
        Result<InventoryRetirementOperationPointer> pointer = replay.ToResult();
        if (pointer.IsFailure)
        {
            return Result.Failure<RoomRetirementDto>(pointer.Error);
        }

        RoomRetirementProcess? process = await retirements.GetAsync(
            pointer.Value.PropertyId,
            pointer.Value.TopologyChangeId,
            cancellationToken).ConfigureAwait(false);
        if (process is null)
        {
            throw new InvalidDataException(
                "A committed room-retirement retry references a missing process.");
        }

        return Result.Success(await coordinator
            .GetDtoAsync(process, cancellationToken)
            .ConfigureAwait(false));
    }
}
