namespace BunkFy.Modules.Retention.Tests;

using System.Reflection;
using BunkFy.DataGovernance;
using BunkFy.Modules.Retention.Application.Ports;
using BunkFy.Modules.Retention.Contracts;
using BunkFy.Modules.Retention.Domain.Aggregates;
using BunkFy.Modules.Retention.Persistence;
using Xunit;

[Trait("Category", "Unit")]
public sealed class RetentionPersonalDataCatalogTests
{
    private static readonly PersonalDataCatalogDocument Catalogue = LoadCatalogue();
    private static readonly Dictionary<string, Assembly> Assemblies = new[]
    {
        typeof(RetentionModuleMetadata).Assembly,
        typeof(RetentionScheduleTarget).Assembly,
        typeof(RetentionExecution).Assembly,
        typeof(RetentionDbContext).Assembly
    }.ToDictionary(assembly => assembly.GetName().Name!, StringComparer.Ordinal);

    [Fact]
    public void Catalogue_classifies_only_the_minimized_scope_coordinates()
    {
        Assert.Equal(
            RetentionTenantTerminationMetadata.PersonalDataCatalogVersion,
            Catalogue.CatalogVersion);
        Assert.Equal(
            [
                "retention.property-reference",
                "retention.tenant-destruction.owner-proof",
                "retention.tenant-scope-reference"
            ],
            Catalogue.Fields.Select(field => field.Id).Order(StringComparer.Ordinal));

        Assert.All(
            Catalogue.Fields,
            field => Assert.DoesNotContain(
                field.AllowedSurfaces,
                surface => surface is PersonalDataSurface.Log or
                    PersonalDataSurface.Metric or
                    PersonalDataSurface.Trace or
                    PersonalDataSurface.SupportBundle));
    }

    [Fact]
    public void Tenant_export_and_execution_storage_have_exact_scope_bindings()
    {
        PersonalDataFieldDefinition tenant = Catalogue.Fields.Single(
            field => field.Id == "retention.tenant-scope-reference");
        PersonalDataFieldDefinition property = Catalogue.Fields.Single(
            field => field.Id == "retention.property-reference");

        Assert.Contains(
            tenant.Bindings,
            binding => binding.Type ==
                    typeof(RetentionExecution).FullName &&
                binding.Member == nameof(RetentionExecution.ScopeId) &&
                binding.Surface == PersonalDataSurface.Persistence);
        Assert.Contains(
            property.Bindings,
            binding => binding.Type ==
                    typeof(RetentionExecution).FullName &&
                binding.Member == nameof(RetentionExecution.PropertyId) &&
                binding.Surface == PersonalDataSurface.Persistence);
        Assert.Contains(
            tenant.Bindings,
            binding => binding.Type ==
                    typeof(RetentionRunRetryRequest).FullName &&
                binding.Member == nameof(RetentionRunRetryRequest.ScopeId) &&
                binding.Surface == PersonalDataSurface.Persistence);
        Assert.Contains(
            property.Bindings,
            binding => binding.Type ==
                    typeof(RetentionRunRetryRequest).FullName &&
                binding.Member == nameof(RetentionRunRetryRequest.PropertyId) &&
                binding.Surface == PersonalDataSurface.Persistence);
        Assert.Equal(
            3,
            tenant.Bindings.Count(binding =>
                binding.Surface == PersonalDataSurface.DataRightsExport));
        Assert.Equal(
            3,
            property.Bindings.Count(binding =>
                binding.Surface == PersonalDataSurface.DataRightsExport));
    }

    [Fact]
    public void Every_catalogue_binding_resolves_to_a_real_public_member()
    {
        List<string> invalid = [];
        foreach (PersonalDataMemberBinding binding in Catalogue.Fields.SelectMany(field => field.Bindings))
        {
            if (!Assemblies.TryGetValue(binding.Assembly, out Assembly? assembly))
            {
                invalid.Add($"Unknown assembly '{binding.Assembly}'.");
                continue;
            }

            Type? type = assembly.GetType(binding.Type, throwOnError: false, ignoreCase: false);
            if (type?.GetProperty(binding.Member, BindingFlags.Instance | BindingFlags.Public) is null)
            {
                invalid.Add($"Unknown member '{binding.Type}.{binding.Member}'.");
            }
        }

        Assert.True(invalid.Count == 0, string.Join(Environment.NewLine, invalid));
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

    private static PersonalDataCatalogDocument LoadCatalogue() => PersonalDataCatalogJson.Parse(
        File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory,
            "DataGovernance",
            "personal-data-catalog.v1.json")));
}
