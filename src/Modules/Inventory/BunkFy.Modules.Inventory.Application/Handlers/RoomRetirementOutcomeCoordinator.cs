namespace BunkFy.Modules.Inventory.Application.Handlers;

using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using Gma.Framework.Results;
using Gma.Framework.Runtime.Time;

internal sealed class RoomRetirementOutcomeCoordinator(
    InventoryManagementMutationCoordinator mutations,
    IRoomRetirementRepository retirements,
    ISystemClock clock)
{
    public async Task<bool> CompleteFromTopologyAsync(
        Guid propertyId,
        Guid roomId,
        CancellationToken cancellationToken)
    {
        Guid? topologyChangeId = await retirements
            .GetTopologyChangeIdByRoomAsync(propertyId, roomId, cancellationToken)
            .ConfigureAwait(false);
        if (!topologyChangeId.HasValue)
        {
            return false;
        }

        RoomRetirementProcess process = await this.AcquireRequiredAsync(
            propertyId,
            topologyChangeId.Value,
            roomId,
            "Room-retirement topology completion does not match a durable Inventory process.",
            cancellationToken).ConfigureAwait(false);
        InventoryRetirementProcessState stateBefore = process.State;
        EnsureSucceeded(
            process.Complete(clock.UtcNow),
            "Room-retirement topology completion");
        return process.State != stateBefore;
    }

    public async Task MarkFinalizedAsync(
        Guid propertyId,
        Guid topologyChangeId,
        Guid roomId,
        CancellationToken cancellationToken)
    {
        RoomRetirementProcess process = await this.AcquireRequiredAsync(
            propertyId,
            topologyChangeId,
            roomId,
            "Room-retirement completion does not match a durable Inventory process.",
            cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(process.MarkFinalized(clock.UtcNow), "Room-retirement completion");
    }

    public async Task RejectAsync(
        Guid propertyId,
        Guid topologyChangeId,
        Guid roomId,
        int reasonCode,
        CancellationToken cancellationToken)
    {
        RoomRetirementProcess process = await this.AcquireRequiredAsync(
            propertyId,
            topologyChangeId,
            roomId,
            "Room-retirement rejection does not match a durable Inventory process.",
            cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(process.Reject(reasonCode, clock.UtcNow), "Room-retirement rejection");
    }

    private async Task<RoomRetirementProcess> AcquireRequiredAsync(
        Guid propertyId,
        Guid topologyChangeId,
        Guid roomId,
        string mismatchMessage,
        CancellationToken cancellationToken)
    {
        await mutations.AcquireRoomRetirementAsync(topologyChangeId, cancellationToken)
            .ConfigureAwait(false);
        RoomRetirementProcess? process = await retirements
            .GetAsync(propertyId, topologyChangeId, cancellationToken)
            .ConfigureAwait(false);
        if (process is null || process.RoomId != roomId)
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
