namespace BunkFy.Modules.Inventory.Application.Handlers;

using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Application.Queries;
using BunkFy.Modules.Inventory.Contracts;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;

internal sealed class GetRoomInventoryChangeImpactQueryHandler(
    IInventoryAvailabilityRepository availability,
    IInventoryBusinessDateProvider businessDates,
    Gma.Framework.Runtime.Time.ISystemClock clock)
    : IQueryHandler<GetRoomInventoryChangeImpactQuery, RoomInventoryChangeImpactDto>
{
    public async Task<Result<RoomInventoryChangeImpactDto>> HandleAsync(
        GetRoomInventoryChangeImpactQuery query,
        CancellationToken cancellationToken)
    {
        DateOnly? businessDate = await businessDates.GetAsync(
            query.PropertyId,
            clock.UtcNow,
            cancellationToken).ConfigureAwait(false);
        if (!businessDate.HasValue)
        {
            return Result.Failure<RoomInventoryChangeImpactDto>(InventoryApplicationErrors.PropertyNotFound);
        }

        RoomInventoryImpactSnapshot? impact = await availability
            .GetRoomImpactAsync(query.PropertyId, query.RoomId, businessDate.Value, cancellationToken)
            .ConfigureAwait(false);
        return impact is null
            ? Result.Failure<RoomInventoryChangeImpactDto>(InventoryApplicationErrors.RoomNotFound)
            : Result.Success(new RoomInventoryChangeImpactDto(
                query.PropertyId,
                query.RoomId,
                impact.ActiveAllocationCount,
                impact.ActiveManualBlockCount,
                impact.ActiveBedRetirementCount,
                impact.ActiveRoomRetirementCount,
                impact.AffectedReservationIds,
                impact.AffectedReservationIdsTruncated,
                !impact.PreventsSalesModeChange));
    }
}
