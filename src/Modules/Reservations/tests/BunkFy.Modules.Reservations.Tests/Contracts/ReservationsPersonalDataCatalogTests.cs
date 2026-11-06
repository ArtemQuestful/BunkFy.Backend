namespace BunkFy.Modules.Reservations.Tests;

using System.Reflection;
using System.Text.Json;
using BunkFy.DataGovernance;
using BunkFy.Extensions.ReservationGuestRecords;
using BunkFy.Modules.Reservations.AdminApi;
using BunkFy.Modules.Reservations.Api;
using BunkFy.Modules.Reservations.Application.Commands;
using BunkFy.Modules.Reservations.Application.Policies;
using BunkFy.Modules.Reservations.Application.Ports;
using BunkFy.Modules.Reservations.Application.Queries;
using BunkFy.Modules.Reservations.Contracts;
using BunkFy.Modules.Reservations.Contracts.Stations;
using BunkFy.Modules.Reservations.Domain.Aggregates;
using BunkFy.Modules.Reservations.Domain.DataRights;
using BunkFy.Modules.Reservations.Domain.Entities;
using BunkFy.Modules.Reservations.Domain.GuestRecords;
using BunkFy.Modules.Reservations.Domain.Retention;
using BunkFy.Modules.Reservations.Domain.StayAmendments;
using BunkFy.Modules.Reservations.Persistence;
using Gma.Framework.Messaging;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

[Trait("Category", "Unit")]
public sealed class ReservationsPersonalDataCatalogTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly PersonalDataCatalogDocument Catalogue = LoadCatalogue();
    private static readonly PersonalDataCatalogDocument GuestsCatalogue = LoadGuestsCatalogue();
    private static readonly Dictionary<string, Assembly> Assemblies = CreateAssemblyIndex();

    private static readonly Dictionary<string, PersonalDataClassification> GuestOwnedRequestMembers = new(StringComparer.Ordinal)
    {
        [nameof(ReservationGuestRecordWriteRequest.DisplayName)] = PersonalDataClassification.DirectIdentifier,
        [nameof(ReservationGuestRecordWriteRequest.LegalName)] = PersonalDataClassification.DirectIdentifier,
        [nameof(ReservationGuestRecordWriteRequest.Email)] = PersonalDataClassification.Contact,
        [nameof(ReservationGuestRecordWriteRequest.Phone)] = PersonalDataClassification.Contact,
        [nameof(ReservationGuestRecordWriteRequest.DateOfBirth)] = PersonalDataClassification.Demographic,
        [nameof(ReservationGuestRecordWriteRequest.NationalityCountryCode)] = PersonalDataClassification.Demographic,
        [nameof(ReservationGuestRecordWriteRequest.PreferredLanguageTag)] = PersonalDataClassification.Preference,
        [nameof(ReservationGuestRecordWriteRequest.LanguageTags)] = PersonalDataClassification.Preference,
        [nameof(ReservationGuestRecordWriteRequest.Notes)] = PersonalDataClassification.FreeText
    };

    private static readonly Dictionary<Type, HashSet<string>> NonPersonalMembers = new()
    {
        [typeof(ListReservationsQuery)] = new(
            [
                nameof(ListReservationsQuery.Statuses),
                nameof(ListReservationsQuery.Order),
                nameof(ListReservationsQuery.Page),
                nameof(ListReservationsQuery.PageSize),
                nameof(ListReservationsQuery.OverlapsFrom),
                nameof(ListReservationsQuery.OverlapsTo),
                nameof(ListReservationsQuery.View),
                nameof(ListReservationsQuery.OperatingDate)
            ],
            StringComparer.Ordinal),
        [typeof(GetReservationOperationsSnapshotQuery)] = new(
            [nameof(GetReservationOperationsSnapshotQuery.UpcomingLimit)],
            StringComparer.Ordinal),
        [typeof(ReservationsModule.OperationsSnapshotRequest)] = new(
            [nameof(ReservationsModule.OperationsSnapshotRequest.UpcomingLimit)],
            StringComparer.Ordinal),
        [typeof(ReservationsAdminApiModule.OperationsSnapshotRequest)] = new(
            [nameof(ReservationsAdminApiModule.OperationsSnapshotRequest.UpcomingLimit)],
            StringComparer.Ordinal),
        [typeof(ReservationOperationsSnapshotDto)] = new(
            [
                nameof(ReservationOperationsSnapshotDto.UpcomingLimit),
                nameof(ReservationOperationsSnapshotDto.HasMoreUpcoming)
            ],
            StringComparer.Ordinal),
        [typeof(GetReservationDetailsHistoryQuery)] = new(
            [
                nameof(GetReservationDetailsHistoryQuery.Page),
                nameof(GetReservationDetailsHistoryQuery.PageSize)
            ],
            StringComparer.Ordinal),
        [typeof(ReservationListResponse)] = new(
            [
                nameof(ReservationListResponse.Page),
                nameof(ReservationListResponse.PageSize),
                nameof(ReservationListResponse.HasMore)
            ],
            StringComparer.Ordinal),
        [typeof(ReservationDetailsHistoryListResponse)] = new(
            [
                nameof(ReservationDetailsHistoryListResponse.Page),
                nameof(ReservationDetailsHistoryListResponse.PageSize),
                nameof(ReservationDetailsHistoryListResponse.HasMore)
            ],
            StringComparer.Ordinal),
        [typeof(ReservationArrivalReminderClaimResult)] = new(
            [nameof(ReservationArrivalReminderClaimResult.ProcessedCount)],
            StringComparer.Ordinal),
        [typeof(ListReservationDataHoldsQuery)] = new(
            [
                nameof(ListReservationDataHoldsQuery.Page),
                nameof(ListReservationDataHoldsQuery.PageSize)
            ],
            StringComparer.Ordinal),
        [typeof(ReservationDataHoldListResponse)] = new(
            [
                nameof(ReservationDataHoldListResponse.Page),
                nameof(ReservationDataHoldListResponse.PageSize)
            ],
            StringComparer.Ordinal),
        [typeof(ListReservationStayAmendmentRecoveryQuery)] = new(
            [nameof(ListReservationStayAmendmentRecoveryQuery.PageSize)],
            StringComparer.Ordinal)
    };

    [Fact]
    public void Every_catalogue_binding_resolves_to_a_real_member()
    {
        foreach (PersonalDataMemberBinding binding in Bindings())
        {
            Assert.True(
                Assemblies.TryGetValue(binding.Assembly, out Assembly? assembly),
                $"Unknown assembly '{binding.Assembly}'.");
            Type? type = assembly.GetType(binding.Type, throwOnError: false, ignoreCase: false);
            Assert.NotNull(type);
            Assert.NotNull(type.GetProperty(binding.Member, BindingFlags.Instance | BindingFlags.Public));
        }
    }

    [Fact]
    public void Every_reservations_owned_personal_persistence_member_is_classified()
    {
        using ReservationsDbContext dbContext = CreateDbContext();
        foreach (IEntityType entityType in dbContext.Model.GetEntityTypes()
                     .Where(entity => PersonalPersistenceTypes().Contains(entity.ClrType)))
        {
            foreach (IProperty property in entityType.GetProperties())
            {
                AssertBinding(entityType.ClrType, property.Name, PersonalDataSurface.Persistence);
            }
        }
    }

    [Fact]
    public void Every_selected_command_query_api_adapter_event_and_domain_member_is_classified()
    {
        foreach ((PersonalDataSurface surface, Type type) in ContractTypes())
        {
            AssertType(type, surface);
        }
    }

    [Fact]
    public void Delegated_guest_inputs_have_positive_owner_classification_and_api_binding_evidence()
    {
        Assert.False(NonPersonalMembers.ContainsKey(typeof(ReservationGuestRecordWriteRequest)));
        foreach ((string member, PersonalDataClassification classification) in GuestOwnedRequestMembers)
        {
            Assert.NotNull(typeof(ReservationGuestRecordWriteRequest).GetProperty(member));
            AssertGuestOwnedBinding(member, classification);
        }
    }

    [Fact]
    public void Operations_snapshot_admin_contract_is_independently_classified_for_support_boundary()
    {
        Type adminInput = typeof(ReservationsAdminApiModule.OperationsSnapshotRequest);
        Type[] adminOutputs =
        [
            typeof(ReservationOperationsSnapshotDto),
            typeof(ReservationOperationsCountDto),
            typeof(ReservationOperationsCohortCountsDto),
            typeof(ReservationOperationsAttentionCountsDto),
            typeof(ReservationListItemDto)
        ];

        AssertType(adminInput, PersonalDataSurface.AdminInput);
        Assert.DoesNotContain(
            Bindings(),
            binding => string.Equals(binding.Type, adminInput.FullName, StringComparison.Ordinal) &&
                       binding.Surface == PersonalDataSurface.ApiInput);

        foreach (Type type in adminOutputs)
        {
            AssertType(type, PersonalDataSurface.AdminOutput);
        }

        PersonalDataFieldDefinition[] adminFields = Catalogue.Fields
            .Where(field => field.Bindings.Any(binding =>
                binding.Surface is PersonalDataSurface.AdminInput or PersonalDataSurface.AdminOutput &&
                (string.Equals(binding.Type, adminInput.FullName, StringComparison.Ordinal) ||
                 adminOutputs.Any(type => string.Equals(binding.Type, type.FullName, StringComparison.Ordinal)))))
            .ToArray();

        Assert.NotEmpty(adminFields);
        Assert.All(
            adminFields,
            field => Assert.Contains(PersonalDataBoundary.Support, field.AllowedBoundaries));
        Assert.Contains(
            Bindings(),
            binding => binding.Surface == PersonalDataSurface.AdminOutput &&
                       string.Equals(binding.Type, typeof(ReservationListItemDto).FullName, StringComparison.Ordinal) &&
                       string.Equals(binding.Member, nameof(ReservationListItemDto.PrimaryGuestName), StringComparison.Ordinal));
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
    public void Adapter_ingress_is_limited_to_explicit_reservation_request_contracts()
    {
        Type[] expected =
        [
            typeof(ExternalReservationAmendmentRequestedIntegrationEvent),
            typeof(ExternalReservationCancellationRequestedIntegrationEvent),
            typeof(ExternalReservationCreateRequestedIntegrationEvent),
            typeof(ExternalReservationGuestDetailsChangeRequestedIntegrationEvent)
        ];
        Type[] actual = ContractTypes()
            .Where(entry => entry.Surface == PersonalDataSurface.AdapterIngress)
            .Select(entry => entry.Type)
            .Distinct()
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected.OrderBy(type => type.FullName, StringComparer.Ordinal), actual);
        Assert.All(
            Catalogue.Fields.SelectMany(field => field.Bindings)
                .Where(binding => binding.Surface == PersonalDataSurface.AdapterIngress),
            binding => Assert.Contains(binding.Type, expected.Select(type => type.FullName)));
    }

    [Fact]
    public void Operational_rejection_reasons_cannot_be_free_form_text()
    {
        foreach (PropertyInfo property in IntegrationEventTypes()
                     .SelectMany(type => type.GetProperties())
                     .Where(property => property.Name.EndsWith("Reason", StringComparison.Ordinal)))
        {
            Type valueType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            Assert.True(valueType.IsEnum, $"{property.DeclaringType?.Name}.{property.Name} must use a bounded enum.");
        }
    }

    [Fact]
    public void Version_one_reminder_accepts_legacy_json_but_does_not_retain_the_guest_name()
    {
        Guid eventId = Guid.NewGuid();
        Guid reservationId = Guid.NewGuid();
        Guid propertyId = Guid.NewGuid();
        string json = $$"""
            {
              "eventId": "{{eventId}}",
              "tenantId": "tenant-a",
              "occurredAtUtc": "2026-07-16T10:30:00+00:00",
              "eventName": "reservation-arrival-reminder-due",
              "version": 1,
              "reservationId": "{{reservationId}}",
              "propertyId": "{{propertyId}}",
              "primaryGuestName": "Maya Chen",
              "arrival": "2026-07-16",
              "expectedArrivalTime": "15:30:00",
              "timeZoneId": "Europe/Moscow",
              "detailsRevision": 3
            }
            """;

        ReservationArrivalReminderDueIntegrationEvent? integrationEvent =
            JsonSerializer.Deserialize<ReservationArrivalReminderDueIntegrationEvent>(json, JsonOptions);

        Assert.NotNull(integrationEvent);
        Assert.Equal(reservationId, integrationEvent.ReservationId);
        Assert.DoesNotContain(
            "primaryGuestName",
            JsonSerializer.Serialize(integrationEvent, JsonOptions),
            StringComparison.OrdinalIgnoreCase);
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

    private static void AssertType(Type type, PersonalDataSurface surface)
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

            if (type == typeof(ReservationGuestRecordWriteRequest) &&
                GuestOwnedRequestMembers.TryGetValue(property.Name, out PersonalDataClassification classification))
            {
                Assert.Equal(PersonalDataSurface.ApiInput, surface);
                AssertGuestOwnedBinding(property.Name, classification);
                continue;
            }

            AssertBinding(type, property.Name, surface);
        }
    }

    private static void AssertGuestOwnedBinding(string member, PersonalDataClassification classification)
    {
        Type type = typeof(ReservationGuestRecordWriteRequest);
        bool Matches(PersonalDataMemberBinding binding) =>
            string.Equals(binding.Assembly, type.Assembly.GetName().Name, StringComparison.Ordinal) &&
            string.Equals(binding.Type, type.FullName, StringComparison.Ordinal) &&
            string.Equals(binding.Member, member, StringComparison.Ordinal) &&
            binding.Surface == PersonalDataSurface.ApiInput;

        Assert.Equal("guests", GuestsCatalogue.Module);
        PersonalDataFieldDefinition field = Assert.Single(
            GuestsCatalogue.Fields, field => field.Bindings.Any(Matches));
        Assert.Equal("guests", field.AuthoritativeOwner);
        Assert.Equal(PersonalDataSubjectKind.Guest, field.DataSubject);
        Assert.Equal(classification, field.Classification);
        Assert.Contains(PersonalDataSurface.ApiInput, field.AllowedSurfaces);
        PersonalDataMemberBinding binding = Assert.Single(field.Bindings, Matches);
        Assert.Equal("transient-request", binding.RetentionPolicy);
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
        foreach (Type type in new[] { typeof(StationCheckInProvenance), typeof(StationArrivalCursor), typeof(StationAllocationUnit),
            typeof(StationDueArrival), typeof(StationDueArrivalPage), typeof(StationCheckInPreparation), typeof(StationCheckInResult),
            typeof(StationDepartureCursor), typeof(StationDueDeparture), typeof(StationDueDeparturePage),
            typeof(StationCheckOutPreparation), typeof(StationCheckOutResult), typeof(StationCheckInOutcome), typeof(StationCheckOutOutcome) })
        { yield return (PersonalDataSurface.ProjectionExport, type); }
        yield return (PersonalDataSurface.ApplicationCommand, typeof(ReservationStationAttributionRead));
        yield return (PersonalDataSurface.DataRightsExport, typeof(BunkFy.Modules.Reservations.Persistence.Repositories.ReservationManagementStaffTenantExport));
        Assembly application = typeof(CreateReservationCommand).Assembly;
        foreach (Type type in application.GetTypes()
                     .Where(type => type.IsPublic && !type.IsAbstract)
                     .Where(type => type.Namespace?.StartsWith(
                                        "BunkFy.Modules.Reservations.Application.Commands",
                                        StringComparison.Ordinal) == true ||
                                    type.Namespace?.StartsWith(
                                        "BunkFy.Modules.Reservations.Application.Queries",
                                        StringComparison.Ordinal) == true)
                     .Where(type => type != typeof(DispatchReservationArrivalRemindersCommand) &&
                                    type != typeof(ReservationArrivalReminderDispatchBatchResult))
                     .Where(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Length > 0))
        {
            PersonalDataSurface surface = type.Namespace!.Contains(".Queries", StringComparison.Ordinal)
                ? PersonalDataSurface.ApplicationQuery
                : PersonalDataSurface.ApplicationCommand;
            yield return (surface, type);
        }

        foreach (Type type in new[]
                 {
                     typeof(BeginReservationRetentionExecutionCommand),
                     typeof(ReservationRetentionExecutionStart),
                     typeof(ApplyReservationRetentionCommand),
                     typeof(ReservationRetentionMutationResult),
                     typeof(CompleteReservationRetentionExecutionCommand),
                     typeof(ReservationRetentionEligibilityResult),
                     typeof(ReservationInventoryAllocationWriteModel),
                     typeof(ReservationReminderSource),
                     typeof(ReservationArrivalReminderDispatch),
                     typeof(ReservationArrivalReminderClaimResult),
                     typeof(ReservationExternalOperationRecord),
                     typeof(ReservationManagementOperationRecord),
                     typeof(ReservationDetailsOperationReplay),
                     typeof(ReservationGuestProfileProjectionWriteModel),
                     typeof(ReservationGuestProcessingRestrictionProjectionWriteModel)
                 })
        {
            yield return (PersonalDataSurface.ApplicationCommand, type);
        }

        foreach ((PersonalDataSurface surface, Type type) in new[]
                 {
                     (
                         PersonalDataSurface.ApiInput,
                         typeof(ReservationGuestRecordWriteRequest)),
                     (
                         PersonalDataSurface.ApiResponse,
                         typeof(ReservationDataHoldListResponse)),
                     (
                         PersonalDataSurface.IntegrationCommand,
                         typeof(ReservationAnonymisationEligibilityRequest)),
                     (
                         PersonalDataSurface.IntegrationCommand,
                         typeof(ReservationAnonymisationRoutingPolicyEvidence)),
                     (
                         PersonalDataSurface.ProjectionExport,
                         typeof(ReservationAnonymisationEligibilityResult))
                 })
        {
            yield return (surface, type);
        }

        foreach (Type type in typeof(ReservationsModule).GetNestedTypes(BindingFlags.Public)
                     .Where(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Length > 0))
        {
            yield return (PersonalDataSurface.ApiInput, type);
        }

        yield return (
            PersonalDataSurface.AdminInput,
            typeof(ReservationsAdminApiModule.OperationsSnapshotRequest));

        Assembly contracts = typeof(ReservationsModuleMetadata).Assembly;
        foreach (Type type in contracts.GetTypes()
                     .Where(type => type.IsPublic && !type.IsAbstract)
                     .Where(type => typeof(IIntegrationEvent).IsAssignableFrom(type) ||
                                    type.Name.EndsWith("Dto", StringComparison.Ordinal) ||
                                    type == typeof(ReservationDetailsHistoryItem) ||
                                    type == typeof(ReservationDetailsHistoryListResponse) ||
                                    type == typeof(ReservationListResponse))
                     .Where(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Length > 0))
        {
            PersonalDataSurface surface = typeof(IIntegrationEvent).IsAssignableFrom(type)
                ? type.Name.StartsWith("ExternalReservation", StringComparison.Ordinal) &&
                  type.Name.EndsWith("RequestedIntegrationEvent", StringComparison.Ordinal)
                    ? PersonalDataSurface.AdapterIngress
                    : PersonalDataSurface.IntegrationEvent
                : PersonalDataSurface.ApiResponse;
            yield return (surface, type);
        }

        foreach (Type type in new[]
                 {
                     typeof(ReservationOperationsSnapshotDto),
                     typeof(ReservationOperationsCountDto),
                     typeof(ReservationOperationsCohortCountsDto),
                     typeof(ReservationOperationsAttentionCountsDto),
                     typeof(ReservationListItemDto)
                 })
        {
            yield return (PersonalDataSurface.AdminOutput, type);
        }

        Assembly domain = typeof(Reservation).Assembly;
        foreach (Type type in domain.GetTypes()
                     .Where(type => type.IsPublic && !type.IsAbstract)
                     .Where(type => type.Namespace?.StartsWith(
                                        "BunkFy.Modules.Reservations.Domain.Events",
                                        StringComparison.Ordinal) == true ||
                                    type == typeof(ReservationDetailsSnapshot))
                     .Where(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Length > 0))
        {
            yield return (PersonalDataSurface.DomainEvent, type);
        }
    }

    private static Type[] IntegrationEventTypes() =>
        typeof(ReservationsModuleMetadata).Assembly
            .GetTypes()
            .Where(type => !type.IsAbstract && typeof(IIntegrationEvent).IsAssignableFrom(type))
            .ToArray();

    private static HashSet<Type> PersonalPersistenceTypes() =>
    [
        typeof(Reservation),
        typeof(ReservationDataRightsCorrectionReceipt),
        typeof(ReservationAnonymisationReceipt),
        typeof(ReservationAnonymisationTombstone),
        typeof(ReservationAnonymisationRestoreReceipt),
        typeof(ReservationRetentionExecution),
        typeof(ReservationRetentionSweepCheckpoint),
        typeof(ReservationRetentionAnonymisationReceipt),
        typeof(ReservationDataHold),
        typeof(ReservationDataHoldReceipt),
        typeof(ReservationProcessingRestriction),
        typeof(ReservationProcessingRestrictionProjection),
        typeof(ReservationProcessingRestrictionReceipt),
        typeof(RequestedInventoryUnit),
        typeof(ReservationGuest),
        typeof(ReservationGuestRecordLinkProcess),
        typeof(ReservationDetailsHistoryEntry),
        typeof(ReservationExternalOperation),
        typeof(ReservationManagementOperation),
        typeof(ReservationStationAttribution),
        typeof(ReservationStayAmendmentOperation),
        typeof(ReservationGuestProfileProjection),
        typeof(ReservationGuestProcessingRestrictionProjection),
        typeof(ReservationArrivalReminder),
        typeof(ReservationInventoryAllocationProjection),
        typeof(ReservationInventoryAllocationUnitProjection)
    ];

    private static Dictionary<string, Assembly> CreateAssemblyIndex() =>
        new[]
        {
            typeof(ReservationsModule).Assembly,
            typeof(ReservationsAdminApiModule).Assembly,
            typeof(CreateReservationCommand).Assembly,
            typeof(ReservationsModuleMetadata).Assembly,
            typeof(Reservation).Assembly,
            typeof(ReservationsDbContext).Assembly,
            typeof(ReservationGuestRecordWriteRequest).Assembly
        }.ToDictionary(assembly => assembly.GetName().Name!, StringComparer.Ordinal);

    private static PersonalDataCatalogDocument LoadCatalogue() => PersonalDataCatalogJson.Parse(
        File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory,
            "DataGovernance",
            "personal-data-catalog.v1.json")));

    private static PersonalDataCatalogDocument LoadGuestsCatalogue()
    {
        // Follow the repository-root convention used by Architecture.Tests;
        // validate the owner catalogue itself, without duplicating it into Reservations.
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BunkFy.slnx")))
            {
                return PersonalDataCatalogJson.Parse(File.ReadAllBytes(Path.Combine(
                    directory.FullName, "src", "Modules", "Guests", "docs", "personal-data-catalog.v1.json")));
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find the BunkFy backend repository root for the Guests owner catalogue.");
    }

    private static ReservationsDbContext CreateDbContext()
    {
        DbContextOptions<ReservationsDbContext> options = new DbContextOptionsBuilder<ReservationsDbContext>()
            .UseInMemoryDatabase($"reservations-data-catalog-{Guid.NewGuid():N}")
            .Options;
        return new(options, new TestScopeContext());
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }
}
