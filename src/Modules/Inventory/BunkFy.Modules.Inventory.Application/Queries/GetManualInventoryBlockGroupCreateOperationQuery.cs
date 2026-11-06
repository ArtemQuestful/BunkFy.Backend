namespace BunkFy.Modules.Inventory.Application.Queries;

using BunkFy.Modules.Inventory.Contracts;
using Gma.Framework.Cqrs;

public sealed record GetManualInventoryBlockGroupCreateOperationQuery(
    Guid PropertyId,
    Guid OperationId)
    : IQuery<ManualInventoryBlockGroupOperationDto>;
