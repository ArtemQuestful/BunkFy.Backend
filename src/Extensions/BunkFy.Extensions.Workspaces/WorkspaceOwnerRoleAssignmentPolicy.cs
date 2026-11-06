namespace BunkFy.Extensions.Workspaces;

using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.AccessControl;
using Gma.Modules.AccessControl.Contracts;
using Gma.Modules.Organizations.Contracts;

internal sealed class WorkspaceOwnerRoleAssignmentPolicy(
    IOrganizationMembershipReader memberships) : IAccessRoleAssignmentPolicy
{
    public async ValueTask<bool> IsAllowedAsync(
        AccessRoleAssignmentPolicyContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!string.Equals(
                context.RoleName,
                WorkspaceAccessRoles.Owner,
                StringComparison.Ordinal))
        {
            return true;
        }

        if (!WorkspaceAccessScopes.IsWorkspaceScope(context.AccessScope) ||
            context.Subject.Kind != AccessSubjectKind.User)
        {
            return false;
        }

        string workspaceId = context.AccessScope.Segments[0].Value;
        if (!Guid.TryParseExact(workspaceId, "D", out Guid organizationId) ||
            organizationId == Guid.Empty)
        {
            return false;
        }

        OrganizationMembershipSnapshotDto? snapshot = await memberships.FindAsync(
            organizationId,
            context.Subject.Id,
            cancellationToken).ConfigureAwait(false);
        return snapshot is
        {
            OrganizationStatus: OrganizationStatus.Active,
            Membership:
            {
                Status: OrganizationMembershipStatus.Active,
                Role: OrganizationMembershipRole.Owner
            }
        };
    }
}
