namespace BunkFy.Modules.Inventory.Application.Validation;

using BunkFy.Modules.Inventory.Application.Queries;
using BunkFy.Modules.Inventory.Contracts;
using Gma.Framework.Cqrs;

internal sealed class ListManualInventoryBlocksQueryValidator
    : IQueryValidator<ListManualInventoryBlocksQuery>
{
    public IEnumerable<string> Validate(ListManualInventoryBlocksQuery query)
    {
        if (query.PropertyId == Guid.Empty)
        {
            yield return "PropertyId is required.";
        }

        if (query.InventoryUnitId == Guid.Empty)
        {
            yield return "InventoryUnitId is invalid.";
        }

        if (query.OverlapsFrom.HasValue != query.OverlapsTo.HasValue)
        {
            yield return "OverlapsFrom and OverlapsTo must be supplied together.";
        }
        else if (query.OverlapsFrom.HasValue && query.OverlapsFrom >= query.OverlapsTo)
        {
            yield return "The inventory block overlap window is invalid.";
        }
        else if (query.OverlapsFrom.HasValue &&
                 query.OverlapsTo!.Value.DayNumber - query.OverlapsFrom.Value.DayNumber >
                 InventoryContractLimits.MaximumScheduleWindowDays)
        {
            yield return $"The inventory block overlap window cannot exceed {InventoryContractLimits.MaximumScheduleWindowDays} days.";
        }
    }
}
