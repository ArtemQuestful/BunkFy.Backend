namespace BunkFy.Extensions.Workspaces;

using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Messaging;
using Gma.Modules.Auth.Contracts;
using Gma.Modules.Organizations.Contracts;
using Microsoft.Extensions.Options;

[IntegrationEventHandler(HandlerName, RequiresExplicitProducerBinding = true)]
internal sealed class OrganizationOwnerStaffBootstrapHandler(
    IStaffIdentityBootstrapper staff,
    IAuthMemberContactReader contacts,
    IOrganizationMembershipInspector memberships,
    IWorkspaceOperationalAdmissionPolicy operationalAdmission,
    IOptions<BunkFyWorkspacesOptions> options)
    : IIntegrationEventHandler<OrganizationMembershipChangedIntegrationEvent>
{
    public const string HandlerName = "bunkfy-workspace-staff-membership";

    public async Task HandleAsync(
        OrganizationMembershipChangedIntegrationEvent integrationEvent,
        CancellationToken cancellationToken)
    {
        if (integrationEvent.Change != OrganizationMembershipChange.Joined ||
            integrationEvent.Status != OrganizationMembershipStatus.Active ||
            integrationEvent.Role != OrganizationMembershipRole.Owner)
        {
            return;
        }

        string canonicalOrganizationId =
            integrationEvent.OrganizationId.ToString("D");
        if (integrationEvent.OrganizationId == Guid.Empty ||
            integrationEvent.MembershipId == Guid.Empty ||
            integrationEvent.MembershipVersion <= 0 ||
            string.IsNullOrWhiteSpace(integrationEvent.SubjectId) ||
            !string.Equals(
                integrationEvent.ScopeId,
                canonicalOrganizationId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Organizations owner Staff bootstrap coordinates are invalid.");
        }

        OrganizationMembershipSnapshot? snapshot = await memberships.FindAsync(
            integrationEvent.OrganizationId,
            integrationEvent.MembershipId,
            integrationEvent.SubjectId,
            cancellationToken).ConfigureAwait(false);
        if (snapshot is null ||
            snapshot.OrganizationId != integrationEvent.OrganizationId ||
            snapshot.MembershipId != integrationEvent.MembershipId ||
            snapshot.MembershipVersion < integrationEvent.MembershipVersion ||
            snapshot.ScopeRevision < 0 ||
            snapshot.OrganizationStatus == OrganizationStatus.Unknown ||
            snapshot.ScopeStatus is OrganizationScopeStatus.Unknown or
                OrganizationScopeStatus.Invalid or
                OrganizationScopeStatus.Missing ||
            snapshot.Role == OrganizationMembershipRole.Unknown ||
            snapshot.MembershipStatus == OrganizationMembershipStatus.Unknown)
        {
            throw new InvalidOperationException(
                "Organizations membership snapshot is unavailable for owner Staff bootstrap.");
        }

        if (snapshot.ScopeStatus == OrganizationScopeStatus.Closed ||
            snapshot.OrganizationStatus is OrganizationStatus.Suspended or
                OrganizationStatus.Archived ||
            snapshot.Role == OrganizationMembershipRole.Member ||
            snapshot.MembershipStatus is OrganizationMembershipStatus.Suspended or
                OrganizationMembershipStatus.Removed)
        {
            return;
        }

        if (snapshot.ScopeStatus != OrganizationScopeStatus.Open ||
            snapshot.OrganizationStatus != OrganizationStatus.Active ||
            snapshot.Role != OrganizationMembershipRole.Owner ||
            snapshot.MembershipStatus != OrganizationMembershipStatus.Active)
        {
            throw new InvalidOperationException(
                "Organizations membership snapshot is invalid for owner Staff bootstrap.");
        }

        WorkspaceOperationalAdmissionDecision admission =
            await operationalAdmission.EvaluateAsync(
                integrationEvent.ScopeId,
                cancellationToken).ConfigureAwait(false);
        if (admission.Outcome != WorkspaceOperationalAdmissionOutcome.Allowed)
        {
            throw new InvalidOperationException(
                "Workspace operational admission did not allow owner Staff bootstrap.");
        }

        string? verifiedEmail = await this.GetVerifiedEmailAsync(
            integrationEvent.SubjectId,
            cancellationToken).ConfigureAwait(false);
        string displayName = verifiedEmail ?? DefaultDisplayName(integrationEvent.SubjectId);
        StaffIdentityBootstrapResult result = await staff.BootstrapAsync(
            new StaffIdentityBootstrapRequest(
                integrationEvent.EventId,
                integrationEvent.MembershipId,
                integrationEvent.SubjectId,
                displayName,
                verifiedEmail,
                "integration:organizations"),
            cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(
                $"Staff identity bootstrap failed with '{result.ErrorCode}'.");
        }
    }

    private async Task<string?> GetVerifiedEmailAsync(
        string subjectId,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(subjectId, out Guid memberId))
        {
            return null;
        }

        return await contacts.GetPreferredVerifiedEmailAsync(
            options.Value.GlobalAuthScopeId,
            memberId,
            cancellationToken).ConfigureAwait(false);
    }

    private static string DefaultDisplayName(string subjectId)
    {
        const int visibleCharacters = 12;
        string suffix = subjectId.Length <= visibleCharacters
            ? subjectId
            : subjectId[..visibleCharacters];
        return $"Workspace member {suffix}";
    }
}
