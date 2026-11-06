namespace BunkFy.Modules.Inventory.Application.Handlers;

using System.Data.Common;
using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Application.Queries;
using BunkFy.Modules.Inventory.Contracts;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Gma.Framework.Scoping;

internal sealed class GetInventoryRetirementContextQueryHandler(
    IInventoryRetirementContextReadRepository contexts,
    IInventoryAvailabilityRepository availability,
    IScopeContext scopeContext)
    : IQueryHandler<GetInventoryRetirementContextQuery, InventoryRetirementContextDto>
{
    public async Task<Result<InventoryRetirementContextDto>> HandleAsync(
        GetInventoryRetirementContextQuery query,
        CancellationToken cancellationToken)
    {
        if (!scopeContext.IsEnabled || string.IsNullOrWhiteSpace(scopeContext.ScopeId))
        {
            return Result.Failure<InventoryRetirementContextDto>(InventoryApplicationErrors.TenantRequired);
        }

        InventoryRetirementTargetSnapshot? target = await contexts.GetTargetAsync(
            query.PropertyId, query.RoomId, query.BedId, cancellationToken).ConfigureAwait(false);
        if (target is null)
        {
            return Result.Failure<InventoryRetirementContextDto>(InventoryApplicationErrors.InventoryUnitNotFound);
        }

        InventoryRetirementImpactDto? impact = null;
        try
        {
            if (query.BedId is { } bedId)
            {
                BedRetirementImpactSnapshot? snapshot = await availability.GetBedRetirementImpactAsync(
                    query.PropertyId, query.RoomId, bedId, null, [], cancellationToken).ConfigureAwait(false);
                if (snapshot is not null)
                {
                    impact = new(snapshot.ActiveAllocationCount, snapshot.ActiveManualBlockCount, 0,
                        target.ParentRoomRetirementActive, snapshot.AffectedReservationIds,
                        snapshot.AffectedReservationIdsTruncated);
                }
            }
            else
            {
                RoomInventoryImpactSnapshot? snapshot = await availability.GetRoomImpactAsync(
                    query.PropertyId, query.RoomId, cancellationToken).ConfigureAwait(false);
                if (snapshot is not null)
                {
                    impact = new(snapshot.ActiveAllocationCount, snapshot.ActiveManualBlockCount,
                        snapshot.ActiveBedRetirementCount, false, snapshot.AffectedReservationIds,
                        snapshot.AffectedReservationIdsTruncated);
                }
            }
        }
        catch (DbException exception) when (exception.IsTransient && !cancellationToken.IsCancellationRequested)
        {
            // Only a recognized transient impact-source failure is a partial read.
            // Never log protected process/claim values or replace impact with zero.
        }
        catch (TimeoutException) when (!cancellationToken.IsCancellationRequested)
        {
            // Target and lifecycle were read independently before the timeout.
        }

        cancellationToken.ThrowIfCancellationRequested();
        InventoryRetirementStatus? status = target.Process?.Status;
        bool available = impact is not null;
        bool noClaims = impact is
        {
            ActiveAllocationCount: 0,
            ActiveManualBlockCount: 0,
            ActiveBedRetirementCount: 0,
            ParentRoomRetirementActive: false
        };
        return Result.Success(new InventoryRetirementContextDto(
            query.PropertyId, query.RoomId, query.BedId, target.IsTopologyActive, target.Process,
            impact, available ? InventoryRetirementImpactStatus.Available : InventoryRetirementImpactStatus.Unavailable,
            available && target.IsTopologyActive && (status is null or InventoryRetirementStatus.Canceled) &&
                impact is { ActiveBedRetirementCount: 0, ParentRoomRetirementActive: false },
            available && status == InventoryRetirementStatus.Rejected && noClaims,
            available && status == InventoryRetirementStatus.Draining));
    }
}
