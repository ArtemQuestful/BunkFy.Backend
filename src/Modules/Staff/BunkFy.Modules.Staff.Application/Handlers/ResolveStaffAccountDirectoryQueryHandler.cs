namespace BunkFy.Modules.Staff.Application.Handlers;

using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Application.Queries;
using BunkFy.Modules.Staff.Contracts;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;

internal sealed class ResolveStaffAccountDirectoryQueryHandler(
    IStaffMemberRepository members)
    : IQueryHandler<ResolveStaffAccountDirectoryQuery, StaffAccountDirectoryResponse>
{
    public async Task<Result<StaffAccountDirectoryResponse>> HandleAsync(
        ResolveStaffAccountDirectoryQuery query,
        CancellationToken cancellationToken)
    {
        string[] subjects = query.AuthSubjectIds
            .Select(subject => subject.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return Result.Success(await members.ResolveAccountDirectoryAsync(
            subjects,
            cancellationToken).ConfigureAwait(false));
    }
}
