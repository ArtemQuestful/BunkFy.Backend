namespace BunkFy.Modules.Inventory.Persistence.Repositories;

using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BunkFy.DataGovernance;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Inventory.Contracts;

internal static class InventoryTenantTerminationExportSchema
{
    private const string CatalogResourceName =
        "BunkFy.Modules.Inventory.Persistence.DataGovernance." +
        "personal-data-catalog.v1.json";
    private const string ExportRetentionPolicy =
        "inventory-tenant-termination-export-fragment";

    private static readonly Type[] SourceTypes =
    [
        typeof(InventoryUnitTenantExport),
        typeof(InventoryRoomConfigurationTenantExport),
        typeof(InventoryManagementOperationTenantExport),
        typeof(InventoryManualBlockTenantExport),
        typeof(InventoryManualBlockGroupTenantExport),
        typeof(InventoryAllocationTenantExport),
        typeof(InventoryAllocationUnitTenantExport),
        typeof(InventoryAllocationAmendmentDecisionTenantExport),
        typeof(InventoryAnonymisationReceiptTenantExport),
        typeof(InventoryAnonymisationTombstoneTenantExport),
        typeof(InventoryAnonymisationRestoreReceiptTenantExport),
        typeof(InventoryBedRetirementTenantExport),
        typeof(InventoryRoomRetirementTenantExport)
    ];

    private static readonly SensitiveBinding[] SensitiveBindings =
    [
        Binding<InventoryManualBlockTenantExport>(
            nameof(InventoryManualBlockTenantExport.Reason),
            "inventory.operational-reason",
            "include-after-authorized-subject-review-or-tenant-export"),
        Binding<InventoryManualBlockGroupTenantExport>(
            nameof(InventoryManualBlockGroupTenantExport.State),
            "inventory.staff-change-operations",
            "include-in-authorized-staff-or-tenant-export"),
        Binding<InventoryManualBlockGroupTenantExport>(
            nameof(InventoryManualBlockGroupTenantExport.Reason),
            "inventory.operational-reason",
            "include-after-authorized-subject-review-or-tenant-export"),
        Binding<InventoryManualBlockGroupTenantExport>(
            nameof(InventoryManualBlockGroupTenantExport.Actors),
            "inventory.staff-actor-reference",
            "include-in-authorized-staff-or-tenant-export"),
        Binding<InventoryAllocationTenantExport>(
            nameof(InventoryAllocationTenantExport.ReservationId),
            "inventory.guest-reservation-reference",
            "include-in-authorized-reservation-or-tenant-export"),
        Binding<InventoryAllocationTenantExport>(
            nameof(InventoryAllocationTenantExport.Payload),
            "inventory.guest-allocation-operations",
            "include-in-authorized-reservation-or-tenant-export"),
        Binding<InventoryAllocationUnitTenantExport>(
            nameof(InventoryAllocationUnitTenantExport.Payload),
            "inventory.guest-allocation-operations",
            "include-in-authorized-reservation-or-tenant-export"),
        Binding<InventoryAllocationAmendmentDecisionTenantExport>(
            nameof(InventoryAllocationAmendmentDecisionTenantExport
                .ReservationId),
            "inventory.guest-reservation-reference",
            "include-in-authorized-reservation-or-tenant-export"),
        Binding<InventoryAllocationAmendmentDecisionTenantExport>(
            nameof(InventoryAllocationAmendmentDecisionTenantExport.Payload),
            "inventory.guest-allocation-operations",
            "include-in-authorized-reservation-or-tenant-export"),
        Binding<InventoryAnonymisationReceiptTenantExport>(
            nameof(InventoryAnonymisationReceiptTenantExport.Proof),
            "inventory.allocation-anonymisation-proof",
            "include-minimum-anonymisation-proof-in-authorized-case-ledger-or-tenant-export"),
        Binding<InventoryAnonymisationReceiptTenantExport>(
            nameof(InventoryAnonymisationReceiptTenantExport.ActorId),
            "inventory.staff-actor-reference",
            "include-in-authorized-staff-or-tenant-export"),
        Binding<InventoryAnonymisationTombstoneTenantExport>(
            nameof(InventoryAnonymisationTombstoneTenantExport.Proof),
            "inventory.allocation-anonymisation-proof",
            "include-minimum-anonymisation-proof-in-authorized-case-ledger-or-tenant-export"),
        Binding<InventoryAnonymisationRestoreReceiptTenantExport>(
            nameof(InventoryAnonymisationRestoreReceiptTenantExport.Proof),
            "inventory.allocation-anonymisation-proof",
            "include-minimum-anonymisation-proof-in-authorized-case-ledger-or-tenant-export"),
        Binding<InventoryBedRetirementTenantExport>(
            nameof(InventoryBedRetirementTenantExport.Reason),
            "inventory.operational-reason",
            "include-after-authorized-subject-review-or-tenant-export"),
        Binding<InventoryBedRetirementTenantExport>(
            nameof(InventoryBedRetirementTenantExport.RequestedBy),
            "inventory.staff-actor-reference",
            "include-in-authorized-staff-or-tenant-export"),
        Binding<InventoryBedRetirementTenantExport>(
            nameof(InventoryBedRetirementTenantExport.CancellationReason),
            "inventory.cancellation-reason",
            "include-after-authorized-subject-review-or-tenant-export"),
        Binding<InventoryBedRetirementTenantExport>(
            nameof(InventoryBedRetirementTenantExport.CanceledBy),
            "inventory.cancellation-actor-reference",
            "include-in-authorized-staff-or-tenant-export"),
        Binding<InventoryRoomRetirementTenantExport>(
            nameof(InventoryRoomRetirementTenantExport.Reason),
            "inventory.operational-reason",
            "include-after-authorized-subject-review-or-tenant-export"),
        Binding<InventoryRoomRetirementTenantExport>(
            nameof(InventoryRoomRetirementTenantExport.RequestedBy),
            "inventory.staff-actor-reference",
            "include-in-authorized-staff-or-tenant-export"),
        Binding<InventoryRoomRetirementTenantExport>(
            nameof(InventoryRoomRetirementTenantExport.CancellationReason),
            "inventory.cancellation-reason",
            "include-after-authorized-subject-review-or-tenant-export"),
        Binding<InventoryRoomRetirementTenantExport>(
            nameof(InventoryRoomRetirementTenantExport.CanceledBy),
            "inventory.cancellation-actor-reference",
            "include-in-authorized-staff-or-tenant-export")
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
            !InventoryTenantTerminationMetadata.RecordTypes.Contains(
                recordType,
                StringComparer.Ordinal) ||
            recordId == Guid.Empty ||
            recordVersion <= 0)
        {
            throw new InvalidDataException(
                "The Inventory tenant-export record is invalid.");
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
                "The Inventory tenant-export fields are invalid.");
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
        InventoryTenantExportFieldAttribute attribute =
            property.GetCustomAttribute<
                InventoryTenantExportFieldAttribute>() ??
            throw new InvalidDataException(
                $"Inventory tenant-export member '{property.Name}' " +
                "has no field identifier.");
        JsonElement serialized = JsonSerializer.SerializeToElement(
            value,
            value?.GetType() ?? typeof(object),
            SerializerOptions);
        if (Encoding.UTF8.GetByteCount(serialized.GetRawText()) >
            DataRightsExportLimits.MaxFieldValueBytes)
        {
            throw new InvalidDataException(
                $"Inventory tenant-export field '{attribute.FieldId}' " +
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
                InventoryTenantExportFieldAttribute>()?.FieldId ??
                throw new InvalidDataException(
                    $"Inventory tenant-export member '{property.Name}' " +
                    "has no field identifier."))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(fieldId => fieldId, StringComparer.Ordinal)
            .ToArray();
        string[] declaredFieldIds =
            InventoryTenantTerminationMetadata.ExportFieldIds
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
                "The Inventory tenant-export field catalogue is invalid.");
        }

        ValidateSensitiveBindings();
        return new DataRightsExportDescriptor(
            InventoryTenantTerminationMetadata.OwnerKey,
            InventoryTenantTerminationMetadata.ExportCatalogId,
            InventoryTenantTerminationMetadata.ExportCatalogSchemaVersion,
            InventoryTenantTerminationMetadata.CatalogVersion,
            InventoryTenantTerminationMetadata.ExportSchemaId,
            InventoryTenantTerminationMetadata.ExportSchemaVersion,
            Array.AsReadOnly(discoveredFieldIds));
    }

    private static void ValidateSensitiveBindings()
    {
        Assembly assembly =
            typeof(InventoryTenantTerminationExportSchema).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(
                CatalogResourceName) ??
            throw new InvalidDataException(
                "The Inventory personal-data catalogue is unavailable.");
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        PersonalDataCatalogDocument catalog =
            PersonalDataCatalogJson.Parse(buffer.ToArray());
        if (catalog.CatalogVersion !=
                InventoryTenantTerminationMetadata
                    .PersonalDataCatalogVersion ||
            !string.Equals(
                catalog.CatalogId,
                "inventory.personal-data",
                StringComparison.Ordinal) ||
            !string.Equals(
                catalog.Module,
                InventoryTenantTerminationMetadata.OwnerKey,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The Inventory personal-data catalogue identity is invalid.");
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
                    "The Inventory tenant-export binding member is missing.");
            string? actualFieldId = property.GetCustomAttribute<
                InventoryTenantExportFieldAttribute>()?.FieldId;
            bool valid = string.Equals(
                    actualFieldId,
                    expected.FieldId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    field.AuthoritativeOwner,
                    InventoryTenantTerminationMetadata.OwnerKey,
                    StringComparison.Ordinal) &&
                string.Equals(
                    policy.Export,
                    expected.ExportPolicy,
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
                    $"The Inventory tenant-export binding " +
                    $"'{expected.Type.FullName}.{expected.Member}' " +
                    "is invalid.");
            }
        }
    }

    private static SensitiveBinding Binding<T>(
        string member,
        string fieldId,
        string exportPolicy) =>
        new(typeof(T), member, fieldId, exportPolicy);

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
        string ExportPolicy);
}
