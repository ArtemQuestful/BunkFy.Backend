namespace BunkFy.Modules.DataRights.Application.Validation;

using BunkFy.Modules.DataRights.Application.Queries;
using Gma.Framework.Cqrs;

internal sealed class GetDataRightsRestrictionReleaseTargetsQueryValidator
    : IQueryValidator<GetDataRightsRestrictionReleaseTargetsQuery>
{
    public IEnumerable<string> Validate(
        GetDataRightsRestrictionReleaseTargetsQuery query)
    {
        if (query.Scope is null || query.CaseId == Guid.Empty)
        {
            yield return "Scope and CaseId are required.";
        }
    }
}
