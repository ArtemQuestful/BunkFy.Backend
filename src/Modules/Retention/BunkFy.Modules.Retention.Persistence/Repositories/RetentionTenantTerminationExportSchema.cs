namespace BunkFy.Modules.Retention.Persistence.Repositories;

using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BunkFy.DataGovernance;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Retention.Contracts;

internal static class RetentionTenantTerminationExportSchema
{
    private const string CatalogResourceName =
        "BunkFy.Modules.Retention.Persistence.DataGovernance." +
        "personal-data-catalog.v1.json";
    private const string ExportRetentionPolicy =
        "retention-tenant-termination-export-fragment";
    private const string AllowedExportPolicy =
        "include-in-authorized-operational-retention-export";

    private static readonly Type[] SourceTypes =
    [
        typeof(RetentionExecutionTenantExport),
        typeof(RetentionScheduleStateTenantExport),
        typeof(RetentionRunRetryRequestTenantExport)
    ];

    private static readonly SensitiveBinding[] SensitiveBindings =
    [
        Binding<RetentionExecutionTenantExport>(
            nameof(RetentionExecutionTenantExport.ScopeId),
            "retention.tenant-scope-reference",
            "organizations"),
        Binding<RetentionExecutionTenantExport>(
            nameof(RetentionExecutionTenantExport.PropertyId),
            "retention.property-reference",
            "properties"),
        Binding<RetentionScheduleStateTenantExport>(
            nameof(RetentionScheduleStateTenantExport.ScopeId),
            "retention.tenant-scope-reference",
            "organizations"),
        Binding<RetentionScheduleStateTenantExport>(
            nameof(RetentionScheduleStateTenantExport.PropertyId),
            "retention.property-reference",
            "properties"),
        Binding<RetentionRunRetryRequestTenantExport>(
            nameof(RetentionRunRetryRequestTenantExport.ScopeId),
            "retention.tenant-scope-reference",
            "organizations"),
        Binding<RetentionRunRetryRequestTenantExport>(
            nameof(RetentionRunRetryRequestTenantExport.PropertyId),
            "retention.property-reference",
            "properties")
    ];

    private static readonly JsonSerializerOptions SerializerOptions =
        CreateSerializerOptions();
    private static readonly Lazy<DataRightsExportDescriptor> DescriptorState =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static DataRightsExportDescriptor Descriptor =>
        DescriptorState.Value;

    public static void EnsureValid() => _ = DescriptorState.Value;

    public static DataRightsExportRecord CreateRecord(
        string recordType,
        Guid recordId,
        long recordVersion,
        object source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Type sourceType = source.GetType();
        if (!SourceTypes.Contains(sourceType) ||
            !RetentionTenantTerminationMetadata.RecordTypes.Contains(
                recordType,
                StringComparer.Ordinal) ||
            recordId == Guid.Empty ||
            recordVersion <= 0)
        {
            throw new InvalidDataException(
                "The Retention tenant-export record is invalid.");
        }

        DataRightsExportField[] fields = sourceType
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => CreateField(
                property,
                property.GetValue(source)))
            .OrderBy(field => field.FieldId, StringComparer.Ordinal)
            .ToArray();
        if (fields.Length is <= 0 or >
                DataRightsExportLimits.MaxFieldsPerRecord ||
            fields.Select(field => field.FieldId)
                .Distinct(StringComparer.Ordinal).Count() != fields.Length)
        {
            throw new InvalidDataException(
                "The Retention tenant-export fields are invalid.");
        }

        return new DataRightsExportRecord(
            recordType,
            recordId,
            recordVersion,
            Array.AsReadOnly(fields));
    }

    private static DataRightsExportField CreateField(
        PropertyInfo property,
        object? value)
    {
        RetentionTenantExportFieldAttribute attribute =
            property.GetCustomAttribute<
                RetentionTenantExportFieldAttribute>() ??
            throw new InvalidDataException(
                $"Retention tenant-export member '{property.Name}' " +
                "has no field identifier.");
        JsonElement serialized = JsonSerializer.SerializeToElement(
            value,
            value?.GetType() ?? typeof(object),
            SerializerOptions);
        if (Encoding.UTF8.GetByteCount(serialized.GetRawText()) >
            DataRightsExportLimits.MaxFieldValueBytes)
        {
            throw new InvalidDataException(
                $"Retention tenant-export field '{attribute.FieldId}' " +
                "exceeds its size limit.");
        }

        return new DataRightsExportField(attribute.FieldId, serialized);
    }

    private static DataRightsExportDescriptor Load()
    {
        string[] discoveredFieldIds = SourceTypes
            .SelectMany(type => type.GetProperties(
                BindingFlags.Instance | BindingFlags.Public))
            .Select(property => property.GetCustomAttribute<
                RetentionTenantExportFieldAttribute>()?.FieldId ??
                throw new InvalidDataException(
                    $"Retention tenant-export member '{property.Name}' " +
                    "has no field identifier."))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(fieldId => fieldId, StringComparer.Ordinal)
            .ToArray();
        string[] declaredFieldIds =
            RetentionTenantTerminationMetadata.ExportFieldIds
                .OrderBy(fieldId => fieldId, StringComparer.Ordinal)
                .ToArray();
        if (!discoveredFieldIds.SequenceEqual(
                declaredFieldIds,
                StringComparer.Ordinal) ||
            discoveredFieldIds.Length >
                DataRightsExportLimits.MaxFieldsPerRecord ||
            discoveredFieldIds.Any(fieldId =>
                fieldId.Length is <= 0 or >
                    DataRightsExportLimits.FieldIdMaxLength))
        {
            throw new InvalidDataException(
                "The Retention tenant-export field catalogue is invalid.");
        }

        ValidateSensitiveBindings();
        return new DataRightsExportDescriptor(
            RetentionTenantTerminationMetadata.OwnerKey,
            RetentionTenantTerminationMetadata.ExportCatalogId,
            RetentionTenantTerminationMetadata.ExportCatalogSchemaVersion,
            RetentionTenantTerminationMetadata.CatalogVersion,
            RetentionTenantTerminationMetadata.ExportSchemaId,
            RetentionTenantTerminationMetadata.ExportSchemaVersion,
            Array.AsReadOnly(discoveredFieldIds));
    }

    private static void ValidateSensitiveBindings()
    {
        Assembly assembly =
            typeof(RetentionTenantTerminationExportSchema).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(
                CatalogResourceName) ??
            throw new InvalidDataException(
                "The Retention personal-data catalogue is unavailable.");
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        PersonalDataCatalogDocument catalog =
            PersonalDataCatalogJson.Parse(buffer.ToArray());
        if (catalog.CatalogVersion !=
                RetentionTenantTerminationMetadata
                    .PersonalDataCatalogVersion ||
            !string.Equals(
                catalog.CatalogId,
                "retention.personal-data",
                StringComparison.Ordinal) ||
            !string.Equals(
                catalog.Module,
                RetentionTenantTerminationMetadata.OwnerKey,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The Retention personal-data catalogue identity is invalid.");
        }

        foreach (SensitiveBinding expected in SensitiveBindings)
        {
            PersonalDataFieldDefinition field = catalog.Fields.Single(
                candidate => string.Equals(
                    candidate.Id,
                    expected.FieldId,
                    StringComparison.Ordinal));
            PersonalDataRightsPolicy policy = catalog.RightsPolicies.Single(
                candidate => string.Equals(
                    candidate.Id,
                    field.RightsPolicy,
                    StringComparison.Ordinal));
            PropertyInfo property = expected.Type.GetProperty(
                    expected.Member,
                    BindingFlags.Instance | BindingFlags.Public) ??
                throw new InvalidDataException(
                    "The Retention tenant-export binding member is missing.");
            string? actualFieldId = property.GetCustomAttribute<
                RetentionTenantExportFieldAttribute>()?.FieldId;
            bool valid = string.Equals(
                    actualFieldId,
                    expected.FieldId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    field.AuthoritativeOwner,
                    expected.AuthoritativeOwner,
                    StringComparison.Ordinal) &&
                string.Equals(
                    policy.Export,
                    AllowedExportPolicy,
                    StringComparison.Ordinal) &&
                field.AllowedSurfaces.Contains(
                    PersonalDataSurface.DataRightsExport) &&
                field.AllowedBoundaries.Contains(
                    PersonalDataBoundary.CrossModule) &&
                field.Bindings.Any(binding =>
                    string.Equals(
                        binding.Assembly,
                        assembly.GetName().Name,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        binding.Type,
                        expected.Type.FullName,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        binding.Member,
                        expected.Member,
                        StringComparison.Ordinal) &&
                    binding.Surface ==
                        PersonalDataSurface.DataRightsExport &&
                    string.Equals(
                        binding.RetentionPolicy,
                        ExportRetentionPolicy,
                        StringComparison.Ordinal));
            if (!valid)
            {
                throw new InvalidDataException(
                    $"The Retention tenant-export binding " +
                    $"'{expected.Type.FullName}.{expected.Member}' " +
                    "is invalid.");
            }
        }
    }

    private static SensitiveBinding Binding<T>(
        string member,
        string fieldId,
        string authoritativeOwner) =>
        new(typeof(T), member, fieldId, authoritativeOwner);

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
        options.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        return options;
    }

    private sealed record SensitiveBinding(
        Type Type,
        string Member,
        string FieldId,
        string AuthoritativeOwner);
}
