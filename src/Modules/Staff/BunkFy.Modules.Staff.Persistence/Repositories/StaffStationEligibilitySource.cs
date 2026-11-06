namespace BunkFy.Modules.Staff.Persistence.Repositories;

using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.Aggregates;
using BunkFy.Modules.Staff.Domain.ValueObjects;
using Gma.Framework.Naming;
using Microsoft.EntityFrameworkCore;

internal sealed class StaffStationEligibilitySource(StaffDbContext dbContext)
    : IStaffStationEligibilitySource
{
    public async Task<StaffStationEligibilitySnapshot?> FindAsync(
        string scopeId,
        Guid propertyId,
        Guid staffMemberId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!dbContext.ScopeFilterEnabled || propertyId == Guid.Empty || staffMemberId == Guid.Empty ||
            !TenantIds.TryNormalize(scopeId, out string? tenantId) ||
            !string.Equals(tenantId, dbContext.CurrentScopeId, StringComparison.Ordinal))
        {
            return null;
        }

        // One owner query, including linkage count and bounded exact-property/restriction facts.
        // No tracking, roster expansion, profile fields, or event-driven positive cache.
        var row = await dbContext.StaffMembers.AsNoTracking().AsSingleQuery()
            .Where(member => member.ScopeId == tenantId && member.Id == staffMemberId)
            .Select(member => new
            {
                member.Id,
                member.ScopeId,
                member.Status,
                member.Version,
                member.AuthSubjectId,
                LinkCount = dbContext.StaffMembers.Count(other => other.ScopeId == tenantId &&
                    other.AuthSubjectId == member.AuthSubjectId),
                Assignments = member.Assignments
                    .Where(assignment => assignment.ScopeId == tenantId &&
                        assignment.PropertyId == propertyId && assignment.IsCurrent)
                    .OrderBy(assignment => assignment.Id)
                    .Select(assignment => new
                    {
                        assignment.Id,
                        assignment.PropertyId,
                        assignment.AssignedAtVersion,
                        assignment.EffectiveFrom,
                        assignment.EffectiveTo,
                        assignment.UnassignedAtVersion
                    }).Take(2).ToList(),
                Restrictions = dbContext.ProcessingRestrictionProjections
                    .Where(restriction => restriction.ScopeId == tenantId && restriction.StaffMemberId == member.Id)
                    .Select(restriction => new
                    {
                        restriction.ContractVersion,
                        restriction.Revision,
                        restriction.IsRestricted,
                        restriction.ActiveRestrictionCount
                    }).Take(2).ToList()
            }).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (row is null || row.Version < 1)
        {
            return null;
        }

        StaffStationAuthLinkState linkState = row.AuthSubjectId is null
            ? StaffStationAuthLinkState.Unlinked
            : string.IsNullOrWhiteSpace(row.AuthSubjectId) ||
              row.AuthSubjectId.Length > StaffAuthSubject.MaxLength ||
              !string.Equals(row.AuthSubjectId, row.AuthSubjectId.Trim(), StringComparison.Ordinal) ||
              row.AuthSubjectId.Any(char.IsControl)
                ? StaffStationAuthLinkState.Malformed
                : row.LinkCount == 1 ? StaffStationAuthLinkState.Linked : StaffStationAuthLinkState.Ambiguous;
        var assignment = row.Assignments.Count == 1 ? row.Assignments[0] : null;
        StaffStationAssignmentState assignmentState = row.Assignments.Count switch
        {
            0 => StaffStationAssignmentState.None,
            > 1 => StaffStationAssignmentState.Ambiguous,
            _ => assignment!.Id == Guid.Empty || assignment.AssignedAtVersion < 1 ||
                 assignment.AssignedAtVersion > row.Version || assignment.EffectiveFrom == default ||
                 assignment.EffectiveTo is not null || assignment.UnassignedAtVersion is not null
                ? StaffStationAssignmentState.Malformed : StaffStationAssignmentState.Open
        };
        StaffProcessingRestrictionGateResult restrictionResult = StaffProcessingRestrictionGateResult.Unknown;
        if (row.Restrictions.Count == 1)
        {
            var restriction = row.Restrictions[0];
            if (restriction.ContractVersion != StaffProcessingRestrictionContract.CurrentVersion)
            {
                restrictionResult = StaffProcessingRestrictionGateResult.Unsupported(
                    restriction.ContractVersion, restriction.Revision);
            }
            else if (restriction.Revision >= 0 && restriction.ActiveRestrictionCount >= 0 &&
                     restriction.IsRestricted == (restriction.ActiveRestrictionCount > 0))
            {
                restrictionResult = restriction.IsRestricted
                    ? StaffProcessingRestrictionGateResult.Restricted(restriction.ContractVersion, restriction.Revision)
                    : StaffProcessingRestrictionGateResult.Allowed(restriction.ContractVersion, restriction.Revision);
            }
        }

        return new(row.ScopeId, propertyId, row.Id, row.Status switch
        {
            StaffMemberState.Active => StaffStatus.Active,
            StaffMemberState.Suspended => StaffStatus.Suspended,
            StaffMemberState.Departed => StaffStatus.Departed,
            _ => StaffStatus.Unknown
        }, row.Version, linkState,
            linkState == StaffStationAuthLinkState.Linked ? row.AuthSubjectId : null,
            assignmentState,
            assignmentState == StaffStationAssignmentState.Open
                ? new(assignment!.Id, assignment.PropertyId, assignment.AssignedAtVersion, assignment.EffectiveFrom)
                : null,
            restrictionResult);
    }
}
