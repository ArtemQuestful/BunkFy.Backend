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

public static class WorkspaceAccessPermissionCatalogue
{
    public static IReadOnlyList<WorkspaceAccessPermissionDto> All { get; } =
    [
        Permission(StationsPermissionCodes.Manage, "Shared stations", "Manage shared stations", "Pair and revoke devices, register staff, and manage station PIN recovery. Does not grant station check-in access.", sensitive: true),
        Permission(AccessControlProfilePermissionCodes.Read, "Workspace access", "View roles", "View workspace roles, permission definitions, and member access assignments."),
        Permission(AccessControlProfilePermissionCodes.Manage, "Workspace access", "Manage roles", "Create, update, and archive custom workspace roles.", sensitive: true, requires: [AccessControlProfilePermissionCodes.Read]),
        Permission(AccessControlProfilePermissionCodes.Assign, "Workspace access", "Assign roles", "Assign operational roles to workspace members within the actor's own authority.", sensitive: true, requires: [AccessControlProfilePermissionCodes.Read]),
        Permission(WorkspacesPermissionCodes.StaffOnboardingManage, "Workspace access", "Manage staff onboarding", "Issue and manage staff join sources, review applicants, and retry provisioning.", sensitive: true, requires: [AccessControlProfilePermissionCodes.Read]),

        Permission(PropertiesAdminPermissionCodes.Read, "Properties", "View properties", "View properties, rooms, beds, and the canonical time-zone catalog at the granted scope."),
        Permission(PropertiesAdminPermissionCodes.PropertiesManage, "Properties", "Manage properties", "Create and update properties.", requires: [PropertiesAdminPermissionCodes.Read]),
        Permission(PropertiesAdminPermissionCodes.TimeZonesManage, "Properties", "Manage property time zones", "Inspect property time-zone compliance, recover operations, and perform bounded corrections.", sensitive: true, requires: [PropertiesAdminPermissionCodes.Read]),
        Permission(PropertiesAdminPermissionCodes.RoomsManage, "Properties", "Manage rooms", "Create, update, and retire rooms.", requires: [PropertiesAdminPermissionCodes.Read]),
        Permission(PropertiesAdminPermissionCodes.BedsManage, "Properties", "Manage beds", "Create, update, and retire beds.", requires: [PropertiesAdminPermissionCodes.Read]),

        Permission(InventoryAdminPermissionCodes.Read, "Inventory", "View inventory", "View room sales modes, availability, and blocks.", requires: [PropertiesAdminPermissionCodes.Read]),
        Permission(InventoryAdminPermissionCodes.Configure, "Inventory", "Configure room sales", "Choose whether rooms are sold whole or by individual bed.", requires: [PropertiesAdminPermissionCodes.Read, InventoryAdminPermissionCodes.Read]),
        Permission(InventoryAdminPermissionCodes.BlocksManage, "Inventory", "Manage inventory blocks", "Take an individual room or bed inventory unit out of service.", requires: [PropertiesAdminPermissionCodes.Read, InventoryAdminPermissionCodes.Read]),
        Permission(InventoryAdminPermissionCodes.BlockGroupsManage, "Inventory", "Manage inventory block groups", "Preview, create, replace, and release confirmed groups of inventory blocks.", sensitive: true, requires: [PropertiesAdminPermissionCodes.Read, InventoryAdminPermissionCodes.Read, InventoryAdminPermissionCodes.BlocksManage]),
        Permission(InventoryAdminPermissionCodes.Retire, "Inventory", "Retire rooms and beds", "Start, inspect, and retry terminal room or bed retirement workflows.", sensitive: true, requires: [PropertiesAdminPermissionCodes.RoomsManage, PropertiesAdminPermissionCodes.BedsManage, InventoryAdminPermissionCodes.Read]),

        Permission(ReservationsAdminPermissionCodes.Read, "Reservations", "View reservations", "View reservation details and stay status."),
        Permission(ReservationsAdminPermissionCodes.Create, "Reservations", "Create reservations", "Create direct reservations and allocate available inventory.", requires: [PropertiesAdminPermissionCodes.Read, InventoryAdminPermissionCodes.Read, ReservationsAdminPermissionCodes.Read]),
        Permission(ReservationsAdminPermissionCodes.Manage, "Reservations", "Manage reservations", "Edit reservation dates, guests, and inventory assignments.", requires: [ReservationsAdminPermissionCodes.Read]),
        Permission(ReservationsAdminPermissionCodes.ManageGuests, "Reservations", "Link guest records", "Link and unlink durable guest records on reservations.", requires: [ReservationsAdminPermissionCodes.Read, GuestsAdminPermissionCodes.Read]),
        Permission(ReservationsAdminPermissionCodes.Cancel, "Reservations", "Cancel reservations", "Cancel reservations and release their inventory.", requires: [ReservationsAdminPermissionCodes.Read]),
        Permission(ReservationsAdminPermissionCodes.CheckIn, "Reservations", "Check in guests", "Start confirmed stays.", requires: [ReservationsAdminPermissionCodes.Read]),
        Permission(ReservationsAdminPermissionCodes.NoShow, "Reservations", "Mark no-shows", "Mark confirmed reservations as no-show.", requires: [ReservationsAdminPermissionCodes.Read]),
        Permission(ReservationsAdminPermissionCodes.CheckOut, "Reservations", "Check out guests", "Complete active stays.", requires: [ReservationsAdminPermissionCodes.Read]),

        Permission(GuestsAdminPermissionCodes.Read, "Guests", "View guest records", "View durable guest profiles and stay history.", sensitive: true),
        Permission(GuestsAdminPermissionCodes.Create, "Guests", "Create guest records", "Create durable guest profiles.", sensitive: true, requires: [GuestsAdminPermissionCodes.Read]),
        Permission(GuestsAdminPermissionCodes.Manage, "Guests", "Edit guest records", "Update durable guest identity and contact data.", sensitive: true, requires: [GuestsAdminPermissionCodes.Read]),
        Permission(GuestsAdminPermissionCodes.Archive, "Guests", "Archive guest records", "Archive durable guest profiles.", sensitive: true, requires: [GuestsAdminPermissionCodes.Read]),
        Permission(GuestsAdminPermissionCodes.DataHoldsManage, "Guests", "Manage Guest data holds", "Place, review, and release holds that block Guest anonymisation.", sensitive: true, requires: [GuestsAdminPermissionCodes.Read]),

        Permission(StaffAdminPermissionCodes.Read, "Staff", "View staff directory", "View operational staff names, roles, and assignments."),
        Permission(StaffAdminPermissionCodes.SensitiveProfileRead, "Staff", "View sensitive staff data", "View staff employment and contact details.", sensitive: true, requires: [StaffAdminPermissionCodes.Read]),
        Permission(StaffAdminPermissionCodes.Create, "Staff", "Create staff profiles", "Create staff employment profiles.", sensitive: true, requires: [StaffAdminPermissionCodes.Read]),
        Permission(StaffAdminPermissionCodes.Manage, "Staff", "Edit staff profiles", "Update staff employment and contact details.", sensitive: true, requires: [StaffAdminPermissionCodes.Read]),
        Permission(StaffAdminPermissionCodes.AccountLinksManage, "Staff", "Manage staff account links", "Link or safely clear the sign-in account correlated with a staff profile.", sensitive: true, requires: [StaffAdminPermissionCodes.Read, StaffAdminPermissionCodes.SensitiveProfileRead]),
        Permission(StaffAdminPermissionCodes.AssignProperties, "Staff", "Assign staff to properties", "Change which properties a staff member can work with.", requires: [StaffAdminPermissionCodes.Read]),
        Permission(StaffAdminPermissionCodes.ManageLifecycle, "Staff", "Manage employment status", "Suspend, restore, or end staff employment.", sensitive: true, requires: [StaffAdminPermissionCodes.Read]),
        Permission(StaffAdminPermissionCodes.EmploymentGovernanceManage, "Staff", "Manage employment governance", "Bind a staff profile to approved employment jurisdiction, transfer, and retention policy evidence.", sensitive: true, requires: [StaffAdminPermissionCodes.Read, StaffAdminPermissionCodes.SensitiveProfileRead]),
        Permission(StaffAdminPermissionCodes.DataHoldsManage, "Staff", "Manage Staff data holds", "Place, review, and release holds that block Staff anonymisation and retention.", sensitive: true, requires: [StaffAdminPermissionCodes.Read, StaffAdminPermissionCodes.SensitiveProfileRead]),

        Permission(IngestionAdminPermissionCodes.Read, "Integrations", "View integrations", "View connections, runs, receipts, and suggested changes."),
        Permission(IngestionAdminPermissionCodes.ConnectionsManage, "Integrations", "Manage connections", "Create and configure reservation-source connections.", requires: [IngestionAdminPermissionCodes.Read]),
        Permission(IngestionAdminPermissionCodes.CredentialsManage, "Integrations", "Manage adapter credentials", "Issue and revoke credentials used by external adapters.", sensitive: true, requires: [IngestionAdminPermissionCodes.Read]),
        Permission(IngestionAdminPermissionCodes.IngressControlManage, "Integrations", "Control adapter ingress", "Suspend or resume all third-party adapter ingress for this workspace.", sensitive: true, requires: [IngestionAdminPermissionCodes.Read]),
        Permission(IngestionAdminPermissionCodes.RunsManage, "Integrations", "Run integrations", "Start, retry, and cancel ingestion runs.", requires: [IngestionAdminPermissionCodes.Read]),
        Permission(IngestionAdminPermissionCodes.RawPayloadsRead, "Integrations", "View raw source data", "Inspect sensitive source payloads retained for diagnostics.", sensitive: true, requires: [IngestionAdminPermissionCodes.Read]),
        Permission(IngestionAdminPermissionCodes.SensitiveHistoryRead, "Integrations", "View sensitive history", "Inspect normalized provider history containing guest and reservation data.", sensitive: true, requires: [IngestionAdminPermissionCodes.Read]),
        Permission(IngestionAdminPermissionCodes.RetentionManage, "Integrations", "Manage retention", "Run retention and redaction operations.", sensitive: true, requires: [IngestionAdminPermissionCodes.Read]),
        Permission(IngestionAdminPermissionCodes.ReprocessingManage, "Integrations", "Reprocess source data", "Replay retained observations through current parsers.", sensitive: true, requires: [IngestionAdminPermissionCodes.Read]),
        Permission(IngestionAdminPermissionCodes.LegalHoldsManage, "Integrations", "Manage legal holds", "Place and release retention legal holds.", sensitive: true, requires: [IngestionAdminPermissionCodes.Read]),
        Permission(IngestionAdminPermissionCodes.ProposalsDecide, "Integrations", "Decide suggested changes", "Accept or reject adapter-proposed reservation changes.", requires: [IngestionAdminPermissionCodes.Read]),

        Permission(DataRightsAdminPermissionCodes.Read, "Data rights", "View data-rights cases", "View scoped case status without exposing discovered personal data.", sensitive: true),
        Permission(DataRightsAdminPermissionCodes.Create, "Data rights", "Create data-rights cases", "Open property-scoped data-rights cases.", sensitive: true, requires: [DataRightsAdminPermissionCodes.Read]),
        Permission(DataRightsAdminPermissionCodes.Discover, "Data rights", "Discover subject records", "Run sensitive module-owned record discovery.", sensitive: true, requires: [DataRightsAdminPermissionCodes.Read]),
        Permission(DataRightsAdminPermissionCodes.Review, "Data rights", "Review case scope", "Verify requesters, routing, and selected record scope.", sensitive: true, requires: [DataRightsAdminPermissionCodes.Read]),
        Permission(DataRightsAdminPermissionCodes.Decide, "Data rights", "Decide cases", "Approve or deny requested operations.", sensitive: true, requires: [DataRightsAdminPermissionCodes.Read, DataRightsAdminPermissionCodes.Review]),
        Permission(DataRightsAdminPermissionCodes.Execute, "Data rights", "Execute approved cases", "Run approved module-owned data-rights work.", sensitive: true, requires: [DataRightsAdminPermissionCodes.Read, DataRightsAdminPermissionCodes.Decide]),
        Permission(DataRightsAdminPermissionCodes.Export, "Data rights", "Generate data exports", "Generate protected case export artifacts.", sensitive: true, requires: [DataRightsAdminPermissionCodes.Read, DataRightsAdminPermissionCodes.Decide]),
        Permission(DataRightsAdminPermissionCodes.DownloadExport, "Data rights", "Download data exports", "Download a protected case export after fresh authorization.", sensitive: true, requires: [DataRightsAdminPermissionCodes.Read, DataRightsAdminPermissionCodes.Export]),
        Permission(DataRightsAdminPermissionCodes.Restrict, "Data rights", "Manage processing restrictions", "Apply or release approved processing restrictions.", sensitive: true, requires: [DataRightsAdminPermissionCodes.Read, DataRightsAdminPermissionCodes.Decide]),
        Permission(DataRightsAdminPermissionCodes.Erase, "Data rights", "Erase or anonymise data", "Execute approved irreversible erasure or anonymisation.", sensitive: true, requires: [DataRightsAdminPermissionCodes.Read, DataRightsAdminPermissionCodes.Decide, DataRightsAdminPermissionCodes.Execute]),
        Permission(DataRightsAdminPermissionCodes.TerminateTenant, "Data rights", "Terminate tenant data", "Execute approved tenant export, revocation, and deletion.", sensitive: true, requires: [DataRightsAdminPermissionCodes.Read, DataRightsAdminPermissionCodes.Decide, DataRightsAdminPermissionCodes.Execute]),
        Permission(DataRightsAdminPermissionCodes.TenantTerminationRead, "Data rights", "View tenant termination", "View bounded case, process, and owner progress.", sensitive: true, requires: [DataRightsAdminPermissionCodes.Read]),
        Permission(DataRightsAdminPermissionCodes.TenantTerminationRequest, "Data rights", "Request tenant termination", "Open a reviewed tenant-termination operator case.", sensitive: true, requires: [DataRightsAdminPermissionCodes.TenantTerminationRead]),
        Permission(DataRightsAdminPermissionCodes.TenantTerminationApprove, "Data rights", "Approve tenant termination", "Bind approval, backup, restore-drill, operator, and owner-catalog evidence.", sensitive: true, requires: [DataRightsAdminPermissionCodes.TenantTerminationRead, DataRightsAdminPermissionCodes.TenantTerminationRequest]),
        Permission(DataRightsAdminPermissionCodes.TenantTerminationExecute, "Data rights", "Execute tenant termination", "Start an approved tenant termination as a distinct executor.", sensitive: true, requires: [DataRightsAdminPermissionCodes.TenantTerminationRead, DataRightsAdminPermissionCodes.TenantTerminationApprove]),
        Permission(DataRightsAdminPermissionCodes.TenantTerminationExportDownload, "Data rights", "Download tenant termination export", "Download the exact protected tenant export for operator review.", sensitive: true, requires: [DataRightsAdminPermissionCodes.TenantTerminationRead, DataRightsAdminPermissionCodes.TenantTerminationExecute]),
        Permission(DataRightsAdminPermissionCodes.TenantTerminationExportConfirm, "Data rights", "Confirm tenant termination export", "Confirm the reviewed frozen export proof before irreversible destruction.", sensitive: true, requires: [DataRightsAdminPermissionCodes.TenantTerminationRead, DataRightsAdminPermissionCodes.TenantTerminationExecute, DataRightsAdminPermissionCodes.TenantTerminationExportDownload]),
        Permission(DataRightsAdminPermissionCodes.TenantTerminationRetry, "Data rights", "Retry tenant termination", "Resume the exact blocked or failed owner work set.", sensitive: true, requires: [DataRightsAdminPermissionCodes.TenantTerminationRead, DataRightsAdminPermissionCodes.TenantTerminationExecute]),
        Permission(DataRightsAdminPermissionCodes.TenantTerminationCancel, "Data rights", "Cancel tenant termination", "Request verified restoration while destruction remains reversible.", sensitive: true, requires: [DataRightsAdminPermissionCodes.TenantTerminationRead, DataRightsAdminPermissionCodes.TenantTerminationExecute]),
        Permission(DataRightsAdminPermissionCodes.TenantTerminationRecover, "Data rights", "Recover tenant termination", "Reconstruct a protected start or re-signal durable coordination.", sensitive: true, requires: [DataRightsAdminPermissionCodes.TenantTerminationRead, DataRightsAdminPermissionCodes.TenantTerminationExecute]),
        Permission(DataRightsAdminPermissionCodes.Manage, "Data rights", "Manage case lifecycle", "Route, cancel, and recover data-rights cases.", sensitive: true, requires: [DataRightsAdminPermissionCodes.Read]),

        Permission(RetentionPermissionCodes.Read, "Retention", "View retention health", "View PII-minimized schedule, due, hold, and failure status."),
        Permission(RetentionPermissionCodes.Manage, "Retention", "Manage retention", "Manage retention schedules and policy-controlled execution.", sensitive: true, requires: [RetentionPermissionCodes.Read]),
        Permission(RetentionPermissionCodes.Retry, "Retention", "Retry retention work", "Retry failed retention execution through the durable task runtime.", sensitive: true, requires: [RetentionPermissionCodes.Read])
    ];

    public static IReadOnlyList<string> ProtectedSeedKeys { get; } =
        WorkspaceAccessProfileSeeds.All.Select(seed => seed.Key).ToArray();

    private static WorkspaceAccessPermissionDto Permission(
        string code,
        string group,
        string label,
        string description,
        bool sensitive = false,
        IReadOnlyList<string>? requires = null) => new(
            code,
            group,
            label,
            description,
            sensitive,
            requires ?? []);
}
