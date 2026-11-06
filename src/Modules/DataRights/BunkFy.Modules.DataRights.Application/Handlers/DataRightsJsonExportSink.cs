namespace BunkFy.Modules.DataRights.Application.Handlers;

using System.Text;
using System.Text.Json;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Contracts;

internal sealed class DataRightsJsonExportSink(
    Utf8JsonWriter writer,
    DataRightsExportDescriptor descriptor,
    Func<long> totalCount,
    Action<long> setTotalCount,
    long maximumRecords)
    : IDataRightsExportSink
{
    private readonly HashSet<string> allowedFields =
        descriptor.FieldIds.ToHashSet(StringComparer.Ordinal);

    public long RecordCount { get; private set; }

    public ValueTask WriteAsync(
        DataRightsExportRecord record,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        this.ValidateRecord(record);
        long nextTotal = checked(totalCount() + 1);
        if (nextTotal > maximumRecords)
        {
            throw new DataRightsExportGenerationException(
                "record-limit-exceeded");
        }

        writer.WriteStartObject();
        writer.WriteString(
            "recordType",
            record.RecordType.Trim().ToLowerInvariant());
        writer.WriteString("recordId", record.RecordId);
        writer.WriteNumber("recordVersion", record.RecordVersion);
        writer.WriteStartObject("fields");
        foreach (DataRightsExportField field in record.Fields
            .OrderBy(item => item.FieldId, StringComparer.Ordinal))
        {
            writer.WritePropertyName(field.FieldId);
            field.Value.WriteTo(writer);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
        this.RecordCount++;
        setTotalCount(nextTotal);
        return ValueTask.CompletedTask;
    }

    private void ValidateRecord(DataRightsExportRecord? record)
    {
        if (record is null ||
            string.IsNullOrWhiteSpace(record.RecordType) ||
            record.RecordType.Trim().Length >
                DataRightsExportLimits.RecordTypeMaxLength ||
            record.RecordId == Guid.Empty ||
            record.RecordVersion <= 0 ||
            record.Fields is null ||
            record.Fields.Count is <= 0 or >
                DataRightsExportLimits.MaxFieldsPerRecord ||
            record.Fields.Any(field =>
                field is null ||
                string.IsNullOrWhiteSpace(field.FieldId) ||
                field.FieldId.Trim().Length >
                    DataRightsExportLimits.FieldIdMaxLength ||
                !this.allowedFields.Contains(field.FieldId) ||
                field.Value.ValueKind == JsonValueKind.Undefined ||
                Encoding.UTF8.GetByteCount(field.Value.GetRawText()) >
                    DataRightsExportLimits.MaxFieldValueBytes) ||
            record.Fields.Select(field => field.FieldId)
                .Distinct(StringComparer.Ordinal).Count() !=
                    record.Fields.Count)
        {
            throw new DataRightsExportGenerationException(
                "owner-record-invalid");
        }
    }
}

internal static class DataRightsExportSchemaValidator
{
    public static DataRightsExportDescriptor Validate(
        DataRightsExportDescriptor? descriptor,
        string ownerKey)
    {
        string normalizedOwner = ownerKey?.Trim().ToLowerInvariant() ??
            string.Empty;
        if (descriptor is null ||
            !string.Equals(
                descriptor.OwnerKey?.Trim(),
                normalizedOwner,
                StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(descriptor.CatalogId) ||
            descriptor.CatalogId.Trim().Length >
                DataRightsExportLimits.SchemaIdentifierMaxLength ||
            descriptor.CatalogSchemaVersion <= 0 ||
            descriptor.CatalogVersion <= 0 ||
            string.IsNullOrWhiteSpace(descriptor.ExportSchemaId) ||
            descriptor.ExportSchemaId.Trim().Length >
                DataRightsExportLimits.SchemaIdentifierMaxLength ||
            descriptor.ExportSchemaVersion <= 0 ||
            descriptor.FieldIds is null ||
            descriptor.FieldIds.Count is <= 0 or >
                DataRightsExportLimits.MaxFieldsPerSchema ||
            descriptor.FieldIds.Any(field =>
                string.IsNullOrWhiteSpace(field) ||
                field.Trim().Length >
                    DataRightsExportLimits.FieldIdMaxLength) ||
            descriptor.FieldIds.Distinct(StringComparer.Ordinal).Count() !=
                descriptor.FieldIds.Count)
        {
            throw new DataRightsExportGenerationException(
                "owner-descriptor-invalid");
        }

        return descriptor;
    }
}
