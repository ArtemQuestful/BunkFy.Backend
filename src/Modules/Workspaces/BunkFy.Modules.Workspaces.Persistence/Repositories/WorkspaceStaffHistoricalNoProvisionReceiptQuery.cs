namespace BunkFy.Modules.Workspaces.Persistence.Repositories;

using BunkFy.Modules.Workspaces.Domain;

internal static class WorkspaceStaffHistoricalNoProvisionReceiptQuery
{
    public static IQueryable<WorkspaceStaffOnboarding> ExcludeExactlyReviewed(
        this IQueryable<WorkspaceStaffOnboarding> applications,
        WorkspacesDbContext dbContext) =>
        applications.Where(application =>
            !dbContext.StaffHistoricalNoProvisionReceipts.Any(receipt =>
                receipt.ScopeId == application.ScopeId &&
                receipt.ContractVersion ==
                    WorkspaceStaffHistoricalNoProvisionReceipt
                        .CurrentContractVersion &&
                receipt.ApplicationId == application.Id &&
                receipt.SourceKind == application.SourceKind &&
                receipt.SourceId == application.SourceId &&
                receipt.ResultApplicationVersion == application.Version &&
                receipt.ResultApplicationStatus == application.Status &&
                (receipt.ResultApplicationStatus ==
                    WorkspaceStaffOnboardingState.Completed ||
                 receipt.ResultApplicationStatus ==
                    WorkspaceStaffOnboardingState.Rejected ||
                 receipt.ResultApplicationStatus ==
                    WorkspaceStaffOnboardingState.Superseded ||
                 receipt.ResultApplicationStatus ==
                    WorkspaceStaffOnboardingState.Expired ||
                 receipt.ResultApplicationStatus ==
                    WorkspaceStaffOnboardingState.Withdrawn) &&
                application.SubjectId ==
                    WorkspaceStaffHistoricalNoProvisionReceipt
                        .SubjectPseudonymPrefix + receipt.Id.ToString() &&
                application.VerifiedAccountEmail == null &&
                application.DisplayName == null &&
                application.LegalName == null &&
                application.WorkEmail == null &&
                application.WorkPhone == null &&
                application.EmployeeNumber == null &&
                application.JobTitle == null &&
                application.Department == null &&
                application.FailureCode == null &&
                application.StaffMemberId == null &&
                application.IdentityAnchorExpectedResolutionEventId == null &&
                application.IdentityAnchorContinuationEventId == null &&
                application.IdentityAnchorResolutionEventId == null &&
                application.IdentityAnchorResolutionStaffMemberId == null &&
                application.IdentityAnchorResolutionApplicationVersion == null &&
                application.IdentityAnchorResolutionDisposition == null &&
                application.IdentityAnchorResolutionIntentAtUtc == null &&
                application.IdentityAnchorResolutionObservedAtUtc == null));
}
