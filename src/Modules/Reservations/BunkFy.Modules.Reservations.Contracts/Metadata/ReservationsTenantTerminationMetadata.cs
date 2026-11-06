namespace BunkFy.Modules.Reservations.Contracts;

using System.Security.Cryptography;
using System.Text;

public static class ReservationsTenantTerminationMetadata
{
    public const string OwnerKey = ReservationsDataRightsCoordinates.Owner;
    public const string DependencyOwnerKey = "inventory";
    public const int CatalogVersion = 8;
    public const int PersonalDataCatalogVersion = 24;
    public const string ExportCatalogId =
        "reservations.tenant-portability";
    public const int ExportCatalogSchemaVersion = 1;
    public const string ExportSchemaId =
        "reservations.tenant-termination-export";
    public const int ExportSchemaVersion = 10;

    public const string ReservationRecordType = "reservation";
    public const string RequestedInventoryUnitRecordType =
        "requested-inventory-unit";
    public const string PendingAmendmentRecordType = "pending-amendment";
    public const string GuestLinkRecordType = "guest-link";
    public const string GuestRecordLinkProcessRecordType =
        "guest-record-link-process";
    public const string DetailsHistoryRecordType = "details-history";
    public const string ExternalOperationRecordType = "external-operation";
    public const string ManagementOperationRecordType =
        "management-operation";
    public const string StayAmendmentOperationRecordType =
        "stay-amendment-operation";
    public const string ArrivalReminderRecordType = "arrival-reminder";
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
            ReservationRecordType,
            RequestedInventoryUnitRecordType,
            PendingAmendmentRecordType,
            GuestLinkRecordType,
            GuestRecordLinkProcessRecordType,
            DetailsHistoryRecordType,
            ExternalOperationRecordType,
            ManagementOperationRecordType,
            StayAmendmentOperationRecordType,
            ArrivalReminderRecordType,
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
            "reservations.scope-id",
            "reservations.property-id",
            "reservations.reservation-id",
            "reservations.record-id",
            "reservations.inventory-unit-id",
            "reservations.guest-id",
            "reservations.booking-state",
            "reservations.guest-details",
            "reservations.provider-provenance",
            "reservations.staff-attribution",
            "reservations.guest-link",
            "reservations.guest-record-link-process",
            "reservations.details-history",
            "reservations.management-operation",
            "reservations.stay-amendment-operation",
            "reservations.reminder-state",
            "reservations.data-rights-proof",
            "reservations.processing-restriction",
            "reservations.data-hold",
            "reservations.anonymisation-proof",
            "reservations.retention-execution",
            "reservations.retention-proof"
        ]);

    public static string CatalogManifest { get; } = string.Join(
        '|',
        OwnerKey,
        "contract=1",
        "phases=export,destroy",
        $"export-dependencies={DependencyOwnerKey}",
        "destroy-dependencies=",
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
