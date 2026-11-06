namespace BunkFy.Modules.Workspaces.Persistence.Repositories;

using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Domain;

internal static class WorkspaceStaffOnboardingExportAuthority
{
    public static bool IsAuthorized(
        WorkspaceStaffOnboarding application,
        StaffWorkspaceOnboardingIdentityAnchorOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(outcome);
        return IsExactAbsent(application, outcome) ||
            IsExactObservedResolution(application, outcome);
    }

    private static bool IsExactAbsent(
        WorkspaceStaffOnboarding application,
        StaffWorkspaceOnboardingIdentityAnchorOutcome outcome) =>
        outcome.ApplicationId == application.Id &&
        outcome.Status ==
            StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Absent &&
        !outcome.StaffMemberId.HasValue &&
        outcome.TargetLifecycle ==
            StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Unknown &&
        outcome.SubjectMatch ==
            StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Unknown &&
        !outcome.WorkspaceApplicationVersion.HasValue &&
        !outcome.ResolutionDisposition.HasValue &&
        !outcome.ResolutionEventId.HasValue &&
        !HasLocalAnchorCoordinates(application);

    private static bool IsExactObservedResolution(
        WorkspaceStaffOnboarding application,
        StaffWorkspaceOnboardingIdentityAnchorOutcome outcome)
    {
        if (outcome.ApplicationId != application.Id ||
            outcome.Status !=
                StaffWorkspaceOnboardingIdentityAnchorOutcomeStatus.Resolved ||
            !outcome.StaffMemberId.HasValue ||
            outcome.StaffMemberId.Value == Guid.Empty ||
            !outcome.WorkspaceApplicationVersion.HasValue ||
            outcome.WorkspaceApplicationVersion.Value <= 0 ||
            !outcome.ResolutionDisposition.HasValue ||
            !outcome.ResolutionEventId.HasValue ||
            outcome.ResolutionEventId.Value == Guid.Empty ||
            outcome.ResolutionEventId.Value == application.Id ||
            !HasExactSubjectRelationship(outcome) ||
            !TryMapDisposition(
                outcome.ResolutionDisposition.Value,
                out WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                    disposition) ||
            application.StaffMemberId != outcome.StaffMemberId ||
            application.IdentityAnchorExpectedResolutionEventId !=
                outcome.ResolutionEventId ||
            application.IdentityAnchorResolutionEventId !=
                outcome.ResolutionEventId ||
            application.IdentityAnchorResolutionStaffMemberId !=
                outcome.StaffMemberId ||
            application.IdentityAnchorResolutionApplicationVersion !=
                outcome.WorkspaceApplicationVersion ||
            application.IdentityAnchorResolutionDisposition != disposition ||
            !application.IdentityAnchorResolutionIntentAtUtc.HasValue ||
            !application.IdentityAnchorResolutionObservedAtUtc.HasValue ||
            application.IdentityAnchorResolutionObservedAtUtc.Value <
                application.IdentityAnchorResolutionIntentAtUtc.Value ||
            application.Version < outcome.WorkspaceApplicationVersion.Value ||
            !StatusMatchesDisposition(application.Status, disposition) ||
            !IsApplicantProfileRedacted(application))
        {
            return false;
        }

        return !application.IdentityAnchorContinuationEventId.HasValue ||
            (application.IdentityAnchorContinuationEventId.Value != Guid.Empty &&
             application.IdentityAnchorContinuationEventId.Value !=
                application.Id &&
             application.IdentityAnchorContinuationEventId.Value !=
                outcome.ResolutionEventId.Value);
    }

    private static bool HasLocalAnchorCoordinates(
        WorkspaceStaffOnboarding application) =>
        application.StaffMemberId.HasValue ||
        application.IdentityAnchorExpectedResolutionEventId.HasValue ||
        application.IdentityAnchorContinuationEventId.HasValue ||
        application.IdentityAnchorResolutionEventId.HasValue ||
        application.IdentityAnchorResolutionStaffMemberId.HasValue ||
        application.IdentityAnchorResolutionApplicationVersion.HasValue ||
        application.IdentityAnchorResolutionDisposition.HasValue ||
        application.IdentityAnchorResolutionIntentAtUtc.HasValue ||
        application.IdentityAnchorResolutionObservedAtUtc.HasValue;

    private static bool HasExactSubjectRelationship(
        StaffWorkspaceOnboardingIdentityAnchorOutcome outcome) =>
        outcome.TargetLifecycle switch
        {
            StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Active or
            StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Suspended or
            StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Departed =>
                outcome.SubjectMatch is
                    StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Exact or
                    StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Mismatch,
            StaffWorkspaceOnboardingIdentityAnchorTargetLifecycle.Anonymised =>
                outcome.SubjectMatch ==
                    StaffWorkspaceOnboardingIdentityAnchorSubjectMatch.Missing,
            _ => false
        };

    private static bool IsApplicantProfileRedacted(
        WorkspaceStaffOnboarding application) =>
        application.VerifiedAccountEmail is null &&
        application.DisplayName is null &&
        application.LegalName is null &&
        application.WorkEmail is null &&
        application.WorkPhone is null &&
        application.EmployeeNumber is null &&
        application.JobTitle is null &&
        application.Department is null;

    private static bool StatusMatchesDisposition(
        WorkspaceStaffOnboardingState status,
        WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
            disposition) =>
        (status, disposition) switch
        {
            (WorkspaceStaffOnboardingState.Completed,
             WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                 .CompletedRedacted) => true,
            (WorkspaceStaffOnboardingState.Rejected,
             WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                 .RejectedRedacted) => true,
            (WorkspaceStaffOnboardingState.Superseded,
             WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                 .SupersededRedacted) => true,
            (WorkspaceStaffOnboardingState.Expired,
             WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                 .ExpiredRedacted) => true,
            (WorkspaceStaffOnboardingState.Withdrawn,
             WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                 .WithdrawnRedacted) => true,
            _ => false
        };

    private static bool TryMapDisposition(
        StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition source,
        out WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition target)
    {
        target = source switch
        {
            StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition
                .CompletedRedacted =>
                WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                    .CompletedRedacted,
            StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition
                .RejectedRedacted =>
                WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                    .RejectedRedacted,
            StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition
                .SupersededRedacted =>
                WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                    .SupersededRedacted,
            StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition
                .ExpiredRedacted =>
                WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                    .ExpiredRedacted,
            StaffWorkspaceOnboardingIdentityAnchorResolutionDisposition
                .WithdrawnRedacted =>
                WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                    .WithdrawnRedacted,
            _ => WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition
                .Unknown
        };
        return target !=
            WorkspaceStaffOnboardingIdentityAnchorResolutionDisposition.Unknown;
    }
}
