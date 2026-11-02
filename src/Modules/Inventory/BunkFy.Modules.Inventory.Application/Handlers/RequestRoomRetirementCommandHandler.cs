namespace BunkFy.Modules.Inventory.Application.Handlers;

using BunkFy.Modules.Inventory.Application.Commands;
using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;

internal sealed class RequestRoomRetirementCommandHandler(
    InventoryManagementMutationCoordinator mutations,
    InventoryManagementOperationJournal journal,
    IInventoryTopologyRepository topology,
    IInventoryReadRepository inventory,
    IRoomRetirementRepository retirements,
    IInventoryAvailabilityRepository availability,
    IInventoryAvailabilitySelectionFence selectionFence,
    IInventoryBusinessDateProvider businessDates,
    RoomRetirementCoordinator coordinator,
    InventoryUnitDefinitionPublisher definitions,
    IScopeContext scopeContext,
    ISystemClock clock,
    IIdGenerator idGenerator)
    : ICommandHandler<RequestRoomRetirementCommand, RoomRetirementDto>
{
    public async Task<Result<RoomRetirementDto>> HandleAsync(
        RequestRoomRetirementCommand command,
        CancellationToken cancellationToken)
    {
        if (command.OperationId == Guid.Empty)
        {
            return Result.Failure<RoomRetirementDto>(
                InventoryApplicationErrors.ManagementOperationInvalid);
        }

        if (!command.Confirmed)
        {
            return Result.Failure<RoomRetirementDto>(
                InventoryApplicationErrors.ConfirmationRequired);
        }

        string? scopeId = scopeContext.ScopeId;
        if (!scopeContext.IsEnabled || string.IsNullOrWhiteSpace(scopeId))
        {
            return Result.Failure<RoomRetirementDto>(InventoryApplicationErrors.TenantRequired);
        }

        string normalizedReason = InventoryManagementMutationFingerprint
            .NormalizeReason(command.Reason);
        string fingerprint = InventoryManagementMutationFingerprint
            .ComputeRoomRetirementRequest(
                command.PropertyId,
                command.RoomId,
                normalizedReason);
        await selectionFence.AcquireAsync(command.PropertyId, cancellationToken)
            .ConfigureAwait(false);
        await mutations.AcquireRoomAsync(command.RoomId, cancellationToken)
            .ConfigureAwait(false);
        InventoryManagementReplayDecision<
            InventoryRetirementOperationPointer> replay = await journal
                .InspectRoomRetirementRequestAsync(
                    command.PropertyId,
                    command.RoomId,
                    command.OperationId,
                    fingerprint,
                    cancellationToken)
                .ConfigureAwait(false);
        if (replay.Exists)
        {
            return await this.ReplayAsync(replay, cancellationToken)
                .ConfigureAwait(false);
        }

        RoomRetirementProcess? existing = await retirements
            .GetByRoomAsync(command.PropertyId, command.RoomId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (!string.Equals(
                    existing.Reason,
                    normalizedReason,
                    StringComparison.Ordinal))
            {
                return Result.Failure<RoomRetirementDto>(
                    InventoryApplicationErrors.RetirementRequestConflict);
            }

            await journal.RecordRoomRetirementRequestAsync(
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

        InventoryRoomTopologySnapshot? roomTopology = await topology
            .GetRoomAsync(command.PropertyId, command.RoomId, cancellationToken)
            .ConfigureAwait(false);
        if (roomTopology is null)
        {
            return Result.Failure<RoomRetirementDto>(InventoryApplicationErrors.RoomNotFound);
        }

        if (roomTopology.Status == RoomStatus.Retired)
        {
            return Result.Failure<RoomRetirementDto>(InventoryApplicationErrors.RoomRetired);
        }

        DateOnly? businessDate = await businessDates.GetAsync(
            command.PropertyId,
            clock.UtcNow,
            cancellationToken).ConfigureAwait(false);
        if (!businessDate.HasValue)
        {
            return Result.Failure<RoomRetirementDto>(InventoryApplicationErrors.PropertyNotFound);
        }

        RoomInventoryImpactSnapshot? impact = await availability
            .GetRoomImpactAsync(command.PropertyId, command.RoomId, businessDate.Value, cancellationToken)
            .ConfigureAwait(false);
        if (impact is null)
        {
            return Result.Failure<RoomRetirementDto>(InventoryApplicationErrors.RoomNotFound);
        }

        if (impact.ActiveBedRetirementCount > 0)
        {
            return Result.Failure<RoomRetirementDto>(InventoryApplicationErrors.BedRetirementInProgress);
        }

        RoomInventoryDto? room = await inventory
            .GetRoomAsync(command.PropertyId, command.RoomId, cancellationToken)
            .ConfigureAwait(false);
        if (room is null)
        {
            return Result.Failure<RoomRetirementDto>(InventoryApplicationErrors.RoomNotFound);
        }

        Guid[] unitIds = room.Units
            .Select(unit => unit.InventoryUnitId)
            .Distinct()
            .Order()
            .ToArray();
        if (unitIds.Length == 0)
        {
            return Result.Failure<RoomRetirementDto>(InventoryApplicationErrors.RoomNotFound);
        }

        DateTimeOffset nowUtc = clock.UtcNow;
        Result<RoomRetirementProcess> created = RoomRetirementProcess.Create(
            idGenerator.NewId(),
            scopeId,
            command.PropertyId,
            command.RoomId,
            normalizedReason,
            command.RequestedBy,
            nowUtc);
        if (created.IsFailure)
        {
            return Result.Failure<RoomRetirementDto>(created.Error);
        }

        await retirements.AddAsync(created.Value, cancellationToken).ConfigureAwait(false);
        await availability.TouchUnitsAsync(command.PropertyId, unitIds, cancellationToken).ConfigureAwait(false);
        await selectionFence.AdvanceAsync(command.PropertyId, cancellationToken)
            .ConfigureAwait(false);
        RoomRetirementDto result = await coordinator.TryAdvanceAsync(
            created.Value,
            excludedAllocationId: null,
            excludedBlockIds: [],
            cancellationToken).ConfigureAwait(false);
        await definitions.PublishRoomAsync(
            command.PropertyId,
            command.RoomId,
            clock.UtcNow,
            cancellationToken).ConfigureAwait(false);
        await journal.RecordRoomRetirementRequestAsync(
                created.Value,
                command.OperationId,
                fingerprint,
                nowUtc,
                cancellationToken)
            .ConfigureAwait(false);
        return Result.Success(result);
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
                "A committed room-retirement operation references a missing process.");
        }

        return Result.Success(await coordinator
            .GetDtoAsync(process, cancellationToken)
            .ConfigureAwait(false));
    }
}
