namespace BunkFy.Modules.Workspaces.Application.Commands;

using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Cqrs;

public sealed record SubmitWorkspaceStaffOnboardingCommand(
    WorkspaceStaffOnboardingSourceKind SourceKind,
    string Token,
    string SubjectId,
    string DisplayName,
    string? LegalName,
    string? WorkEmail,
    string? WorkPhone,
    string? EmployeeNumber,
    string? JobTitle,
    string? Department)
    : ITransactionalCommand<WorkspaceStaffOnboardingSubmissionOutcome>;

public sealed record WorkspaceStaffOnboardingSubmissionOutcome(
    WorkspaceStaffOnboardingSubmissionOutcomeKind Kind,
    WorkspaceStaffOnboardingDto? Application)
{
    public static WorkspaceStaffOnboardingSubmissionOutcome Applied(
        WorkspaceStaffOnboardingDto application) =>
        new(
            WorkspaceStaffOnboardingSubmissionOutcomeKind.Applied,
            application ?? throw new ArgumentNullException(nameof(application)));

    public static WorkspaceStaffOnboardingSubmissionOutcome
        AuthorityMovedToStaff() =>
        new(
            WorkspaceStaffOnboardingSubmissionOutcomeKind
                .AuthorityMovedToStaff,
            Application: null);
}

public enum WorkspaceStaffOnboardingSubmissionOutcomeKind
{
    Unknown = 0,
    Applied = 1,
    AuthorityMovedToStaff = 2
}
