namespace BunkFy.Host.Api;

using BunkFy.Modules.Staff.Application.Ports;
using Gma.Modules.Auth.Application.Ports;

internal sealed class StaffRememberedBrowserAccountProfileReader(
    IStaffMemberRepository members) : IRememberedBrowserAccountProfileReader
{
    public async Task<RememberedBrowserAccountProfile?> FindAsync(
        string scopeId,
        Guid memberId,
        CancellationToken cancellationToken)
    {
        BunkFy.Modules.Staff.Domain.Aggregates.StaffMember? member =
            await members.GetByAuthSubjectAsync(memberId.ToString(), cancellationToken).ConfigureAwait(false);
        return member is null
            ? null
            : new RememberedBrowserAccountProfile(member.DisplayName, member.JobTitle);
    }
}
