namespace BunkFy.Modules.Inventory.Application.Handlers;

using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;

internal sealed class BedRetirementOutcomeCoordinator(
    InventoryManagementMutationCoordinator mutations,
    IBedRetirementRepository retirements,
    ISystemClock clock)
{
    public async Task<bool> CompleteFromTopologyAsync(
        Guid propertyId,
        Guid roomId,
        Guid bedId,
        CancellationToken cancellationToken)
    {
        Guid? topologyChangeId = await retirements
            .GetTopologyChangeIdByBedAsync(propertyId, bedId, cancellationToken)
            .ConfigureAwait(false);
        if (!topologyChangeId.HasValue)
        {
            return false;
        }

        BedRetirementProcess process = await this.AcquireRequiredAsync(
            propertyId,
            topologyChangeId.Value,
            roomId,
            bedId,
            "Bed-retirement topology completion does not match a durable Inventory process.",
            cancellationToken).ConfigureAwait(false);
        InventoryRetirementProcessState stateBefore = process.State;
        EnsureSucceeded(
            process.Complete(clock.UtcNow),
            "Bed-retirement topology completion");
        return process.State != stateBefore;
    }

    public async Task MarkFinalizedAsync(
        Guid propertyId,
        Guid topologyChangeId,
        Guid roomId,
        Guid bedId,
        CancellationToken cancellationToken)
    {
        BedRetirementProcess process = await this.AcquireRequiredAsync(
            propertyId,
            topologyChangeId,
            roomId,
            bedId,
            "Bed-retirement completion does not match a durable Inventory process.",
            cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(process.MarkFinalized(clock.UtcNow), "Bed-retirement completion");
    }

    public async Task RejectAsync(
        Guid propertyId,
        Guid topologyChangeId,
        Guid roomId,
        Guid bedId,
        int reasonCode,
        CancellationToken cancellationToken)
    {
        BedRetirementProcess process = await this.AcquireRequiredAsync(
            propertyId,
            topologyChangeId,
            roomId,
            bedId,
            "Bed-retirement rejection does not match a durable Inventory process.",
            cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(process.Reject(reasonCode, clock.UtcNow), "Bed-retirement rejection");
    }

    private async Task<BedRetirementProcess> AcquireRequiredAsync(
        Guid propertyId,
        Guid topologyChangeId,
        Guid roomId,
        Guid bedId,
        string mismatchMessage,
        CancellationToken cancellationToken)
    {
        await mutations.AcquireBedRetirementAsync(topologyChangeId, cancellationToken)
            .ConfigureAwait(false);
        BedRetirementProcess? process = await retirements
            .GetAsync(propertyId, topologyChangeId, cancellationToken)
            .ConfigureAwait(false);
        if (process is null || process.RoomId != roomId || process.BedId != bedId)
        {
            throw new InvalidOperationException(mismatchMessage);
        }

        return process;
    }

    private static void EnsureSucceeded(Result result, string operation)
    {
        if (result.IsFailure)
        {
            throw new InvalidOperationException(
                $"{operation} failed with '{result.Error.Code}'.");
        }
    }
}
