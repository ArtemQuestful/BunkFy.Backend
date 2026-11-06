namespace BunkFy.Modules.Guests.Contracts;

using System.Security.Cryptography;
using System.Text;

public static class GuestsTenantTerminationMetadata
{
    public const string OwnerKey = GuestsDataRightsCoordinates.Owner;
    public const string DependencyOwnerKey = "reservations";
    public const int CatalogVersion = 6;
    public const int PersonalDataCatalogVersion = 17;
    public const string ExportCatalogId = "guests.tenant-portability";
    public const int ExportCatalogSchemaVersion = 1;
    public const string ExportSchemaId =
        "guests.tenant-termination-export";
    public const int ExportSchemaVersion = 5;

    public const string GuestProfileRecordType = "guest-profile";
    public const string ManagementOperationRecordType =
        "guest-management-operation";
    public const string DataRightsCorrectionReceiptRecordType =
        "data-rights-correction-receipt";
    public const string ProcessingRestrictionRecordType =
        "processing-restriction";
    public const string ProcessingRestrictionReceiptRecordType =
        "processing-restriction-receipt";
    public const string DataHoldRecordType = "data-hold";
    public const string DataHoldReceiptRecordType = "data-hold-receipt";
    public const string AnonymisationReceiptRecordType =
        "anonymisation-receipt";
    public const string AnonymisationTombstoneRecordType =
        "anonymisation-tombstone";
    public const string AnonymisationRestoreReceiptRecordType =
        "anonymisation-restore-receipt";
    public const string RetentionExecutionRecordType =
        "retention-execution";
    public const string RetentionAnonymisationReceiptRecordType =
        "retention-anonymisation-receipt";

    public static IReadOnlyList<string> RecordTypes { get; } =
        Array.AsReadOnly(
        [
            GuestProfileRecordType,
            ManagementOperationRecordType,
            DataRightsCorrectionReceiptRecordType,
            ProcessingRestrictionRecordType,
            ProcessingRestrictionReceiptRecordType,
            DataHoldRecordType,
            DataHoldReceiptRecordType,
            AnonymisationReceiptRecordType,
            AnonymisationTombstoneRecordType,
            AnonymisationRestoreReceiptRecordType,
            RetentionExecutionRecordType,
            RetentionAnonymisationReceiptRecordType
        ]);

    public static IReadOnlyList<string> ExportFieldIds { get; } =
        Array.AsReadOnly(
        [
            "guests.scope-id",
            "guests.property-id",
            "guests.guest-id",
            "guests.record-id",
            "guests.profile-state",
            "guests.management-operation",
            "guests.staff-attribution",
            "guests.data-rights-proof",
            "guests.processing-restriction",
            "guests.data-hold",
            "guests.anonymisation-proof",
            "guests.retention-execution",
            "guests.retention-proof"
        ]);

    public static string CatalogManifest { get; } = string.Join(
        '|',
        OwnerKey,
        "contract=1",
        "phases=export,destroy",
        $"export-dependencies={DependencyOwnerKey}",
        $"destroy-dependencies={DependencyOwnerKey}",
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
