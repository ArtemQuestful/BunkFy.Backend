namespace BunkFy.Modules.Staff.Persistence.Repositories;

using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BunkFy.DataGovernance;
using BunkFy.Modules.DataRights.Contracts;

internal static class StaffDataRightsExportSchema
{
    public const string ExportSchemaId = "staff.subject-export";
    public const int ExportSchemaVersion = 5;

    private const string CatalogResourceName =
        "BunkFy.Modules.Staff.Persistence.DataGovernance.personal-data-catalog.v1.json";
    private const string TenantCapableExportPolicy =
        "include-in-authorized-staff-or-tenant-export";
    private const string SubjectExportPolicy =
        "include-in-authorized-staff-export";
    private const string ExportRetentionPolicy = "staff-data-rights-export-fragment";

    private static readonly JsonSerializerOptions ValueSerializerOptions = CreateSerializerOptions();
    private static readonly Lazy<SchemaState> State = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static DataRightsExportDescriptor Descriptor => State.Value.Descriptor;

    public static void EnsureValid() => _ = State.Value;

    public static DataRightsExportRecord CreateProfileRecord(
        StaffProfileDataRightsExport profile) =>
        CreateRecord(
            StaffDataRightsDiscoveryContributor.ProfileRecordType,
            profile.StaffMemberId,
            profile.Version,
            typeof(StaffProfileDataRightsExport),
            [
                (nameof(profile.StaffMemberId), profile.StaffMemberId),
                (nameof(profile.DisplayName), profile.DisplayName),
                (nameof(profile.LegalName), profile.LegalName),
                (nameof(profile.WorkEmail), profile.WorkEmail),
                (nameof(profile.WorkPhone), profile.WorkPhone),
                (nameof(profile.EmployeeNumber), profile.EmployeeNumber),
                (nameof(profile.JobTitle), profile.JobTitle),
                (nameof(profile.Department), profile.Department),
                (nameof(profile.AuthSubjectId), profile.AuthSubjectId),
                (nameof(profile.Status), profile.Status),
                (nameof(profile.Version), profile.Version),
                (nameof(profile.CreatedAtUtc), profile.CreatedAtUtc),
                (nameof(profile.LastChangedAtUtc), profile.LastChangedAtUtc),
                (nameof(profile.SuspendedAtUtc), profile.SuspendedAtUtc),
                (nameof(profile.DepartedAtUtc), profile.DepartedAtUtc),
                (nameof(profile.DepartureEffectiveOn), profile.DepartureEffectiveOn)
            ]);

    public static DataRightsExportRecord CreateAssignmentRecord(
        StaffAssignmentDataRightsExport assignment) =>
        CreateRecord(
            StaffDataRightsExportContributor.AssignmentRecordType,
            assignment.AssignmentId,
            assignment.RecordVersion,
            typeof(StaffAssignmentDataRightsExport),
            [
                (nameof(assignment.AssignmentId), assignment.AssignmentId),
                (nameof(assignment.StaffMemberId), assignment.StaffMemberId),
                (nameof(assignment.PropertyId), assignment.PropertyId),
                (nameof(assignment.PropertyJobTitle), assignment.PropertyJobTitle),
                (nameof(assignment.IsPrimary), assignment.IsPrimary),
                (nameof(assignment.IsCurrent), assignment.IsCurrent),
                (nameof(assignment.EffectiveFrom), assignment.EffectiveFrom),
                (nameof(assignment.EffectiveTo), assignment.EffectiveTo),
                (nameof(assignment.AssignedAtUtc), assignment.AssignedAtUtc),
                (nameof(assignment.AssignedAtVersion), assignment.AssignedAtVersion),
                (nameof(assignment.UnassignedAtUtc), assignment.UnassignedAtUtc),
                (nameof(assignment.UnassignedAtVersion), assignment.UnassignedAtVersion)
            ]);

    public static DataRightsExportRecord
        CreateEmploymentGovernanceRecord(
            StaffEmploymentGovernanceDataRightsExport governance) =>
        CreateRecord(
            StaffDataRightsExportContributor
                .EmploymentGovernanceRecordType,
            governance.StaffMemberId,
            governance.Version,
            typeof(StaffEmploymentGovernanceDataRightsExport),
            [
                (nameof(governance.StaffMemberId), governance.StaffMemberId),
                (nameof(governance.ContractVersion), governance.ContractVersion),
                (nameof(governance.SelectedStaffVersion), governance.SelectedStaffVersion),
                (nameof(governance.OperatingCountryCode), governance.OperatingCountryCode),
                (nameof(governance.PolicyId), governance.PolicyId),
                (nameof(governance.PolicyVersion), governance.PolicyVersion),
                (nameof(governance.DataRegionId), governance.DataRegionId),
                (nameof(governance.TransferProfileId), governance.TransferProfileId),
                (nameof(governance.RetentionPolicyId), governance.RetentionPolicyId),
                (nameof(governance.RetentionPolicyVersion), governance.RetentionPolicyVersion),
                (nameof(governance.PolicyContentSha256), governance.PolicyContentSha256),
                (nameof(governance.PolicyEffectiveAtUtc), governance.PolicyEffectiveAtUtc),
                (nameof(governance.PolicyExpiresAtUtc), governance.PolicyExpiresAtUtc),
                (nameof(governance.EvaluatedAtUtc), governance.EvaluatedAtUtc),
                (nameof(governance.AcceptedAcknowledgements), governance.AcceptedAcknowledgements),
                (nameof(governance.ConfiguredAtUtc), governance.ConfiguredAtUtc),
                (nameof(governance.Version), governance.Version)
            ]);

    public static DataRightsExportRecord CreateDataHoldRecord(
        StaffDataHoldDataRightsExport hold) =>
        CreateRecord(
            StaffDataRightsExportContributor.DataHoldRecordType,
            hold.HoldId,
            hold.Version,
            typeof(StaffDataHoldDataRightsExport),
            [
                (nameof(hold.HoldId), hold.HoldId),
                (nameof(hold.StaffMemberId), hold.StaffMemberId),
                (nameof(hold.ReasonCode), hold.ReasonCode),
                (nameof(hold.Status), hold.Status),
                (nameof(hold.PlacedAtUtc), hold.PlacedAtUtc),
                (nameof(hold.ReleasedAtUtc), hold.ReleasedAtUtc),
                (nameof(hold.Version), hold.Version)
            ]);

    public static DataRightsExportRecord CreateMemberMutationOperationRecord(
        StaffMemberMutationOperationDataRightsExport operation) =>
        CreateRecord(
            StaffDataRightsExportContributor.MemberMutationOperationRecordType,
            DataRightsExportRecordIds.CreateDeterministicChild(
                operation.StaffMemberId,
                operation.OperationId.ToString("N")),
            operation.ResultVersion,
            typeof(StaffMemberMutationOperationDataRightsExport),
            [
                (nameof(operation.OperationId), operation.OperationId),
                (nameof(operation.ScopeId), operation.ScopeId),
                (nameof(operation.StaffMemberId), operation.StaffMemberId),
                (nameof(operation.Kind), operation.Kind),
                (nameof(operation.ExpectedVersion), operation.ExpectedVersion),
                (nameof(operation.RequestFingerprint), operation.RequestFingerprint),
                (nameof(operation.ResultStatus), operation.ResultStatus),
                (nameof(operation.ResultVersion), operation.ResultVersion),
                (nameof(operation.CompletedAtUtc), operation.CompletedAtUtc)
            ]);

    public static DataRightsExportRecord
        CreateIdentityProvisioningAnchorRecord(
            StaffIdentityProvisioningAnchorDataRightsExport anchor) =>
        CreateRecord(
            StaffDataRightsExportContributor
                .IdentityProvisioningAnchorRecordType,
            IdentityProvisioningAnchorRecordId.Create(
                anchor.StaffMemberId,
                anchor.SourceKind,
                anchor.SourceId),
            recordVersion: 1,
            typeof(StaffIdentityProvisioningAnchorDataRightsExport),
            [
                (nameof(anchor.StaffMemberId), anchor.StaffMemberId),
                (nameof(anchor.SourceKind), anchor.SourceKind),
                (nameof(anchor.SourceId), anchor.SourceId),
                (nameof(anchor.ResolutionEventId),
                    anchor.ResolutionEventId),
                (nameof(anchor.AnchoredAtUtc), anchor.AnchoredAtUtc)
            ]);

    public static DataRightsExportRecord
        CreateIdentityProvisioningAnchorResolutionRecord(
            StaffIdentityProvisioningAnchorResolutionDataRightsExport
                resolution) =>
        CreateRecord(
            StaffDataRightsExportContributor
                .IdentityProvisioningAnchorResolutionRecordType,
            IdentityProvisioningAnchorRecordId.CreateResolution(
                resolution.StaffMemberId,
                resolution.SourceKind,
                resolution.SourceId),
            resolution.WorkspaceApplicationVersion,
            typeof(StaffIdentityProvisioningAnchorResolutionDataRightsExport),
            [
                (nameof(resolution.StaffMemberId), resolution.StaffMemberId),
                (nameof(resolution.SourceKind), resolution.SourceKind),
                (nameof(resolution.SourceId), resolution.SourceId),
                (nameof(resolution.WorkspaceApplicationVersion),
                    resolution.WorkspaceApplicationVersion),
                (nameof(resolution.Disposition), resolution.Disposition),
                (nameof(resolution.ResolutionEventId),
                    resolution.ResolutionEventId),
                (nameof(resolution.ResolvedAtUtc), resolution.ResolvedAtUtc)
            ]);

    private static DataRightsExportRecord CreateRecord(
        string recordType,
        Guid recordId,
        long recordVersion,
        Type sourceType,
        IReadOnlyCollection<(string Member, object? Value)> values)
    {
        if (recordId == Guid.Empty ||
            recordVersion <= 0 ||
            string.IsNullOrWhiteSpace(recordType) ||
            recordType.Length > DataRightsExportLimits.RecordTypeMaxLength ||
            values.Count is <= 0 or > DataRightsExportLimits.MaxFieldsPerRecord)
        {
            throw new InvalidDataException("The Staff data-rights export record is invalid.");
        }

        DataRightsExportField[] fields = values
            .Select(value => CreateField(sourceType, value.Member, value.Value))
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
                $"The Staff data-rights export member '{key}' is not catalogue-approved.");
        }

        JsonElement serialized = JsonSerializer.SerializeToElement(
            value,
            value?.GetType() ?? typeof(object),
            ValueSerializerOptions);
        if (Encoding.UTF8.GetByteCount(serialized.GetRawText()) >
            DataRightsExportLimits.MaxFieldValueBytes)
        {
            throw new InvalidDataException(
                $"The Staff data-rights export field '{fieldId}' exceeds its size limit.");
        }

        return new DataRightsExportField(fieldId, serialized);
    }

    private static SchemaState Load()
    {
        Assembly assembly = typeof(StaffDataRightsExportSchema).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(CatalogResourceName) ??
            throw new InvalidDataException("The Staff personal-data catalogue is unavailable.");
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        PersonalDataCatalogDocument catalog = PersonalDataCatalogJson.Parse(buffer.ToArray());
        if (!string.Equals(catalog.CatalogId, "staff.personal-data", StringComparison.Ordinal) ||
            !string.Equals(catalog.Module, StaffDataRightsDiscoveryContributor.Owner, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Staff personal-data catalogue identity is invalid.");
        }

        Type[] sourceTypes =
        [
            typeof(StaffProfileDataRightsExport),
            typeof(StaffAssignmentDataRightsExport),
            typeof(StaffEmploymentGovernanceDataRightsExport),
            typeof(StaffDataHoldDataRightsExport),
            typeof(StaffMemberMutationOperationDataRightsExport),
            typeof(StaffIdentityProvisioningAnchorDataRightsExport),
            typeof(StaffIdentityProvisioningAnchorResolutionDataRightsExport)
        ];
        HashSet<string> expectedMembers = sourceTypes
            .SelectMany(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(property => !string.Equals(
                    property.Name,
                    nameof(StaffAssignmentDataRightsExport.RecordVersion),
                    StringComparison.Ordinal))
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
                        StaffDataRightsDiscoveryContributor.Owner,
                        StringComparison.Ordinal) ||
                    !IsAuthorizedExportPolicy(rightsPolicy.Export) ||
                    !field.AllowedBoundaries.Contains(PersonalDataBoundary.CrossModule) ||
                    !string.Equals(
                        binding.RetentionPolicy,
                        ExportRetentionPolicy,
                        StringComparison.Ordinal) ||
                    !fieldIdsByMember.TryAdd(key, field.Id))
                {
                    throw new InvalidDataException(
                        $"The Staff data-rights export binding '{key}' is invalid.");
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
                $"The Staff data-rights export catalogue is missing: {string.Join(", ", missing)}.");
        }

        string[] fieldIds = fieldIdsByMember.Values
            .Distinct(StringComparer.Ordinal)
            .OrderBy(fieldId => fieldId, StringComparer.Ordinal)
            .ToArray();
        DataRightsExportDescriptor descriptor = new(
            StaffDataRightsDiscoveryContributor.Owner,
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

    private static bool IsAuthorizedExportPolicy(string exportPolicy) =>
        string.Equals(
            exportPolicy,
            TenantCapableExportPolicy,
            StringComparison.Ordinal) ||
        string.Equals(
            exportPolicy,
            SubjectExportPolicy,
            StringComparison.Ordinal);

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
