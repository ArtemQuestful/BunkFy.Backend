namespace BunkFy.Modules.Properties.Application.Validation;

using BunkFy.Modules.Properties.Application.Queries;
using Gma.Framework.Cqrs;

internal sealed class GetPropertyTimeZoneRecoveryQueryValidator
    : IQueryValidator<GetPropertyTimeZoneRecoveryQuery>
{
    public IEnumerable<string> Validate(
        GetPropertyTimeZoneRecoveryQuery query)
    {
        if (query.PropertyId == Guid.Empty)
        {
            yield return "Property id is required.";
        }

        if (query.OperationId == Guid.Empty)
        {
            yield return "Operation id is required.";
        }
    }
}
