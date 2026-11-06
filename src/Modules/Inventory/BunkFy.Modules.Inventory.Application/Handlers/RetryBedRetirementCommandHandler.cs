namespace BunkFy.Modules.Inventory.Application.Handlers;

using BunkFy.Modules.Inventory.Application.Commands;
using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;

internal sealed class RetryBedRetirementCommandHandler(
    InventoryManagementMutationCoordinator mutations,
    InventoryManagementOperationJournal journal,
    IBedRetirementRepository retirements,
    IInventoryAvailabilityRepository availability,
    IInventoryBusinessDateProvider businessDates,
    BedRetirementCoordinator coordinator,
    ISystemClock clock,
    IIdGenerator idGenerator)
    : ICommandHandler<RetryBedRetirementCommand, BedRetirementDto>
{
    public async Task<Result<BedRetirementDto>> HandleAsync(
        RetryBedRetirementCommand command,
        CancellationToken cancellationToken)
    {
        if (command.OperationId == Guid.Empty)
        {
            return Result.Failure<BedRetirementDto>(
                InventoryApplicationErrors.ManagementOperationInvalid);
        }

        string fingerprint = InventoryManagementMutationFingerprint
            .ComputeBedRetirementRetry(
                command.PropertyId,
                command.TopologyChangeId,
                command.ExpectedVersion);
        await mutations.AcquireBedRetirementAsync(
                command.TopologyChangeId,
                cancellationToken)
            .ConfigureAwait(false);
        InventoryManagementReplayDecision<
            InventoryRetirementOperationPointer> replay = await journal
                .InspectBedRetirementRetryAsync(
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

        BedRetirementProcess? process = await retirements
            .GetAsync(command.PropertyId, command.TopologyChangeId, cancellationToken)
            .ConfigureAwait(false);
        if (process is null)
        {
            return Result.Failure<BedRetirementDto>(InventoryApplicationErrors.BedRetirementNotFound);
        }

        await mutations.AcquireRoomAsync(process.RoomId, cancellationToken)
            .ConfigureAwait(false);
        if (process.Version != command.ExpectedVersion)
        {
            return Result.Failure<BedRetirementDto>(
                InventoryApplicationErrors.VersionConflict);
        }

        if (process.State != InventoryRetirementProcessState.Rejected)
        {
            return Result.Failure<BedRetirementDto>(InventoryApplicationErrors.BedRetirementRetryInvalid);
        }

        DateOnly? businessDate = await businessDates.GetAsync(
            command.PropertyId,
            clock.UtcNow,
            cancellationToken).ConfigureAwait(false);
        if (!businessDate.HasValue)
        {
            return Result.Failure<BedRetirementDto>(InventoryApplicationErrors.PropertyNotFound);
        }

        BedRetirementImpactSnapshot? impact = await availability.GetBedRetirementImpactAsync(
            process.PropertyId,
            process.RoomId,
            process.BedId,
            businessDate.Value,
            excludedAllocationId: null,
            excludedBlockIds: [],
            cancellationToken).ConfigureAwait(false);
        if (impact is null)
        {
            return Result.Failure<BedRetirementDto>(InventoryApplicationErrors.InventoryUnitNotFound);
        }

        if (impact.HasActiveClaims)
        {
            return Result.Failure<BedRetirementDto>(InventoryApplicationErrors.BedRetirementStillDraining);
        }

        DateTimeOffset nowUtc = clock.UtcNow;
        Result requested = process.RequestFinalization(
            idGenerator.NewId(),
            nowUtc);
        if (requested.IsFailure)
        {
            return Result.Failure<BedRetirementDto>(requested.Error);
        }

        await journal.RecordBedRetirementRetryAsync(
                process,
                command.OperationId,
                command.ExpectedVersion,
                fingerprint,
                nowUtc,
                cancellationToken)
            .ConfigureAwait(false);
        return Result.Success(process.ToDto(impact));
    }

    private async Task<Result<BedRetirementDto>> ReplayAsync(
        InventoryManagementReplayDecision<
            InventoryRetirementOperationPointer> replay,
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
                "A committed bed-retirement retry references a missing process.");
        }

        return Result.Success(await coordinator
            .GetDtoAsync(process, cancellationToken)
            .ConfigureAwait(false));
    }
}
