namespace BunkFy.Modules.Workspaces.Tests;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Ingestion.Contracts;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Retention.Contracts;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Stations.Contracts;
using BunkFy.Modules.Workspaces.Contracts;
using Gma.Framework.Permissions;
using Gma.Modules.AccessControl.Contracts;
using Xunit;

[Trait("Category", "Unit")]
public sealed class WorkspaceAccessProfileSeedTests
{
    [Fact]
    public void Station_management_is_sensitive_delegable_and_manager_only()
    {
        Assert.Equal(7, WorkspaceAccessProfileSeeds.Version);
        WorkspaceAccessPermissionDto permission = Assert.Single(
            WorkspaceAccessPermissionCatalogue.All, item => item.Code == StationsPermissionCodes.Manage);
        Assert.True(permission.IsSensitive);
        Assert.Empty(permission.RequiredPermissions);
        Assert.Equal(permission.Code, Assert.Single(StationsModuleMetadata.Descriptor.GetPermissions()).Code);
        Assert.Contains(permission.Code, WorkspaceAccessRoles.DelegablePermissions);
        Assert.Contains(permission.Code, WorkspaceAccessProfileSeeds.Manager.Permissions);
        Assert.DoesNotContain(permission.Code, WorkspaceAccessRoles.CompanySupportPermissionCeiling);
        Assert.DoesNotContain(permission.Code, WorkspaceAccessRoles.LegacyMemberPermissions);
        Assert.All(WorkspaceAccessProfileSeeds.All.Where(profile => profile.Key != WorkspaceAccessProfileSeeds.ManagerKey),
            profile => Assert.DoesNotContain(permission.Code, profile.Permissions));
    }

    [Fact]
    public void Membership_marker_grants_no_permissions()
    {
        Assert.Empty(WorkspaceAccessRoles.MembershipMarkerPermissions);
    }

    [Fact]
    public void Internal_provisioner_has_exactly_the_delegable_product_permissions()
    {
        Assert.Equal(
            WorkspaceAccessRoles.DelegablePermissions,
            WorkspaceAccessRoles.ProvisionerPermissions);
        Assert.DoesNotContain(
            AccessControlPermissionGrants.OwnerWildcard,
            WorkspaceAccessRoles.ProvisionerPermissions);
    }

    [Fact]
    public void Seed_profiles_are_unique_delegable_and_never_owner_profiles()
    {
        string[] keys = WorkspaceAccessProfileSeeds.All.Select(profile => profile.Key).ToArray();
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());

        HashSet<string> delegable = WorkspaceAccessRoles.DelegablePermissions.ToHashSet(StringComparer.Ordinal);
        Assert.All(WorkspaceAccessProfileSeeds.All, profile =>
        {
            Assert.DoesNotContain(AccessControlPermissionGrants.OwnerWildcard, profile.Permissions);
            Assert.DoesNotContain(profile.Permissions, permission => !delegable.Contains(permission));
            Assert.Equal(
                profile.Permissions.Count,
                profile.Permissions.Distinct(StringComparer.Ordinal).Count());
        });
    }

    [Fact]
    public void Front_desk_seed_preserves_the_legacy_operational_baseline()
    {
        Assert.Equal(
            WorkspaceAccessRoles.LegacyMemberPermissions.ToArray(),
            WorkspaceAccessProfileSeeds.FrontDesk.Permissions.ToArray());
    }

    [Fact]
    public void Data_rights_permissions_are_never_granted_to_operational_seed_profiles()
    {
        HashSet<string> dataRightsPermissions = WorkspaceAccessPermissionCatalogue.All
            .Where(permission => permission.Code.StartsWith(
                DataRightsModuleMetadata.Name + ".",
                StringComparison.Ordinal))
            .Select(permission => permission.Code)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            DataRightsModuleMetadata.Descriptor.GetPermissions()
                .Select(permission => permission.Code)
                .Order(StringComparer.Ordinal),
            dataRightsPermissions.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(
            WorkspaceAccessRoles.LegacyMemberPermissions,
            dataRightsPermissions.Contains);
        Assert.All(WorkspaceAccessProfileSeeds.All, profile =>
            Assert.DoesNotContain(profile.Permissions, dataRightsPermissions.Contains));
    }

    [Theory]
    [InlineData(DataRightsAdminPermissionCodes.TenantTerminationExportDownload)]
    [InlineData(DataRightsAdminPermissionCodes.TenantTerminationExportConfirm)]
    public void Tenant_termination_export_handoff_permissions_are_sensitive_delegable_and_never_seeded(
        string permissionCode)
    {
        WorkspaceAccessPermissionDto permission = Assert.Single(
            WorkspaceAccessPermissionCatalogue.All,
            item => item.Code == permissionCode);

        Assert.True(permission.IsSensitive);
        Assert.Contains(permission.Code, WorkspaceAccessRoles.DelegablePermissions);
        Assert.DoesNotContain(permission.Code, WorkspaceAccessRoles.CompanySupportPermissionCeiling);
        Assert.All(WorkspaceAccessProfileSeeds.All, profile =>
            Assert.DoesNotContain(permission.Code, profile.Permissions));
    }

    [Fact]
    public void Guest_data_hold_permission_is_delegable_but_not_seeded()
    {
        Assert.Contains(
            GuestsAdminPermissionCodes.DataHoldsManage,
            WorkspaceAccessRoles.DelegablePermissions);
        Assert.DoesNotContain(
            GuestsAdminPermissionCodes.DataHoldsManage,
            WorkspaceAccessRoles.LegacyMemberPermissions);
        Assert.All(WorkspaceAccessProfileSeeds.All, profile =>
            Assert.DoesNotContain(
                GuestsAdminPermissionCodes.DataHoldsManage,
                profile.Permissions));
    }

    [Fact]
    public void Workspace_access_profile_permissions_are_delegable_with_bounded_sensitivity()
    {
        WorkspaceAccessPermissionDto read = Assert.Single(
            WorkspaceAccessPermissionCatalogue.All,
            item => item.Code == AccessControlProfilePermissionCodes.Read);
        WorkspaceAccessPermissionDto manage = Assert.Single(
            WorkspaceAccessPermissionCatalogue.All,
            item => item.Code == AccessControlProfilePermissionCodes.Manage);
        WorkspaceAccessPermissionDto assign = Assert.Single(
            WorkspaceAccessPermissionCatalogue.All,
            item => item.Code == AccessControlProfilePermissionCodes.Assign);

        Assert.False(read.IsSensitive);
        Assert.Empty(read.RequiredPermissions);
        Assert.True(manage.IsSensitive);
        Assert.Equal([AccessControlProfilePermissionCodes.Read], manage.RequiredPermissions);
        Assert.True(assign.IsSensitive);
        Assert.Equal([AccessControlProfilePermissionCodes.Read], assign.RequiredPermissions);

        string[] profilePermissionCodes = [read.Code, manage.Code, assign.Code];
        Assert.All(
            profilePermissionCodes,
            code => Assert.Contains(code, WorkspaceAccessRoles.DelegablePermissions));
        Assert.All(
            profilePermissionCodes,
            code => Assert.Contains(code, WorkspaceAccessProfileSeeds.Manager.Permissions));
        Assert.Contains(read.Code, WorkspaceAccessRoles.CompanySupportPermissionCeiling);
        Assert.DoesNotContain(manage.Code, WorkspaceAccessRoles.CompanySupportPermissionCeiling);
        Assert.DoesNotContain(assign.Code, WorkspaceAccessRoles.CompanySupportPermissionCeiling);
    }

    [Fact]
    public void Staff_onboarding_management_is_sensitive_delegable_and_manager_only()
    {
        Assert.Equal(7, WorkspaceAccessProfileSeeds.Version);
        WorkspaceAccessPermissionDto permission = Assert.Single(
            WorkspaceAccessPermissionCatalogue.All,
            item => item.Code ==
                WorkspacesPermissionCodes.StaffOnboardingManage);

        Assert.True(permission.IsSensitive);
        Assert.Equal(
            [AccessControlProfilePermissionCodes.Read],
            permission.RequiredPermissions);
        Assert.Contains(
            permission.Code,
            WorkspaceAccessRoles.DelegablePermissions);
        Assert.Contains(
            permission.Code,
            WorkspaceAccessProfileSeeds.Manager.Permissions);
        Assert.DoesNotContain(
            permission.Code,
            WorkspaceAccessRoles.LegacyMemberPermissions);
        Assert.DoesNotContain(
            permission.Code,
            WorkspaceAccessRoles.CompanySupportPermissionCeiling);
        Assert.All(
            WorkspaceAccessProfileSeeds.All.Where(
                profile => profile.Key != WorkspaceAccessProfileSeeds.ManagerKey),
            profile => Assert.DoesNotContain(
                permission.Code,
                profile.Permissions));
    }

    [Fact]
    public void Property_time_zone_management_is_sensitive_delegable_and_manager_only()
    {
        Assert.Equal(7, WorkspaceAccessProfileSeeds.Version);
        WorkspaceAccessPermissionDto permission = Assert.Single(
            WorkspaceAccessPermissionCatalogue.All,
            item => item.Code == PropertiesAdminPermissionCodes.TimeZonesManage);

        Assert.True(permission.IsSensitive);
        Assert.Equal(
            [PropertiesAdminPermissionCodes.Read],
            permission.RequiredPermissions);
        Assert.Contains(permission.Code, WorkspaceAccessRoles.DelegablePermissions);
        Assert.Contains(permission.Code, WorkspaceAccessProfileSeeds.Manager.Permissions);
        Assert.DoesNotContain(permission.Code, WorkspaceAccessRoles.LegacyMemberPermissions);
        Assert.DoesNotContain(permission.Code, WorkspaceAccessRoles.CompanySupportPermissionCeiling);
        Assert.All(
            WorkspaceAccessProfileSeeds.All.Where(
                profile => profile.Key != WorkspaceAccessProfileSeeds.ManagerKey),
            profile => Assert.DoesNotContain(permission.Code, profile.Permissions));
    }

    [Fact]
    public void Inventory_retirement_is_sensitive_delegable_and_manager_only()
    {
        WorkspaceAccessPermissionDto permission = Assert.Single(
            WorkspaceAccessPermissionCatalogue.All,
            item => item.Code == InventoryAdminPermissionCodes.Retire);

        Assert.True(permission.IsSensitive);
        Assert.Equal(
            [
                PropertiesAdminPermissionCodes.RoomsManage,
                PropertiesAdminPermissionCodes.BedsManage,
                InventoryAdminPermissionCodes.Read
            ],
            permission.RequiredPermissions);
        Assert.Contains(permission.Code, WorkspaceAccessRoles.DelegablePermissions);
        Assert.Contains(permission.Code, WorkspaceAccessProfileSeeds.Manager.Permissions);
        Assert.DoesNotContain(permission.Code, WorkspaceAccessRoles.LegacyMemberPermissions);
        Assert.DoesNotContain(permission.Code, WorkspaceAccessRoles.CompanySupportPermissionCeiling);
        Assert.All(
            WorkspaceAccessProfileSeeds.All.Where(
                profile => profile.Key != WorkspaceAccessProfileSeeds.ManagerKey),
            profile => Assert.DoesNotContain(permission.Code, profile.Permissions));
    }

    [Fact]
    public void Inventory_block_group_management_is_sensitive_delegable_and_manager_only()
    {
        WorkspaceAccessPermissionDto permission = Assert.Single(
            WorkspaceAccessPermissionCatalogue.All,
            item => item.Code ==
                InventoryAdminPermissionCodes.BlockGroupsManage);

        Assert.True(permission.IsSensitive);
        Assert.Equal(
            [
                PropertiesAdminPermissionCodes.Read,
                InventoryAdminPermissionCodes.Read,
                InventoryAdminPermissionCodes.BlocksManage
            ],
            permission.RequiredPermissions);
        Assert.Contains(permission.Code, WorkspaceAccessRoles.DelegablePermissions);
        Assert.Contains(permission.Code, WorkspaceAccessProfileSeeds.Manager.Permissions);
        Assert.DoesNotContain(permission.Code, WorkspaceAccessRoles.LegacyMemberPermissions);
        Assert.DoesNotContain(permission.Code, WorkspaceAccessRoles.CompanySupportPermissionCeiling);
        Assert.All(
            WorkspaceAccessProfileSeeds.All.Where(
                profile => profile.Key != WorkspaceAccessProfileSeeds.ManagerKey),
            profile => Assert.DoesNotContain(permission.Code, profile.Permissions));
    }

    [Theory]
    [InlineData(StaffAdminPermissionCodes.AccountLinksManage)]
    [InlineData(StaffAdminPermissionCodes.EmploymentGovernanceManage)]
    [InlineData(StaffAdminPermissionCodes.DataHoldsManage)]
    public void Sensitive_staff_permissions_are_delegable_and_never_seeded(
        string permissionCode)
    {
        Assert.Contains(
            permissionCode,
            WorkspaceAccessRoles.DelegablePermissions);
        Assert.DoesNotContain(
            permissionCode,
            WorkspaceAccessRoles.LegacyMemberPermissions);
        Assert.DoesNotContain(
            permissionCode,
            WorkspaceAccessRoles.CompanySupportPermissionCeiling);
        Assert.All(WorkspaceAccessProfileSeeds.All, profile =>
            Assert.DoesNotContain(
                permissionCode,
                profile.Permissions));

        WorkspaceAccessPermissionDto permission = Assert.Single(
            WorkspaceAccessPermissionCatalogue.All,
            item => item.Code == permissionCode);
        Assert.True(permission.IsSensitive);
        Assert.Equal(
            [
                StaffAdminPermissionCodes.Read,
                StaffAdminPermissionCodes.SensitiveProfileRead
            ],
            permission.RequiredPermissions);
    }

    [Fact]
    public void Company_support_role_has_a_bounded_non_sensitive_permission_ceiling()
    {
        HashSet<string> delegable = WorkspaceAccessRoles.DelegablePermissions
            .ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<string> ceiling = WorkspaceAccessRoles.CompanySupportPermissionCeiling;

        Assert.NotEmpty(ceiling);
        Assert.Equal(ceiling.Count, ceiling.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(ceiling, permission => !delegable.Contains(permission));
        Assert.DoesNotContain(AccessControlPermissionGrants.OwnerWildcard, ceiling);
        Assert.DoesNotContain(StaffAdminPermissionCodes.SensitiveProfileRead, ceiling);
        Assert.DoesNotContain(StaffAdminPermissionCodes.AccountLinksManage, ceiling);
        Assert.DoesNotContain(
            WorkspacesPermissionCodes.StaffOnboardingManage,
            ceiling);
        Assert.DoesNotContain(IngestionAdminPermissionCodes.CredentialsManage, ceiling);
        Assert.DoesNotContain(IngestionAdminPermissionCodes.RawPayloadsRead, ceiling);
        Assert.DoesNotContain(IngestionAdminPermissionCodes.SensitiveHistoryRead, ceiling);
        Assert.DoesNotContain(RetentionPermissionCodes.Manage, ceiling);
        Assert.DoesNotContain(RetentionPermissionCodes.Retry, ceiling);
        Assert.DoesNotContain(
            ceiling,
            permission => permission.StartsWith(
                DataRightsModuleMetadata.Name + ".",
                StringComparison.Ordinal));
    }
}
