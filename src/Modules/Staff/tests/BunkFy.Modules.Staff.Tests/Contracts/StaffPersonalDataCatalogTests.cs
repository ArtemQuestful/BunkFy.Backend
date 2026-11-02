namespace BunkFy.Modules.Staff.Tests;

using System.Reflection;
using System.Security.Cryptography;
using BunkFy.DataGovernance;
using BunkFy.Modules.Staff.AdminApi;
using BunkFy.Modules.Staff.Api;
using BunkFy.Modules.Staff.Api.Requests;
using BunkFy.Modules.Staff.Application.Commands;
using BunkFy.Modules.Staff.Application.Queries;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.Aggregates;
using BunkFy.Modules.Staff.Domain.DataRights;
using BunkFy.Modules.Staff.Domain.Entities;
using BunkFy.Modules.Staff.Domain.Governance;
using BunkFy.Modules.Staff.Domain.Retention;
using BunkFy.Modules.Staff.Persistence;
using BunkFy.Modules.Staff.Persistence.Models;
using BunkFy.Modules.Staff.Persistence.Repositories;
using Gma.Framework.Messaging;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

[Trait("Category", "Unit")]
public sealed class StaffPersonalDataCatalogTests
{
    private static readonly PersonalDataCatalogDocument Catalogue = LoadCatalogue();
    private static readonly Dictionary<string, Assembly> Assemblies = CreateAssemblyIndex();
    private static readonly string[] ExpectedStationLabelProperties = ["DisplayName", "StaffMemberId", "Version"];
    private static readonly string[] ExpectedDirectoryMemberProperties =
        ["Assignments", "Department", "DisplayName", "JobTitle", "StaffMemberId", "Status", "Version"];
    private static readonly string[] ExpectedDirectoryListItemProperties =
        ["CurrentPropertyCount", "Department", "DisplayName", "JobTitle", "StaffMemberId", "Status", "Version"];
    private static readonly string[] ExpectedPropertyDirectoryListItemProperties =
        ["Assignment", "Department", "DisplayName", "JobTitle", "StaffMemberId", "Status", "Version"];
    private static readonly string[] ExpectedDirectoryAssignmentProperties =
        ["AssignmentId", "EffectiveFrom", "IsPrimary", "PropertyId", "PropertyJobTitle"];
    private static readonly Dictionary<Type, string[]> ExpectedEfShadowRelationshipProperties = new()
    {
        [typeof(StaffEmploymentGovernanceBinding)] = ["StaffEmploymentGovernanceId"],
        [typeof(StaffEmploymentGovernanceAcknowledgement)] = ["ScopeId", "StaffMemberId"]
    };

    private static readonly Dictionary<Type, HashSet<string>> NonPersonalMembers = new()
    {
        [typeof(ListStaffMembersQuery)] = new(
            [nameof(ListStaffMembersQuery.Page), nameof(ListStaffMembersQuery.PageSize)],
            StringComparer.Ordinal),
        [typeof(ListStaffMembersAtPropertyQuery)] = new(
            [nameof(ListStaffMembersAtPropertyQuery.Page), nameof(ListStaffMembersAtPropertyQuery.PageSize)],
            StringComparer.Ordinal),
        [typeof(ListStaffDataHoldsQuery)] = new(
            [nameof(ListStaffDataHoldsQuery.Page), nameof(ListStaffDataHoldsQuery.PageSize)],
            StringComparer.Ordinal),
        [typeof(StaffDirectoryListResponse)] = new(
            [nameof(StaffDirectoryListResponse.Page), nameof(StaffDirectoryListResponse.PageSize),
                nameof(StaffDirectoryListResponse.HasMore)],
            StringComparer.Ordinal),
        [typeof(StaffPropertyDirectoryListResponse)] = new(
            [nameof(StaffPropertyDirectoryListResponse.Page), nameof(StaffPropertyDirectoryListResponse.PageSize),
                nameof(StaffPropertyDirectoryListResponse.HasMore)],
            StringComparer.Ordinal),
        [typeof(StaffDataHoldListResponse)] = new(
            [nameof(StaffDataHoldListResponse.Page), nameof(StaffDataHoldListResponse.PageSize)],
            StringComparer.Ordinal),
        [typeof(StaffIdentityBootstrapResult)] = new(
            [nameof(StaffIdentityBootstrapResult.IsSuccess), nameof(StaffIdentityBootstrapResult.ErrorCode)],
            StringComparer.Ordinal),
        [typeof(StaffOnboardingProvisioningResult)] = new(
            [nameof(StaffOnboardingProvisioningResult.IsSuccess), nameof(StaffOnboardingProvisioningResult.ErrorCode)],
            StringComparer.Ordinal),
        [typeof(StaffPropertyAssignmentProvisioningResult)] = new(
            [nameof(StaffPropertyAssignmentProvisioningResult.IsSuccess),
                nameof(StaffPropertyAssignmentProvisioningResult.ErrorCode)],
            StringComparer.Ordinal),
        [typeof(StaffAdminApiModule.StaffAuthSubjectRequest)] = new(
            [nameof(StaffAdminApiModule.StaffAuthSubjectRequest.Confirmed)],
            StringComparer.Ordinal),
        [typeof(StaffAdminApiModule.StaffDepartureRequest)] = new(
            [nameof(StaffAdminApiModule.StaffDepartureRequest.Confirmed)],
            StringComparer.Ordinal),
        [typeof(ReleaseStaffDataHoldRequest)] = new(
            [nameof(ReleaseStaffDataHoldRequest.Confirmed)],
            StringComparer.Ordinal)
    };

    [Fact]
    public void Catalogue_version_matches_tenant_termination_contract()
    {
        Assert.Equal(
            StaffTenantTerminationMetadata.PersonalDataCatalogVersion,
            Catalogue.CatalogVersion);
    }

    [Fact]
    public void Station_labels_have_exact_positive_owner_bindings_and_no_private_profile_fields()
    {
        Assert.Equal(ExpectedStationLabelProperties,
            typeof(StaffStationLabel).GetProperties().Select(x => x.Name).Order());
        AssertType(typeof(StaffStationLabel), PersonalDataSurface.ProjectionExport);
        foreach (string id in new[] { "staff.display-name", "staff.staff-member-id", "staff.record-version" })
        {
            var field = Assert.Single(Catalogue.Fields, x => x.Id == id);
            Assert.Equal("staff", field.AuthoritativeOwner);
            Assert.Contains(field.Bindings, b => b.Type == typeof(StaffStationLabel).FullName &&
                b.Surface == PersonalDataSurface.ProjectionExport && b.RetentionPolicy == "transient-response");
        }
    }

    [Fact]
    public void V20_catalogue_and_owner_manifest_have_immutable_digests()
    {
        string dataGovernanceDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "DataGovernance");

        Assert.Equal(4, StaffTenantTerminationMetadata.CatalogVersion);
        Assert.Equal(20, StaffTenantTerminationMetadata.PersonalDataCatalogVersion);
        Assert.Equal(3, StaffTenantTerminationMetadata.ExportSchemaVersion);
        Assert.Equal(20, Catalogue.CatalogVersion);
        Assert.Equal(
            "a2677b0ea21efa57f9c4720caf30a65e703af48f8c42ba7310d7a3db7579ef35",
            ComputeSha256(Path.Combine(
                dataGovernanceDirectory,
                "personal-data-catalog.v1.json")));
        Assert.Equal(
            "f97e58eabea1944d7f2b2ca6b8f9d972bb80a58d7267f6d621bc4896a7c7588a",
            StaffTenantTerminationMetadata.CatalogSha256);
        Assert.Equal(
            "a6fcfded670edf18bc70e8e824a63b298e57f9868a961aefecbe905bad4f3123",
            ComputeSha256(Path.Combine(
                dataGovernanceDirectory,
                "personal-data-inventory.v1.md")));
    }

    [Fact]
    public void Every_catalogue_binding_resolves_to_a_real_member()
    {
        foreach (PersonalDataMemberBinding binding in Bindings())
        {
            Assert.True(Assemblies.TryGetValue(binding.Assembly, out Assembly? assembly),
                $"Unknown assembly '{binding.Assembly}'.");
            Type? type = assembly.GetType(binding.Type, throwOnError: false, ignoreCase: false);
            Assert.NotNull(type);
            Assert.NotNull(type.GetProperty(binding.Member, BindingFlags.Instance | BindingFlags.Public));
        }
    }

    [Fact]
    public void Every_staff_owned_personal_persistence_member_is_classified()
    {
        using StaffDbContext dbContext = CreateDbContext();
        foreach (Type entityType in new[]
                 {
                     typeof(StaffMember),
                     typeof(StaffPropertyAssignment),
                     typeof(StaffDataRightsCorrectionReceipt),
                     typeof(StaffAnonymisationReceipt),
                     typeof(StaffAnonymisationRestoreReceipt),
                     typeof(StaffAnonymisationTombstone),
                     typeof(StaffProcessingRestriction),
                     typeof(StaffProcessingRestrictionProjection),
                     typeof(StaffProcessingRestrictionReceipt),
                     typeof(StaffEmploymentGovernance),
                     typeof(StaffEmploymentGovernanceBinding),
                     typeof(StaffEmploymentGovernanceAcknowledgement),
                     typeof(StaffEmploymentGovernanceChangeReceipt),
                     typeof(StaffDataHold),
                     typeof(StaffDataHoldReceipt),
                     typeof(StaffRetentionExecution),
                     typeof(StaffRetentionSweepCheckpoint),
                     typeof(StaffRetentionAnonymisationReceipt),
                     typeof(StaffOperationLock),
                     typeof(StaffMemberMutationOperation)
                 })
        {
            IEntityType model = dbContext.Model.FindEntityType(entityType)!;
            string[] expectedShadowProperties =
                ExpectedEfShadowRelationshipProperties.GetValueOrDefault(entityType) ?? [];
            string[] actualShadowProperties = model.GetProperties()
                .Where(property => property.PropertyInfo is null)
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(
                expectedShadowProperties.Order(StringComparer.Ordinal),
                actualShadowProperties);

            foreach (IProperty property in model.GetProperties()
                         .Where(property => property.PropertyInfo is not null))
            {
                AssertBinding(entityType, property.Name, PersonalDataSurface.Persistence);
            }
        }

        foreach (string searchMember in new[]
                 {
                     nameof(StaffMember.DisplayNameSearch),
                     nameof(StaffMember.LegalNameSearch),
                     nameof(StaffMember.WorkEmailSearch),
                     nameof(StaffMember.WorkPhoneSearch),
                     nameof(StaffMember.EmployeeNumberSearch)
                 })
        {
            AssertBinding(typeof(StaffMember), searchMember, PersonalDataSurface.SearchIndex);
        }
    }

    [Fact]
    public void Every_selected_command_query_api_admin_contract_and_event_member_is_classified()
    {
        foreach ((PersonalDataSurface surface, Type type) in ContractTypes())
        {
            AssertType(type, surface);
        }

        AssertType(
            typeof(StaffProfileDataRightsExport),
            PersonalDataSurface.DataRightsExport);
        AssertType(
            typeof(StaffAssignmentDataRightsExport),
            PersonalDataSurface.DataRightsExport,
            nameof(StaffAssignmentDataRightsExport.RecordVersion));
        AssertType(
            typeof(StaffEmploymentGovernanceDataRightsExport),
            PersonalDataSurface.DataRightsExport);
        AssertType(
            typeof(StaffDataHoldDataRightsExport),
            PersonalDataSurface.DataRightsExport);
        AssertType(
            typeof(StaffMemberMutationOperationDataRightsExport),
            PersonalDataSurface.DataRightsExport);
    }

    [Fact]
    public void Direct_or_unstructured_staff_data_cannot_enter_operational_outputs()
    {
        HashSet<PersonalDataClassification> restricted =
        [
            PersonalDataClassification.DirectIdentifier,
            PersonalDataClassification.Contact,
            PersonalDataClassification.Demographic,
            PersonalDataClassification.Preference,
            PersonalDataClassification.FreeText,
            PersonalDataClassification.SearchInput,
            PersonalDataClassification.StructuredPayload
        ];
        HashSet<PersonalDataSurface> prohibited =
        [
            PersonalDataSurface.IntegrationEvent,
            PersonalDataSurface.Notification,
            PersonalDataSurface.Log,
            PersonalDataSurface.Metric,
            PersonalDataSurface.Trace,
            PersonalDataSurface.SupportBundle
        ];

        PersonalDataFieldDefinition[] offenders = Catalogue.Fields
            .Where(field => restricted.Contains(field.Classification))
            .Where(field => field.AllowedSurfaces.Any(prohibited.Contains) ||
                            field.Bindings.Any(binding => prohibited.Contains(binding.Surface)))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Directory_contracts_exclude_sensitive_profile_and_history_fields()
    {
        string[] memberProperties = typeof(StaffDirectoryMemberDto).GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] assignmentProperties = typeof(StaffDirectoryAssignmentDto).GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] listItemProperties = typeof(StaffDirectoryListItemDto).GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] propertyListItemProperties = typeof(StaffPropertyDirectoryListItemDto).GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ExpectedDirectoryMemberProperties.Order(StringComparer.Ordinal),
            memberProperties);
        Assert.Equal(
            ExpectedDirectoryAssignmentProperties.Order(StringComparer.Ordinal),
            assignmentProperties);
        Assert.Equal(
            ExpectedDirectoryListItemProperties.Order(StringComparer.Ordinal),
            listItemProperties);
        Assert.Equal(
            ExpectedPropertyDirectoryListItemProperties.Order(StringComparer.Ordinal),
            propertyListItemProperties);
    }

    [Fact]
    public void Checked_in_inventory_matches_deterministic_catalogue_rendering()
    {
        string expected = File.ReadAllText(Path.Combine(
                AppContext.BaseDirectory,
                "DataGovernance",
                "personal-data-inventory.v1.md"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Equal(expected, PersonalDataInventoryRenderer.RenderMarkdown(Catalogue));
    }

    private static void AssertType(
        Type type,
        PersonalDataSurface surface,
        params string[] nonPersonalMembers)
    {
        HashSet<string> explicitExclusions = new(nonPersonalMembers, StringComparer.Ordinal);
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            bool isIntegrationEventMetadata = typeof(IIntegrationEvent).IsAssignableFrom(type) &&
                                              property.Name is "EventName" or "Version";
            if (isIntegrationEventMetadata ||
                explicitExclusions.Contains(property.Name) ||
                (NonPersonalMembers.TryGetValue(type, out HashSet<string>? configuredExclusions) &&
                 configuredExclusions.Contains(property.Name)))
            {
                continue;
            }

            AssertBinding(type, property.Name, surface);
        }
    }

    private static void AssertBinding(Type type, string member, PersonalDataSurface surface)
    {
        bool found = Bindings().Any(binding =>
            string.Equals(binding.Assembly, type.Assembly.GetName().Name, StringComparison.Ordinal) &&
            string.Equals(binding.Type, type.FullName, StringComparison.Ordinal) &&
            string.Equals(binding.Member, member, StringComparison.Ordinal) &&
            binding.Surface == surface);
        Assert.True(found, $"Missing {surface} classification for {type.FullName}.{member}.");
    }

    private static IEnumerable<PersonalDataMemberBinding> Bindings() =>
        Catalogue.Fields.SelectMany(field => field.Bindings);

    private static IEnumerable<(PersonalDataSurface Surface, Type Type)> ContractTypes()
    {
        Assembly application = typeof(CreateStaffMemberCommand).Assembly;
        foreach (Type type in application.GetTypes()
                     .Where(type => type.IsPublic && !type.IsAbstract)
                     .Where(type => type.Namespace?.StartsWith(
                                        "BunkFy.Modules.Staff.Application.Commands",
                                        StringComparison.Ordinal) == true ||
                                    type.Namespace?.StartsWith(
                                        "BunkFy.Modules.Staff.Application.Queries",
                                        StringComparison.Ordinal) == true)
                     .Where(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Length > 0))
        {
            PersonalDataSurface surface = type.Namespace!.Contains(".Queries", StringComparison.Ordinal)
                ? PersonalDataSurface.ApplicationQuery
                : PersonalDataSurface.ApplicationCommand;
            yield return (surface, type);
        }

        foreach (Type type in typeof(StaffModule).Assembly.GetTypes()
                     .Where(type => type.IsPublic && !type.IsAbstract)
                     .Where(type => type.Namespace == typeof(StaffProfileWriteRequest).Namespace)
                     .Where(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Length > 0))
        {
            yield return (PersonalDataSurface.ApiInput, type);
        }

        foreach (Type type in typeof(StaffAdminApiModule).GetNestedTypes(BindingFlags.Public)
                     .Where(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Length > 0))
        {
            yield return (PersonalDataSurface.AdminInput, type);
        }

        Assembly contracts = typeof(StaffModuleMetadata).Assembly;
        foreach (Type type in contracts.GetTypes()
                     .Where(type => type.IsPublic && !type.IsAbstract)
                     .Where(type => typeof(IIntegrationEvent).IsAssignableFrom(type) ||
                                    type.Name.EndsWith("Dto", StringComparison.Ordinal) ||
                                    type == typeof(StaffDirectoryListResponse) ||
                                    type == typeof(StaffStationLabel) ||
                                    type == typeof(StaffPropertyDirectoryListResponse) ||
                                    type == typeof(StaffAccountDirectoryResponse) ||
                                    type == typeof(StaffIdentityBootstrapRequest) ||
                                    type == typeof(StaffIdentityBootstrapResult) ||
                                    type == typeof(StaffAnonymisationRestoreState) ||
                                    type == typeof(
                                        StaffDataRightsAuthorityState) ||
                                    type == typeof(
                                        StaffNotificationRecipient) ||
                                    type == typeof(
                                        StaffRetentionAnonymisationPrerequisiteRequest) ||
                                    type == typeof(StaffOnboardingProvisioningRequest) ||
                                    type == typeof(StaffOnboardingProvisioningResult) ||
                                    type == typeof(StaffPropertyAssignmentProvisioningRequest) ||
                                    type == typeof(StaffPropertyAssignmentProvisioningResult))
                     .Where(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Length > 0))
        {
            if (typeof(IIntegrationEvent).IsAssignableFrom(type))
            {
                yield return (PersonalDataSurface.IntegrationEvent, type);
            }
            else if (type == typeof(StaffIdentityBootstrapRequest) ||
                     type == typeof(
                         StaffRetentionAnonymisationPrerequisiteRequest) ||
                     type == typeof(StaffOnboardingProvisioningRequest) ||
                     type == typeof(StaffPropertyAssignmentProvisioningRequest))
            {
                yield return (PersonalDataSurface.IntegrationCommand, type);
            }
            else if (type == typeof(StaffIdentityBootstrapResult) ||
                     type == typeof(StaffOnboardingProvisioningResult) ||
                     type == typeof(StaffPropertyAssignmentProvisioningResult))
            {
                yield return (PersonalDataSurface.ProjectionExport, type);
            }
            else if (type == typeof(StaffAnonymisationReceiptDto))
            {
                yield return (PersonalDataSurface.ProjectionExport, type);
            }
            else if (type == typeof(StaffAnonymisationRestoreState))
            {
                yield return (PersonalDataSurface.ProjectionExport, type);
            }
            else if (type == typeof(StaffDataRightsAuthorityState) || type == typeof(StaffStationLabel) ||
                     type == typeof(StaffNotificationRecipient))
            {
                yield return (PersonalDataSurface.ProjectionExport, type);
            }
            else
            {
                yield return (PersonalDataSurface.ApiResponse, type);
                yield return (PersonalDataSurface.AdminOutput, type);
            }
        }

        Assembly domain = typeof(StaffMember).Assembly;
        foreach (Type type in domain.GetTypes()
                     .Where(type => type.IsPublic && !type.IsAbstract)
                     .Where(type => type.Namespace?.StartsWith(
                                        "BunkFy.Modules.Staff.Domain.Events",
                                        StringComparison.Ordinal) == true)
                     .Where(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Length > 0))
        {
            yield return (PersonalDataSurface.DomainEvent, type);
        }
    }

    private static Dictionary<string, Assembly> CreateAssemblyIndex() =>
        new[]
        {
            typeof(StaffAdminApiModule).Assembly,
            typeof(StaffModule).Assembly,
            typeof(CreateStaffMemberCommand).Assembly,
            typeof(StaffModuleMetadata).Assembly,
            typeof(StaffMember).Assembly,
            typeof(StaffDbContext).Assembly
        }.ToDictionary(assembly => assembly.GetName().Name!, StringComparer.Ordinal);

    private static PersonalDataCatalogDocument LoadCatalogue() => PersonalDataCatalogJson.Parse(
        File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory,
            "DataGovernance",
            "personal-data-catalog.v1.json")));

    private static string ComputeSha256(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static StaffDbContext CreateDbContext()
    {
        DbContextOptions<StaffDbContext> options = new DbContextOptionsBuilder<StaffDbContext>()
            .UseInMemoryDatabase($"staff-data-catalog-{Guid.NewGuid():N}")
            .Options;
        return new(options, new TestScopeContext());
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }
}
