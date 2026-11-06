namespace BunkFy.Modules.Workspaces.Tests.Contracts;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Application.Contributors;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.AccessControl;
using Gma.Framework.Messaging;
using Gma.Framework.ModuleComposition;
using Gma.Framework.Permissions;
using Gma.Modules.Organizations.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspacesModuleMetadataTests
{
    [Fact]
    public void Descriptor_exposes_scoped_staff_onboarding_permission()
    {
        ModulePermissionDescriptor permission = Assert.Single(
            WorkspacesModuleMetadata.Descriptor.GetPermissions());

        Assert.Equal(
            WorkspacesPermissionCodes.StaffOnboardingManage,
            permission.Code);
        Assert.Equal(
            PermissionScopeRequirement.Scoped,
            permission.ScopeRequirement);
        Assert.Equal(
            PermissionScopeGrantPolicy.Descendants,
            permission.ScopeGrantPolicy);
    }

    [Fact]
    public void Descriptor_publishes_rights_completion_and_recovers_releases()
    {
        Assert.Contains(
            WorkspacesModuleMetadata.Descriptor.GetPublishedEvents(),
            published =>
                published.EventType ==
                DataRightsTenantCorrectionAppliedIntegrationEvent.EventType);
        Assert.Contains(
            WorkspacesModuleMetadata.Descriptor.GetPublishedEvents(),
            published =>
                published.EventType ==
                WorkspaceStaffOnboardingProcessingRestrictionChangedIntegrationEvent
                    .EventType);
        Assert.Contains(
            WorkspacesModuleMetadata.Descriptor.GetSubscriptions(),
            subscription =>
                subscription.ProducerModule ==
                    WorkspacesModuleMetadata.Name &&
                subscription.EventType ==
                    WorkspaceStaffOnboardingProcessingRestrictionChangedIntegrationEvent
                        .EventType &&
                subscription.HandlerName ==
                    WorkspacesModuleMetadata
                        .StaffOnboardingRestrictionRecoveryHandlerName);
        Assert.Contains(
            Assert.Single(
                WorkspacesModuleMetadata.Descriptor
                    .GetCompositionProfiles()).Requires,
            feature =>
                feature.Id ==
                MessagingCompositionFeatures.Outbox);
        Assert.Contains(
            WorkspacesModuleMetadata.Descriptor.GetSubscriptions(),
            subscription =>
                subscription.ProducerModule == OrganizationsModuleMetadata.Name &&
                subscription.EventType ==
                    OrganizationEnrollmentClaimWithdrawnIntegrationEvent.EventType &&
                subscription.HandlerName ==
                    WorkspacesModuleMetadata.EnrollmentClaimWithdrawnHandlerName);
    }

    [Fact]
    public void Application_registers_one_workspaces_restriction_contributor()
    {
        ServiceCollection services = new();
        IConfiguration configuration =
            new ConfigurationBuilder().Build();

        services.AddWorkspacesApplication(configuration, "global");
        services.AddWorkspacesApplication(configuration, "global");

        ServiceDescriptor registration = Assert.Single(
            services,
            descriptor =>
                descriptor.ServiceType ==
                    typeof(IDataRightsRestrictionContributor));
        Assert.Equal(
            typeof(
                WorkspaceStaffOnboardingDataRightsRestrictionContributor),
            registration.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, registration.Lifetime);
    }

    [Fact]
    public void Application_registers_staff_onboarding_as_workspace_descendant_only()
    {
        ServiceCollection services = new();
        IConfiguration configuration = new ConfigurationBuilder().Build();
        services.AddWorkspacesApplication(configuration, "global");
        using ServiceProvider provider = services.BuildServiceProvider();
        IAccessScopeMatchOptionsResolver resolver = provider
            .GetRequiredService<IAccessScopeMatchOptionsResolver>();

        AccessScopeMatchOptions options = resolver.Resolve(
            PermissionCode.Create(
                WorkspacesPermissionCodes.StaffOnboardingManage));
        AccessScope workspace = AccessScope.Parse("workspace:workspace-a");
        AccessScope property = AccessScope.Parse(
            "workspace:workspace-a/property:property-a");
        AccessScope otherProperty = AccessScope.Parse(
            "workspace:workspace-b/property:property-a");

        Assert.True(options.AllowAncestorScopeGrants);
        Assert.False(options.AllowGlobalScopeGrant);
        Assert.True(AccessScopeMatcher.GrantSatisfiesRequest(
            workspace,
            property,
            options));
        Assert.False(AccessScopeMatcher.GrantSatisfiesRequest(
            workspace,
            otherProperty,
            options));
        Assert.False(AccessScopeMatcher.GrantSatisfiesRequest(
            AccessScope.Global,
            property,
            options));
    }
}
