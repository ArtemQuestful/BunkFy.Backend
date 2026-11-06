namespace BunkFy.Extensions.Workspaces;

using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.AccessControl;
using Gma.Modules.AccessControl.Contracts;
using Gma.Modules.Organizations.Contracts;
using Microsoft.Extensions.Logging;

internal sealed class WorkspaceOwnerMembershipAccessDecisionProvider(
    IAccessControlRoleProvisioner accessControl,
    IOrganizationMembershipReader memberships,
    ILogger<WorkspaceOwnerMembershipAccessDecisionProvider> logger)
    : IAccessDecisionProvider
{
    private const string StaleReason = "bunkfy.workspace.owner-membership-stale";
    private const string UnavailableReason = "bunkfy.workspace.owner-membership-unavailable";

    public async Task<AccessDecision> DecideAsync(
        AccessRequirement requirement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        if (!TryGetWorkspaceId(requirement, out string workspaceId))
        {
            return AccessDecision.Abstain();
        }

        return await this.DecideWorkspaceAsync(
            requirement.Subject,
            workspaceId,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AccessDecision>> DecideManyAsync(
        IReadOnlyList<AccessRequirement> requirements,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        if (requirements.Any(requirement => requirement is null))
        {
            throw new ArgumentException(
                "Authorization requirements cannot contain null values.",
                nameof(requirements));
        }

        Dictionary<(string SubjectId, string WorkspaceId), AccessDecision> decisions = [];
        AccessDecision[] results = new AccessDecision[requirements.Count];
        for (int index = 0; index < requirements.Count; index++)
        {
            AccessRequirement requirement = requirements[index];
            if (!TryGetWorkspaceId(requirement, out string workspaceId))
            {
                results[index] = AccessDecision.Abstain();
                continue;
            }

            var key = (requirement.Subject.Id, workspaceId);
            if (!decisions.TryGetValue(key, out AccessDecision? decision))
            {
                decision = await this.DecideWorkspaceAsync(
                    requirement.Subject,
                    workspaceId,
                    cancellationToken).ConfigureAwait(false);
                decisions.Add(key, decision);
            }

            results[index] = decision;
        }

        return results;
    }

    private async Task<AccessDecision> DecideWorkspaceAsync(
        AccessSubject subject,
        string workspaceId,
        CancellationToken cancellationToken)
    {
        AccessScope workspaceScope = WorkspaceAccessScopes.Create(workspaceId);
        bool hasOwnerAssignment = await accessControl.HasAssignmentAsync(
            subject,
            WorkspaceAccessRoles.Owner,
            workspaceScope,
            cancellationToken).ConfigureAwait(false);
        if (!hasOwnerAssignment)
        {
            return AccessDecision.Abstain();
        }

        AccessDecision decision;
        if (!Guid.TryParseExact(workspaceId, "D", out Guid organizationId) ||
            organizationId == Guid.Empty)
        {
            decision = AccessDecision.Denied(
                StaleReason,
                "The workspace owner assignment is not backed by an active organization owner.");
        }
        else
        {
            try
            {
                OrganizationMembershipSnapshotDto? snapshot = await memberships.FindAsync(
                    organizationId,
                    subject.Id,
                    cancellationToken).ConfigureAwait(false);
                decision = snapshot is
                {
                    OrganizationStatus: OrganizationStatus.Active,
                    Membership:
                    {
                        Status: OrganizationMembershipStatus.Active,
                        Role: OrganizationMembershipRole.Owner
                    }
                }
                    ? AccessDecision.Abstain()
                    : AccessDecision.Denied(
                        StaleReason,
                        "The workspace owner assignment is not backed by an active organization owner.");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(
                    "Workspace owner membership verification failed because {ExceptionType} was raised.",
                    exception.GetType().Name);
                decision = AccessDecision.Denied(
                    UnavailableReason,
                    "Workspace owner membership could not be verified.");
            }
        }

        return decision;
    }

    private static bool TryGetWorkspaceId(
        AccessRequirement requirement,
        out string workspaceId)
    {
        if (requirement.Subject.Kind == AccessSubjectKind.User &&
            WorkspaceAccessScopes.IsWorkspaceOrPropertyScope(requirement.Scope))
        {
            workspaceId = requirement.Scope.Segments[0].Value;
            return true;
        }

        workspaceId = string.Empty;
        return false;
    }
}
