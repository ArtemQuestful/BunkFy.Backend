namespace BunkFy.Modules.Inventory.Persistence.Repositories;

using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using BunkFy.Modules.Properties.Contracts;
using Microsoft.EntityFrameworkCore;

internal sealed class InventoryRetirementContextReadRepository(InventoryDbContext dbContext)
    : IInventoryRetirementContextReadRepository
{
    private static readonly InventoryRetirementProcessState[] ActiveStates =
    [
        InventoryRetirementProcessState.Draining,
        InventoryRetirementProcessState.FinalizationRequested,
        InventoryRetirementProcessState.FinalizedAwaitingTopology,
        InventoryRetirementProcessState.Rejected
    ];

    public async Task<InventoryRetirementTargetSnapshot?> GetTargetAsync(
        Guid propertyId, Guid roomId, Guid? bedId, CancellationToken cancellationToken)
    {
        if (!dbContext.ScopeFilterEnabled || string.IsNullOrWhiteSpace(dbContext.CurrentScopeId))
        {
            throw new InvalidOperationException("Retirement context reads require an active tenant scope.");
        }

        InventoryPropertyTopology? property = await dbContext.PropertyTopology.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == propertyId && item.IsKnown, cancellationToken)
            .ConfigureAwait(false);
        InventoryRoomTopology? room = await dbContext.RoomTopology.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == roomId && item.PropertyId == propertyId && item.IsKnown,
                cancellationToken).ConfigureAwait(false);
        Guid unitId = bedId ?? roomId;
        InventoryUnitKind kind = bedId.HasValue ? InventoryUnitKind.Bed : InventoryUnitKind.Room;
        InventoryUnit? unit = await dbContext.InventoryUnits.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == unitId && item.PropertyId == propertyId &&
                item.RoomId == roomId && item.BedId == bedId && item.Kind == kind && item.IsKnown,
                cancellationToken).ConfigureAwait(false);
        if (property is null || room is null || unit is null)
        {
            return null;
        }

        bool active = property.Status == PropertyStatus.Active &&
            room.Status == RoomStatus.Active && unit.IsTopologyActive;
        if (bedId is { } exactBedId)
        {
            BedRetirementProcess? process = await this.BedProcessQuery(propertyId, roomId, exactBedId)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            bool parentActive = await dbContext.RoomRetirements.AsNoTracking().AnyAsync(
                item => item.PropertyId == propertyId && item.RoomId == roomId && ActiveStates.Contains(item.State),
                cancellationToken).ConfigureAwait(false);
            return new(active, process is null ? null : new(
                process.Id, (InventoryRetirementStatus)process.State, process.Version, process.Reason,
                process.RequestedBy, process.RejectionReasonCode, process.CancellationReason, process.CanceledBy,
                process.CreatedAtUtc, process.UpdatedAtUtc, process.CompletedAtUtc, process.CanceledAtUtc), parentActive);
        }

        RoomRetirementProcess? roomProcess = await this.RoomProcessQuery(propertyId, roomId)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return new(active, roomProcess is null ? null : new(
            roomProcess.Id, (InventoryRetirementStatus)roomProcess.State, roomProcess.Version, roomProcess.Reason,
            roomProcess.RequestedBy, roomProcess.RejectionReasonCode, roomProcess.CancellationReason, roomProcess.CanceledBy,
            roomProcess.CreatedAtUtc, roomProcess.UpdatedAtUtc, roomProcess.CompletedAtUtc, roomProcess.CanceledAtUtc), false);
    }

    // Dedicated read queries never reuse or change tracked write-side active lookups.
    internal IQueryable<RoomRetirementProcess> RoomProcessQuery(Guid propertyId, Guid roomId) =>
        dbContext.RoomRetirements.AsNoTracking()
            .Where(process => process.PropertyId == propertyId && process.RoomId == roomId)
            .OrderByDescending(process => ActiveStates.Contains(process.State))
            .ThenByDescending(process => process.CreatedAtUtc)
            .ThenByDescending(process => process.Id);

    internal IQueryable<BedRetirementProcess> BedProcessQuery(Guid propertyId, Guid roomId, Guid bedId) =>
        dbContext.BedRetirements.AsNoTracking()
            .Where(process => process.PropertyId == propertyId && process.RoomId == roomId && process.BedId == bedId)
            .OrderByDescending(process => ActiveStates.Contains(process.State))
            .ThenByDescending(process => process.CreatedAtUtc)
            .ThenByDescending(process => process.Id);
}
