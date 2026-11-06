namespace BunkFy.Modules.Workspaces.Domain;

using Gma.Framework.Results;

public static class WorkspaceStaffRetentionErrors
{
    public static readonly Error ExecutionCoordinateInvalid = new(
        "Workspaces.StaffOnboardingRetentionExecutionCoordinateInvalid",
        "The Staff onboarding retention execution coordinate is invalid.");
    public static readonly Error ExecutionTransitionInvalid = new(
        "Workspaces.StaffOnboardingRetentionExecutionTransitionInvalid",
        "The Staff onboarding retention execution transition is invalid.");
    public static readonly Error ExecutionResultInvalid = new(
        "Workspaces.StaffOnboardingRetentionExecutionResultInvalid",
        "The Staff onboarding retention execution result is invalid.");
    public static readonly Error RequestInvalid = new(
        "Workspaces.StaffRetentionCorrelationRequestInvalid",
        "The workspace Staff retention correlation request is invalid.");

    public static readonly Error ReceiptInvalid = new(
        "Workspaces.StaffRetentionCorrelationReceiptInvalid",
        "The workspace Staff retention correlation receipt is invalid.");

    public static readonly Error ActiveOnboarding = new(
        "Workspaces.StaffRetentionCorrelationActiveOnboarding",
        "Active workspace Staff onboarding prevents correlation scrubbing.");

    public static readonly Error ActiveAccessProcess = new(
        "Workspaces.StaffRetentionCorrelationActiveAccessProcess",
        "An active workspace Staff access process prevents correlation scrubbing.");

    public static readonly Error AccessMappingConflict = new(
        "Workspaces.StaffRetentionCorrelationAccessMappingConflict",
        "The workspace Staff departure mapping does not match the retention request.");

    public static readonly Error IdentityAnchorUnavailable = new(
        "Workspaces.StaffRetentionCorrelationIdentityAnchorUnavailable",
        "Workspace Staff identity-anchor resolution must be exact and observed before retention correlation scrubbing.");
}
