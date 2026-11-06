namespace BunkFy.Modules.Workspaces.Api;

using Gma.Framework.Security;

public sealed class WorkspacesApiSecurityOptions
{
    public AuthenticationAssuranceRequirement? StaffOnboardingManagementAssurance { get; set; }
    public bool StaffOnboardingRecoveryPostEnabled { get; set; }
}
