namespace BunkFy.Modules.Properties.Persistence.Repositories;

using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BunkFy.DataGovernance;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Properties.Contracts;

internal static class PropertiesTenantTerminationExportSchema
{
    private const string CatalogResourceName =
        "BunkFy.Modules.Properties.Persistence.DataGovernance." +
        "personal-data-catalog.v1.json";
    private const string ActorFieldId =
        "properties.staff-actor-reference";
    private const string ExportRetentionPolicy =
        "properties-tenant-termination-export-fragment";
    private const string AllowedActorExportPolicy =
        "include-in-authorized-staff-audit-or-tenant-export";

    private static readonly Type[] SourceTypes =
    [
        typeof(PropertiesPropertyTenantExport),
        typeof(PropertiesPropertyMutationOperationTenantExport),
        typeof(PropertiesPropertyTimeZoneOperationTenantExport),
        typeof(PropertiesGovernanceAcknowledgementTenantExport),
        typeof(PropertiesRoomTenantExport),
        typeof(PropertiesBedTenantExport),
        typeof(PropertiesGovernanceRevisionTenantExport)
    ];

    private static readonly JsonSerializerOptions SerializerOptions =
        CreateSerializerOptions();
    private static readonly Lazy<SchemaState> State =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static DataRightsExportDescriptor Descriptor =>
        State.Value.Descriptor;

    public static void EnsureValid() => _ = State.Value;

    public static DataRightsExportRecord CreateRecord(
        string recordType,
        Guid recordId,
        long recordVersion,
        object source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Type sourceType = source.GetType();
        if (!SourceTypes.Contains(sourceType) ||
            !PropertiesTenantTerminationMetadata.RecordTypes.Contains(
                recordType,
                StringComparer.Ordinal) ||
            recordId == Guid.Empty ||
            recordVersion <= 0)
        {
            throw new InvalidDataException(
                "The Properties tenant-export record is invalid.");
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
                "The Properties tenant-export fields are invalid.");
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
        PropertiesTenantExportFieldAttribute attribute =
            property.GetCustomAttribute<
                PropertiesTenantExportFieldAttribute>() ??
            throw new InvalidDataException(
                $"Properties tenant-export member '{property.Name}' " +
                "has no field identifier.");
        JsonElement serialized = JsonSerializer.SerializeToElement(
            value,
            value?.GetType() ?? typeof(object),
            SerializerOptions);
        if (Encoding.UTF8.GetByteCount(serialized.GetRawText()) >
            DataRightsExportLimits.MaxFieldValueBytes)
        {
            throw new InvalidDataException(
                $"Properties tenant-export field '{attribute.FieldId}' " +
                "exceeds its size limit.");
        }

        return new DataRightsExportField(attribute.FieldId, serialized);
    }

    private static SchemaState Load()
    {
        string[] discoveredFieldIds = SourceTypes
            .SelectMany(type => type.GetProperties(
                BindingFlags.Instance | BindingFlags.Public))
            .Select(property => property.GetCustomAttribute<
                PropertiesTenantExportFieldAttribute>()?.FieldId ??
                throw new InvalidDataException(
                    $"Properties tenant-export member '{property.Name}' " +
                    "has no field identifier."))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(fieldId => fieldId, StringComparer.Ordinal)
            .ToArray();
        string[] declaredFieldIds =
            PropertiesTenantTerminationMetadata.ExportFieldIds
                .OrderBy(fieldId => fieldId, StringComparer.Ordinal)
                .ToArray();
        if (!discoveredFieldIds.SequenceEqual(
                declaredFieldIds,
                StringComparer.Ordinal) ||
            discoveredFieldIds.Any(fieldId =>
                fieldId.Length is <= 0 or >
                    DataRightsExportLimits.FieldIdMaxLength))
        {
            throw new InvalidDataException(
                "The Properties tenant-export field catalogue is invalid.");
        }

        ValidateActorExportBinding();
        return new SchemaState(new DataRightsExportDescriptor(
            PropertiesTenantTerminationMetadata.OwnerKey,
            PropertiesTenantTerminationMetadata.ExportCatalogId,
            PropertiesTenantTerminationMetadata.ExportCatalogSchemaVersion,
            PropertiesTenantTerminationMetadata.CatalogVersion,
            PropertiesTenantTerminationMetadata.ExportSchemaId,
            PropertiesTenantTerminationMetadata.ExportSchemaVersion,
            Array.AsReadOnly(discoveredFieldIds)));
    }

    private static void ValidateActorExportBinding()
    {
        Assembly assembly =
            typeof(PropertiesTenantTerminationExportSchema).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(
                CatalogResourceName) ??
            throw new InvalidDataException(
                "The Properties personal-data catalogue is unavailable.");
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        PersonalDataCatalogDocument catalog =
            PersonalDataCatalogJson.Parse(buffer.ToArray());
        PersonalDataFieldDefinition actor = catalog.Fields.Single(field =>
            string.Equals(field.Id, ActorFieldId, StringComparison.Ordinal));
        PersonalDataRightsPolicy policy = catalog.RightsPolicies.Single(
            candidate => string.Equals(
                candidate.Id,
                actor.RightsPolicy,
                StringComparison.Ordinal));
        (string Type, string Member)[] expectedBindings =
        [
            (
                typeof(PropertiesGovernanceRevisionTenantExport).FullName!,
                nameof(PropertiesGovernanceRevisionTenantExport.ActorId)),
            (
                typeof(PropertiesPropertyTimeZoneOperationTenantExport)
                    .FullName!,
                nameof(PropertiesPropertyTimeZoneOperationTenantExport
                    .ActorId))
        ];
        bool valid = catalog.CatalogVersion ==
                PropertiesTenantTerminationMetadata
                    .PersonalDataCatalogVersion &&
            string.Equals(
                policy.Export,
                AllowedActorExportPolicy,
                StringComparison.Ordinal) &&
            actor.AllowedSurfaces.Contains(
                PersonalDataSurface.DataRightsExport) &&
            actor.AllowedBoundaries.Contains(
                PersonalDataBoundary.CrossModule) &&
            expectedBindings.All(expected => actor.Bindings.Any(binding =>
                    string.Equals(
                        binding.Assembly,
                        assembly.GetName().Name,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        binding.Type,
                        expected.Type,
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
                        StringComparison.Ordinal)));
        if (!valid)
        {
            throw new InvalidDataException(
                "The Properties tenant-export actor binding is invalid.");
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
        options.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        return options;
    }

    private sealed record SchemaState(DataRightsExportDescriptor Descriptor);
}
