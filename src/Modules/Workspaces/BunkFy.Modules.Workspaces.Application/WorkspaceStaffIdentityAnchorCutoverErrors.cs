namespace BunkFy.Modules.Workspaces.Application;

using Gma.Framework.Results;

public static class WorkspaceStaffIdentityAnchorCutoverErrors
{
    public static readonly Error TenantRequired = new(
        "Workspaces.IdentityAnchorCutoverTenantRequired",
        "A tenant context is required for identity-anchor cutover.");
    public static readonly Error RequestInvalid = new(
        "Workspaces.IdentityAnchorCutoverRequestInvalid",
        "The identity-anchor cutover request is invalid.");
    public static readonly Error OwnerManifestRequired = new(
        "Workspaces.IdentityAnchorCutoverOwnerManifestRequired",
        "An explicitly reviewed owner-membership binding manifest is required.");
    public static readonly Error OwnerManifestInvalid = new(
        "Workspaces.IdentityAnchorCutoverOwnerManifestInvalid",
        "The owner-membership binding manifest is invalid for this tenant.");
    public static readonly Error SourceEvidenceChanged = new(
        "Workspaces.IdentityAnchorCutoverSourceEvidenceChanged",
        "The Workspaces source evidence changed after operator review.");
    public static readonly Error AnchorStateChanged = new(
        "Workspaces.IdentityAnchorCutoverAnchorStateChanged",
        "The Staff identity-anchor state changed after operator review.");
    public static readonly Error SourcePageInvalid = new(
        "Workspaces.IdentityAnchorCutoverSourcePageInvalid",
        "The Workspaces identity-anchor source page did not advance deterministically.");
    public static readonly Error OwnerManifestChanged = new(
        "Workspaces.IdentityAnchorCutoverOwnerManifestChanged",
        "The owner-membership binding manifest digest does not match operator review.");
    public static readonly Error Blocked = new(
        "Workspaces.IdentityAnchorCutoverBlocked",
        "Ambiguous, conflicting, or unproven identity evidence blocks reconciliation.");
    public static readonly Error StaffUnavailable = new(
        "Workspaces.IdentityAnchorCutoverStaffUnavailable",
        "Staff identity-anchor inspection or reconciliation is unavailable.");
    public static readonly Error ApplyOutcomeUnknown = new(
        "Workspaces.IdentityAnchorCutoverApplyOutcomeUnknown",
        "The Staff identity-anchor apply outcome is indeterminate; archive the output and rebuild status before further reconciliation.");
    public static readonly Error OrganizationsUnavailable = new(
        "Workspaces.IdentityAnchorCutoverOrganizationsUnavailable",
        "Revision-fenced Organizations identity evidence is unavailable.");
    public static readonly Error OrganizationsEvidenceChanged = new(
        "Workspaces.IdentityAnchorCutoverOrganizationsEvidenceChanged",
        "Organizations identity evidence changed while the cutover plan was being built.");
}
