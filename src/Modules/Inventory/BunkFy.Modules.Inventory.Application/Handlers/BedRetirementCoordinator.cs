namespace BunkFy.Modules.Inventory.Application.Handlers;

using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Identity;
using Gma.Framework.Runtime.Time;

internal sealed class BedRetirementCoordinator(
    InventoryManagementMutationCoordinator mutations,
    IBedRetirementRepository retirements,
    IInventoryAvailabilityRepository availability,
    IInventoryBusinessDateProvider businessDates,
    ISystemClock clock,
    IIdGenerator idGenerator)
{
    public async Task<BedRetirementDto> TryAdvanceAsync(
        BedRetirementProcess process,
        Guid? excludedAllocationId,
        IReadOnlyCollection<Guid> excludedBlockIds,
        CancellationToken cancellationToken)
    {
        BedRetirementImpactSnapshot impact = await this.GetImpactAsync(
            process,
            excludedAllocationId,
            excludedBlockIds,
            cancellationToken).ConfigureAwait(false);
        if (process.State == InventoryRetirementProcessState.Draining && !impact.HasActiveClaims)
        {
            Result requested = process.RequestFinalization(idGenerator.NewId(), clock.UtcNow);
            if (requested.IsFailure)
            {
                throw new InvalidOperationException(
                    $"Validated bed-retirement advancement failed with '{requested.Error.Code}'.");
            }
        }

        return process.ToDto(impact);
    }

    public async Task TryAdvanceForUnitsAsync(
        Guid propertyId,
        IReadOnlyCollection<Guid> inventoryUnitIds,
        Guid? excludedAllocationId,
        IReadOnlyCollection<Guid> excludedBlockIds,
        CancellationToken cancellationToken)
    {
        IReadOnlyCollection<BedRetirementProcess> processes = await retirements
            .ListActiveForUnitsAsync(propertyId, inventoryUnitIds, cancellationToken)
            .ConfigureAwait(false);
        foreach (BedRetirementProcess process in processes.Where(item => item.State == InventoryRetirementProcessState.Draining))
        {
            await mutations.AcquireBedRetirementAsync(process.Id, cancellationToken)
                .ConfigureAwait(false);
            await retirements.ReloadAsync(process, cancellationToken)
                .ConfigureAwait(false);
            if (process.State != InventoryRetirementProcessState.Draining)
            {
                continue;
            }

            await this.TryAdvanceAsync(
                process,
                excludedAllocationId,
                excludedBlockIds,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<BedRetirementDto> GetDtoAsync(
        BedRetirementProcess process,
        CancellationToken cancellationToken) =>
        process.ToDto(await this.GetImpactAsync(
            process,
            excludedAllocationId: null,
            excludedBlockIds: [],
            cancellationToken).ConfigureAwait(false));

    private async Task<BedRetirementImpactSnapshot> GetImpactAsync(
        BedRetirementProcess process,
        Guid? excludedAllocationId,
        IReadOnlyCollection<Guid> excludedBlockIds,
        CancellationToken cancellationToken)
    {
        DateTimeOffset nowUtc = clock.UtcNow;
        DateOnly queryDate = await businessDates.GetAsync(
                process.PropertyId,
                nowUtc,
                cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException(
                "A bed-retirement process references Inventory topology without a valid property business date.");
        return await availability.GetBedRetirementImpactAsync(
            process.PropertyId,
            process.RoomId,
            process.BedId,
            queryDate,
            excludedAllocationId,
            excludedBlockIds,
            cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException(
                "A bed-retirement process references missing Inventory topology.");
    }
}
