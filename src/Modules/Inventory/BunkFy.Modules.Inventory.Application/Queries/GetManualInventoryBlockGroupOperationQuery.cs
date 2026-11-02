namespace BunkFy.Modules.Inventory.Application.Queries;

using BunkFy.Modules.Inventory.Contracts;
using Gma.Framework.Cqrs;

public sealed record GetManualInventoryBlockGroupOperationQuery(
    Guid PropertyId,
    Guid BlockGroupId,
    Guid OperationId)
    : IQuery<ManualInventoryBlockGroupOperationDto>;
