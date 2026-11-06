namespace BunkFy.Modules.Reservations.Persistence.Repositories;

using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BunkFy.DataGovernance;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Reservations.Contracts;

internal static class ReservationsTenantTerminationExportSchema
{
    private const string CatalogResourceName =
        "BunkFy.Modules.Reservations.Persistence.DataGovernance." +
        "personal-data-catalog.v1.json";
    private const string ExportRetentionPolicy =
        "reservations-tenant-termination-export-fragment";

    private static readonly Type[] SourceTypes =
    [
        typeof(ReservationTenantExport),
        typeof(ReservationRequestedInventoryUnitTenantExport),
        typeof(ReservationPendingAmendmentTenantExport),
        typeof(ReservationGuestLinkTenantExport),
        typeof(ReservationGuestRecordLinkProcessTenantExport),
        typeof(ReservationDetailsHistoryTenantExport),
        typeof(ReservationExternalOperationTenantExport),
        typeof(ReservationManagementOperationTenantExport),
        typeof(ReservationStayAmendmentOperationTenantExport),
        typeof(ReservationArrivalReminderTenantExport),
        typeof(ReservationDataRightsCorrectionReceiptTenantExport),
        typeof(ReservationProcessingRestrictionTenantExport),
        typeof(ReservationProcessingRestrictionReceiptTenantExport),
        typeof(ReservationDataHoldTenantExport),
        typeof(ReservationDataHoldReceiptTenantExport),
        typeof(ReservationAnonymisationReceiptTenantExport),
        typeof(ReservationAnonymisationTombstoneTenantExport),
        typeof(ReservationAnonymisationRestoreReceiptTenantExport),
        typeof(ReservationRetentionExecutionTenantExport),
        typeof(ReservationRetentionAnonymisationReceiptTenantExport)
    ];

    private static readonly SensitiveBinding[] SensitiveBindings =
    [
        GuestData<ReservationTenantExport>(
            nameof(ReservationTenantExport.BookingState),
            "reservations.booking-state"),
        GuestData<ReservationTenantExport>(
            nameof(ReservationTenantExport.GuestDetails),
            "reservations.guest-details"),
        Provider<ReservationTenantExport>(
            nameof(ReservationTenantExport.ProviderProvenance)),
        Staff<ReservationTenantExport>(
            nameof(ReservationTenantExport.StaffAttribution)),
        GuestData<ReservationPendingAmendmentTenantExport>(
            nameof(ReservationPendingAmendmentTenantExport.BookingState),
            "reservations.booking-state"),
        GuestData<ReservationPendingAmendmentTenantExport>(
            nameof(ReservationPendingAmendmentTenantExport.GuestDetails),
            "reservations.guest-details"),
        Provider<ReservationPendingAmendmentTenantExport>(
            nameof(ReservationPendingAmendmentTenantExport
                .ProviderProvenance)),
        Staff<ReservationPendingAmendmentTenantExport>(
            nameof(ReservationPendingAmendmentTenantExport
                .StaffAttribution)),
        Binding<ReservationGuestLinkTenantExport>(
            nameof(ReservationGuestLinkTenantExport.GuestLink),
            "reservations.guest-link",
            "include-in-authorized-guest-or-tenant-export"),
        Staff<ReservationGuestLinkTenantExport>(
            nameof(ReservationGuestLinkTenantExport.StaffAttribution)),
        Binding<ReservationGuestRecordLinkProcessTenantExport>(
            nameof(ReservationGuestRecordLinkProcessTenantExport.Process),
            "reservations.guest-record-link-process",
            "include-in-authorized-guest-or-tenant-export"),
        Staff<ReservationGuestRecordLinkProcessTenantExport>(
            nameof(ReservationGuestRecordLinkProcessTenantExport
                .StaffAttribution)),
        GuestData<ReservationDetailsHistoryTenantExport>(
            nameof(ReservationDetailsHistoryTenantExport.DetailsHistory),
            "reservations.details-history"),
        Provider<ReservationDetailsHistoryTenantExport>(
            nameof(ReservationDetailsHistoryTenantExport.ProviderProvenance)),
        Staff<ReservationDetailsHistoryTenantExport>(
            nameof(ReservationDetailsHistoryTenantExport.StaffAttribution)),
        Provider<ReservationExternalOperationTenantExport>(
            nameof(ReservationExternalOperationTenantExport
                .ProviderProvenance)),
        Binding<ReservationManagementOperationTenantExport>(
            nameof(ReservationManagementOperationTenantExport
                .ManagementOperation),
            "reservations.management-operation",
            "include-in-authorized-guest-or-tenant-export"),
        Staff<ReservationManagementOperationTenantExport>(nameof(ReservationManagementOperationTenantExport.StaffAttribution)),
        Binding<ReservationStayAmendmentOperationTenantExport>(
            nameof(ReservationStayAmendmentOperationTenantExport
                .StayAmendmentOperation),
            "reservations.stay-amendment-operation",
            "include-in-authorized-guest-or-tenant-export"),
        Staff<ReservationStayAmendmentOperationTenantExport>(
            nameof(ReservationStayAmendmentOperationTenantExport
                .StaffAttribution)),
        GuestData<ReservationArrivalReminderTenantExport>(
            nameof(ReservationArrivalReminderTenantExport.ReminderState),
            "reservations.reminder-state"),
        Binding<ReservationDataRightsCorrectionReceiptTenantExport>(
            nameof(ReservationDataRightsCorrectionReceiptTenantExport
                .DataRightsProof),
            "reservations.data-rights-proof",
            "include-minimum-coordinate-in-authorized-case-ledger-or-tenant-export"),
        Restriction<ReservationProcessingRestrictionTenantExport>(
            nameof(ReservationProcessingRestrictionTenantExport
                .ProcessingRestriction)),
        Staff<ReservationProcessingRestrictionTenantExport>(
            nameof(ReservationProcessingRestrictionTenantExport
                .StaffAttribution)),
        Restriction<
            ReservationProcessingRestrictionReceiptTenantExport>(
            nameof(ReservationProcessingRestrictionReceiptTenantExport
                .ProcessingRestriction)),
        Hold<ReservationDataHoldTenantExport>(
            nameof(ReservationDataHoldTenantExport.DataHold)),
        Staff<ReservationDataHoldTenantExport>(
            nameof(ReservationDataHoldTenantExport.StaffAttribution)),
        Hold<ReservationDataHoldReceiptTenantExport>(
            nameof(ReservationDataHoldReceiptTenantExport.DataHold)),
        Anonymisation<ReservationAnonymisationReceiptTenantExport>(
            nameof(ReservationAnonymisationReceiptTenantExport
                .AnonymisationProof)),
        Staff<ReservationAnonymisationReceiptTenantExport>(
            nameof(ReservationAnonymisationReceiptTenantExport
                .StaffAttribution)),
        Anonymisation<ReservationAnonymisationTombstoneTenantExport>(
            nameof(ReservationAnonymisationTombstoneTenantExport
                .AnonymisationProof)),
        Anonymisation<ReservationAnonymisationRestoreReceiptTenantExport>(
            nameof(ReservationAnonymisationRestoreReceiptTenantExport
                .AnonymisationProof)),
        Binding<ReservationRetentionExecutionTenantExport>(
            nameof(ReservationRetentionExecutionTenantExport
                .RetentionExecution),
            "reservations.retention-execution",
            "include-in-controller-authorized-tenant-export"),
        Binding<ReservationRetentionAnonymisationReceiptTenantExport>(
            nameof(ReservationRetentionAnonymisationReceiptTenantExport
                .RetentionProof),
            "reservations.retention-proof",
            "include-minimum-retention-proof-in-controller-authorized-tenant-export"),
        Staff<ReservationRetentionAnonymisationReceiptTenantExport>(
            nameof(ReservationRetentionAnonymisationReceiptTenantExport
                .StaffAttribution))
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
            !ReservationsTenantTerminationMetadata.RecordTypes.Contains(
                recordType,
                StringComparer.Ordinal) ||
            recordId == Guid.Empty ||
            recordVersion <= 0)
        {
            throw new InvalidDataException(
                "The Reservations tenant-export record is invalid.");
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
                "The Reservations tenant-export fields are invalid.");
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
        ReservationsTenantExportFieldAttribute attribute =
            property.GetCustomAttribute<
                ReservationsTenantExportFieldAttribute>() ??
            throw new InvalidDataException(
                $"Reservations tenant-export member '{property.Name}' " +
                "has no field identifier.");
        JsonElement serialized = JsonSerializer.SerializeToElement(
            value,
            value?.GetType() ?? typeof(object),
            SerializerOptions);
        if (Encoding.UTF8.GetByteCount(serialized.GetRawText()) >
            DataRightsExportLimits.MaxFieldValueBytes)
        {
            throw new InvalidDataException(
                $"Reservations tenant-export field '{attribute.FieldId}' " +
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
                ReservationsTenantExportFieldAttribute>()?.FieldId ??
                throw new InvalidDataException(
                    $"Reservations tenant-export member " +
                    $"'{property.Name}' has no field identifier."))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(fieldId => fieldId, StringComparer.Ordinal)
            .ToArray();
        string[] declaredFieldIds =
            ReservationsTenantTerminationMetadata.ExportFieldIds
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
                "The Reservations tenant-export field catalogue is invalid.");
        }

        ValidateSensitiveBindings();
        return new DataRightsExportDescriptor(
            ReservationsTenantTerminationMetadata.OwnerKey,
            ReservationsTenantTerminationMetadata.ExportCatalogId,
            ReservationsTenantTerminationMetadata.ExportCatalogSchemaVersion,
            ReservationsTenantTerminationMetadata.CatalogVersion,
            ReservationsTenantTerminationMetadata.ExportSchemaId,
            ReservationsTenantTerminationMetadata.ExportSchemaVersion,
            Array.AsReadOnly(discoveredFieldIds));
    }

    private static void ValidateSensitiveBindings()
    {
        Assembly assembly =
            typeof(ReservationsTenantTerminationExportSchema).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(
                CatalogResourceName) ??
            throw new InvalidDataException(
                "The Reservations personal-data catalogue is unavailable.");
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        PersonalDataCatalogDocument catalog =
            PersonalDataCatalogJson.Parse(buffer.ToArray());
        if (catalog.CatalogVersion !=
                ReservationsTenantTerminationMetadata
                    .PersonalDataCatalogVersion ||
            !string.Equals(
                catalog.CatalogId,
                "reservations.personal-data",
                StringComparison.Ordinal) ||
            !string.Equals(
                catalog.Module,
                ReservationsTenantTerminationMetadata.OwnerKey,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The Reservations personal-data catalogue identity is invalid.");
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
                    "The Reservations tenant-export binding member is missing.");
            string? actualFieldId = property.GetCustomAttribute<
                ReservationsTenantExportFieldAttribute>()?.FieldId;
            bool valid = string.Equals(
                    actualFieldId,
                    expected.FieldId,
                    StringComparison.Ordinal) &&
                string.Equals(
                    field.AuthoritativeOwner,
                    ReservationsTenantTerminationMetadata.OwnerKey,
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
                    $"The Reservations tenant-export binding " +
                    $"'{expected.Type.FullName}.{expected.Member}' " +
                    "is invalid.");
            }
        }
    }

    private static SensitiveBinding GuestData<T>(
        string member,
        string fieldId) =>
        Binding<T>(
            member,
            fieldId,
            "include-in-authorized-guest-or-tenant-export");

    private static SensitiveBinding Provider<T>(string member) =>
        Binding<T>(
            member,
            "reservations.provider-provenance",
            "include-subject-linked-provider-provenance-or-tenant-export");

    private static SensitiveBinding Staff<T>(string member) =>
        Binding<T>(
            member,
            "reservations.staff-attribution",
            "include-in-authorized-staff-or-tenant-export");

    private static SensitiveBinding Restriction<T>(string member) =>
        Binding<T>(
            member,
            "reservations.processing-restriction",
            "include-minimum-restriction-state-in-authorized-case-ledger-or-tenant-export");

    private static SensitiveBinding Hold<T>(string member) =>
        Binding<T>(
            member,
            "reservations.data-hold",
            "include-minimum-hold-state-in-authorized-case-ledger-or-tenant-export");

    private static SensitiveBinding Anonymisation<T>(string member) =>
        Binding<T>(
            member,
            "reservations.anonymisation-proof",
            "include-minimum-anonymisation-proof-in-authorized-case-ledger-or-tenant-export");

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
