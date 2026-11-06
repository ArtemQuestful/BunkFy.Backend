namespace BunkFy.Modules.Staff.Persistence.Repositories;

using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.Aggregates;
using BunkFy.Modules.Staff.Domain.ValueObjects;
using Gma.Framework.Naming;
using Gma.Framework.Results;
using Microsoft.EntityFrameworkCore;

internal sealed class StaffOperationalIdentityReader(StaffDbContext dbContext)
    : IStaffOperationalIdentityReader
{
    public async Task<StaffOperationalIdentitySnapshot?> FindAsync(
        string tenantId,
        string authSubjectId,
        CancellationToken cancellationToken = default)
    {
        string normalizedTenant = ScopeIds.Normalize(tenantId, nameof(tenantId));
        Result<StaffAuthSubject> subject = StaffAuthSubject.Create(authSubjectId);
        if (subject.IsFailure || subject.Value.Value is null)
        {
            throw new ArgumentException(
                "An Auth subject id is required.",
                nameof(authSubjectId));
        }

        string normalizedSubject = subject.Value.Value;

        var row = await dbContext.StaffMembers
            .AsNoTracking()
            .Where(member =>
                member.ScopeId == normalizedTenant &&
                member.AuthSubjectId == normalizedSubject)
            .Select(member => new
            {
                member.Id,
                member.AuthSubjectId,
                member.Status,
                member.Version
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? null
            : new StaffOperationalIdentitySnapshot(
                row.Id,
                row.AuthSubjectId!,
                Map(row.Status),
                row.Version);
    }

    private static StaffStatus Map(StaffMemberState state) => state switch
    {
        StaffMemberState.Active => StaffStatus.Active,
        StaffMemberState.Suspended => StaffStatus.Suspended,
        StaffMemberState.Departed => StaffStatus.Departed,
        _ => StaffStatus.Unknown
    };
}
