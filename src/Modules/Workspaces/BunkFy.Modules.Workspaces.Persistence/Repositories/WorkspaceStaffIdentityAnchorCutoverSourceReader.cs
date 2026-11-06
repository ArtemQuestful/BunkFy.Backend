namespace BunkFy.Modules.Workspaces.Persistence.Repositories;

using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Domain;
using Microsoft.EntityFrameworkCore;

internal sealed class WorkspaceStaffIdentityAnchorCutoverSourceReader(
    WorkspacesDbContext dbContext)
    : IWorkspaceStaffIdentityAnchorCutoverSourceReader
{
    public async Task<WorkspaceStaffIdentityAnchorSourcePage>
        ListRelevantPageAsync(
            Guid? afterApplicationId,
            int pageSize,
            CancellationToken cancellationToken)
    {
        if (afterApplicationId == Guid.Empty)
        {
            throw new ArgumentException(
                "The Workspaces identity-anchor source cursor is invalid.",
                nameof(afterApplicationId));
        }

        if (pageSize is < 1 or >
            WorkspaceStaffIdentityAnchorCutoverSourceLimits.PageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageSize),
                pageSize,
                "The Workspaces identity-anchor source page size is invalid.");
        }

        IQueryable<WorkspaceStaffOnboarding> query = dbContext
            .StaffOnboardingApplications
            .AsNoTracking()
            .ExcludeExactlyReviewed(dbContext);
        if (afterApplicationId.HasValue)
        {
            Guid cursor = afterApplicationId.Value;
            query = query.Where(application =>
                application.Id.CompareTo(cursor) > 0);
        }

        WorkspaceStaffIdentityAnchorSourceRecord[] loaded = await query
            .OrderBy(application => application.Id)
            .Select(application =>
                new WorkspaceStaffIdentityAnchorSourceRecord(
                    application.Id,
                    application.StaffMemberId,
                    application.SubjectId,
                    application.Status))
            .Take(pageSize + 1)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        bool hasMore = loaded.Length > pageSize;
        WorkspaceStaffIdentityAnchorSourceRecord[] selected = loaded
            .Take(pageSize)
            .ToArray();
        Guid? nextApplicationId = selected.Length == 0
            ? afterApplicationId
            : selected[^1].ApplicationId;
        return new(
            Array.AsReadOnly(selected),
            nextApplicationId,
            hasMore);
    }
}
