namespace BunkFy.Modules.Inventory.Contracts;

public sealed record ManualInventoryBlockGroupOperationDto(
    Guid OperationId,
    Guid PropertyId,
    Guid? RequestedBlockGroupId,
    ManualInventoryBlockGroupOperationKind Kind,
    ManualInventoryBlockGroupOperationStatus Status,
    ManualInventoryBlockGroupMutationReceiptDto Receipt,
    DateTimeOffset CompletedAtUtc);
