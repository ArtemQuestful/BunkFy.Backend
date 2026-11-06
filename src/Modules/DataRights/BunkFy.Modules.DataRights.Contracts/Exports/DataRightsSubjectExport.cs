namespace BunkFy.Modules.DataRights.Contracts;

using System.Text.Json;

public interface IDataRightsSubjectExportContributor
{
    string OwnerKey { get; }

    IReadOnlyCollection<DataRightsCaseType> SupportedCaseTypes { get; }

    DataRightsExportDescriptor Descriptor { get; }

    Task<DataRightsSubjectExportResult> ExportAsync(
        DataRightsSubjectExportRequest request,
        IDataRightsExportSink sink,
        CancellationToken cancellationToken);
}

/// <summary>
/// Receives one transient owner fragment. Callers must discard every written
/// record unless the contributor returns a successful result.
/// </summary>
public interface IDataRightsExportSink
{
    ValueTask WriteAsync(
        DataRightsExportRecord record,
        CancellationToken cancellationToken);
}

public sealed record DataRightsSubjectExportRequest(
    string TenantId,
    DataRightsCaseType CaseType,
    Guid? PropertyId,
    DataRightsSubjectCoordinate Coordinate);

public sealed record DataRightsExportDescriptor(
    string OwnerKey,
    string CatalogId,
    int CatalogSchemaVersion,
    int CatalogVersion,
    string ExportSchemaId,
    int ExportSchemaVersion,
    IReadOnlyCollection<string> FieldIds);

public sealed record DataRightsExportRecord(
    string RecordType,
    Guid RecordId,
    long RecordVersion,
    IReadOnlyCollection<DataRightsExportField> Fields);

public sealed record DataRightsExportField(
    string FieldId,
    JsonElement Value);

public enum DataRightsSubjectExportStatus
{
    Unknown = 0,
    Succeeded = 1,
    ScopeUnavailable = 2,
    NotFound = 3,
    Stale = 4,
    RetryRequired = 5
}

public sealed record DataRightsSubjectExportResult(
    DataRightsSubjectExportStatus Status,
    int RecordCount)
{
    public static DataRightsSubjectExportResult Success(int recordCount) =>
        new(DataRightsSubjectExportStatus.Succeeded, recordCount);

    public static DataRightsSubjectExportResult ScopeUnavailable() =>
        new(DataRightsSubjectExportStatus.ScopeUnavailable, 0);

    public static DataRightsSubjectExportResult NotFound() =>
        new(DataRightsSubjectExportStatus.NotFound, 0);

    public static DataRightsSubjectExportResult Stale() =>
        new(DataRightsSubjectExportStatus.Stale, 0);

    public static DataRightsSubjectExportResult RetryRequired() =>
        new(DataRightsSubjectExportStatus.RetryRequired, 0);
}

public static class DataRightsExportLimits
{
    public const int OwnerKeyMaxLength = 100;
    public const int SchemaIdentifierMaxLength = 200;
    public const int RecordTypeMaxLength = 100;
    public const int FieldIdMaxLength = 200;
    public const int MaxFieldsPerSchema = 512;
    public const int MaxFieldsPerRecord = 64;
    public const int MaxFieldValueBytes = 16 * 1024;
}
