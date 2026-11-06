namespace BunkFy.Modules.Workspaces.Application.Handlers;

using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.AccessControl;
using Gma.Framework.Messaging;
using Gma.Modules.AccessControl.Contracts;
using Gma.Modules.Organizations.Contracts;

[IntegrationEventHandler(HandlerName, RequiresExplicitProducerBinding = true)]
internal sealed class OrganizationMembershipAccessProfileSeedHandler(
    WorkspaceAccessProvisioner provisioner,
    WorkspaceStaffAccessMutationCoordinator mutations,
    IAccessControlRoleProvisioner roles,
    IOrganizationMembershipReader memberships,
    IStaffOperationalIdentityReader staff,
    WorkspaceOperationalAdmissionEvaluator operationalAdmission)
    : IIntegrationEventHandler<OrganizationMembershipChangedIntegrationEvent>
{
    public const string HandlerName = WorkspacesModuleMetadata.MembershipAccessSeedHandlerName;

    public async Task HandleAsync(
        OrganizationMembershipChangedIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        string workspaceId = integrationEvent.OrganizationId.ToString("D");
        if (!string.Equals(
                integrationEvent.ScopeId,
                workspaceId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The Organizations membership event does not match its workspace scope.");
        }

        await mutations.AcquireSubjectAsync(
                integrationEvent.SubjectId,
                cancellationToken).ConfigureAwait(false);

        StaffOperationalIdentitySnapshot? identity = await staff.FindAsync(
            workspaceId,
            integrationEvent.SubjectId,
            cancellationToken).ConfigureAwait(false);
        if (identity is not null)
        {
            Guid lockedStaffMemberId = identity.StaffMemberId;
            await mutations.AcquireStaffAsync(
                lockedStaffMemberId,
                cancellationToken).ConfigureAwait(false);
            identity = await staff.FindAsync(
                workspaceId,
                integrationEvent.SubjectId,
                cancellationToken).ConfigureAwait(false);
            if (identity is not null &&
                identity.StaffMemberId != lockedStaffMemberId)
            {
                throw new InvalidOperationException(
                    "The Staff operational identity changed while workspace access was being reconciled.");
            }
        }

        OrganizationMembershipDto? membership = await this.ReadCurrentAsync(
            integrationEvent,
            cancellationToken).ConfigureAwait(false);
        if (membership is
            {
                Status: OrganizationMembershipStatus.Active,
                Role: OrganizationMembershipRole.Owner
            })
        {
            await this.RequireOperationalAsync(workspaceId, cancellationToken)
                .ConfigureAwait(false);
            await provisioner.EnsureSeedProfilesAsync(workspaceId, cancellationToken)
                .ConfigureAwait(false);
            await roles.EnsureRoleAsync(
                new AccessControlRoleDefinition(
                    WorkspaceAccessRoles.Owner,
                    WorkspaceAccessRoles.OwnerPermissions),
                cancellationToken).ConfigureAwait(false);
            await roles.EnsureAssignmentAsync(
                AccessSubject.User(integrationEvent.SubjectId),
                WorkspaceAccessRoles.Owner,
                WorkspaceAccessScopes.Create(workspaceId),
                cancellationToken).ConfigureAwait(false);
            await provisioner.DenyMemberAsync(
                workspaceId,
                integrationEvent.SubjectId,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await this.RemoveOwnerAsync(
            workspaceId,
            integrationEvent.SubjectId,
            cancellationToken).ConfigureAwait(false);
        if (membership is
            {
                Status: OrganizationMembershipStatus.Active,
                Role: OrganizationMembershipRole.Member
            } &&
            identity is { Status: StaffStatus.Active })
        {
            try
            {
                await this.RequireOperationalAsync(workspaceId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                await provisioner.DenyMemberAsync(
                    workspaceId,
                    integrationEvent.SubjectId,
                    cancellationToken).ConfigureAwait(false);
                throw;
            }

            await provisioner.ProvisionDefaultMemberAsync(
                workspaceId,
                integrationEvent.SubjectId,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await provisioner.DenyMemberAsync(
            workspaceId,
            integrationEvent.SubjectId,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<OrganizationMembershipDto?> ReadCurrentAsync(
        OrganizationMembershipChangedIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        OrganizationMembershipSnapshotDto? snapshot = await memberships.FindAsync(
            integrationEvent.OrganizationId,
            integrationEvent.SubjectId,
            cancellationToken).ConfigureAwait(false);
        if (snapshot?.OrganizationStatus != OrganizationStatus.Active)
        {
            return null;
        }

        OrganizationMembershipDto? membership = snapshot.Membership;
        if (membership?.MembershipId == integrationEvent.MembershipId &&
            membership.Version < integrationEvent.MembershipVersion)
        {
            throw new InvalidOperationException(
                "The Organizations membership snapshot is behind its integration event.");
        }

        return membership;
    }

    private async Task RemoveOwnerAsync(
        string workspaceId,
        string subjectId,
        CancellationToken cancellationToken)
    {
        AccessControlAssignmentRemovalOutcome outcome = await roles.RemoveAssignmentAsync(
            AccessSubject.User(subjectId),
            WorkspaceAccessRoles.Owner,
            WorkspaceAccessScopes.Create(workspaceId),
            cancellationToken).ConfigureAwait(false);
        if (outcome is AccessControlAssignmentRemovalOutcome.LastOwnerProtected or
            AccessControlAssignmentRemovalOutcome.Unknown)
        {
            throw new InvalidOperationException(
                "AccessControl could not remove the obsolete workspace owner assignment.");
        }
    }

    private async Task RequireOperationalAsync(
        string workspaceId,
        CancellationToken cancellationToken)
    {
        WorkspaceOperationalAdmissionDecision admission =
            await operationalAdmission.EvaluateAsync(
                workspaceId,
                cancellationToken).ConfigureAwait(false);
        if (admission.Outcome != WorkspaceOperationalAdmissionOutcome.Allowed)
        {
            throw new InvalidOperationException(
                "Workspace operational admission did not allow access-profile reconciliation.");
        }
    }
}
