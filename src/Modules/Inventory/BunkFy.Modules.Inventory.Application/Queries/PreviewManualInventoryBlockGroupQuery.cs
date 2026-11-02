namespace BunkFy.Modules.Inventory.Application.Queries;

using BunkFy.Modules.Inventory.Contracts;
using Gma.Framework.Cqrs;

public sealed record PreviewManualInventoryBlockGroupQuery(
    Guid PropertyId,
    InventoryBlockTarget Target,
    DateOnly Arrival,
    DateOnly Departure,
    string Reason,
    Guid? BlockGroupId = null,
    long? ExpectedVersion = null)
    : IQuery<ManualInventoryBlockGroupSelectionPreviewDto>;
