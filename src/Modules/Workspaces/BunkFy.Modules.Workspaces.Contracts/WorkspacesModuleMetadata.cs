namespace BunkFy.Modules.Workspaces.Contracts;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Staff.Contracts;
using Gma.Framework.Messaging;
using Gma.Framework.ModuleComposition;
using Gma.Framework.Modules;
using Gma.Framework.Permissions;
using Gma.Framework.Tasks;
using Gma.Modules.AccessControl.Contracts;
using Gma.Modules.Auth.Contracts;
using Gma.Modules.Organizations.Contracts;

public static class WorkspacesModuleMetadata
{
    public const string Name = "workspaces";
    public const string Schema = "workspaces";
    public const string InvitationChangedHandlerName = "staff-invitation-changed";
    public const string InvitationExpiredHandlerName = "staff-invitation-expired";
    public const string EnrollmentClaimChangedHandlerName = "staff-enrollment-claim-changed";
    public const string EnrollmentClaimExpiredHandlerName = "staff-enrollment-claim-expired";
    public const string EnrollmentClaimWithdrawnHandlerName = "staff-enrollment-claim-withdrawn";
    public const string EnrollmentLinkChangedHandlerName = "staff-enrollment-link-changed";
    public const string EnrollmentLinkExpiredHandlerName = "staff-enrollment-link-expired";
    public const string MembershipAccessSeedHandlerName = "bunkfy-workspace-access-profile-seeds";
    public const string StaffAccessLifecycleHandlerName = "bunkfy-workspace-staff-access-lifecycle";
    public const string PropertyCreatedHandlerName = "workspace-property-created";
    public const string PropertyUpdatedHandlerName = "workspace-property-updated";
    public const string PropertyRetiredHandlerName = "workspace-property-retired";
    public const string StaffOnboardingRestrictionRecoveryHandlerName =
        "staff-onboarding-restriction-release-recovery";
    public const string StaffOnboardingIdentityAnchorCreatedHandlerName =
        "staff-onboarding-identity-anchor-created";
    public const string StaffOnboardingIdentityAnchorContinuationHandlerName =
        "staff-onboarding-identity-anchor-continuation";
    public const string PropertiesProjectionName = "properties";
    public const int PropertiesProjectionVersion = 1;
    public const string ProjectionWorkerGroup = "projection-workers";

    public static ModuleDescriptor Descriptor { get; } = ModuleDescriptor
        .Create(Name)
        .WithSchema(Schema)
        .WithPermissions([
            new ModulePermissionDescriptor(
                WorkspacesPermissionCodes.StaffOnboardingManage,
                "Manage Workspaces-owned Staff onboarding sources and decisions.",
                PermissionScopeRequirement.Scoped,
                PermissionScopeGrantPolicy.Descendants)
        ])
        .WithSubscription<OrganizationInvitationChangedIntegrationEvent>(
            OrganizationsModuleMetadata.Name,
            InvitationChangedHandlerName)
        .WithSubscription<OrganizationInvitationExpiredIntegrationEvent>(
            OrganizationsModuleMetadata.Name,
            InvitationExpiredHandlerName)
        .WithSubscription<OrganizationEnrollmentClaimChangedIntegrationEvent>(
            OrganizationsModuleMetadata.Name,
            EnrollmentClaimChangedHandlerName)
        .WithSubscription<OrganizationEnrollmentClaimExpiredIntegrationEvent>(
            OrganizationsModuleMetadata.Name,
            EnrollmentClaimExpiredHandlerName)
        .WithSubscription<OrganizationEnrollmentClaimWithdrawnIntegrationEvent>(
            OrganizationsModuleMetadata.Name,
            EnrollmentClaimWithdrawnHandlerName)
        .WithSubscription<OrganizationEnrollmentLinkChangedIntegrationEvent>(
            OrganizationsModuleMetadata.Name,
            EnrollmentLinkChangedHandlerName)
        .WithSubscription<OrganizationEnrollmentLinkExpiredIntegrationEvent>(
            OrganizationsModuleMetadata.Name,
            EnrollmentLinkExpiredHandlerName)
        .WithSubscription<OrganizationMembershipChangedIntegrationEvent>(
            OrganizationsModuleMetadata.Name,
            MembershipAccessSeedHandlerName)
        .WithSubscription<StaffMemberLifecycleChangedIntegrationEvent>(
            StaffModuleMetadata.Name,
            StaffAccessLifecycleHandlerName)
        .WithSubscription<PropertyCreatedIntegrationEvent>(
            PropertiesModuleMetadata.Name,
            PropertyCreatedHandlerName)
        .WithSubscription<PropertyUpdatedIntegrationEvent>(
            PropertiesModuleMetadata.Name,
            PropertyUpdatedHandlerName)
        .WithSubscription<PropertyRetiredIntegrationEvent>(
            PropertiesModuleMetadata.Name,
            PropertyRetiredHandlerName)
        .WithSubscription<
            WorkspaceStaffOnboardingProcessingRestrictionChangedIntegrationEvent>(
            Name,
            StaffOnboardingRestrictionRecoveryHandlerName)
        .WithSubscription<StaffIdentityProvisioningAnchorCreatedIntegrationEvent>(
            StaffModuleMetadata.Name,
            StaffOnboardingIdentityAnchorCreatedHandlerName)
        .WithSubscription<
            WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedIntegrationEvent>(
            Name,
            StaffOnboardingIdentityAnchorContinuationHandlerName)
        .WithPublishedEvent<
            DataRightsTenantCorrectionAppliedIntegrationEvent>()
        .WithPublishedEvent<
            WorkspaceStaffOnboardingProcessingRestrictionChangedIntegrationEvent>()
        .WithPublishedEvent<
            WorkspaceStaffOnboardingIdentityAnchorResolvedIntegrationEvent>()
        .WithPublishedEvent<
            WorkspaceStaffOnboardingIdentityAnchorContinuationRequestedIntegrationEvent>()
        .WithTask<RebuildWorkspacePropertiesPayload>()
        .WithTask<ReconcileWorkspaceStaffIdentityAnchorsPayload>()
        .WithProfile(WorkspacesProfiles.Default)
        .Build();
}

public static class WorkspacesProfiles
{
    public const string DefaultName = "default";

    public static ModuleProfileDescriptor Default { get; } = new(
        WorkspacesModuleMetadata.Name,
        DefaultName,
        provides: [],
        requires:
        [
            MessagingCompositionFeatures.OutboxRequired(
                Provider,
                "Workspaces publishes Staff onboarding rights completion through its durable outbox."),
            MessagingCompositionFeatures.NatsConsumersRequired(
                Provider,
                "Organizations join facts drive durable BunkFy Staff provisioning.",
                optional: true)
        ],
        requiredModules:
        [
            new RequiredCompositionModule(AuthModuleMetadata.Name, Provider,
                reason: "Applicant identity and verified email are Auth-owned."),
            new RequiredCompositionModule(OrganizationsModuleMetadata.Name, Provider,
                reason: "Invitations, enrollment claims, and memberships are Organizations-owned."),
            new RequiredCompositionModule(AccessControlModuleMetadata.Name, Provider,
                reason: "Completed onboarding installs a constrained workspace assignment."),
            new RequiredCompositionModule(StaffModuleMetadata.Name, Provider,
                reason: "Accepted applicant profiles become Staff-owned employment records."),
            new RequiredCompositionModule(PropertiesModuleMetadata.Name, Provider,
                reason: "Invitation access plans validate their property scope from a local projection.")
        ],
        displayName: "BunkFy workspaces",
        description: "Product-owned Staff enrollment and workspace provisioning coordination.");

    private static string Provider => $"{WorkspacesModuleMetadata.Name}/{DefaultName}";
}
