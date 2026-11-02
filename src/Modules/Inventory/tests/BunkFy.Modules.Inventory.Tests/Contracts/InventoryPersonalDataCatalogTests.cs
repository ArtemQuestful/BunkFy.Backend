namespace BunkFy.Modules.Inventory.Tests;

using System.Reflection;
using BunkFy.DataGovernance;
using BunkFy.Modules.Inventory.Api;
using BunkFy.Modules.Inventory.Application.Commands;
using BunkFy.Modules.Inventory.Application.Ports;
using BunkFy.Modules.Inventory.Application.Queries;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Domain.Aggregates;
using BunkFy.Modules.Inventory.Domain.DataRights;
using BunkFy.Modules.Inventory.Domain.Entities;
using BunkFy.Modules.Inventory.Domain.Events;
using BunkFy.Modules.Inventory.Persistence;
using BunkFy.Modules.Inventory.Persistence.TenantTermination;
using BunkFy.Modules.Properties.Contracts;
using Gma.Framework.Messaging;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

[Trait("Category", "Unit")]
public sealed class InventoryPersonalDataCatalogTests
{
    private static readonly PersonalDataCatalogDocument Catalogue = LoadCatalogue();
    private static readonly Dictionary<string, Assembly> Assemblies = CreateAssemblyIndex();
    private static readonly Dictionary<Type, HashSet<string>> NonPersonalMembers = new()
    {
        [typeof(ConfigureRoomSalesModeCommand)] = new(
            [nameof(ConfigureRoomSalesModeCommand.OperationId)],
            StringComparer.Ordinal),
        [typeof(InventoryModule.CreateManualBlockRequest)] = OperationIdMember(),
        [typeof(InventoryModule.CreateManualBlockGroupRequest)] = new(
            [
                "OperationId",
                "ExpectedSelectionDigest",
                "ExpectedAffectedBlockCount",
                "Confirmed"
            ],
            StringComparer.Ordinal),
        [typeof(InventoryModule.ReplaceManualBlockGroupRequest)] =
            GroupMutationControlMembers(),
        [typeof(InventoryModule.ReleaseManualBlockGroupRequest)] =
            OperationVersionAndConfirmationMembers(),
        [typeof(BunkFy.Modules.Inventory.AdminApi.InventoryAdminApiModule.CreateManualBlockGroupRequest)] =
            GroupMutationControlMembers(),
        [typeof(BunkFy.Modules.Inventory.AdminApi.InventoryAdminApiModule.ReplaceManualBlockGroupRequest)] =
            GroupMutationControlMembers(),
        [typeof(BunkFy.Modules.Inventory.AdminApi.InventoryAdminApiModule.ReleaseManualBlockGroupRequest)] =
            OperationVersionAndConfirmationMembers(),
        [typeof(InventoryModule.RequestBedRetirementRequest)] =
            OperationAndConfirmationMembers(),
        [typeof(InventoryModule.RequestRoomRetirementRequest)] =
            OperationAndConfirmationMembers(),
        [typeof(InventoryModule.CancelRetirementRequest)] =
            OperationVersionAndConfirmationMembers(),
        [typeof(CreateManualInventoryBlockCommand)] = OperationIdMember(),
        [typeof(CreateManualInventoryBlockGroupCommand)] = new(
            [
                "OperationId",
                "ExpectedSelectionDigest",
                "ExpectedAffectedBlockCount",
                "Confirmed"
            ],
            StringComparer.Ordinal),
        [typeof(ReplaceManualInventoryBlockGroupCommand)] =
            GroupMutationControlMembers(),
        [typeof(ReleaseManualInventoryBlockCommand)] = OperationIdMember(),
        [typeof(ReleaseManualInventoryBlockGroupCommand)] =
            OperationVersionAndConfirmationMembers(),
        [typeof(RequestBedRetirementCommand)] = OperationAndConfirmationMembers(),
        [typeof(RequestRoomRetirementCommand)] = OperationAndConfirmationMembers(),
        [typeof(RetryBedRetirementCommand)] = OperationAndVersionMembers(),
        [typeof(RetryRoomRetirementCommand)] = OperationAndVersionMembers(),
        [typeof(CancelBedRetirementCommand)] =
            OperationVersionAndConfirmationMembers(),
        [typeof(CancelRoomRetirementCommand)] =
            OperationVersionAndConfirmationMembers(),
        [typeof(ManualInventoryBlockListResponse)] = PaginationMembers(),
        [typeof(ManualInventoryBlockGroupListResponse)] =
            new(["PageSize", "NextCursor"], StringComparer.Ordinal),
        [typeof(ManualInventoryBlockGroupMemberListResponse)] =
            new(["PageSize", "NextCursor"], StringComparer.Ordinal),
        [typeof(RoomInventoryListResponse)] = PaginationMembers(),
        [typeof(BedRetirementImpactSnapshot)] = new([nameof(BedRetirementImpactSnapshot.HasActiveClaims)], StringComparer.Ordinal),
        [typeof(RoomInventoryImpactSnapshot)] = new(
            [
                nameof(RoomInventoryImpactSnapshot.PreventsSalesModeChange),
                nameof(RoomInventoryImpactSnapshot.PreventsRoomRetirementFinalization)
            ],
            StringComparer.Ordinal)
    };

    private static HashSet<string> OperationAndVersionMembers() => new(
        ["OperationId", "ExpectedVersion"],
        StringComparer.Ordinal);

    private static HashSet<string> GroupMutationControlMembers() => new(
        [
            "OperationId",
            "ExpectedSelectionDigest",
            "ExpectedAffectedBlockCount",
            "Confirmed"
        ],
        StringComparer.Ordinal);

    private static HashSet<string> OperationAndConfirmationMembers() => new(
        ["OperationId", "Confirmed"],
        StringComparer.Ordinal);

    private static HashSet<string> OperationVersionAndConfirmationMembers() => new(
        ["OperationId", "ExpectedVersion", "Confirmed"],
        StringComparer.Ordinal);

    [Fact]
    public void Every_catalogue_binding_resolves_to_a_real_member()
    {
        List<string> invalid = [];
        foreach (PersonalDataMemberBinding binding in Bindings())
        {
            if (!Assemblies.TryGetValue(binding.Assembly, out Assembly? assembly))
            {
                invalid.Add($"Unknown assembly '{binding.Assembly}'.");
                continue;
            }

            Type? type = assembly.GetType(binding.Type, throwOnError: false, ignoreCase: false);
            if (type is null)
            {
                invalid.Add($"Unknown type '{binding.Type}'.");
                continue;
            }

            if (type.GetProperty(binding.Member, BindingFlags.Instance | BindingFlags.Public) is null)
            {
                invalid.Add($"Unknown member '{binding.Type}.{binding.Member}'.");
            }
        }

        Assert.True(invalid.Count == 0, string.Join(Environment.NewLine, invalid));
    }

    [Fact]
    public void Every_inventory_owned_personal_persistence_member_is_classified()
    {
        using InventoryDbContext dbContext = CreateDbContext();
        List<string> missing = [];
        foreach (Type entityType in PersistenceTypes())
        {
            IEntityType model = Assert.Single(
                dbContext.Model.GetEntityTypes(),
                candidate => candidate.ClrType == entityType);
            foreach (IProperty property in model.GetProperties().Where(property => !property.IsShadowProperty()))
            {
                AddMissing(missing, entityType, property.Name, PersonalDataSurface.Persistence);
            }
        }

        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void Every_selected_personal_boundary_member_is_classified()
    {
        List<string> missing = [];
        foreach ((PersonalDataSurface surface, Type type) in BoundaryTypes())
        {
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                bool isIntegrationEventMetadata = typeof(IIntegrationEvent).IsAssignableFrom(type) &&
                                                  property.Name is "EventName" or "Version";
                if (isIntegrationEventMetadata ||
                    (NonPersonalMembers.TryGetValue(type, out HashSet<string>? excluded) &&
                     excluded.Contains(property.Name)))
                {
                    continue;
                }

                AddMissing(missing, type, property.Name, surface);
            }
        }

        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void Pure_facility_projections_are_not_catalogued_as_personal_persistence()
    {
        Type[] facilityTypes =
        [
            typeof(InventoryPropertyTopology),
            typeof(InventoryRoomTopology),
            typeof(InventoryBedTopology),
            typeof(InventoryUnit),
            typeof(RoomInventoryConfiguration),
            typeof(InventoryManagementOperation),
            typeof(InventoryProjectionRebuildCheckpoint)
        ];

        PersonalDataMemberBinding[] offenders = Bindings()
            .Where(binding => binding.Surface == PersonalDataSurface.Persistence)
            .Where(binding => facilityTypes.Any(type =>
                string.Equals(binding.Assembly, type.Assembly.GetName().Name, StringComparison.Ordinal) &&
                string.Equals(binding.Type, type.FullName, StringComparison.Ordinal)))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Unstructured_operational_reasons_cannot_enter_broad_outputs()
    {
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
            .Where(field => field.Classification == PersonalDataClassification.FreeText)
            .Where(field => field.AllowedSurfaces.Any(prohibited.Contains) ||
                            field.Bindings.Any(binding => prohibited.Contains(binding.Surface)))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Block_created_event_does_not_duplicate_the_free_text_reason()
    {
        Assert.Equal(3, ManualInventoryBlockCreatedIntegrationEvent.EventVersion);
        Assert.Null(typeof(ManualInventoryBlockCreatedIntegrationEvent).GetProperty("Reason"));
    }

    [Fact]
    public void Affected_reservation_samples_remain_bounded()
    {
        Assert.InRange(InventoryImpactLimits.AffectedReservationSampleSize, 1, 25);
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

    private static void AddMissing(
        List<string> missing,
        Type type,
        string member,
        PersonalDataSurface surface)
    {
        bool found = Bindings().Any(binding =>
            string.Equals(binding.Assembly, type.Assembly.GetName().Name, StringComparison.Ordinal) &&
            string.Equals(binding.Type, type.FullName, StringComparison.Ordinal) &&
            string.Equals(binding.Member, member, StringComparison.Ordinal) &&
            binding.Surface == surface);
        if (!found)
        {
            missing.Add($"Missing {surface} classification for {type.FullName}.{member}.");
        }
    }

    private static IEnumerable<PersonalDataMemberBinding> Bindings() =>
        Catalogue.Fields.SelectMany(field => field.Bindings);

    private static Type[] PersistenceTypes() =>
    [
        typeof(InventoryAllocation),
        typeof(InventoryAllocationUnit),
        typeof(InventoryAllocationAmendmentDecision),
        typeof(InventoryAllocationOperationLock),
        typeof(InventoryAllocationAnonymisationReceipt),
        typeof(InventoryAllocationAnonymisationTombstone),
        typeof(InventoryAllocationAnonymisationRestoreReceipt),
        typeof(ManualInventoryBlock),
        typeof(ManualInventoryBlockGroup),
        typeof(BedRetirementProcess),
        typeof(RoomRetirementProcess),
        typeof(InventoryTenantDestroyOperation),
        typeof(InventoryTenantDestroyReceipt)
    ];

    private static IEnumerable<(PersonalDataSurface Surface, Type Type)> BoundaryTypes()
    {
        foreach (Type type in new[]
                 {
                     typeof(InventoryModule.CreateManualBlockRequest),
                     typeof(InventoryModule.CreateManualBlockGroupRequest),
                     typeof(InventoryModule.PreviewManualBlockGroupRequest),
                     typeof(InventoryModule.ReplaceManualBlockGroupRequest),
                     typeof(InventoryModule.ReleaseManualBlockGroupRequest),
                     typeof(BunkFy.Modules.Inventory.AdminApi.InventoryAdminApiModule.CreateManualBlockGroupRequest),
                     typeof(BunkFy.Modules.Inventory.AdminApi.InventoryAdminApiModule.PreviewManualBlockGroupRequest),
                     typeof(BunkFy.Modules.Inventory.AdminApi.InventoryAdminApiModule.ReplaceManualBlockGroupRequest),
                     typeof(BunkFy.Modules.Inventory.AdminApi.InventoryAdminApiModule.ReleaseManualBlockGroupRequest),
                     typeof(InventoryModule.RequestBedRetirementRequest),
                     typeof(InventoryModule.RequestRoomRetirementRequest),
                     typeof(InventoryModule.CancelRetirementRequest)
                 })
        {
            yield return (PersonalDataSurface.ApiInput, type);
        }

        foreach (Type type in new[]
                 {
                     typeof(ConfigureRoomSalesModeCommand),
                     typeof(CreateManualInventoryBlockCommand),
                     typeof(CreateManualInventoryBlockGroupCommand),
                     typeof(ReplaceManualInventoryBlockGroupCommand),
                     typeof(ReleaseManualInventoryBlockCommand),
                     typeof(ReleaseManualInventoryBlockGroupCommand),
                     typeof(RequestBedRetirementCommand),
                     typeof(RequestRoomRetirementCommand),
                     typeof(RetryBedRetirementCommand),
                     typeof(RetryRoomRetirementCommand),
                     typeof(CancelBedRetirementCommand),
                     typeof(CancelRoomRetirementCommand),
                     typeof(InventoryAllocationAmendmentDecisionRecord),
                     typeof(ApplyInventoryAllocationAnonymisationCommand),
                     typeof(RestoreInventoryAllocationAnonymisationCommand)
                 })
        {
            yield return (PersonalDataSurface.ApplicationCommand, type);
        }

        foreach (Type type in new[]
                 {
                     typeof(GetInventoryAvailabilityQuery),
                     typeof(GetRoomInventoryChangeImpactQuery),
                     typeof(GetBedRetirementQuery),
                     typeof(GetRoomRetirementQuery),
                     typeof(GetInventoryRetirementContextQuery),
                     typeof(InventoryRetirementTargetSnapshot),
                     typeof(ListManualInventoryBlocksQuery),
                     typeof(PreviewManualInventoryBlockGroupQuery),
                     typeof(GetManualInventoryBlockGroupQuery),
                     typeof(ListManualInventoryBlockGroupsQuery),
                     typeof(ListManualInventoryBlockGroupMembersQuery),
                     typeof(GetManualInventoryBlockGroupCreateOperationQuery),
                     typeof(GetManualInventoryBlockGroupOperationQuery)
                 })
        {
            yield return (PersonalDataSurface.ApplicationQuery, type);
        }

        foreach (Type type in new[]
                 {
                     typeof(InventoryAllocationUnitSnapshot),
                     typeof(InventoryAvailabilityContextSnapshot),
                     typeof(InventoryAvailabilityConflictSnapshot),
                     typeof(BedRetirementImpactSnapshot),
                     typeof(RoomInventoryImpactSnapshot)
                 })
        {
            yield return (PersonalDataSurface.ApplicationCommand, type);
        }

        foreach (Type type in new[]
                 {
                     typeof(InventoryAvailabilityResponse),
                     typeof(InventoryUnitAvailabilityDto),
                     typeof(ManualInventoryBlockDto),
                     typeof(ManualInventoryBlockListResponse),
                     typeof(ManualInventoryBlockGroupDto),
                     typeof(ManualInventoryBlockGroupListResponse),
                     typeof(ManualInventoryBlockGroupMemberListResponse),
                     typeof(ManualInventoryBlockGroupOperationDto),
                     typeof(ManualInventoryBlockGroupSelectionPreviewDto),
                     typeof(ManualInventoryBlockGroupPreviewMemberDto),
                     typeof(ManualInventoryBlockGroupMutationReceiptDto),
                     typeof(BedRetirementDto),
                     typeof(RoomRetirementDto),
                     typeof(InventoryRetirementContextDto),
                     typeof(InventoryRetirementProcessSummaryDto),
                     typeof(InventoryRetirementImpactDto),
                     typeof(RoomInventoryChangeImpactDto)
                 })
        {
            yield return (PersonalDataSurface.ApiResponse, type);
        }

        yield return (
            PersonalDataSurface.DataRightsExport,
            typeof(InventoryAllocationAnonymisationReceiptDto));

        yield return (PersonalDataSurface.AdminOutput, typeof(BedRetirementDto));
        yield return (PersonalDataSurface.AdminOutput, typeof(RoomRetirementDto));
        yield return (PersonalDataSurface.AdminOutput, typeof(ManualInventoryBlockDto));
        yield return (PersonalDataSurface.AdminOutput, typeof(ManualInventoryBlockGroupDto));
        yield return (PersonalDataSurface.AdminOutput, typeof(ManualInventoryBlockGroupListResponse));
        yield return (PersonalDataSurface.AdminOutput, typeof(ManualInventoryBlockGroupMemberListResponse));
        yield return (PersonalDataSurface.AdminOutput, typeof(ManualInventoryBlockGroupOperationDto));
        yield return (PersonalDataSurface.AdminOutput, typeof(ManualInventoryBlockGroupSelectionPreviewDto));
        yield return (PersonalDataSurface.AdminOutput, typeof(ManualInventoryBlockGroupPreviewMemberDto));
        yield return (PersonalDataSurface.AdminOutput, typeof(ManualInventoryBlockGroupMutationReceiptDto));

        foreach (Type type in new[]
                 {
                     typeof(InventoryAvailabilityProjectionExport),
                     typeof(InventoryUnitProjectionExport),
                     typeof(InventoryAllocationProjectionExport),
                     typeof(ManualInventoryBlockProjectionExport)
                 })
        {
            yield return (PersonalDataSurface.ProjectionExport, type);
        }

        foreach (Type type in InventoryIntegrationEventTypes())
        {
            yield return (PersonalDataSurface.IntegrationEvent, type);
        }

        foreach (Type type in new[]
                 {
                     typeof(ManualInventoryBlockCreatedDomainEvent),
                     typeof(ManualInventoryBlockReleasedDomainEvent),
                     typeof(RoomSalesModeChangedDomainEvent),
                     typeof(BedRetirementFinalizationRequestedDomainEvent),
                     typeof(RoomRetirementFinalizationRequestedDomainEvent)
                 })
        {
            yield return (PersonalDataSurface.DomainEvent, type);
        }
    }

    private static Type[] InventoryIntegrationEventTypes() =>
    [
        typeof(InventoryAllocationRequestedIntegrationEvent),
        typeof(InventoryAllocationConfirmedIntegrationEvent),
        typeof(InventoryAllocationRejectedIntegrationEvent),
        typeof(InventoryAllocationAmendmentRequestedIntegrationEvent),
        typeof(InventoryAllocationAmendmentConfirmedIntegrationEvent),
        typeof(InventoryAllocationAmendmentRejectedIntegrationEvent),
        typeof(InventoryAllocationReleaseRequestedIntegrationEvent),
        typeof(InventoryAllocationReleasedIntegrationEvent),
        typeof(InventoryAllocationReleaseRejectedIntegrationEvent),
        typeof(ManualInventoryBlockCreatedIntegrationEvent),
        typeof(ManualInventoryBlockReleasedIntegrationEvent),
        typeof(RoomSalesModeChangedIntegrationEvent),
        typeof(BedRetirementFinalizationRequestedIntegrationEvent),
        typeof(BedRetirementFinalizedIntegrationEvent),
        typeof(BedRetirementFinalizationRejectedIntegrationEvent),
        typeof(RoomRetirementFinalizationRequestedIntegrationEvent),
        typeof(RoomRetirementFinalizedIntegrationEvent),
        typeof(RoomRetirementFinalizationRejectedIntegrationEvent)
    ];

    private static Dictionary<string, Assembly> CreateAssemblyIndex() =>
        new[]
        {
            typeof(InventoryModule).Assembly,
            typeof(BunkFy.Modules.Inventory.AdminApi.InventoryAdminApiModule).Assembly,
            typeof(ConfigureRoomSalesModeCommand).Assembly,
            typeof(InventoryModuleMetadata).Assembly,
            typeof(InventoryAllocation).Assembly,
            typeof(InventoryDbContext).Assembly,
            typeof(BedRetirementFinalizationRequestedIntegrationEvent).Assembly
        }.ToDictionary(assembly => assembly.GetName().Name!, StringComparer.Ordinal);

    private static HashSet<string> PaginationMembers() =>
        new(["HasMore", "Page", "PageSize"], StringComparer.Ordinal);

    private static HashSet<string> OperationIdMember() =>
        new(["OperationId"], StringComparer.Ordinal);

    private static PersonalDataCatalogDocument LoadCatalogue() => PersonalDataCatalogJson.Parse(
        File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory,
            "DataGovernance",
            "personal-data-catalog.v1.json")));

    private static InventoryDbContext CreateDbContext()
    {
        DbContextOptions<InventoryDbContext> options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase($"inventory-data-catalog-{Guid.NewGuid():N}")
            .Options;
        return new(options, new TestScopeContext());
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }
}
