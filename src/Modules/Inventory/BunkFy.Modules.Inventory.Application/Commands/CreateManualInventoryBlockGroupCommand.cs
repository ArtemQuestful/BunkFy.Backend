namespace BunkFy.Modules.Inventory.Application.Commands;

using BunkFy.Modules.Inventory.Contracts;
using Gma.Framework.Cqrs;

public sealed record CreateManualInventoryBlockGroupCommand(
    Guid OperationId,
    Guid PropertyId,
    InventoryBlockTarget Target,
    DateOnly Arrival,
    DateOnly Departure,
    string Reason,
    string ExpectedSelectionDigest,
    int ExpectedAffectedBlockCount,
    bool Confirmed,
    string? ActorId = null)
    : ITransactionalCommand<ManualInventoryBlockGroupMutationReceiptDto>;
