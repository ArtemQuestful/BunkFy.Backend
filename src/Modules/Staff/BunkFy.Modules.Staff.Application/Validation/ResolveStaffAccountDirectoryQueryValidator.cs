namespace BunkFy.Modules.Staff.Application.Validation;

using BunkFy.Modules.Staff.Application.Queries;
using BunkFy.Modules.Staff.Contracts;
using Gma.Framework.Cqrs;

internal sealed class ResolveStaffAccountDirectoryQueryValidator
    : IQueryValidator<ResolveStaffAccountDirectoryQuery>
{
    public IEnumerable<string> Validate(ResolveStaffAccountDirectoryQuery query)
    {
        if (query.AuthSubjectIds is null ||
            query.AuthSubjectIds.Count is 0 or > StaffContractLimits.AccountDirectoryResolveMaxCount)
        {
            yield return "AuthSubjectIds must contain between one and the supported number of identifiers.";
            yield break;
        }

        if (query.AuthSubjectIds.Any(subject =>
                string.IsNullOrWhiteSpace(subject) ||
                subject.Trim().Length > StaffContractLimits.AuthSubjectIdMaxLength))
        {
            yield return "AuthSubjectIds must contain only valid identifiers.";
        }
    }
}
