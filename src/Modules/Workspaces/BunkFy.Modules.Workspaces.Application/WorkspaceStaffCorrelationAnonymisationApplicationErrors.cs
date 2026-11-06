namespace BunkFy.Modules.Workspaces.Application;

using Gma.Framework.Results;

public static class
    WorkspaceStaffCorrelationAnonymisationApplicationErrors
{
    public static readonly Error TenantRequired = new(
        "Workspaces.StaffCorrelationAnonymisationTenantRequired",
        "A tenant context is required for workspace Staff correlation anonymisation.");

    public static readonly Error RequestInvalid = new(
        "Workspaces.StaffCorrelationAnonymisationRequestInvalid",
        "The workspace Staff correlation anonymisation request is invalid.");

    public static readonly Error ApprovalRequired = new(
        "Workspaces.StaffCorrelationAnonymisationApprovalRequired",
        "An active approved workspace Staff correlation anonymisation execution is required.");

    public static readonly Error IdempotencyConflict = new(
        "Workspaces.StaffCorrelationAnonymisationIdempotencyConflict",
        "The workspace Staff correlation anonymisation idempotency key was already used for different coordinates.");

    public static readonly Error ProofUnavailable = new(
        "Workspaces.StaffCorrelationAnonymisationProofUnavailable",
        "The committed workspace Staff correlation anonymisation proof is unavailable or inconsistent.");

    public static readonly Error OperationLockUnavailable = new(
        "Workspaces.StaffCorrelationAnonymisationOperationLockUnavailable",
        "The workspace Staff correlation anonymisation operation lock is unavailable.");

    public static readonly Error StateChanged = new(
        "Workspaces.StaffCorrelationAnonymisationBlocked.FrozenStateChanged",
        "The workspace Staff correlation state changed after approval.");

    public static readonly Error NotEligible = new(
        "Workspaces.StaffCorrelationAnonymisationBlocked.NotEligible",
        "The workspace Staff correlation is not eligible for anonymisation.");

    public static readonly Error IdentityAnchorUnavailable = new(
        "Workspaces.StaffCorrelationAnonymisationBlocked.IdentityAnchorUnavailable",
        "Workspace Staff identity-anchor resolution must be exact and observed before correlation anonymisation.");

    public static readonly Error RestoreRequestInvalid = new(
        "Workspaces.StaffCorrelationAnonymisationRestoreRequestInvalid",
        "The workspace Staff correlation anonymisation restore request is invalid.");

    public static readonly Error RestoreProofConflict = new(
        "Workspaces.StaffCorrelationAnonymisationRestoreProofConflict",
        "The workspace Staff correlation anonymisation restore proof conflicts with current state.");
}
