namespace BunkFy.Modules.Inventory.Application.Handlers;

using BunkFy.Modules.Inventory.Application.Commands;
using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;

internal sealed class RequestBedRetirementCommandHandler(
    InventoryManagementMutationCoordinator mutations,
    InventoryManagementOperationJournal journal,
    IInventoryReadRepository inventory,
    IBedRetirementRepository retirements,
    IRoomRetirementRepository roomRetirements,
    IInventoryAvailabilityRepository availability,
    IInventoryAvailabilitySelectionFence selectionFence,
    BedRetirementCoordinator coordinator,
    InventoryUnitDefinitionPublisher definitions,
    IScopeContext scopeContext,
    ISystemClock clock,
    IIdGenerator idGenerator)
    : ICommandHandler<RequestBedRetirementCommand, BedRetirementDto>
{
    public async Task<Result<BedRetirementDto>> HandleAsync(
        RequestBedRetirementCommand command,
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

        string? scopeId = scopeContext.ScopeId;
        if (!scopeContext.IsEnabled || string.IsNullOrWhiteSpace(scopeId))
        {
            return Result.Failure<BedRetirementDto>(InventoryApplicationErrors.TenantRequired);
        }

        string normalizedReason = InventoryManagementMutationFingerprint
            .NormalizeReason(command.Reason);
        string fingerprint = InventoryManagementMutationFingerprint
            .ComputeBedRetirementRequest(
                command.PropertyId,
                command.RoomId,
                command.BedId,
                normalizedReason);
        await selectionFence.AcquireAsync(command.PropertyId, cancellationToken)
            .ConfigureAwait(false);
        await mutations.AcquireRoomAsync(command.RoomId, cancellationToken)
            .ConfigureAwait(false);
        InventoryManagementReplayDecision<
            InventoryRetirementOperationPointer> replay = await journal
                .InspectBedRetirementRequestAsync(
                    command.PropertyId,
                    command.BedId,
                    command.OperationId,
                    fingerprint,
                    cancellationToken)
                .ConfigureAwait(false);
        if (replay.Exists)
        {
            return await this.ReplayAsync(replay, cancellationToken)
                .ConfigureAwait(false);
        }

        BedRetirementProcess? existing = await retirements
            .GetByBedAsync(command.PropertyId, command.BedId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.RoomId != command.RoomId)
            {
                return Result.Failure<BedRetirementDto>(
                    InventoryApplicationErrors.InventoryUnitNotFound);
            }

            if (!string.Equals(
                    existing.Reason,
                    normalizedReason,
                    StringComparison.Ordinal))
            {
                return Result.Failure<BedRetirementDto>(
                    InventoryApplicationErrors.RetirementRequestConflict);
            }

            await journal.RecordBedRetirementRequestAsync(
                    existing,
                    command.OperationId,
                    fingerprint,
                    clock.UtcNow,
                    cancellationToken)
                .ConfigureAwait(false);
            return Result.Success(await coordinator
                .GetDtoAsync(existing, cancellationToken)
                .ConfigureAwait(false));
        }

        InventoryUnitSnapshot? unit = await inventory.GetUnitAsync(
            command.PropertyId,
            command.BedId,
            cancellationToken).ConfigureAwait(false);
        if (unit is null ||
            unit.Unit.RoomId != command.RoomId ||
            unit.Unit.Kind != InventoryUnitKind.Bed)
        {
            return Result.Failure<BedRetirementDto>(
                InventoryApplicationErrors.InventoryUnitNotFound);
        }

        if (!unit.Unit.IsTopologyActive)
        {
            return Result.Failure<BedRetirementDto>(InventoryApplicationErrors.InventoryUnitInactive);
        }

        RoomRetirementProcess? roomRetirement = await roomRetirements
            .GetByRoomAsync(command.PropertyId, command.RoomId, cancellationToken)
            .ConfigureAwait(false);
        if (roomRetirement is not null && RoomRetirementProcess.IsDrainActive(roomRetirement.State))
        {
            return Result.Failure<BedRetirementDto>(InventoryApplicationErrors.RoomRetirementInProgress);
        }

        DateTimeOffset nowUtc = clock.UtcNow;
        Result<BedRetirementProcess> created = BedRetirementProcess.Create(
            idGenerator.NewId(),
            scopeId,
            command.PropertyId,
            command.RoomId,
            command.BedId,
            normalizedReason,
            command.RequestedBy,
            nowUtc);
        if (created.IsFailure)
        {
            return Result.Failure<BedRetirementDto>(created.Error);
        }

        await retirements.AddAsync(created.Value, cancellationToken).ConfigureAwait(false);
        await availability.TouchUnitsAsync(
            command.PropertyId,
            [command.BedId],
            cancellationToken).ConfigureAwait(false);
        await selectionFence.AdvanceAsync(command.PropertyId, cancellationToken)
            .ConfigureAwait(false);
        BedRetirementDto result = await coordinator.TryAdvanceAsync(
            created.Value,
            excludedAllocationId: null,
            excludedBlockIds: [],
            cancellationToken).ConfigureAwait(false);
        await definitions.PublishRoomAsync(
            command.PropertyId,
            command.RoomId,
            clock.UtcNow,
            cancellationToken).ConfigureAwait(false);
        await journal.RecordBedRetirementRequestAsync(
                created.Value,
                command.OperationId,
                fingerprint,
                nowUtc,
                cancellationToken)
            .ConfigureAwait(false);
        return Result.Success(result);
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
                "A committed bed-retirement operation references a missing process.");
        }

        return Result.Success(await coordinator
            .GetDtoAsync(process, cancellationToken)
            .ConfigureAwait(false));
    }
}
