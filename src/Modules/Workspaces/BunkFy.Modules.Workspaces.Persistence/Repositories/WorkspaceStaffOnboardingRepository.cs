namespace BunkFy.Modules.Workspaces.Persistence.Repositories;

using BunkFy.Modules.Workspaces.Application.Mapping;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Domain.DataRights;
using Gma.Framework.Pagination;
using Microsoft.EntityFrameworkCore;

internal sealed class WorkspaceStaffOnboardingRepository(WorkspacesDbContext dbContext)
    : IWorkspaceStaffOnboardingRepository
{
    public Task<WorkspaceStaffOnboardingCoordinate?> FindCoordinateAsync(
        Guid applicationId,
        CancellationToken cancellationToken) =>
        dbContext.StaffOnboardingApplications
            .AsNoTracking()
            .Where(application => application.Id == applicationId)
            .Select(application => new WorkspaceStaffOnboardingCoordinate(
                application.Id,
                application.SourceKind,
                application.SourceId))
            .SingleOrDefaultAsync(cancellationToken);

    public Task<WorkspaceStaffOnboarding?> GetAsync(
        Guid applicationId,
        CancellationToken cancellationToken) =>
        dbContext.StaffOnboardingApplications.SingleOrDefaultAsync(
            application => application.Id == applicationId,
            cancellationToken);

    public Task<WorkspaceStaffOnboarding?> GetOperationalAsync(
        Guid applicationId,
        CancellationToken cancellationToken) =>
        (
            from application in dbContext.StaffOnboardingApplications
                .ExcludeExactlyReviewed(dbContext)
            join projection in
                dbContext.StaffOnboardingProcessingRestrictionProjections
                on new
                {
                    application.ScopeId,
                    ApplicationId = application.Id
                }
                equals new
                {
                    projection.ScopeId,
                    projection.ApplicationId
                }
            where application.Id == applicationId &&
                projection.ContractVersion ==
                    WorkspaceStaffOnboardingProcessingRestrictionContract
                        .CurrentVersion &&
                !projection.IsRestricted
            select application
        ).SingleOrDefaultAsync(cancellationToken);

    public Task<WorkspaceStaffOnboarding?>
        GetOperationalBySourceAndSubjectAsync(
            WorkspaceStaffOnboardingSource sourceKind,
            Guid sourceId,
            string subjectId,
            CancellationToken cancellationToken)
    {
        string normalizedSubject = subjectId.Trim();
        return (
            from application in dbContext.StaffOnboardingApplications
                .AsNoTracking()
                .ExcludeExactlyReviewed(dbContext)
            join projection in
                dbContext.StaffOnboardingProcessingRestrictionProjections
                    .AsNoTracking()
                on new
                {
                    application.ScopeId,
                    ApplicationId = application.Id
                }
                equals new
                {
                    projection.ScopeId,
                    projection.ApplicationId
                }
            where application.SourceKind == sourceKind &&
                application.SourceId == sourceId &&
                application.SubjectId == normalizedSubject &&
                projection.ContractVersion ==
                    WorkspaceStaffOnboardingProcessingRestrictionContract
                        .CurrentVersion &&
                !projection.IsRestricted
            select application
        ).SingleOrDefaultAsync(cancellationToken);
    }

    public Task<Guid?> FindIdBySourceAndSubjectAsync(
        WorkspaceStaffOnboardingSource sourceKind,
        Guid sourceId,
        string subjectId,
        CancellationToken cancellationToken)
    {
        string normalizedSubject = subjectId.Trim();
        return dbContext.StaffOnboardingApplications
            .AsNoTracking()
            .Where(application =>
                application.SourceKind == sourceKind &&
                application.SourceId == sourceId &&
                application.SubjectId == normalizedSubject)
            .Select(application => (Guid?)application.Id)
            .SingleOrDefaultAsync(cancellationToken);
    }

    public Task<WorkspaceStaffOnboarding?> GetByClaimAsync(
        Guid claimId,
        CancellationToken cancellationToken) =>
        dbContext.StaffOnboardingApplications.SingleOrDefaultAsync(
            application => application.ClaimId == claimId,
            cancellationToken);

    public async Task<IReadOnlyList<WorkspaceStaffOnboarding>> ListActiveBySourceAsync(
        WorkspaceStaffOnboardingSource sourceKind,
        Guid sourceId,
        CancellationToken cancellationToken) =>
        await dbContext.StaffOnboardingApplications
            .Where(application => application.SourceKind == sourceKind &&
                application.SourceId == sourceId &&
                application.Status != WorkspaceStaffOnboardingState.Completed &&
                application.Status != WorkspaceStaffOnboardingState.Rejected &&
                application.Status != WorkspaceStaffOnboardingState.Superseded &&
                application.Status != WorkspaceStaffOnboardingState.Expired &&
                application.Status != WorkspaceStaffOnboardingState.Withdrawn)
            .OrderBy(application => application.CreatedAtUtc)
            .ThenBy(application => application.Id)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);

    public async Task<WorkspaceStaffOnboardingSummaryListResponse> ListActionableAsync(
        PageRequest page,
        CancellationToken cancellationToken)
    {
        WorkspaceStaffOnboardingActionableSummaryDto[] fetched = await (
            from application in dbContext.StaffOnboardingApplications
                .AsNoTracking()
                .ExcludeExactlyReviewed(dbContext)
            join projection in
                dbContext.StaffOnboardingProcessingRestrictionProjections
                    .AsNoTracking()
                on new
                {
                    application.ScopeId,
                    ApplicationId = application.Id
                }
                equals new
                {
                    projection.ScopeId,
                    projection.ApplicationId
                }
            where
                (application.Status == WorkspaceStaffOnboardingState.PendingApproval &&
                    application.ClaimId != null) ||
                application.Status == WorkspaceStaffOnboardingState.Failed
            where projection.ContractVersion ==
                    WorkspaceStaffOnboardingProcessingRestrictionContract
                        .CurrentVersion &&
                !projection.IsRestricted
            orderby application.CreatedAtUtc, application.Id
            select new WorkspaceStaffOnboardingActionableSummaryDto(
                application.Id,
                (WorkspaceStaffOnboardingSourceKind)application.SourceKind,
                application.SourceId,
                application.ClaimId,
                application.ClaimVersion,
                (WorkspaceStaffOnboardingStatus)application.Status,
                application.Version,
                application.FailureCode,
                application.CreatedAtUtc,
                application.LastChangedAtUtc,
                application.StaffMemberId.HasValue)
        )
            .Skip(page.SkipCount)
            .Take(page.PageSize + 1)
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        bool hasMore = fetched.Length > page.PageSize;
        return new WorkspaceStaffOnboardingSummaryListResponse(
            fetched.Take(page.PageSize).ToArray(),
            page.Page,
            page.PageSize,
            hasMore);
    }

    public Task ReloadAsync(
        WorkspaceStaffOnboarding application,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(application);
        return dbContext.Entry(application)
            .ReloadAsync(cancellationToken);
    }

    public Task AddAsync(
        WorkspaceStaffOnboarding application,
        CancellationToken cancellationToken)
    {
        dbContext.StaffOnboardingApplications.Add(application);
        return Task.CompletedTask;
    }
}
