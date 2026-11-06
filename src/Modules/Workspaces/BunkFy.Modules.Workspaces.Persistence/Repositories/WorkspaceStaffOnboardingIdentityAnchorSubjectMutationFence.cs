namespace BunkFy.Modules.Workspaces.Persistence.Repositories;

using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Naming;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

internal sealed class
    WorkspaceStaffOnboardingIdentityAnchorSubjectMutationFence(
        WorkspacesDbContext dbContext,
        IStaffWorkspaceOnboardingIdentityAnchorOutcomeReader outcomes,
        IScopeContext scopeContext,
        ILogger<
            WorkspaceStaffOnboardingIdentityAnchorSubjectMutationFence>
            logger)
    : IWorkspaceStaffOnboardingIdentityAnchorSubjectMutationFence
{
    private const int PageSize =
        StaffWorkspaceOnboardingIdentityAnchorLifecycleLimits.MaximumBatchSize;

    public async Task<bool> CanMutateAsync(
        string tenantId,
        string? subjectId,
        CancellationToken cancellationToken)
    {
        string normalizedSubject = subjectId?.Trim() ?? string.Empty;
        if (!TenantIds.TryNormalize(tenantId, out string? normalizedTenant))
        {
            return false;
        }

        if (!scopeContext.IsEnabled ||
            !TenantIds.TryNormalize(
                scopeContext.ScopeId,
                out string? activeTenant) ||
            !string.Equals(
                normalizedTenant,
                activeTenant,
                StringComparison.Ordinal))
        {
            return false;
        }

        if (normalizedSubject.Length == 0)
        {
            return true;
        }

        if (normalizedSubject.Length >
            WorkspaceStaffOnboardingRules.SubjectIdMaxLength)
        {
            return false;
        }

        Guid? afterApplicationId = null;
        while (true)
        {
            IQueryable<WorkspaceStaffOnboarding> query = dbContext
                .StaffOnboardingApplications
                .AsNoTracking()
                .Where(application =>
                    application.ScopeId == normalizedTenant &&
                    application.SubjectId == normalizedSubject);
            if (afterApplicationId.HasValue)
            {
                Guid cursor = afterApplicationId.Value;
                query = query.Where(application =>
                    application.Id.CompareTo(cursor) > 0);
            }

            WorkspaceStaffOnboarding[] page = await query
                .OrderBy(application => application.Id)
                .Take(PageSize)
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            if (page.Length == 0)
            {
                return true;
            }

            IReadOnlyList<StaffWorkspaceOnboardingIdentityAnchorOutcome> read;
            try
            {
                read = await outcomes.ReadAsync(
                        page.Select(application =>
                            new
                                StaffWorkspaceOnboardingIdentityAnchorOutcomeRequest(
                                    application.Id,
                                    application.SubjectId))
                            .ToArray(),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Staff identity-anchor subject-mutation preflight was cancelled outside the Workspaces request.");
                return false;
            }
            catch (Exception exception) when (exception is not
                OperationCanceledException)
            {
                logger.LogWarning(
                    "Staff identity-anchor subject-mutation preflight is unavailable for the Workspaces request.");
                return false;
            }

            if (read.Count != page.Length ||
                read.Select(outcome => outcome.ApplicationId)
                    .Distinct().Count() != read.Count)
            {
                return false;
            }

            Dictionary<Guid, StaffWorkspaceOnboardingIdentityAnchorOutcome>
                byApplication = read.ToDictionary(
                    outcome => outcome.ApplicationId);
            if (page.Any(application =>
                !byApplication.TryGetValue(
                    application.Id,
                    out StaffWorkspaceOnboardingIdentityAnchorOutcome?
                        outcome) ||
                !WorkspaceStaffOnboardingExportAuthority.IsAuthorized(
                    application,
                    outcome)))
            {
                return false;
            }

            Guid next = page[^1].Id;
            if (afterApplicationId.HasValue &&
                next.CompareTo(afterApplicationId.Value) <= 0)
            {
                return false;
            }

            if (page.Length < PageSize)
            {
                return true;
            }

            afterApplicationId = next;
        }
    }
}
