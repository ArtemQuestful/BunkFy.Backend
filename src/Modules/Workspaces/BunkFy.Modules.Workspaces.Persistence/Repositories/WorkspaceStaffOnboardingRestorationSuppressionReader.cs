namespace BunkFy.Modules.Workspaces.Persistence.Repositories;

using BunkFy.Modules.Workspaces.Application.Ports;
using BunkFy.Modules.Workspaces.Domain;
using Gma.Framework.Naming;
using Microsoft.EntityFrameworkCore;

internal sealed class WorkspaceStaffOnboardingRestorationSuppressionReader(
    WorkspacesDbContext dbContext)
    : IWorkspaceStaffOnboardingRestorationSuppressionReader
{
    public async Task<WorkspaceStaffOnboardingRestorationSuppressionState>
        ReadAsync(
            string scopeId,
            Guid staffMemberId,
            string authSubjectId,
            CancellationToken cancellationToken)
    {
        string subjectId = authSubjectId?.Trim() ?? string.Empty;
        if (!TenantIds.TryNormalize(scopeId, out string? normalizedScopeId) ||
            staffMemberId == Guid.Empty ||
            subjectId.Length is 0 or >
                WorkspaceStaffOnboardingRules.SubjectIdMaxLength)
        {
            return WorkspaceStaffOnboardingRestorationSuppressionState
                .Conflict;
        }

        IQueryable<WorkspaceStaffOnboarding> candidates =
            dbContext.StaffOnboardingApplications
                .AsNoTracking()
                .Where(application =>
                    application.ScopeId == normalizedScopeId &&
                    application.SubjectId == subjectId &&
                    (application.StaffMemberId == staffMemberId ||
                        application.IdentityAnchorResolutionStaffMemberId ==
                            staffMemberId) &&
                    (application.IdentityAnchorResolutionEventId != null ||
                        application.IdentityAnchorResolutionStaffMemberId !=
                            null ||
                        application
                            .IdentityAnchorResolutionApplicationVersion != null ||
                        application.IdentityAnchorResolutionDisposition != null ||
                        application.IdentityAnchorResolutionIntentAtUtc != null ||
                        application.IdentityAnchorResolutionObservedAtUtc != null));

        bool hasConflict = await candidates.AnyAsync(
            application =>
                application.IdentityAnchorResolutionEventId == null ||
                application.IdentityAnchorResolutionEventId == Guid.Empty ||
                application.IdentityAnchorExpectedResolutionEventId == null ||
                application.IdentityAnchorExpectedResolutionEventId !=
                    application.IdentityAnchorResolutionEventId ||
                (application.IdentityAnchorContinuationEventId != null &&
                    (application.IdentityAnchorContinuationEventId ==
                        application.IdentityAnchorResolutionEventId ||
                     application.IdentityAnchorContinuationEventId ==
                        application.Id)) ||
                application.StaffMemberId != staffMemberId ||
                application.IdentityAnchorResolutionStaffMemberId !=
                    staffMemberId ||
                application.IdentityAnchorResolutionApplicationVersion == null ||
                application.IdentityAnchorResolutionApplicationVersion <= 0 ||
                application.IdentityAnchorResolutionApplicationVersion >
                    application.Version ||
                application.IdentityAnchorResolutionDisposition == null ||
                application.IdentityAnchorResolutionIntentAtUtc == null ||
                (application.IdentityAnchorResolutionObservedAtUtc != null &&
                    application.IdentityAnchorResolutionObservedAtUtc <
                        application.IdentityAnchorResolutionIntentAtUtc) ||
                application.VerifiedAccountEmail != null ||
                application.DisplayName != null ||
                application.LegalName != null ||
                application.WorkEmail != null ||
                application.WorkPhone != null ||
                application.EmployeeNumber != null ||
                application.JobTitle != null ||
                application.Department != null ||
                !((application.Status ==
                        WorkspaceStaffOnboardingState.Completed &&
                    application.IdentityAnchorResolutionDisposition ==
                        WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                            .CompletedRedacted) ||
                  (application.Status ==
                        WorkspaceStaffOnboardingState.Rejected &&
                    application.IdentityAnchorResolutionDisposition ==
                        WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                            .RejectedRedacted) ||
                  (application.Status ==
                        WorkspaceStaffOnboardingState.Superseded &&
                    application.IdentityAnchorResolutionDisposition ==
                        WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                            .SupersededRedacted) ||
                  (application.Status ==
                        WorkspaceStaffOnboardingState.Expired &&
                    application.IdentityAnchorResolutionDisposition ==
                        WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                            .ExpiredRedacted) ||
                  (application.Status ==
                        WorkspaceStaffOnboardingState.Withdrawn &&
                    application.IdentityAnchorResolutionDisposition ==
                        WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                            .WithdrawnRedacted)),
            cancellationToken).ConfigureAwait(false);
        if (hasConflict)
        {
            return WorkspaceStaffOnboardingRestorationSuppressionState
                .Conflict;
        }

        bool isSuppressed = await candidates.AnyAsync(
            application =>
                application.IdentityAnchorResolutionDisposition !=
                WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                    .CompletedRedacted,
            cancellationToken).ConfigureAwait(false);
        return isSuppressed
            ? WorkspaceStaffOnboardingRestorationSuppressionState.Suppressed
            : WorkspaceStaffOnboardingRestorationSuppressionState.None;
    }
}
