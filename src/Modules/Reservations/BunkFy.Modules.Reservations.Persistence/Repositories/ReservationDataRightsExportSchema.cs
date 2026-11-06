namespace BunkFy.Modules.Reservations.Persistence.Repositories;

using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BunkFy.DataGovernance;
using BunkFy.Modules.DataRights.Contracts;

internal static class ReservationDataRightsExportSchema
{
    public const string ExportSchemaId = "reservations.subject-export";
    public const int ExportSchemaVersion = 9;

    private const string CatalogResourceName =
        "BunkFy.Modules.Reservations.Persistence.DataGovernance.personal-data-catalog.v1.json";
    private const string ExportRetentionPolicy = "reservation-data-rights-export-fragment";

    private static readonly Dictionary<string, string> ExportPoliciesByRightsPolicy =
        new(StringComparer.Ordinal)
        {
            ["guest-reservation-data"] =
                "include-in-authorized-guest-or-tenant-export",
            ["guest-reservation-link"] =
                "include-in-authorized-guest-or-tenant-export",
            ["adapter-provenance"] =
                "include-subject-linked-provider-provenance-or-tenant-export",
            ["reservation-data-rights-correction-accountability"] =
                "include-minimum-coordinate-in-authorized-case-ledger-or-tenant-export",
            ["reservation-processing-restriction-accountability"] =
                "include-minimum-restriction-state-in-authorized-case-ledger-or-tenant-export",
            ["reservation-data-hold-accountability"] =
                "include-minimum-hold-state-in-authorized-case-ledger-or-tenant-export",
            ["reservation-anonymisation-accountability"] =
                "include-minimum-anonymisation-proof-in-authorized-case-ledger-or-tenant-export"
        };

    private static readonly Type[] SourceTypes =
    [
        typeof(ReservationDataRightsExport),
        typeof(ReservationPendingAmendmentDataRightsExport),
        typeof(ReservationGuestLinkDataRightsExport),
        typeof(ReservationDetailsHistoryDataRightsExport),
        typeof(ReservationDataRightsCorrectionReceiptDataRightsExport),
        typeof(ReservationProcessingRestrictionDataRightsExport),
        typeof(ReservationProcessingRestrictionStateDataRightsExport),
        typeof(ReservationProcessingRestrictionReceiptDataRightsExport),
        typeof(ReservationDataHoldDataRightsExport),
        typeof(ReservationDataHoldReceiptDataRightsExport),
        typeof(ReservationAnonymisationReceiptDataRightsExport),
        typeof(ReservationExternalOperationDataRightsExport),
        typeof(ReservationManagementOperationDataRightsExport),
        typeof(ReservationStayAmendmentOperationDataRightsExport),
        typeof(ReservationArrivalReminderDataRightsExport)
    ];

    private static readonly JsonSerializerOptions ValueSerializerOptions = CreateSerializerOptions();
    private static readonly Lazy<SchemaState> State = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static DataRightsExportDescriptor Descriptor => State.Value.Descriptor;

    public static void EnsureValid() => _ = State.Value;

    public static DataRightsExportRecord CreateReservationRecord(
        ReservationDataRightsExport reservation) =>
        CreateRecord(
            ReservationDataRightsDiscoveryContributor.ReservationRecordType,
            reservation.ReservationId,
            reservation.Version,
            reservation);

    public static DataRightsExportRecord CreatePendingAmendmentRecord(
        ReservationPendingAmendmentDataRightsExport amendment) =>
        CreateRecord(
            ReservationDataRightsExportContributor.PendingAmendmentRecordType,
            amendment.AmendmentRequestId,
            amendment.ReservationVersion,
            amendment);

    public static DataRightsExportRecord CreateGuestLinkRecord(
        ReservationGuestLinkDataRightsExport link) =>
        CreateRecord(
            ReservationDataRightsExportContributor.GuestLinkRecordType,
            link.GuestId,
            link.LinkVersion,
            link);

    public static DataRightsExportRecord CreateDetailsHistoryRecord(
        ReservationDetailsHistoryDataRightsExport history) =>
        CreateRecord(
            ReservationDataRightsExportContributor.DetailsHistoryRecordType,
            history.ChangeId,
            history.ToRevision,
            history);

    public static DataRightsExportRecord CreateExternalOperationRecord(
        ReservationExternalOperationDataRightsExport operation) =>
        CreateRecord(
            ReservationDataRightsExportContributor.ExternalOperationRecordType,
            operation.OperationId,
            recordVersion: 1,
            operation);

    public static DataRightsExportRecord CreateManagementOperationRecord(
        ReservationManagementOperationDataRightsExport operation) =>
        CreateRecord(
            ReservationDataRightsExportContributor
                .ManagementOperationRecordType,
            operation.OperationId,
            recordVersion: 3,
            operation);

    public static DataRightsExportRecord CreateStayAmendmentOperationRecord(
        ReservationStayAmendmentOperationDataRightsExport operation) =>
        CreateRecord(
            ReservationDataRightsExportContributor
                .StayAmendmentOperationRecordType,
            DataRightsExportRecordIds.CreateDeterministicChild(
                operation.ReservationId,
                operation.OperationId.ToString("N")),
            operation.OperationVersion,
            operation);

    public static DataRightsExportRecord CreateDataRightsCorrectionReceiptRecord(
        ReservationDataRightsCorrectionReceiptDataRightsExport receipt) =>
        CreateRecord(
            ReservationDataRightsExportContributor.DataRightsCorrectionReceiptRecordType,
            receipt.ReceiptId,
            receipt.CurrentVersion,
            receipt);

    public static DataRightsExportRecord CreateArrivalReminderRecord(
        ReservationArrivalReminderDataRightsExport reminder) =>
        CreateRecord(
            ReservationDataRightsExportContributor.ArrivalReminderRecordType,
            reminder.ReminderId,
            reminder.Version,
            reminder);

    public static DataRightsExportRecord CreateProcessingRestrictionRecord(
        ReservationProcessingRestrictionDataRightsExport restriction) =>
        CreateRecord(
            ReservationDataRightsExportContributor.ProcessingRestrictionRecordType,
            restriction.RestrictionId,
            restriction.Version,
            restriction);

    public static DataRightsExportRecord CreateProcessingRestrictionStateRecord(
        ReservationProcessingRestrictionStateDataRightsExport state) =>
        CreateRecord(
            ReservationDataRightsExportContributor
                .ProcessingRestrictionStateRecordType,
            state.ReservationId,
            checked(state.Revision + 1),
            state);

    public static DataRightsExportRecord CreateProcessingRestrictionReceiptRecord(
        ReservationProcessingRestrictionReceiptDataRightsExport receipt) =>
        CreateRecord(
            ReservationDataRightsExportContributor
                .ProcessingRestrictionReceiptRecordType,
            receipt.ReceiptId,
            receipt.ResultingProjectionRevision,
            receipt);

    public static DataRightsExportRecord CreateDataHoldRecord(
        ReservationDataHoldDataRightsExport hold) =>
        CreateRecord(
            ReservationDataRightsExportContributor.DataHoldRecordType,
            hold.HoldId,
            hold.Version,
            hold);

    public static DataRightsExportRecord CreateDataHoldReceiptRecord(
        ReservationDataHoldReceiptDataRightsExport receipt) =>
        CreateRecord(
            ReservationDataRightsExportContributor.DataHoldReceiptRecordType,
            receipt.ReceiptId,
            receipt.ResultingHoldVersion,
            receipt);

    public static DataRightsExportRecord CreateAnonymisationReceiptRecord(
        ReservationAnonymisationReceiptDataRightsExport receipt) =>
        CreateRecord(
            ReservationDataRightsExportContributor
                .AnonymisationReceiptRecordType,
            receipt.ReceiptId,
            receipt.ResultingReservationVersion,
            receipt);

    private static DataRightsExportRecord CreateRecord(
        string recordType,
        Guid recordId,
        long recordVersion,
        object source)
    {
        Type sourceType = source.GetType();
        PropertyInfo[] properties = sourceType.GetProperties(BindingFlags.Instance | BindingFlags.Public);
        if (!SourceTypes.Contains(sourceType) ||
            recordId == Guid.Empty ||
            recordVersion <= 0 ||
            string.IsNullOrWhiteSpace(recordType) ||
            recordType.Length > DataRightsExportLimits.RecordTypeMaxLength ||
            properties.Length is <= 0 or > DataRightsExportLimits.MaxFieldsPerRecord)
        {
            throw new InvalidDataException("The Reservations data-rights export record is invalid.");
        }

        DataRightsExportField[] fields = properties
            .Select(property => CreateField(
                sourceType,
                property.Name,
                property.GetValue(source)))
            .OrderBy(field => field.FieldId, StringComparer.Ordinal)
            .ToArray();
        return new DataRightsExportRecord(
            recordType,
            recordId,
            recordVersion,
            Array.AsReadOnly(fields));
    }

    private static DataRightsExportField CreateField(
        Type sourceType,
        string member,
        object? value)
    {
        string key = MemberKey(sourceType, member);
        if (!State.Value.FieldIdsByMember.TryGetValue(key, out string? fieldId))
        {
            throw new InvalidDataException(
                $"The Reservations data-rights export member '{key}' is not catalogue-approved.");
        }

        JsonElement serialized = JsonSerializer.SerializeToElement(
            value,
            value?.GetType() ?? typeof(object),
            ValueSerializerOptions);
        if (Encoding.UTF8.GetByteCount(serialized.GetRawText()) >
            DataRightsExportLimits.MaxFieldValueBytes)
        {
            throw new InvalidDataException(
                $"The Reservations data-rights export field '{fieldId}' exceeds its size limit.");
        }

        return new DataRightsExportField(fieldId, serialized);
    }

    private static SchemaState Load()
    {
        Assembly assembly = typeof(ReservationDataRightsExportSchema).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(CatalogResourceName) ??
            throw new InvalidDataException("The Reservations personal-data catalogue is unavailable.");
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        PersonalDataCatalogDocument catalog = PersonalDataCatalogJson.Parse(buffer.ToArray());
        if (!string.Equals(catalog.CatalogId, "reservations.personal-data", StringComparison.Ordinal) ||
            !string.Equals(catalog.Module, ReservationDataRightsDiscoveryContributor.Owner, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Reservations personal-data catalogue identity is invalid.");
        }

        HashSet<string> expectedMembers = SourceTypes
            .SelectMany(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Select(property => MemberKey(type, property.Name)))
            .ToHashSet(StringComparer.Ordinal);
        Dictionary<string, string> fieldIdsByMember = new(StringComparer.Ordinal);

        foreach (PersonalDataFieldDefinition field in catalog.Fields)
        {
            PersonalDataRightsPolicy rightsPolicy = catalog.RightsPolicies.Single(
                policy => string.Equals(policy.Id, field.RightsPolicy, StringComparison.Ordinal));
            foreach (PersonalDataMemberBinding binding in field.Bindings.Where(
                         binding =>
                             binding.Surface ==
                                 PersonalDataSurface.DataRightsExport &&
                             string.Equals(
                                 binding.RetentionPolicy,
                                 ExportRetentionPolicy,
                                 StringComparison.Ordinal)))
            {
                string key = string.Join('|', binding.Type, binding.Member);
                if (!expectedMembers.Contains(key) ||
                    field.Id.Length > DataRightsExportLimits.FieldIdMaxLength ||
                    !string.Equals(
                        field.AuthoritativeOwner,
                        ReservationDataRightsDiscoveryContributor.Owner,
                        StringComparison.Ordinal) ||
                    !ExportPoliciesByRightsPolicy.TryGetValue(
                        rightsPolicy.Id,
                        out string? expectedPolicy) ||
                    !string.Equals(rightsPolicy.Export, expectedPolicy, StringComparison.Ordinal) ||
                    !field.AllowedBoundaries.Contains(PersonalDataBoundary.CrossModule) ||
                    !fieldIdsByMember.TryAdd(key, field.Id))
                {
                    throw new InvalidDataException(
                        $"The Reservations data-rights export binding '{key}' is invalid.");
                }
            }
        }

        string[] missing = expectedMembers
            .Except(fieldIdsByMember.Keys, StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException(
                $"The Reservations data-rights export catalogue is missing: {string.Join(", ", missing)}.");
        }

        string[] fieldIds = fieldIdsByMember.Values
            .Distinct(StringComparer.Ordinal)
            .OrderBy(fieldId => fieldId, StringComparer.Ordinal)
            .ToArray();
        DataRightsExportDescriptor descriptor = new(
            ReservationDataRightsDiscoveryContributor.Owner,
            catalog.CatalogId,
            catalog.SchemaVersion,
            catalog.CatalogVersion,
            ExportSchemaId,
            ExportSchemaVersion,
            Array.AsReadOnly(fieldIds));
        return new SchemaState(descriptor, fieldIdsByMember);
    }

    private static string MemberKey(Type sourceType, string member) =>
        string.Join('|', sourceType.FullName, member);

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
        return options;
    }

    private sealed record SchemaState(
        DataRightsExportDescriptor Descriptor,
        IReadOnlyDictionary<string, string> FieldIdsByMember);
}
