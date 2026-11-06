namespace BunkFy.Modules.Properties.Contracts;

using System.Security.Cryptography;
using System.Text;

public static class PropertiesTenantTerminationMetadata
{
    public const string OwnerKey = "properties";
    public const string ExportDependencyOwnerKey = "workspaces";
    public const string InventoryDestroyDependencyOwnerKey = "inventory";
    public const string OperationsNotificationsDestroyDependencyOwnerKey =
        "operations-notifications";
    public const string RetentionDestroyDependencyOwnerKey = "retention";
    public const int CatalogVersion = 8;
    public const int PersonalDataCatalogVersion = 5;
    public const string ExportCatalogId =
        "properties.tenant-portability";
    public const int ExportCatalogSchemaVersion = 1;
    public const string ExportSchemaId =
        "properties.tenant-termination-export";
    public const int ExportSchemaVersion = 7;

    public const string PropertyRecordType = "property";
    public const string PropertyMutationOperationRecordType =
        "property-mutation-operation";
    public const string PropertyTimeZoneOperationRecordType =
        "property-time-zone-operation";
    public const string GovernanceAcknowledgementRecordType =
        "property-governance-acknowledgement";
    public const string RoomRecordType = "room";
    public const string BedRecordType = "bed";
    public const string GovernanceRevisionRecordType =
        "property-governance-revision";

    public static IReadOnlyList<string> RecordTypes { get; } =
        Array.AsReadOnly(
        [
            PropertyRecordType,
            PropertyMutationOperationRecordType,
            PropertyTimeZoneOperationRecordType,
            GovernanceAcknowledgementRecordType,
            RoomRecordType,
            BedRecordType,
            GovernanceRevisionRecordType
        ]);

    public static IReadOnlyList<string> DestroyDependencyOwnerKeys { get; } =
        Array.AsReadOnly(
        [
            InventoryDestroyDependencyOwnerKey,
            OperationsNotificationsDestroyDependencyOwnerKey,
            RetentionDestroyDependencyOwnerKey
        ]);

    public static IReadOnlyList<string> ExportFieldIds { get; } =
        Array.AsReadOnly(
        [
            "properties.scope-id",
            "properties.property-id",
            "properties.property-name",
            "properties.property-code",
            "properties.time-zone-id",
            "properties.property-status",
            "properties.processing-status",
            "properties.governance-policy",
            "properties.property-version",
            "properties.projection-ordinal",
            "properties.property-created-at",
            "properties.property-updated-at",
            "properties.property-retired-at",
            "properties.record-id",
            "properties.property-mutation-operation",
            "properties.property-time-zone-operation",
            "properties.governance-acknowledgement-id",
            "properties.governance-acknowledgement-version",
            "properties.room-id",
            "properties.room-name",
            "properties.building-label",
            "properties.floor-label",
            "properties.room-status",
            "properties.room-version",
            "properties.room-created-at",
            "properties.room-updated-at",
            "properties.room-retired-at",
            "properties.bed-id",
            "properties.bed-label",
            "properties.bed-status",
            "properties.bed-version",
            "properties.bed-created-at",
            "properties.bed-updated-at",
            "properties.bed-retired-at",
            "properties.governance-revision-id",
            "properties.governance-action",
            "properties.governance-decision-code",
            "properties.governance-previous",
            "properties.governance-current",
            "properties.staff-actor-reference",
            "properties.occurred-at"
        ]);

    public static string CatalogManifest { get; } = string.Join(
        '|',
        OwnerKey,
        "contract=1",
        "phases=export,destroy",
        $"export-dependencies={ExportDependencyOwnerKey}",
        "destroy-dependencies=" +
            string.Join(',', DestroyDependencyOwnerKeys),
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
