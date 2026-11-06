namespace BunkFy.Modules.Inventory.Application.Queries;

using BunkFy.Modules.Inventory.Contracts;
using Gma.Framework.Cqrs;

public sealed record GetManualInventoryBlockGroupQuery(
    Guid PropertyId,
    Guid BlockGroupId)
    : IQuery<ManualInventoryBlockGroupDto>;
