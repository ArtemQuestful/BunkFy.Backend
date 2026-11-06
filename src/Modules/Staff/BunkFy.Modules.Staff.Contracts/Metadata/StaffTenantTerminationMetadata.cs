namespace BunkFy.Modules.Staff.Contracts;

using System.Security.Cryptography;
using System.Text;

public static class StaffTenantTerminationMetadata
{
    public const string OwnerKey = StaffModuleMetadata.Name;
    public const string DependencyOwnerKey = "guests";
    public const int CatalogVersion = 6;
    public const int PersonalDataCatalogVersion = 21;
    public const string ExportCatalogId = "staff.tenant-portability";
    public const int ExportCatalogSchemaVersion = 1;
    public const string ExportSchemaId = "staff.tenant-termination-export";
    public const int ExportSchemaVersion = 6;

    public const string StaffMemberRecordType = "staff-member";
    public const string PropertyAssignmentRecordType = "property-assignment";
    public const string MemberMutationOperationRecordType =
        "member-mutation-operation";
    public const string IdentityProvisioningAnchorRecordType =
        "identity-provisioning-anchor";
    public const string IdentityProvisioningAnchorResolutionRecordType =
        "identity-provisioning-anchor-resolution";
    public const string DataRightsCorrectionReceiptRecordType =
        "data-rights-correction-receipt";
    public const string ProcessingRestrictionRecordType =
        "processing-restriction";
    public const string ProcessingRestrictionReceiptRecordType =
        "processing-restriction-receipt";
    public const string EmploymentGovernanceRecordType =
        "employment-governance";
    public const string EmploymentGovernanceChangeReceiptRecordType =
        "employment-governance-change-receipt";
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
            StaffMemberRecordType,
            IdentityProvisioningAnchorRecordType,
            IdentityProvisioningAnchorResolutionRecordType,
            PropertyAssignmentRecordType,
            MemberMutationOperationRecordType,
            DataRightsCorrectionReceiptRecordType,
            ProcessingRestrictionRecordType,
            ProcessingRestrictionReceiptRecordType,
            EmploymentGovernanceRecordType,
            EmploymentGovernanceChangeReceiptRecordType,
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
            "staff.scope-id",
            "staff.property-id",
            "staff.staff-member-id",
            "staff.record-id",
            "staff.profile-state",
            "staff.staff-attribution",
            "staff.assignment-record",
            "staff.member-mutation-operation",
            "staff.identity-provisioning-anchor",
            "staff.identity-provisioning-anchor-resolution",
            "staff.data-rights-proof",
            "staff.processing-restriction",
            "staff.employment-governance",
            "staff.employment-governance-proof",
            "staff.data-hold",
            "staff.anonymisation-proof",
            "staff.retention-execution",
            "staff.retention-proof"
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
