namespace BunkFy.Modules.Staff.Persistence.Repositories;

using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BunkFy.DataGovernance;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Staff.Contracts;

internal static class StaffTenantTerminationExportSchema
{
    private const string CatalogResourceName =
        "BunkFy.Modules.Staff.Persistence.DataGovernance." +
        "personal-data-catalog.v1.json";
    private const string ExportRetentionPolicy =
        "staff-tenant-termination-export-fragment";

    private static readonly Type[] SourceTypes =
    [
        typeof(StaffMemberTenantExport),
        typeof(StaffMemberMutationOperationTenantExport),
        typeof(StaffIdentityProvisioningAnchorTenantExport),
        typeof(StaffIdentityProvisioningAnchorResolutionTenantExport),
        typeof(StaffPropertyAssignmentTenantExport),
        typeof(StaffDataRightsCorrectionReceiptTenantExport),
        typeof(StaffProcessingRestrictionTenantExport),
        typeof(StaffProcessingRestrictionReceiptTenantExport),
        typeof(StaffEmploymentGovernanceTenantExport),
        typeof(StaffEmploymentGovernanceChangeReceiptTenantExport),
        typeof(StaffDataHoldTenantExport),
        typeof(StaffDataHoldReceiptTenantExport),
        typeof(StaffAnonymisationReceiptTenantExport),
        typeof(StaffAnonymisationTombstoneTenantExport),
        typeof(StaffAnonymisationRestoreReceiptTenantExport),
        typeof(StaffRetentionExecutionTenantExport),
        typeof(StaffRetentionAnonymisationReceiptTenantExport)
    ];

    private static readonly SensitiveBinding[] SensitiveBindings =
    [
        Binding<StaffMemberTenantExport>(
            nameof(StaffMemberTenantExport.ProfileState),
            "staff.profile-state",
            "include-in-authorized-staff-or-tenant-export"),
        Attribution<StaffMemberTenantExport>(),
        Binding<StaffMemberMutationOperationTenantExport>(
            nameof(StaffMemberMutationOperationTenantExport
                .MemberMutationOperation),
            "staff.member-mutation-operation",
            "include-in-authorized-staff-or-tenant-export"),
        Binding<StaffIdentityProvisioningAnchorTenantExport>(
            nameof(StaffIdentityProvisioningAnchorTenantExport
                .IdentityProvisioningAnchor),
            "staff.identity-provisioning-anchor",
            "include-in-authorized-staff-or-tenant-export"),
        Binding<StaffIdentityProvisioningAnchorResolutionTenantExport>(
            nameof(StaffIdentityProvisioningAnchorResolutionTenantExport
                .IdentityProvisioningAnchorResolution),
            "staff.identity-provisioning-anchor-resolution",
            "include-in-authorized-staff-or-tenant-export"),
        Binding<StaffPropertyAssignmentTenantExport>(
            nameof(StaffPropertyAssignmentTenantExport.AssignmentState),
            "staff.assignment-record",
            "include-in-authorized-staff-or-tenant-export"),
        Attribution<StaffPropertyAssignmentTenantExport>(),
        Binding<StaffDataRightsCorrectionReceiptTenantExport>(
            nameof(StaffDataRightsCorrectionReceiptTenantExport
                .DataRightsProof),
            "staff.data-rights-proof",
            "include-minimum-coordinate-in-authorized-case-ledger-or-tenant-export"),
        Restriction<StaffProcessingRestrictionTenantExport>(),
        Attribution<StaffProcessingRestrictionTenantExport>(),
        Restriction<StaffProcessingRestrictionReceiptTenantExport>(),
        Attribution<StaffProcessingRestrictionReceiptTenantExport>(),
        Binding<StaffEmploymentGovernanceTenantExport>(
            nameof(StaffEmploymentGovernanceTenantExport
                .EmploymentGovernance),
            "staff.employment-governance",
            "include-in-authorized-staff-or-tenant-export"),
        Attribution<StaffEmploymentGovernanceTenantExport>(),
        Binding<StaffEmploymentGovernanceChangeReceiptTenantExport>(
            nameof(StaffEmploymentGovernanceChangeReceiptTenantExport
                .EmploymentGovernanceProof),
            "staff.employment-governance-proof",
            "include-minimum-proof-in-controller-authorized-tenant-export"),
        Attribution<StaffEmploymentGovernanceChangeReceiptTenantExport>(),
        Hold<StaffDataHoldTenantExport>(),
        Attribution<StaffDataHoldTenantExport>(),
        Hold<StaffDataHoldReceiptTenantExport>(),
        Attribution<StaffDataHoldReceiptTenantExport>(),
        Anonymisation<StaffAnonymisationReceiptTenantExport>(),
        Attribution<StaffAnonymisationReceiptTenantExport>(),
        Anonymisation<StaffAnonymisationTombstoneTenantExport>(),
        Anonymisation<StaffAnonymisationRestoreReceiptTenantExport>(),
        Binding<StaffRetentionExecutionTenantExport>(
            nameof(StaffRetentionExecutionTenantExport.RetentionExecution),
            "staff.retention-execution",
            "include-in-controller-authorized-tenant-export"),
        Binding<StaffRetentionAnonymisationReceiptTenantExport>(
            nameof(StaffRetentionAnonymisationReceiptTenantExport
                .RetentionProof),
            "staff.retention-proof",
            "include-minimum-retention-proof-in-controller-authorized-tenant-export"),
        Attribution<StaffRetentionAnonymisationReceiptTenantExport>()
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
            !StaffTenantTerminationMetadata.RecordTypes.Contains(
                recordType,
                StringComparer.Ordinal) ||
            recordId == Guid.Empty ||
            recordVersion <= 0)
        {
            throw new InvalidDataException(
                "The Staff tenant-export record is invalid.");
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
                "The Staff tenant-export fields are invalid.");
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
        StaffTenantExportFieldAttribute attribute =
            property.GetCustomAttribute<StaffTenantExportFieldAttribute>() ??
            throw new InvalidDataException(
                $"Staff tenant-export member '{property.Name}' " +
                "has no field identifier.");
        JsonElement serialized = JsonSerializer.SerializeToElement(
            value,
            value?.GetType() ?? typeof(object),
            SerializerOptions);
        if (Encoding.UTF8.GetByteCount(serialized.GetRawText()) >
            DataRightsExportLimits.MaxFieldValueBytes)
        {
            throw new InvalidDataException(
                $"Staff tenant-export field '{attribute.FieldId}' " +
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
                StaffTenantExportFieldAttribute>()?.FieldId ??
                throw new InvalidDataException(
                    $"Staff tenant-export member '{property.Name}' " +
                    "has no field identifier."))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(fieldId => fieldId, StringComparer.Ordinal)
            .ToArray();
        string[] declaredFieldIds = StaffTenantTerminationMetadata
            .ExportFieldIds
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
                "The Staff tenant-export field catalogue is invalid.");
        }

        ValidateSensitiveBindings();
        return new DataRightsExportDescriptor(
            StaffTenantTerminationMetadata.OwnerKey,
            StaffTenantTerminationMetadata.ExportCatalogId,
            StaffTenantTerminationMetadata.ExportCatalogSchemaVersion,
            StaffTenantTerminationMetadata.CatalogVersion,
            StaffTenantTerminationMetadata.ExportSchemaId,
            StaffTenantTerminationMetadata.ExportSchemaVersion,
            Array.AsReadOnly(discoveredFieldIds));
    }

    private static void ValidateSensitiveBindings()
    {
        Assembly assembly = typeof(StaffTenantTerminationExportSchema)
            .Assembly;
        using Stream stream = assembly.GetManifestResourceStream(
                CatalogResourceName) ??
            throw new InvalidDataException(
                "The Staff personal-data catalogue is unavailable.");
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        PersonalDataCatalogDocument catalog =
            PersonalDataCatalogJson.Parse(buffer.ToArray());
        if (catalog.CatalogVersion !=
                StaffTenantTerminationMetadata.PersonalDataCatalogVersion ||
            !string.Equals(
                catalog.CatalogId,
                "staff.personal-data",
                StringComparison.Ordinal) ||
            !string.Equals(
                catalog.Module,
                StaffTenantTerminationMetadata.OwnerKey,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The Staff personal-data catalogue identity is invalid.");
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
                    "The Staff tenant-export binding member is missing.");
            string? actualFieldId = property.GetCustomAttribute<
                StaffTenantExportFieldAttribute>()?.FieldId;
            bool valid = string.Equals(
                    actualFieldId,
                    expected.FieldId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    field.AuthoritativeOwner,
                    StaffTenantTerminationMetadata.OwnerKey,
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
                    $"The Staff tenant-export binding " +
                    $"'{expected.Type.FullName}.{expected.Member}' " +
                    "is invalid.");
            }
        }
    }

    private static SensitiveBinding Attribution<T>() =>
        Binding<T>(
            "StaffAttribution",
            "staff.staff-attribution",
            "include-in-authorized-staff-or-tenant-export");

    private static SensitiveBinding Restriction<T>() =>
        Binding<T>(
            "ProcessingRestriction",
            "staff.processing-restriction",
            "include-current-state-in-authorized-staff-or-tenant-export");

    private static SensitiveBinding Hold<T>() =>
        Binding<T>(
            "DataHold",
            "staff.data-hold",
            "include-in-authorized-staff-or-tenant-export");

    private static SensitiveBinding Anonymisation<T>() =>
        Binding<T>(
            "AnonymisationProof",
            "staff.anonymisation-proof",
            "include-minimum-proof-in-authorized-case-ledger-or-tenant-export");

    private static SensitiveBinding Binding<T>(
        string member,
        string fieldId,
        string exportPolicy) =>
        new(typeof(T), member, fieldId, exportPolicy);

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        JsonSerializerOptions options =
            new(JsonSerializerDefaults.Web);
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
