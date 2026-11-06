namespace BunkFy.Extensions.Workspaces.Tests;

using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.AccessControl;
using Gma.Framework.Messaging;
using Gma.Framework.Tenancy;
using Gma.Modules.AccessControl.Contracts;
using Gma.Modules.Organizations.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspaceAccessCompositionTests
{
    [Fact]
    public void Workspace_extension_registers_exact_scope_aware_subscriptions_and_access_guards()
    {
        ServiceCollection services = new();

        services.AddBunkFyWorkspaces();

        IntegrationEventSubscription[] subscriptions = services
            .Where(descriptor => descriptor.ServiceType == typeof(IntegrationEventSubscription))
            .Select(descriptor => descriptor.ImplementationInstance)
            .OfType<IntegrationEventSubscription>()
            .ToArray();
        Assert.Equal(2, subscriptions.Length);
        IntegrationEventSubscription membership = Assert.Single(
            subscriptions,
            item => item.EventType ==
                typeof(OrganizationMembershipChangedIntegrationEvent));
        Assert.Equal(StaffModuleMetadata.Name, membership.ConsumerModule);
        Assert.Equal(OrganizationsModuleMetadata.Name, membership.ProducerModule);
        Assert.Equal(typeof(OrganizationOwnerStaffBootstrapHandler), membership.HandlerType);
        Assert.Equal(OrganizationOwnerStaffBootstrapHandler.HandlerName, membership.HandlerName);
        IntegrationEventSubscription resolution = Assert.Single(
            subscriptions,
            item => item.EventType ==
                typeof(WorkspaceStaffOnboardingIdentityAnchorResolvedIntegrationEvent));
        Assert.Equal(StaffModuleMetadata.Name, resolution.ConsumerModule);
        Assert.Equal(WorkspacesModuleMetadata.Name, resolution.ProducerModule);
        Assert.Equal(typeof(WorkspaceStaffOnboardingIdentityAnchorResolutionHandler), resolution.HandlerType);
        Assert.Equal(WorkspaceStaffOnboardingIdentityAnchorResolutionHandler.HandlerName, resolution.HandlerName);
        Assert.All(subscriptions, subscription =>
            Assert.True(subscription.IsTenantScoped()));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IAccessProfileAssignmentPolicy) &&
            descriptor.ImplementationType == typeof(WorkspaceAccessProfileAssignmentPolicy));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IAccessRoleAssignmentPolicy) &&
            descriptor.ImplementationType == typeof(WorkspaceOwnerRoleAssignmentPolicy));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IAccessDecisionProvider) &&
            descriptor.ImplementationType == typeof(WorkspaceOwnerMembershipAccessDecisionProvider));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IOrganizationMutationAdmissionPolicy) &&
            descriptor.ImplementationType == typeof(WorkspaceOrganizationMutationAdmissionPolicy));
    }

    [Fact]
    public void Member_role_is_a_front_desk_baseline_without_administration_permissions()
    {
        Assert.Contains(
            ReservationsAdminPermissionCodes.Create,
            WorkspaceAccessRoles.LegacyMemberPermissions);
        Assert.Contains(
            ReservationsAdminPermissionCodes.CheckIn,
            WorkspaceAccessRoles.LegacyMemberPermissions);
        Assert.Contains(
            GuestsAdminPermissionCodes.Manage,
            WorkspaceAccessRoles.LegacyMemberPermissions);
        Assert.Contains(
            InventoryAdminPermissionCodes.BlocksManage,
            WorkspaceAccessRoles.LegacyMemberPermissions);
        Assert.Contains(
            StaffAdminPermissionCodes.Read,
            WorkspaceAccessRoles.LegacyMemberPermissions);
        Assert.DoesNotContain(
            "properties.properties.manage",
            WorkspaceAccessRoles.LegacyMemberPermissions);
        Assert.DoesNotContain(
            StaffAdminPermissionCodes.Manage,
            WorkspaceAccessRoles.LegacyMemberPermissions);
        Assert.DoesNotContain(
            "ingestion.connections.manage",
            WorkspaceAccessRoles.LegacyMemberPermissions);
        Assert.Empty(WorkspaceAccessRoles.MembershipMarkerPermissions);
    }

    [Fact]
    public void Custom_profiles_are_limited_to_the_explicit_operational_allowlist()
    {
        Assert.All(WorkspaceAccessRoles.LegacyMemberPermissions, permission =>
            Assert.Contains(permission, WorkspaceAccessRoles.DelegablePermissions));
        Assert.All(WorkspaceAccessProfileSeeds.Manager.Permissions, permission =>
            Assert.Contains(permission, WorkspaceAccessRoles.DelegablePermissions));
        Assert.Equal(
            WorkspaceAccessRoles.DelegablePermissions.Count,
            WorkspaceAccessRoles.DelegablePermissions.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(WorkspaceAccessRoles.OwnerPermissions, permission =>
            WorkspaceAccessRoles.DelegablePermissions.Contains(permission, StringComparer.Ordinal));
    }
}
