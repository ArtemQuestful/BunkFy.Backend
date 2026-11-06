namespace BunkFy.Modules.Retention.Contracts;

using System.Security.Cryptography;
using System.Text;

public static class RetentionTenantTerminationMetadata
{
    public const string OwnerKey = RetentionModuleMetadata.Name;
    public const string DependencyOwnerKey = "ingestion";
    public const int CatalogVersion = 3;
    public const int PersonalDataCatalogVersion = 4;
    public const string ExportCatalogId = "retention.tenant-portability";
    public const int ExportCatalogSchemaVersion = 1;
    public const string ExportSchemaId =
        "retention.tenant-termination-export";
    public const int ExportSchemaVersion = 2;
    public const string ScheduleStateRecordNamespaceId =
        "6b61f812-2d23-4d0c-9929-45211d45f285";

    public const string ExecutionRecordType = "retention-execution";
    public const string ScheduleStateRecordType =
        "retention-schedule-state";
    public const string RunRetryRequestRecordType =
        "retention-run-retry-request";

    public static IReadOnlyList<string> RecordTypes { get; } =
        Array.AsReadOnly(
        [
            ExecutionRecordType,
            ScheduleStateRecordType,
            RunRetryRequestRecordType
        ]);

    public static IReadOnlyList<string> ExportFieldIds { get; } =
        Array.AsReadOnly(
        [
            "retention.tenant-scope-reference",
            "retention.property-reference",
            "retention.execution-record",
            "retention.schedule-state",
            "retention.run-retry-request"
        ]);

    public static string CatalogManifest { get; } = string.Join(
        '|',
        OwnerKey,
        "contract=1",
        "phases=export,destroy",
        $"dependencies={DependencyOwnerKey}",
        "mandatory=true",
        $"catalog={CatalogVersion}",
        $"personal-data-catalog={PersonalDataCatalogVersion}",
        $"export-schema={ExportSchemaId}:{ExportSchemaVersion}",
        $"schedule-namespace={ScheduleStateRecordNamespaceId}",
        "records=" + string.Join(',', RecordTypes),
        "fields=" + string.Join(',', ExportFieldIds));

    public static string CatalogSha256 { get; } =
        Convert.ToHexStringLower(
            SHA256.HashData(
                Encoding.UTF8.GetBytes(CatalogManifest)));
}
