namespace BunkFy.Modules.Properties.Tests;

using System.Reflection;
using BunkFy.DataGovernance;
using BunkFy.Modules.Properties.AdminApi;
using BunkFy.Modules.Properties.Api;
using BunkFy.Modules.Properties.Application.Commands;
using BunkFy.Modules.Properties.Application.Ports;
using BunkFy.Modules.Properties.Application.Queries;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.Modules.Properties.Domain.Aggregates;
using BunkFy.Modules.Properties.Domain.Entities;
using BunkFy.Modules.Properties.Domain.Events;
using BunkFy.Modules.Properties.Persistence;
using BunkFy.Modules.Properties.Persistence.Repositories;
using BunkFy.Modules.Properties.Persistence.TenantTermination;
using Gma.Framework.AccessControl;
using Gma.Framework.Messaging;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

[Trait("Category", "Unit")]
public sealed class PropertiesPersonalDataCatalogTests
{
    private const string AuthorizationFieldId = "properties.authorization-subject-reference";
    private const string ActorFieldId = "properties.staff-actor-reference";
    private const string TimeZoneFieldId =
        "properties.time-zone.management-coordinate";
    private const string TerminationProofFieldId =
        "properties.tenant-destruction.owner-proof";

    private static readonly PersonalDataCatalogDocument Catalogue = LoadCatalogue();
    private static readonly Dictionary<string, Assembly> Assemblies = CreateAssemblyIndex();
    private static readonly string[] PersonLinkedNameParts =
    [
        "Account",
        "Actor",
        "AssignedBy",
        "Birth",
        "ChangedBy",
        "Comment",
        "Contact",
        "CreatedBy",
        "Document",
        "Email",
        "Employee",
        "Guest",
        "Identity",
        "Initiator",
        "Language",
        "Member",
        "Nationality",
        "Note",
        "Owner",
        "Passport",
        "Person",
        "Phone",
        "Recipient",
        "RequestedBy",
        "Requester",
        "Reservation",
        "Staff",
        "Subject",
        "UpdatedBy",
        "User"
    ];

    [Fact]
    public void Every_catalogue_binding_resolves_to_a_real_public_member()
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
    public void Catalogue_contains_current_person_and_owner_proof_coordinates()
    {
        Assert.Equal(
            [AuthorizationFieldId, ActorFieldId, TerminationProofFieldId, TimeZoneFieldId],
            Catalogue.Fields.Select(field => field.Id).Order(StringComparer.Ordinal));

        List<string> expectedBindings =
        [
            BindingKey(typeof(ListVisiblePropertiesQuery), nameof(ListVisiblePropertiesQuery.Subject), PersonalDataSurface.ApplicationQuery),
            BindingKey(typeof(CreatePropertyCommand), nameof(CreatePropertyCommand.ActorId), PersonalDataSurface.ApplicationCommand),
            BindingKey(typeof(CreatePropertyCommand), nameof(CreatePropertyCommand.ActorId), PersonalDataSurface.AdminInput),
            BindingKey(typeof(ActivatePropertyProcessingCommand), nameof(ActivatePropertyProcessingCommand.ActorId), PersonalDataSurface.ApplicationCommand),
            BindingKey(typeof(SuspendPropertyProcessingCommand), nameof(SuspendPropertyProcessingCommand.ActorId), PersonalDataSurface.ApplicationCommand),
            BindingKey(typeof(RetirePropertyCommand), nameof(RetirePropertyCommand.ActorId), PersonalDataSurface.ApplicationCommand),
            BindingKey(typeof(PropertyGovernanceRevisionWriteModel), nameof(PropertyGovernanceRevisionWriteModel.ActorId), PersonalDataSurface.ApplicationCommand),
            BindingKey(typeof(PropertyTimeZoneRevisionWriteModel), nameof(PropertyTimeZoneRevisionWriteModel.ActorId), PersonalDataSurface.ApplicationCommand),
            BindingKey(typeof(PropertyTimeZoneRevisionReadModel), nameof(PropertyTimeZoneRevisionReadModel.ActorId), PersonalDataSurface.ApplicationQuery),
            BindingKey(typeof(SetPropertyTimeZoneCommand), nameof(SetPropertyTimeZoneCommand.ActorId), PersonalDataSurface.AdminInput),
            BindingKey(typeof(PropertyProcessingPolicyActivatedDomainEvent), nameof(PropertyProcessingPolicyActivatedDomainEvent.ActorId), PersonalDataSurface.DomainEvent),
            BindingKey(typeof(PropertyProcessingSuspendedDomainEvent), nameof(PropertyProcessingSuspendedDomainEvent.ActorId), PersonalDataSurface.DomainEvent),
            BindingKey(typeof(PropertyRetiredDomainEvent), nameof(PropertyRetiredDomainEvent.ActorId), PersonalDataSurface.DomainEvent),
            BindingKey(typeof(PropertyRetiredIntegrationEvent), nameof(PropertyRetiredIntegrationEvent.ActorId), PersonalDataSurface.IntegrationEvent),
            BindingKey(typeof(PropertyGovernanceRevision), nameof(PropertyGovernanceRevision.ActorId), PersonalDataSurface.Persistence),
            BindingKey(typeof(PropertyTimeZoneOperation), nameof(PropertyTimeZoneOperation.ActorId), PersonalDataSurface.Persistence),
            BindingKey(
                typeof(PropertiesGovernanceRevisionTenantExport),
                nameof(PropertiesGovernanceRevisionTenantExport.ActorId),
                PersonalDataSurface.DataRightsExport),
            BindingKey(
                typeof(PropertiesPropertyTimeZoneOperationTenantExport),
                nameof(PropertiesPropertyTimeZoneOperationTenantExport.ActorId),
                PersonalDataSurface.DataRightsExport)
        ];
        expectedBindings.AddRange(TimeZoneBoundaryMembers()
            .SelectMany(entry => entry.Members.Select(member =>
                BindingKey(entry.Type, member, entry.Surface))));
        foreach (Type proofType in new[]
                 {
                     typeof(PropertiesTenantDestroyOperation),
                     typeof(PropertiesTenantDestroyReceipt)
                 })
        {
            expectedBindings.AddRange(PersistedMembers(proofType)
                .Select(member => BindingKey(
                    proofType,
                    member,
                    PersonalDataSurface.Persistence)));
        }

        Assert.Equal(
            expectedBindings.Order(StringComparer.Ordinal),
            Bindings().Select(BindingKey).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Every_discovered_person_linked_boundary_member_is_classified()
    {
        List<string> missing = [];
        foreach ((PersonalDataSurface surface, Type type) in BoundaryTypes())
        {
            foreach (PropertyInfo property in type
                         .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                         .Where(IsPersonLinked))
            {
                AddMissing(missing, type, property.Name, surface);
            }
        }

        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void Time_zone_public_admin_and_cli_boundaries_are_explicitly_classified()
    {
        List<string> missing = [];
        foreach ((PersonalDataSurface surface, Type type, string[] members) in
                 TimeZoneBoundaryMembers())
        {
            foreach (string member in members)
            {
                AddMissing(missing, type, member, surface);
            }
        }

        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void Facility_topology_has_no_person_linked_persistence_or_catalogue_bindings()
    {
        using PropertiesDbContext dbContext = CreateDbContext();
        List<string> candidates = [];
        foreach (Type entityType in new[] { typeof(Property), typeof(Room), typeof(Bed) })
        {
            IEntityType model = Assert.Single(
                dbContext.Model.GetEntityTypes(),
                candidate => candidate.ClrType == entityType);
            foreach (IProperty property in model.GetProperties().Where(property => !property.IsShadowProperty()))
            {
                PropertyInfo? member = entityType.GetProperty(
                    property.Name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (member is not null && IsPersonLinked(member))
                {
                    candidates.Add($"Unexpected person-linked topology member {entityType.FullName}.{property.Name}.");
                }
            }
        }

        Assert.True(candidates.Count == 0, string.Join(Environment.NewLine, candidates));
        string[] persistedActors = Bindings()
            .Where(binding =>
                binding.Surface == PersonalDataSurface.Persistence &&
                binding.Member == nameof(PropertyGovernanceRevision.ActorId))
            .Select(binding => binding.Type)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[]
            {
                typeof(PropertyGovernanceRevision).FullName!,
                typeof(PropertyTimeZoneOperation).FullName!
            }.Order(StringComparer.Ordinal),
            persistedActors);
    }

    [Fact]
    public void Every_termination_proof_persistence_member_is_classified()
    {
        List<string> missing = [];
        foreach (Type proofType in new[]
                 {
                     typeof(PropertiesTenantDestroyOperation),
                     typeof(PropertiesTenantDestroyReceipt)
                 })
        {
            foreach (string member in PersistedMembers(proofType))
            {
                AddMissing(
                    missing,
                    proofType,
                    member,
                    PersonalDataSurface.Persistence);
            }
        }

        Assert.Empty(missing);
    }

    [Fact]
    public void Person_coordinates_enter_only_the_explicit_admin_inputs_and_receipt_outputs()
    {
        HashSet<PersonalDataSurface> prohibited =
        [
            PersonalDataSurface.ApiInput,
            PersonalDataSurface.SearchIndex,
            PersonalDataSurface.ProjectionExport,
            PersonalDataSurface.IntegrationCommand,
            PersonalDataSurface.Notification,
            PersonalDataSurface.Log,
            PersonalDataSurface.Metric,
            PersonalDataSurface.Trace,
            PersonalDataSurface.Cache,
            PersonalDataSurface.SupportBundle,
            PersonalDataSurface.AdapterIngress,
            PersonalDataSurface.FileIngress
        ];

        Assert.DoesNotContain(
            PersonBindings(),
            binding => prohibited.Contains(binding.Surface));
        Assert.Equal(
            new[]
            {
                BindingKey(
                    typeof(CreatePropertyCommand),
                    nameof(CreatePropertyCommand.ActorId),
                    PersonalDataSurface.AdminInput),
                BindingKey(
                    typeof(SetPropertyTimeZoneCommand),
                    nameof(SetPropertyTimeZoneCommand.ActorId),
                    PersonalDataSurface.AdminInput)
            }.Order(StringComparer.Ordinal),
            PersonBindings()
                .Where(binding => binding.Surface == PersonalDataSurface.AdminInput)
                .Select(BindingKey)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            new[]
            {
                BindingKey(
                    typeof(SetPropertyTimeZoneReceiptDto),
                    nameof(SetPropertyTimeZoneReceiptDto.ActorId),
                    PersonalDataSurface.ApiResponse),
                BindingKey(
                    typeof(SetPropertyTimeZoneReceiptDto),
                    nameof(SetPropertyTimeZoneReceiptDto.ActorId),
                    PersonalDataSurface.AdminOutput),
                BindingKey(
                    typeof(PropertyTimeZoneRecoveryDto),
                    nameof(PropertyTimeZoneRecoveryDto.Receipt),
                    PersonalDataSurface.ApiResponse),
                BindingKey(
                    typeof(PropertyTimeZoneRecoveryDto),
                    nameof(PropertyTimeZoneRecoveryDto.Receipt),
                    PersonalDataSurface.AdminOutput)
            }.Order(StringComparer.Ordinal),
            PersonBindings()
                .Where(binding => binding.Surface is
                    PersonalDataSurface.ApiResponse or
                    PersonalDataSurface.AdminOutput)
                .Select(BindingKey)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Property_retirement_is_the_only_person_linked_integration_event()
    {
        (Type Type, string Member)[] candidates = IntegrationEventTypes()
            .SelectMany(type => type
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(IsPersonLinked)
                .Select(property => (type, property.Name)))
            .ToArray();

        (Type Type, string Member) candidate = Assert.Single(candidates);
        Assert.Equal(typeof(PropertyRetiredIntegrationEvent), candidate.Type);
        Assert.Equal(nameof(PropertyRetiredIntegrationEvent.ActorId), candidate.Member);
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

    private static IEnumerable<(
        PersonalDataSurface Surface,
        Type Type,
        string[] Members)> TimeZoneBoundaryMembers()
    {
        yield return (
            PersonalDataSurface.ApiInput,
            typeof(PropertiesModule.PropertyCreateRequest),
            [nameof(PropertiesModule.PropertyCreateRequest.TimeZoneId)]);
        yield return (
            PersonalDataSurface.ApiInput,
            typeof(PropertiesModule.PropertyUpdateRequest),
            [nameof(PropertiesModule.PropertyUpdateRequest.TimeZoneId)]);
        yield return (
            PersonalDataSurface.ApiInput,
            typeof(PropertiesModule.SetPropertyTimeZoneRequest),
            PublicMemberNames(typeof(PropertiesModule.SetPropertyTimeZoneRequest)));
        yield return (
            PersonalDataSurface.AdminInput,
            typeof(PropertiesAdminApiModule.PropertyCreateRequest),
            [nameof(PropertiesAdminApiModule.PropertyCreateRequest.TimeZoneId)]);
        yield return (
            PersonalDataSurface.AdminInput,
            typeof(PropertiesAdminApiModule.PropertyUpdateRequest),
            [nameof(PropertiesAdminApiModule.PropertyUpdateRequest.TimeZoneId)]);
        yield return (
            PersonalDataSurface.AdminInput,
            typeof(PropertiesAdminApiModule.SetPropertyTimeZoneRequest),
            PublicMemberNames(typeof(PropertiesAdminApiModule.SetPropertyTimeZoneRequest)));

        yield return (
            PersonalDataSurface.ApplicationCommand,
            typeof(CreatePropertyCommand),
            [nameof(CreatePropertyCommand.TimeZoneId)]);
        yield return (
            PersonalDataSurface.AdminInput,
            typeof(CreatePropertyCommand),
            [nameof(CreatePropertyCommand.TimeZoneId)]);
        yield return (
            PersonalDataSurface.ApplicationCommand,
            typeof(UpdatePropertyCommand),
            [nameof(UpdatePropertyCommand.TimeZoneId)]);
        yield return (
            PersonalDataSurface.AdminInput,
            typeof(UpdatePropertyCommand),
            [nameof(UpdatePropertyCommand.TimeZoneId)]);
        yield return (
            PersonalDataSurface.ApplicationCommand,
            typeof(SetPropertyTimeZoneCommand),
            PublicMemberNames(typeof(SetPropertyTimeZoneCommand)));
        yield return (
            PersonalDataSurface.AdminInput,
            typeof(SetPropertyTimeZoneCommand),
            PublicMemberNames(typeof(SetPropertyTimeZoneCommand))
                .Where(member => member != nameof(SetPropertyTimeZoneCommand.ActorId))
                .ToArray());
        yield return (
            PersonalDataSurface.ApiInput,
            typeof(SetPropertyTimeZoneCommand),
            [nameof(SetPropertyTimeZoneCommand.PropertyId)]);

        foreach (Type type in new[]
                 {
                     typeof(ListPropertyTimeZoneCatalogQuery),
                     typeof(ListPropertyTimeZoneComplianceQuery),
                     typeof(GetPropertyTimeZoneRecoveryQuery)
                 })
        {
            string[] members = PublicMemberNames(type);
            yield return (PersonalDataSurface.ApplicationQuery, type, members);
            yield return (PersonalDataSurface.ApiInput, type, members);
            yield return (PersonalDataSurface.AdminInput, type, members);
        }

        foreach (Type type in new[]
                 {
                     typeof(PropertyTimeZoneCountryDto),
                     typeof(PropertyTimeZoneCatalogItemDto),
                     typeof(PropertyTimeZoneCatalogPageDto),
                     typeof(PropertyTimeZoneComplianceItemDto),
                     typeof(PropertyTimeZoneCompliancePageDto),
                     typeof(SetPropertyTimeZoneReceiptDto),
                     typeof(PropertyTimeZoneRecoveryDto)
                 })
        {
            string[] members = PublicMemberNames(type);
            yield return (PersonalDataSurface.ApiResponse, type, members);
            yield return (PersonalDataSurface.AdminOutput, type, members);
        }

        string[] propertyTimeZoneMembers =
        [
            nameof(PropertyDto.TimeZoneId),
            nameof(PropertyDto.TimeZoneStatus),
            nameof(PropertyDto.CanonicalTimeZoneId),
            nameof(PropertyDto.TimeZoneCatalogVersion),
            nameof(PropertyDto.TimeZoneObservedAtUtc),
            nameof(PropertyDto.TimeZoneCorrectionAllowed)
        ];
        foreach (Type type in new[] { typeof(PropertyDto), typeof(PropertyListItemDto) })
        {
            yield return (PersonalDataSurface.ApiResponse, type, propertyTimeZoneMembers);
            yield return (PersonalDataSurface.AdminOutput, type, propertyTimeZoneMembers);
        }
    }

    private static string[] PublicMemberNames(Type type) => type
        .GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Select(property => property.Name)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static IEnumerable<(PersonalDataSurface Surface, Type Type)> BoundaryTypes()
    {
        foreach (Type type in PublicTypes(typeof(PropertiesModule).Assembly)
                     .Where(type => type.DeclaringType == typeof(PropertiesModule) &&
                                    type.IsNestedPublic && type.Name.EndsWith("Request", StringComparison.Ordinal)))
        {
            yield return (PersonalDataSurface.ApiInput, type);
        }

        foreach (Type type in PublicTypes(typeof(PropertiesAdminApiModule).Assembly)
                     .Where(type => type.DeclaringType == typeof(PropertiesAdminApiModule) &&
                                    type.IsNestedPublic && type.Name.EndsWith("Request", StringComparison.Ordinal)))
        {
            yield return (PersonalDataSurface.AdminInput, type);
        }

        Assembly application = typeof(RetirePropertyCommand).Assembly;
        foreach (Type type in PublicTypes(application)
                     .Where(type => type.Namespace == "BunkFy.Modules.Properties.Application.Commands"))
        {
            yield return (PersonalDataSurface.ApplicationCommand, type);
        }

        foreach (Type type in PublicTypes(application)
                     .Where(type => type.Namespace == "BunkFy.Modules.Properties.Application.Queries"))
        {
            yield return (PersonalDataSurface.ApplicationQuery, type);
        }

        Assembly contracts = typeof(PropertiesModuleMetadata).Assembly;
        Type[] responses = PublicTypes(contracts)
            .Where(type => type.Name.EndsWith("Dto", StringComparison.Ordinal) ||
                           type.Name.EndsWith("ListResponse", StringComparison.Ordinal))
            .ToArray();
        foreach (Type type in responses)
        {
            yield return (PersonalDataSurface.ApiResponse, type);
            yield return (PersonalDataSurface.AdminOutput, type);
        }

        foreach (Type type in PublicTypes(contracts)
                     .Where(type => !type.IsInterface && type.Name.EndsWith("ProjectionExport", StringComparison.Ordinal)))
        {
            yield return (PersonalDataSurface.ProjectionExport, type);
        }

        foreach (Type type in IntegrationEventTypes())
        {
            yield return (PersonalDataSurface.IntegrationEvent, type);
        }

        foreach (Type type in PublicTypes(typeof(PropertyRetiredDomainEvent).Assembly)
                     .Where(type => type.Namespace == "BunkFy.Modules.Properties.Domain.Events"))
        {
            yield return (PersonalDataSurface.DomainEvent, type);
        }
    }

    private static Type[] IntegrationEventTypes() => PublicTypes(typeof(PropertyRetiredIntegrationEvent).Assembly)
        .Where(type => !type.IsAbstract && typeof(IIntegrationEvent).IsAssignableFrom(type))
        .ToArray();

    private static Type[] PublicTypes(Assembly assembly) => assembly
        .GetTypes()
        .Where(type => type.IsPublic || type.IsNestedPublic)
        .Where(type => !type.IsAbstract && !type.IsInterface)
        .ToArray();

    private static bool IsPersonLinked(PropertyInfo property)
    {
        if (property.PropertyType == typeof(AccessSubject) ||
            PersonLinkedNameParts.Any(part => property.Name.Contains(part, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return property.Name is "Reason" or "Message" or "Payload" &&
               (property.PropertyType == typeof(string) || property.PropertyType == typeof(object));
    }

    private static void AddMissing(
        List<string> missing,
        Type type,
        string member,
        PersonalDataSurface surface)
    {
        if (!Bindings().Any(binding =>
                string.Equals(binding.Assembly, type.Assembly.GetName().Name, StringComparison.Ordinal) &&
                string.Equals(binding.Type, type.FullName, StringComparison.Ordinal) &&
                string.Equals(binding.Member, member, StringComparison.Ordinal) &&
                binding.Surface == surface))
        {
            missing.Add($"Missing {surface} classification for {type.FullName}.{member}.");
        }
    }

    private static IEnumerable<PersonalDataMemberBinding> Bindings() =>
        Catalogue.Fields.SelectMany(field => field.Bindings);

    private static IEnumerable<PersonalDataMemberBinding> PersonBindings() =>
        Catalogue.Fields
            .Where(field => field.Id is AuthorizationFieldId or ActorFieldId)
            .SelectMany(field => field.Bindings);

    private static string[] PersistedMembers(Type type)
    {
        using PropertiesDbContext dbContext = CreateDbContext();
        IEntityType model = Assert.Single(
            dbContext.Model.GetEntityTypes(),
            candidate => candidate.ClrType == type);
        return model.GetProperties()
            .Where(property => !property.IsShadowProperty())
            .Select(property => property.Name)
            .ToArray();
    }

    private static string BindingKey(PersonalDataMemberBinding binding) =>
        string.Join('|', binding.Assembly, binding.Type, binding.Member, binding.Surface);

    private static string BindingKey(Type type, string member, PersonalDataSurface surface) =>
        string.Join('|', type.Assembly.GetName().Name, type.FullName, member, surface);

    private static Dictionary<string, Assembly> CreateAssemblyIndex() =>
        new[]
        {
            typeof(PropertiesAdminApiModule).Assembly,
            typeof(PropertiesModule).Assembly,
            typeof(RetirePropertyCommand).Assembly,
            typeof(PropertiesModuleMetadata).Assembly,
            typeof(Property).Assembly,
            typeof(PropertiesDbContext).Assembly
        }.ToDictionary(assembly => assembly.GetName().Name!, StringComparer.Ordinal);

    private static PersonalDataCatalogDocument LoadCatalogue() => PersonalDataCatalogJson.Parse(
        File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory,
            "DataGovernance",
            "personal-data-catalog.v1.json")));

    private static PropertiesDbContext CreateDbContext()
    {
        DbContextOptions<PropertiesDbContext> options = new DbContextOptionsBuilder<PropertiesDbContext>()
            .UseInMemoryDatabase($"properties-data-catalog-{Guid.NewGuid():N}")
            .Options;
        return new(options, new TestScopeContext());
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId => "tenant-a";
    }
}
