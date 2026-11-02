namespace BunkFy.Modules.Inventory.Application.Handlers;

using BunkFy.Modules.Inventory.Application.Commands;
using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using BunkFy.Modules.Inventory.Domain.Errors;
using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;

internal sealed class ConfigureRoomSalesModeCommandHandler(
    InventoryManagementMutationCoordinator mutations,
    InventoryManagementOperationJournal journal,
    IInventoryTopologyRepository topologyRepository,
    IRoomInventoryConfigurationRepository configurationRepository,
    IInventoryAvailabilityRepository availability,
    IInventoryAvailabilitySelectionFence selectionFence,
    IInventoryBusinessDateProvider businessDates,
    ISystemClock clock,
    IIdGenerator idGenerator)
    : ICommandHandler<ConfigureRoomSalesModeCommand, RoomInventoryMutationReceiptDto>
{
    public async Task<Result<RoomInventoryMutationReceiptDto>> HandleAsync(
        ConfigureRoomSalesModeCommand command,
        CancellationToken cancellationToken)
    {
        if (command.OperationId == Guid.Empty)
        {
            return Result.Failure<RoomInventoryMutationReceiptDto>(
                InventoryApplicationErrors.ManagementOperationInvalid);
        }

        RoomSalesMode salesMode = command.SalesMode switch
        {
            InventorySalesMode.RoomLevel => RoomSalesMode.RoomLevel,
            InventorySalesMode.BedLevel => RoomSalesMode.BedLevel,
            _ => RoomSalesMode.Unconfigured
        };
        if (salesMode == RoomSalesMode.Unconfigured)
        {
            return Result.Failure<RoomInventoryMutationReceiptDto>(
                InventoryDomainErrors.SalesModeInvalid);
        }

        string fingerprint =
            InventoryManagementMutationFingerprint.ComputeRoomSalesMode(
                command.PropertyId,
                command.RoomId,
                command.ExpectedVersion,
                command.SalesMode);
        await selectionFence.AcquireAsync(command.PropertyId, cancellationToken)
            .ConfigureAwait(false);
        await mutations.AcquireRoomAsync(command.RoomId, cancellationToken)
            .ConfigureAwait(false);
        InventoryManagementReplayDecision<RoomInventoryMutationReceiptDto>
            replay = await journal
            .InspectRoomAsync(
                command.PropertyId,
                command.RoomId,
                command.OperationId,
                command.ExpectedVersion,
                fingerprint,
                cancellationToken).ConfigureAwait(false);
        if (replay.Exists)
        {
            return replay.ToResult();
        }

        InventoryRoomTopologySnapshot? topology = await topologyRepository
            .GetRoomAsync(command.PropertyId, command.RoomId, cancellationToken)
            .ConfigureAwait(false);
        if (topology is null)
        {
            return Result.Failure<RoomInventoryMutationReceiptDto>(InventoryDomainErrors.RoomNotFound);
        }

        if (topology.Status == RoomStatus.Retired)
        {
            return Result.Failure<RoomInventoryMutationReceiptDto>(InventoryDomainErrors.RoomRetired);
        }

        if (command.SalesMode == InventorySalesMode.BedLevel && topology.ActiveBedCount == 0)
        {
            return Result.Failure<RoomInventoryMutationReceiptDto>(InventoryDomainErrors.BedLevelRequiresBeds);
        }

        RoomInventoryConfiguration? configuration = await configurationRepository
            .GetAsync(command.PropertyId, command.RoomId, cancellationToken)
            .ConfigureAwait(false);
        if (configuration is null)
        {
            return Result.Failure<RoomInventoryMutationReceiptDto>(InventoryDomainErrors.RoomNotFound);
        }

        Result<RoomSalesModeConfigurationOutcome> evaluation =
            configuration.EvaluateConfiguration(
                salesMode,
                command.ExpectedVersion);
        if (evaluation.IsFailure)
        {
            return Result.Failure<RoomInventoryMutationReceiptDto>(
                evaluation.Error);
        }

        if (evaluation.Value == RoomSalesModeConfigurationOutcome.Changed)
        {
            DateOnly? businessDate = await businessDates.GetAsync(
                command.PropertyId,
                clock.UtcNow,
                cancellationToken).ConfigureAwait(false);
            if (!businessDate.HasValue)
            {
                return Result.Failure<RoomInventoryMutationReceiptDto>(InventoryApplicationErrors.PropertyNotFound);
            }

            RoomInventoryImpactSnapshot? impact = await availability
                .GetRoomImpactAsync(command.PropertyId, command.RoomId, businessDate.Value, cancellationToken)
                .ConfigureAwait(false);
            if (impact?.PreventsSalesModeChange == true)
            {
                return Result.Failure<RoomInventoryMutationReceiptDto>(InventoryApplicationErrors.RoomHasActiveClaims);
            }
        }

        DateTimeOffset nowUtc = clock.UtcNow;
        Result result = configuration.Configure(
            salesMode,
            command.ExpectedVersion,
            evaluation.Value == RoomSalesModeConfigurationOutcome.Changed
                ? idGenerator.NewId()
                : Guid.Empty,
            nowUtc,
            command.ActorId);
        if (result.IsFailure)
        {
            return Result.Failure<RoomInventoryMutationReceiptDto>(result.Error);
        }

        if (evaluation.Value == RoomSalesModeConfigurationOutcome.Changed)
        {
            await selectionFence.AdvanceAsync(command.PropertyId, cancellationToken)
                .ConfigureAwait(false);
        }

        RoomInventoryMutationReceiptDto receipt = await journal.RecordRoomAsync(
            configuration,
            command.OperationId,
            command.ExpectedVersion,
            fingerprint,
            nowUtc,
            cancellationToken).ConfigureAwait(false);
        return Result.Success(receipt);
    }
}
