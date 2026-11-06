namespace BunkFy.Modules.Properties.Application.Validation;

using BunkFy.Modules.Properties.Application.Queries;
using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Cqrs;

internal sealed class ListPropertyTimeZoneComplianceQueryValidator
    : IQueryValidator<ListPropertyTimeZoneComplianceQuery>
{
    public IEnumerable<string> Validate(
        ListPropertyTimeZoneComplianceQuery query)
    {
        if (query.PageSize is <= 0 or
            > PropertiesContractLimits.PropertyTimeZonePageSizeMax)
        {
            yield return "Page size must be between 1 and 100.";
        }

        if (query.Cursor?.Length > 2048)
        {
            yield return "Cursor must be 2048 characters or fewer.";
        }
    }
}
