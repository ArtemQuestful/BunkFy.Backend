namespace BunkFy.Modules.Workspaces.Application.Commands;

using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Cqrs;

public sealed record ApplyWorkspaceStaffOnboardingDataRightsCorrectionCommand(
    Guid ExecutionId,
    Guid CaseId,
    long ApprovalRevision,
    Guid ApplicationId,
    long ExpectedVersion,
    string DisplayName,
    string? LegalName,
    string? WorkEmail,
    string? WorkPhone,
    string? EmployeeNumber,
    string? JobTitle,
    string? Department,
    string ActorId)
    : ITransactionalCommand<
        WorkspaceStaffOnboardingDataRightsCorrectionOutcome>;

public sealed record WorkspaceStaffOnboardingDataRightsCorrectionOutcome(
    WorkspaceStaffOnboardingDataRightsCorrectionOutcomeKind Kind,
    WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto? Receipt)
{
    public static WorkspaceStaffOnboardingDataRightsCorrectionOutcome Applied(
        WorkspaceStaffOnboardingDataRightsCorrectionReceiptDto receipt) =>
        new(
            WorkspaceStaffOnboardingDataRightsCorrectionOutcomeKind.Applied,
            receipt ?? throw new ArgumentNullException(nameof(receipt)));

    public static WorkspaceStaffOnboardingDataRightsCorrectionOutcome
        AuthorityMovedToStaff() =>
        new(
            WorkspaceStaffOnboardingDataRightsCorrectionOutcomeKind
                .AuthorityMovedToStaff,
            Receipt: null);
}

public enum WorkspaceStaffOnboardingDataRightsCorrectionOutcomeKind
{
    Unknown = 0,
    Applied = 1,
    AuthorityMovedToStaff = 2
}
