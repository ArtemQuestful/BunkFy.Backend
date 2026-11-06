namespace BunkFy.Modules.Workspaces.Contracts;

using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Ingestion.Contracts;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Retention.Contracts;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Stations.Contracts;
using Gma.Modules.AccessControl.Contracts;

public static class WorkspaceAccessRoles
{
    public const string Owner = "bunkfy-workspace-owner";
    public const string Provisioner = "bunkfy-workspace-access-provisioner";
    public const string MembershipMarker = "bunkfy-workspace-member-v2";
    public const string LegacyMember = "bunkfy-workspace-member";
    public const string CompanySupport = "bunkfy-company-support";

    public static IReadOnlyList<string> OwnerPermissions { get; } =
        [AccessControlPermissionGrants.OwnerWildcard];

    public static IReadOnlyList<string> MembershipMarkerPermissions { get; } = [];

    public static IReadOnlyList<string> LegacyMemberPermissions { get; } =
    [
        PropertiesAdminPermissionCodes.Read,
        InventoryAdminPermissionCodes.Read,
        InventoryAdminPermissionCodes.BlocksManage,
        ReservationsAdminPermissionCodes.Read,
        ReservationsAdminPermissionCodes.Create,
        ReservationsAdminPermissionCodes.Manage,
        ReservationsAdminPermissionCodes.ManageGuests,
        ReservationsAdminPermissionCodes.Cancel,
        ReservationsAdminPermissionCodes.CheckIn,
        ReservationsAdminPermissionCodes.NoShow,
        ReservationsAdminPermissionCodes.CheckOut,
        GuestsAdminPermissionCodes.Read,
        GuestsAdminPermissionCodes.Create,
        GuestsAdminPermissionCodes.Manage,
        StaffAdminPermissionCodes.Read
    ];

    public static IReadOnlyList<string> DelegablePermissions { get; } =
    [
        StationsPermissionCodes.Manage,
        AccessControlProfilePermissionCodes.Read,
        AccessControlProfilePermissionCodes.Manage,
        AccessControlProfilePermissionCodes.Assign,
        WorkspacesPermissionCodes.StaffOnboardingManage,
        PropertiesAdminPermissionCodes.Read,
        PropertiesAdminPermissionCodes.PropertiesManage,
        PropertiesAdminPermissionCodes.TimeZonesManage,
        PropertiesAdminPermissionCodes.RoomsManage,
        PropertiesAdminPermissionCodes.BedsManage,
        InventoryAdminPermissionCodes.Read,
        InventoryAdminPermissionCodes.Configure,
        InventoryAdminPermissionCodes.BlocksManage,
        InventoryAdminPermissionCodes.BlockGroupsManage,
        InventoryAdminPermissionCodes.Retire,
        ReservationsAdminPermissionCodes.Read,
        ReservationsAdminPermissionCodes.Create,
        ReservationsAdminPermissionCodes.Manage,
        ReservationsAdminPermissionCodes.ManageGuests,
        ReservationsAdminPermissionCodes.Cancel,
        ReservationsAdminPermissionCodes.CheckIn,
        ReservationsAdminPermissionCodes.NoShow,
        ReservationsAdminPermissionCodes.CheckOut,
        GuestsAdminPermissionCodes.Read,
        GuestsAdminPermissionCodes.Create,
        GuestsAdminPermissionCodes.Manage,
        GuestsAdminPermissionCodes.Archive,
        GuestsAdminPermissionCodes.DataHoldsManage,
        StaffAdminPermissionCodes.Read,
        StaffAdminPermissionCodes.SensitiveProfileRead,
        StaffAdminPermissionCodes.Create,
        StaffAdminPermissionCodes.Manage,
        StaffAdminPermissionCodes.AccountLinksManage,
        StaffAdminPermissionCodes.AssignProperties,
        StaffAdminPermissionCodes.ManageLifecycle,
        StaffAdminPermissionCodes.EmploymentGovernanceManage,
        StaffAdminPermissionCodes.DataHoldsManage,
        IngestionAdminPermissionCodes.Read,
        IngestionAdminPermissionCodes.ConnectionsManage,
        IngestionAdminPermissionCodes.CredentialsManage,
        IngestionAdminPermissionCodes.IngressControlManage,
        IngestionAdminPermissionCodes.RunsManage,
        IngestionAdminPermissionCodes.RawPayloadsRead,
        IngestionAdminPermissionCodes.SensitiveHistoryRead,
        IngestionAdminPermissionCodes.RetentionManage,
        IngestionAdminPermissionCodes.ReprocessingManage,
        IngestionAdminPermissionCodes.LegalHoldsManage,
        IngestionAdminPermissionCodes.ProposalsDecide,
        DataRightsAdminPermissionCodes.Read,
        DataRightsAdminPermissionCodes.Create,
        DataRightsAdminPermissionCodes.Discover,
        DataRightsAdminPermissionCodes.Review,
        DataRightsAdminPermissionCodes.Decide,
        DataRightsAdminPermissionCodes.Execute,
        DataRightsAdminPermissionCodes.Export,
        DataRightsAdminPermissionCodes.DownloadExport,
        DataRightsAdminPermissionCodes.Restrict,
        DataRightsAdminPermissionCodes.Erase,
        DataRightsAdminPermissionCodes.TerminateTenant,
        DataRightsAdminPermissionCodes.TenantTerminationRead,
        DataRightsAdminPermissionCodes.TenantTerminationRequest,
        DataRightsAdminPermissionCodes.TenantTerminationApprove,
        DataRightsAdminPermissionCodes.TenantTerminationExecute,
        DataRightsAdminPermissionCodes.TenantTerminationExportDownload,
        DataRightsAdminPermissionCodes.TenantTerminationExportConfirm,
        DataRightsAdminPermissionCodes.TenantTerminationRetry,
        DataRightsAdminPermissionCodes.TenantTerminationCancel,
        DataRightsAdminPermissionCodes.TenantTerminationRecover,
        DataRightsAdminPermissionCodes.Manage,
        RetentionPermissionCodes.Read,
        RetentionPermissionCodes.Manage,
        RetentionPermissionCodes.Retry
    ];

    public static IReadOnlyList<string> CompanySupportPermissionCeiling { get; } =
        DelegablePermissions
            .Except(
            [
                StationsPermissionCodes.Manage,
                AccessControlProfilePermissionCodes.Manage,
                AccessControlProfilePermissionCodes.Assign,
                WorkspacesPermissionCodes.StaffOnboardingManage,
                PropertiesAdminPermissionCodes.TimeZonesManage,
                GuestsAdminPermissionCodes.Archive,
                GuestsAdminPermissionCodes.DataHoldsManage,
                StaffAdminPermissionCodes.SensitiveProfileRead,
                StaffAdminPermissionCodes.AccountLinksManage,
                StaffAdminPermissionCodes.EmploymentGovernanceManage,
                StaffAdminPermissionCodes.DataHoldsManage,
                IngestionAdminPermissionCodes.CredentialsManage,
                InventoryAdminPermissionCodes.BlockGroupsManage,
                InventoryAdminPermissionCodes.Retire,
                IngestionAdminPermissionCodes.RawPayloadsRead,
                IngestionAdminPermissionCodes.SensitiveHistoryRead,
                IngestionAdminPermissionCodes.RetentionManage,
                IngestionAdminPermissionCodes.LegalHoldsManage,
                DataRightsAdminPermissionCodes.Read,
                DataRightsAdminPermissionCodes.Create,
                DataRightsAdminPermissionCodes.Discover,
                DataRightsAdminPermissionCodes.Review,
                DataRightsAdminPermissionCodes.Decide,
                DataRightsAdminPermissionCodes.Execute,
                DataRightsAdminPermissionCodes.Export,
                DataRightsAdminPermissionCodes.DownloadExport,
                DataRightsAdminPermissionCodes.Restrict,
                DataRightsAdminPermissionCodes.Erase,
                DataRightsAdminPermissionCodes.TerminateTenant,
                DataRightsAdminPermissionCodes.TenantTerminationRead,
                DataRightsAdminPermissionCodes.TenantTerminationRequest,
                DataRightsAdminPermissionCodes.TenantTerminationApprove,
                DataRightsAdminPermissionCodes.TenantTerminationExecute,
                DataRightsAdminPermissionCodes.TenantTerminationExportDownload,
                DataRightsAdminPermissionCodes.TenantTerminationExportConfirm,
                DataRightsAdminPermissionCodes.TenantTerminationRetry,
                DataRightsAdminPermissionCodes.TenantTerminationCancel,
                DataRightsAdminPermissionCodes.TenantTerminationRecover,
                DataRightsAdminPermissionCodes.Manage,
                RetentionPermissionCodes.Manage,
                RetentionPermissionCodes.Retry
            ],
            StringComparer.Ordinal)
            .ToArray();

    public static IReadOnlyList<string> ProvisionerPermissions { get; } =
        DelegablePermissions;
}
