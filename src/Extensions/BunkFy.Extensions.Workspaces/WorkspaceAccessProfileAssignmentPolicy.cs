namespace BunkFy.Extensions.Workspaces;

using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.AccessControl;
using Gma.Modules.AccessControl.Contracts;

internal sealed class WorkspaceAccessProfileAssignmentPolicy(
    IAccessControlRoleProvisioner accessControl,
    IAccessAuthorizationService authorization) : IAccessProfileAssignmentPolicy
{
    public async ValueTask<bool> IsAllowedAsync(
        AccessProfileAssignmentPolicyContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Subject.Kind != AccessSubjectKind.User ||
            !WorkspaceAccessScopes.IsWorkspaceOrPropertyScope(
                context.OwnerScope,
                context.AssignmentScope) ||
            context.Permissions.Any(permission =>
                !WorkspaceAccessRoles.DelegablePermissions.Contains(
                    permission,
                    StringComparer.Ordinal)))
        {
            return false;
        }

        bool targetIsOwner = await accessControl.HasAssignmentAsync(
                context.Subject,
                WorkspaceAccessRoles.Owner,
                context.OwnerScope,
                cancellationToken).ConfigureAwait(false);
        if (targetIsOwner)
        {
            return false;
        }

        bool targetIsMember = await accessControl.HasAssignmentAsync(
                context.Subject,
                WorkspaceAccessRoles.MembershipMarker,
                context.OwnerScope,
                cancellationToken)
            .ConfigureAwait(false) || await accessControl.HasAssignmentAsync(
                context.Subject,
                WorkspaceAccessRoles.LegacyMember,
                context.OwnerScope,
                cancellationToken)
            .ConfigureAwait(false);
        if (!targetIsMember)
        {
            return false;
        }

        if (context.Actor.Kind == AccessSubjectKind.System)
        {
            return string.Equals(
                context.Actor.Id,
                WorkspaceAccessActors.Provisioner,
                StringComparison.Ordinal);
        }

        if (context.Actor.Kind != AccessSubjectKind.User)
        {
            return false;
        }

        if (await accessControl.HasAssignmentAsync(
                context.Actor,
                WorkspaceAccessRoles.Owner,
                context.OwnerScope,
                cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        AccessRequirement[] requirements = context.Permissions
            .Select(permission => new AccessRequirement(
                context.Actor,
                Gma.Framework.Permissions.PermissionCode.Create(permission),
                context.AssignmentScope))
            .Append(new AccessRequirement(
                context.Actor,
                Gma.Framework.Permissions.PermissionCode.Create(
                    AccessControlProfilePermissionCodes.Assign),
                context.OwnerScope))
            .ToArray();
        IReadOnlyList<AccessDecision> decisions = await authorization.AuthorizeManyAsync(
            requirements,
            cancellationToken).ConfigureAwait(false);
        return decisions.All(decision => decision.IsAllowed);
    }
}
