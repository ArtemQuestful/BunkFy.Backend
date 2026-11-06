namespace BunkFy.Modules.Inventory.Application.Validation;

using BunkFy.Modules.Inventory.Application.Queries;
using Gma.Framework.Cqrs;

internal sealed class GetInventoryRetirementContextQueryValidator : IQueryValidator<GetInventoryRetirementContextQuery>
{
    public IEnumerable<string> Validate(GetInventoryRetirementContextQuery query)
    {
        if (query.PropertyId == Guid.Empty || query.RoomId == Guid.Empty || query.BedId == Guid.Empty)
        {
            yield return "PropertyId, RoomId and a supplied BedId must be valid.";
        }
    }
}
