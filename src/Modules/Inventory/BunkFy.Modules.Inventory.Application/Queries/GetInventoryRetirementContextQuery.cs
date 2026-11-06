namespace BunkFy.Modules.Inventory.Application.Queries;

using BunkFy.Modules.Inventory.Contracts;
using Gma.Framework.Cqrs;

public sealed record GetInventoryRetirementContextQuery(Guid PropertyId, Guid RoomId, Guid? BedId = null)
    : IQuery<InventoryRetirementContextDto>;
