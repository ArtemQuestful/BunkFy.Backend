namespace BunkFy.Modules.Workspaces.Contracts;

using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Ingestion.Contracts;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Retention.Contracts;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Stations.Contracts;
using Gma.Modules.AccessControl.Contracts;

public static class WorkspaceAccessProfileSeeds
{
    public const int Version = 7;
    public const string ManagerKey = "manager";
    public const string FrontDeskKey = "front-desk";
    public const string HousekeepingKey = "housekeeping";
    public const string ViewerKey = "viewer";

    public static AccessProfileDefinition Manager { get; } = new(
        ManagerKey,
        "Manager",
        "Manage daily property operations, staff, guests, reservations, and standard integrations.",
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
            StaffAdminPermissionCodes.Read,
            StaffAdminPermissionCodes.SensitiveProfileRead,
            StaffAdminPermissionCodes.Create,
            StaffAdminPermissionCodes.Manage,
            StaffAdminPermissionCodes.AssignProperties,
            StaffAdminPermissionCodes.ManageLifecycle,
            IngestionAdminPermissionCodes.Read,
            IngestionAdminPermissionCodes.ConnectionsManage,
            IngestionAdminPermissionCodes.IngressControlManage,
            IngestionAdminPermissionCodes.RunsManage,
            IngestionAdminPermissionCodes.ProposalsDecide,
            RetentionPermissionCodes.Read
        ]);

    public static AccessProfileDefinition FrontDesk { get; } = new(
        FrontDeskKey,
        "Front desk",
        "Handle reservations, guest records, arrivals, departures, and inventory blocks.",
        WorkspaceAccessRoles.LegacyMemberPermissions);

    public static AccessProfileDefinition Housekeeping { get; } = new(
        HousekeepingKey,
        "Housekeeping",
        "View room inventory and manage operational room or bed blocks.",
        [
            PropertiesAdminPermissionCodes.Read,
            InventoryAdminPermissionCodes.Read,
            InventoryAdminPermissionCodes.BlocksManage
        ]);

    public static AccessProfileDefinition Viewer { get; } = new(
        ViewerKey,
        "Viewer",
        "Read operational workspace data without changing it.",
        [
            PropertiesAdminPermissionCodes.Read,
            InventoryAdminPermissionCodes.Read,
            ReservationsAdminPermissionCodes.Read,
            GuestsAdminPermissionCodes.Read,
            StaffAdminPermissionCodes.Read
        ]);

    public static IReadOnlyList<AccessProfileDefinition> All { get; } =
        [Manager, FrontDesk, Housekeeping, Viewer];
}
