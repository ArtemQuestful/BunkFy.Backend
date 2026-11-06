namespace BunkFy.Modules.Guests.Tests;

using System.Reflection;
using BunkFy.DataGovernance;
using BunkFy.Extensions.ReservationGuestRecords;
using BunkFy.Modules.Guests.AdminApi;
using BunkFy.Modules.Guests.Api;
using BunkFy.Modules.Guests.Application.Commands;
using BunkFy.Modules.Guests.Application.Policies;
using BunkFy.Modules.Guests.Application.Ports;
using BunkFy.Modules.Guests.Application.Queries;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Guests.Domain.Aggregates;
using BunkFy.Modules.Guests.Domain.DataRights;
using BunkFy.Modules.Guests.Domain.Events;
using BunkFy.Modules.Guests.Domain.Retention;
using BunkFy.Modules.Guests.Persistence;
using BunkFy.Modules.Guests.Persistence.Repositories;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

[Trait("Category", "Unit")]
public sealed class GuestsPersonalDataCatalogTests
{
    private static readonly PersonalDataCatalogDocument Catalogue = LoadCatalogue();
    private static readonly Dictionary<string, Assembly> Assemblies = CreateAssemblyIndex();

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
    public void Every_guest_owned_persistence_member_is_classified()
    {
        using GuestsDbContext dbContext = CreateDbContext();
        foreach (Type entityType in new[]
                 {
                     typeof(GuestProfile),
                     typeof(GuestManagementOperation),
                     typeof(GuestStayHistoryEntry),
                     typeof(GuestDataRightsCorrectionReceipt),
                     typeof(GuestProcessingRestrictionProjection),
                     typeof(GuestProcessingRestriction),
                     typeof(GuestProcessingRestrictionReceipt),
                     typeof(GuestDataHold),
                     typeof(GuestDataHoldReceipt),
                     typeof(GuestAnonymisationReceipt),
                     typeof(GuestAnonymisationTombstone),
                     typeof(GuestAnonymisationRestoreReceipt),
                     typeof(GuestRetentionExecution),
                     typeof(GuestRetentionSweepCheckpoint),
                     typeof(GuestRetentionAnonymisationReceipt)
                 })
        {
            IEntityType model = dbContext.Model.FindEntityType(entityType)!;
            foreach (IProperty property in model.GetProperties())
            {
                AssertBinding(entityType, property.Name, PersonalDataSurface.Persistence);
            }
        }

        foreach (string searchMember in new[]
                 {
                     nameof(GuestProfile.DisplayNameSearch),
                     nameof(GuestProfile.LegalNameSearch),
                     nameof(GuestProfile.EmailSearch),
                     nameof(GuestProfile.PhoneSearch)
                 })
        {
            AssertBinding(typeof(GuestProfile), searchMember, PersonalDataSurface.SearchIndex);
        }
    }

    [Fact]
    public void Every_selected_command_query_response_export_and_event_member_is_classified()
    {
        AssertType(typeof(CreateGuestProfileCommand), PersonalDataSurface.ApplicationCommand);
        AssertType(
            typeof(GuestProfileCreationRequest),
            PersonalDataSurface.IntegrationCommand);
        AssertType(typeof(UpdateGuestProfileCommand), PersonalDataSurface.ApplicationCommand);
        AssertType(typeof(ArchiveGuestProfileCommand), PersonalDataSurface.ApplicationCommand);
        AssertType(
            typeof(GuestManagementOperationRecord),
            PersonalDataSurface.ApplicationCommand);
        AssertType(typeof(ApplyGuestDataRightsCorrectionCommand), PersonalDataSurface.ApplicationCommand);
        AssertType(
            typeof(ApplyGuestProcessingRestrictionCommand),
            PersonalDataSurface.ApplicationCommand);
        AssertType(
            typeof(ReleaseGuestProcessingRestrictionCommand),
            PersonalDataSurface.ApplicationCommand);
        AssertType(typeof(PlaceGuestDataHoldCommand), PersonalDataSurface.ApplicationCommand);
        AssertType(typeof(ReleaseGuestDataHoldCommand), PersonalDataSurface.ApplicationCommand);
        AssertType(typeof(ApplyGuestAnonymisationCommand), PersonalDataSurface.ApplicationCommand);
        AssertType(typeof(RestoreGuestAnonymisationCommand), PersonalDataSurface.ApplicationCommand);
        AssertType(
            typeof(BeginGuestRetentionExecutionCommand),
            PersonalDataSurface.ApplicationCommand);
        AssertType(
            typeof(GuestRetentionExecutionStart),
            PersonalDataSurface.ApplicationCommand);
        AssertType(
            typeof(ApplyGuestRetentionCommand),
            PersonalDataSurface.ApplicationCommand);
        AssertType(
            typeof(GuestRetentionMutationResult),
            PersonalDataSurface.ApplicationCommand);
        AssertType(
            typeof(CompleteGuestRetentionExecutionCommand),
            PersonalDataSurface.ApplicationCommand);
        AssertType(
            typeof(GuestRetentionEligibilityResult),
            PersonalDataSurface.ApplicationCommand);
        AssertBinding(
            typeof(GuestRetentionAnonymisationProofTenantExport),
            nameof(GuestRetentionAnonymisationProofTenantExport
                .TimeZoneCatalogVersion),
            PersonalDataSurface.DataRightsExport);
        AssertType(typeof(GetGuestProfileQuery), PersonalDataSurface.ApplicationQuery);
        AssertType(
            typeof(GetGuestStayHistoryQuery),
            PersonalDataSurface.ApplicationQuery,
            nameof(GetGuestStayHistoryQuery.Page),
            nameof(GetGuestStayHistoryQuery.PageSize));
        AssertType(
            typeof(ListGuestProcessingRestrictionsQuery),
            PersonalDataSurface.ApplicationQuery,
            nameof(ListGuestProcessingRestrictionsQuery.Page),
            nameof(ListGuestProcessingRestrictionsQuery.PageSize));
        AssertType(
            typeof(ListGuestDataHoldsQuery),
            PersonalDataSurface.ApplicationQuery,
            nameof(ListGuestDataHoldsQuery.Status),
            nameof(ListGuestDataHoldsQuery.Page),
            nameof(ListGuestDataHoldsQuery.PageSize));
        AssertType(
            typeof(ListGuestProfilesQuery),
            PersonalDataSurface.ApplicationQuery,
            nameof(ListGuestProfilesQuery.Status),
            nameof(ListGuestProfilesQuery.Page),
            nameof(ListGuestProfilesQuery.PageSize));

        AssertType(typeof(GuestsModule.GuestProfileWriteRequest), PersonalDataSurface.ApiInput);
        AssertType(typeof(GuestsModule.GuestProfileUpdateRequest), PersonalDataSurface.ApiInput);
        AssertType(typeof(GuestsModule.GuestDataRightsCorrectionRequest), PersonalDataSurface.ApiInput);
        AssertType(
            typeof(GuestsModule.ApplyGuestProcessingRestrictionRequest),
            PersonalDataSurface.ApiInput);
        AssertType(
            typeof(GuestsModule.ReleaseGuestProcessingRestrictionRequest),
            PersonalDataSurface.ApiInput);
        AssertType(typeof(GuestsModule.PlaceGuestDataHoldRequest), PersonalDataSurface.ApiInput);
        AssertType(
            typeof(GuestsModule.ReleaseGuestDataHoldRequest),
            PersonalDataSurface.ApiInput,
            nameof(GuestsModule.ReleaseGuestDataHoldRequest.Confirmed));
        AssertType(
            typeof(GuestsModule.ArchiveGuestProfileRequest),
            PersonalDataSurface.ApiInput,
            nameof(GuestsModule.ArchiveGuestProfileRequest.Confirmed));
        AssertType(
            typeof(GuestsAdminApiModule.GuestProfileWriteRequest),
            PersonalDataSurface.ApiInput);
        AssertType(
            typeof(GuestsAdminApiModule.GuestProfileUpdateRequest),
            PersonalDataSurface.ApiInput);
        AssertType(
            typeof(GuestsAdminApiModule.ArchiveGuestProfileRequest),
            PersonalDataSurface.ApiInput,
            nameof(GuestsAdminApiModule.ArchiveGuestProfileRequest.Confirmed));
        AssertType(
            typeof(ReservationGuestRecordWriteRequest),
            PersonalDataSurface.ApiInput,
            nameof(ReservationGuestRecordWriteRequest.ExpectedReservationVersion));

        AssertType(typeof(GuestProfileDto), PersonalDataSurface.ApiResponse);
        AssertType(typeof(GuestListItemDto), PersonalDataSurface.ApiResponse);
        AssertType(typeof(GuestMutationReceiptDto), PersonalDataSurface.ApiResponse);
        AssertType(typeof(GuestDataRightsCorrectionReceiptDto), PersonalDataSurface.ApiResponse);
        AssertType(typeof(GuestProcessingRestrictionDto), PersonalDataSurface.ApiResponse);
        AssertType(
            typeof(GuestProcessingRestrictionReceiptDto),
            PersonalDataSurface.ApiResponse);
        AssertType(typeof(GuestDataHoldDto), PersonalDataSurface.ApiResponse);
        AssertType(typeof(GuestDataHoldReceiptDto), PersonalDataSurface.ApiResponse);
        AssertType(typeof(GuestAnonymisationReceiptDto), PersonalDataSurface.ProjectionExport);
        AssertType(typeof(GuestStayHistoryItem), PersonalDataSurface.ApiResponse);
        AssertType(typeof(GuestProfileEligibilityProjectionExport), PersonalDataSurface.ProjectionExport);
        AssertType(typeof(ReservationGuestStayProjectionExport), PersonalDataSurface.ProjectionExport);
        AssertType(
            typeof(GuestProcessingRestrictionProjectionExport),
            PersonalDataSurface.ProjectionExport);
        AssertType(
            typeof(GuestProcessingRestrictionGateRequest),
            PersonalDataSurface.IntegrationCommand);
        AssertType(
            typeof(GuestProcessingRestrictionGateResult),
            PersonalDataSurface.ProjectionExport);
        AssertType(
            typeof(GuestAnonymisationEligibilityRequest),
            PersonalDataSurface.IntegrationCommand,
            nameof(GuestAnonymisationEligibilityRequest.ContractVersion),
            nameof(GuestAnonymisationEligibilityRequest.RoutingPolicy));
        AssertType(
            typeof(GuestAnonymisationRoutingPolicyEvidence),
            PersonalDataSurface.IntegrationCommand);
        AssertType(
            typeof(GuestAnonymisationEligibilityResult),
            PersonalDataSurface.ProjectionExport,
            nameof(GuestAnonymisationEligibilityResult.ContractVersion),
            nameof(GuestAnonymisationEligibilityResult.Status),
            nameof(GuestAnonymisationEligibilityResult.BlockerCode),
            nameof(GuestAnonymisationEligibilityResult.AffectedPropertyCount));
        AssertType(
            typeof(GuestProcessingRestrictionChangedDomainEvent),
            PersonalDataSurface.DomainEvent);
        AssertType(
            typeof(GuestDataRightsCorrectionAppliedDomainEvent),
            PersonalDataSurface.DomainEvent);
        AssertType(
            typeof(GuestProfileAnonymisedDomainEvent),
            PersonalDataSurface.DomainEvent);
        AssertType(typeof(GuestProfileDataRightsExport), PersonalDataSurface.DataRightsExport);
        AssertType(typeof(GuestStayDataRightsExport), PersonalDataSurface.DataRightsExport);
        AssertType(
            typeof(GuestManagementOperationDataRightsExport),
            PersonalDataSurface.DataRightsExport);

        foreach (Type eventType in IntegrationEventTypes())
        {
            AssertType(
                eventType,
                PersonalDataSurface.IntegrationEvent,
                "EventName",
                "Version");
        }
    }

    [Fact]
    public void Direct_or_unstructured_guest_data_cannot_enter_operational_outputs()
    {
        HashSet<PersonalDataClassification> restricted =
        [
            PersonalDataClassification.DirectIdentifier,
            PersonalDataClassification.Contact,
            PersonalDataClassification.Demographic,
            PersonalDataClassification.Preference,
            PersonalDataClassification.FreeText,
            PersonalDataClassification.SearchInput
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
    public void Checked_in_inventory_matches_deterministic_catalogue_rendering()
    {
        string expected = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "DataGovernance",
            "personal-data-inventory.v1.md"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Equal(expected, PersonalDataInventoryRenderer.RenderMarkdown(Catalogue));
    }

    private static void AssertType(Type type, PersonalDataSurface surface, params string[] nonPersonalMembers)
    {
        HashSet<string> excluded = new(nonPersonalMembers, StringComparer.Ordinal);
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!excluded.Contains(property.Name))
            {
                AssertBinding(type, property.Name, surface);
            }
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

    private static Type[] IntegrationEventTypes() =>
    [
        typeof(GuestProfileArchivedIntegrationEvent),
        typeof(GuestProfileAnonymisedIntegrationEvent),
        typeof(GuestProfileCreatedIntegrationEvent),
        typeof(GuestProcessingRestrictionChangedIntegrationEvent),
        typeof(GuestProfileUpdatedIntegrationEvent),
        typeof(ReservationGuestLinkedIntegrationEvent),
        typeof(ReservationGuestStayChangedIntegrationEvent)
    ];

    private static Dictionary<string, Assembly> CreateAssemblyIndex() =>
        new[]
        {
            typeof(GuestsAdminApiModule).Assembly,
            typeof(GuestsModule).Assembly,
            typeof(CreateGuestProfileCommand).Assembly,
            typeof(GuestsModuleMetadata).Assembly,
            typeof(GuestProfile).Assembly,
            typeof(GuestsDbContext).Assembly,
            typeof(ReservationGuestRecordWriteRequest).Assembly
        }.ToDictionary(assembly => assembly.GetName().Name!, StringComparer.Ordinal);

    private static PersonalDataCatalogDocument LoadCatalogue() => PersonalDataCatalogJson.Parse(
        File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory,
            "DataGovernance",
            "personal-data-catalog.v1.json")));

    private static GuestsDbContext CreateDbContext()
    {
        DbContextOptions<GuestsDbContext> options = new DbContextOptionsBuilder<GuestsDbContext>()
            .UseInMemoryDatabase($"guests-data-catalog-{Guid.NewGuid():N}")
            .Options;
        return new(options, new TestScopeContext());
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }
}
