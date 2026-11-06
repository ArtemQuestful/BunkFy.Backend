namespace BunkFy.Modules.Workspaces.Persistence.Repositories;

using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Domain;
using Microsoft.EntityFrameworkCore;

internal sealed class WorkspaceStaffOnboardingRetentionRepository(
    WorkspacesDbContext dbContext)
    : IWorkspaceStaffOnboardingRetentionRepository
{
    public async Task<IReadOnlyList<WorkspaceStaffOnboardingRetentionCandidate>>
        ListEligibleAsync(
            string tenantId,
            DateTimeOffset sourceExpiredBeforeUtc,
            int maximumCount,
            CancellationToken cancellationToken)
    {
        string normalizedTenantId = tenantId.Trim();
        return await (
            from plan in dbContext.StaffAccessPlans.AsNoTracking()
            join application in dbContext.StaffOnboardingApplications.AsNoTracking()
                on new { plan.ScopeId, SourceId = plan.Id }
                equals new { application.ScopeId, application.SourceId }
            where plan.ScopeId == normalizedTenantId &&
                  plan.SourceKind == WorkspaceStaffOnboardingSource.EnrollmentLink &&
                  plan.SourceExpiredAtUtc != null &&
                  plan.SourceExpiredAtUtc <= sourceExpiredBeforeUtc &&
                  application.SourceKind ==
                    WorkspaceStaffOnboardingSource.EnrollmentLink &&
                  ((application.Status == WorkspaceStaffOnboardingState.Submitted &&
                    application.ClaimId == null &&
                    application.ClaimVersion == null) ||
                   (application.Status == WorkspaceStaffOnboardingState.PendingApproval &&
                    application.ClaimId != null &&
                    application.ClaimVersion != null))
            orderby plan.SourceExpiredAtUtc, application.LastChangedAtUtc, application.Id
            select new WorkspaceStaffOnboardingRetentionCandidate(
                application.Id,
                application.Version,
                plan.SourceExpiredAtUtc!.Value))
            .Take(maximumCount)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
