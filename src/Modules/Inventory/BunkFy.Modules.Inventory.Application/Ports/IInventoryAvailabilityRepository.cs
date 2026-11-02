namespace BunkFy.Modules.Inventory.Application.Ports;

using BunkFy.Modules.Inventory.Domain.Aggregates;

public interface IInventoryAvailabilityRepository
{
    Task<InventoryAvailabilityContextSnapshot> GetContextAsync(
        Guid propertyId,
        IReadOnlyCollection<Guid> inventoryUnitIds,
        CancellationToken cancellationToken);

    Task<InventoryAvailabilityConflictSnapshot> GetConflictsAsync(
        Guid propertyId,
        IReadOnlyCollection<Guid> conflictUnitIds,
        DateOnly arrival,
        DateOnly departure,
        Guid? excludedAllocationId,
        IReadOnlyCollection<Guid> excludedBlockIds,
        CancellationToken cancellationToken);

    Task<RoomInventoryImpactSnapshot?> GetRoomImpactAsync(
        Guid propertyId,
        Guid roomId,
        CancellationToken cancellationToken) => throw new NotSupportedException(
            "Call the property-business-date-aware room impact overload.");

    Task<RoomInventoryImpactSnapshot?> GetRoomImpactAsync(
        Guid propertyId,
        Guid roomId,
        DateOnly queryDate,
        CancellationToken cancellationToken) =>
        this.GetRoomImpactAsync(propertyId, roomId, cancellationToken);

    Task<RoomInventoryImpactSnapshot?> GetRoomImpactAsync(
        Guid propertyId,
        Guid roomId,
        DateOnly queryDate,
        Guid? excludedAllocationId,
        IReadOnlyCollection<Guid> excludedBlockIds,
        CancellationToken cancellationToken) =>
        this.GetRoomImpactAsync(propertyId, roomId, queryDate, cancellationToken);

    Task<BedRetirementImpactSnapshot?> GetBedRetirementImpactAsync(
        Guid propertyId,
        Guid roomId,
        Guid bedId,
        Guid? excludedAllocationId,
        IReadOnlyCollection<Guid> excludedBlockIds,
        CancellationToken cancellationToken) => throw new NotSupportedException(
            "Call the property-business-date-aware bed impact overload.");

    Task<BedRetirementImpactSnapshot?> GetBedRetirementImpactAsync(
        Guid propertyId,
        Guid roomId,
        Guid bedId,
        DateOnly queryDate,
        Guid? excludedAllocationId,
        IReadOnlyCollection<Guid> excludedBlockIds,
        CancellationToken cancellationToken) => this.GetBedRetirementImpactAsync(
            propertyId,
            roomId,
            bedId,
            excludedAllocationId,
            excludedBlockIds,
            cancellationToken);

    Task TouchUnitsAsync(
        Guid propertyId,
        IReadOnlyCollection<Guid> inventoryUnitIds,
        CancellationToken cancellationToken);
}
