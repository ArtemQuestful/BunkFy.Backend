namespace BunkFy.Modules.Staff.Persistence.Repositories;

using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.Aggregates;
using Microsoft.EntityFrameworkCore;

internal sealed class StaffStationLabelReader(StaffDbContext db) : IStaffStationLabelReader
{
    public async Task<IReadOnlyList<StaffStationLabel>> ResolveAsync(string scopeId, Guid propertyId,
        IReadOnlyCollection<Guid> exactStaffIds, DateOnly propertyLocalDate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exactStaffIds);
        cancellationToken.ThrowIfCancellationRequested();
        if (!db.ScopeFilterEnabled || db.CurrentScopeId != scopeId || !Guid.TryParseExact(scopeId, "D", out Guid tenant) ||
            tenant == Guid.Empty || tenant.ToString("D") != scopeId || propertyId == Guid.Empty || propertyLocalDate == default ||
            exactStaffIds.Count > 200 || exactStaffIds.Any(x => x == Guid.Empty) || exactStaffIds.Distinct().Count() != exactStaffIds.Count)
        { throw new ArgumentException("Invalid bounded station label request."); }
        if (exactStaffIds.Count == 0)
        { return []; }
        Guid[] ids = exactStaffIds.ToArray();
        return await db.StaffMembers.AsNoTracking().Where(member => ids.Contains(member.Id) && member.ScopeId == scopeId &&
                member.Status == StaffMemberState.Active && member.Version > 0 &&
                member.Assignments.Count(a => a.ScopeId == scopeId && a.PropertyId == propertyId && a.IsCurrent) == 1 &&
                member.Assignments.Any(a => a.ScopeId == scopeId && a.PropertyId == propertyId && a.IsCurrent &&
                    a.EffectiveFrom <= propertyLocalDate && a.EffectiveFrom != default && a.AssignedAtVersion > 0 &&
                    a.AssignedAtVersion <= member.Version && a.EffectiveTo == null && a.UnassignedAtVersion == null) &&
                db.ProcessingRestrictionProjections.Count(r => r.ScopeId == scopeId && r.StaffMemberId == member.Id) == 1 &&
                db.ProcessingRestrictionProjections.Any(r => r.ScopeId == scopeId && r.StaffMemberId == member.Id &&
                    r.ContractVersion == StaffProcessingRestrictionContract.CurrentVersion && r.Revision >= 0 &&
                    !r.IsRestricted && r.ActiveRestrictionCount == 0))
            .OrderBy(member => member.Id).Select(member => new StaffStationLabel(member.Id, member.DisplayName, member.Version))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
    }
}
