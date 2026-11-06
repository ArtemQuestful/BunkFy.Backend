namespace BunkFy.Modules.Inventory.Contracts;

using System.Security.Cryptography;
using System.Text;

public static class InventoryTenantTerminationMetadata
{
    public const string OwnerKey = InventoryDataRightsCoordinates.Owner;
    public const string ExportDependencyOwnerKey = "properties";
    public const string DestroyDependencyOwnerKey = "reservations";
    public const int CatalogVersion = 8;
    public const int PersonalDataCatalogVersion = 9;
    public const string ExportCatalogId = "inventory.tenant-portability";
    public const int ExportCatalogSchemaVersion = 1;
    public const string ExportSchemaId =
        "inventory.tenant-termination-export";
    public const int ExportSchemaVersion = 6;

    public const string InventoryUnitRecordType = "inventory-unit";
    public const string RoomConfigurationRecordType = "room-configuration";
    public const string ManagementOperationRecordType =
        "management-operation";
    public const string ManualBlockRecordType = "manual-block";
    public const string ManualBlockGroupRecordType = "manual-block-group";
    public const string AllocationRecordType = "allocation";
    public const string AllocationUnitRecordType = "allocation-unit";
    public const string AllocationAmendmentDecisionRecordType =
        "allocation-amendment-decision";
    public const string AllocationAnonymisationReceiptRecordType =
        "allocation-anonymisation-receipt";
    public const string AllocationAnonymisationTombstoneRecordType =
        "allocation-anonymisation-tombstone";
    public const string AllocationAnonymisationRestoreReceiptRecordType =
        "allocation-anonymisation-restore-receipt";
    public const string BedRetirementRecordType = "bed-retirement";
    public const string RoomRetirementRecordType = "room-retirement";

    public static IReadOnlyList<string> RecordTypes { get; } =
        Array.AsReadOnly(
        [
            InventoryUnitRecordType,
            RoomConfigurationRecordType,
            ManagementOperationRecordType,
            ManualBlockRecordType,
            ManualBlockGroupRecordType,
            AllocationRecordType,
            AllocationUnitRecordType,
            AllocationAmendmentDecisionRecordType,
            AllocationAnonymisationReceiptRecordType,
            AllocationAnonymisationTombstoneRecordType,
            AllocationAnonymisationRestoreReceiptRecordType,
            BedRetirementRecordType,
            RoomRetirementRecordType
        ]);

    public static IReadOnlyList<string> ExportFieldIds { get; } =
        Array.AsReadOnly(
        [
            "inventory.scope-id",
            "inventory.property-id",
            "inventory.room-id",
            "inventory.bed-id",
            "inventory.inventory-unit-id",
            "inventory.unit-kind",
            "inventory.unit-label",
            "inventory.topology-active",
            "inventory.source-version",
            "inventory.details-version",
            "inventory.known",
            "inventory.availability-mutation-version",
            "inventory.sales-mode",
            "inventory.version",
            "inventory.created-at",
            "inventory.updated-at",
            "inventory.operation-id",
            "inventory.management-operation",
            "inventory.staff-change-operations",
            "inventory.block-id",
            "inventory.block-group-id",
            "inventory.arrival",
            "inventory.departure",
            "inventory.operational-reason",
            "inventory.state",
            "inventory.released-at",
            "inventory.guest-allocation-operations",
            "inventory.guest-reservation-reference",
            "inventory.allocation-anonymisation-proof",
            "inventory.retirement-id",
            "inventory.staff-actor-reference",
            "inventory.cancellation-reason",
            "inventory.cancellation-actor-reference",
            "inventory.canceled-at",
            "inventory.rejection-reason-code",
            "inventory.completed-at"
        ]);

    public static string CatalogManifest { get; } = string.Join(
        '|',
        OwnerKey,
        "contract=1",
        "phases=export,destroy",
        $"export-dependencies={ExportDependencyOwnerKey}",
        $"destroy-dependencies={DestroyDependencyOwnerKey}",
        "mandatory=true",
        $"catalog={CatalogVersion}",
        $"personal-data-catalog={PersonalDataCatalogVersion}",
        $"export-schema={ExportSchemaId}:{ExportSchemaVersion}",
        "records=" + string.Join(',', RecordTypes),
        "fields=" + string.Join(',', ExportFieldIds));

    public static string CatalogSha256 { get; } =
        Convert.ToHexStringLower(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(CatalogManifest)));
}
