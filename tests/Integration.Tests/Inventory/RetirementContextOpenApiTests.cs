namespace Integration.Tests;

using System.Globalization;
using System.Text.Json;
using BunkFy.Host.Api;
using BunkFy.Modules.Inventory.Api;
using BunkFy.Modules.Inventory.Contracts;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;
using Xunit;

public sealed class RetirementContextOpenApiTests
{
    [Fact]
    public void Serialized_context_declares_nullable_inline_properties_without_changing_shared_or_command_schemas()
    {
        SchemaRepository before = GenerateSchemas(useFilter: false);
        SchemaRepository after = GenerateSchemas(useFilter: true);
        using JsonDocument document = JsonDocument.Parse(Serialize(after));
        JsonElement context = document.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty(nameof(InventoryRetirementContextDto));

        foreach (string property in new[] { "process", "impact" })
        {
            JsonElement value = context.GetProperty("properties").GetProperty(property);
            Assert.Equal("object", value.GetProperty("type").GetString());
            Assert.True(value.GetProperty("nullable").GetBoolean());
            Assert.False(value.TryGetProperty("$ref", out _));
            Assert.True(value.GetProperty("properties").EnumerateObject().Any());
        }

        Assert.Contains("3.0.", document.RootElement.GetProperty("openapi").GetString(), StringComparison.Ordinal);
        foreach ((string name, IOpenApiSchema schema) in before.Schemas)
        {
            if (name == nameof(InventoryRetirementContextDto))
            {
                continue;
            }

            Assert.Equal(SerializeSchema(schema), SerializeSchema(after.Schemas[name]));
        }

        Assert.Equal(JsonSchemaType.Object, after.Schemas[nameof(InventoryRetirementProcessSummaryDto)].Type);
        Assert.Equal(JsonSchemaType.Object, after.Schemas[nameof(InventoryRetirementImpactDto)].Type);
    }

    [Theory]
    [InlineData("process", null)]
    [InlineData("impact", null)]
    [InlineData(null, nameof(InventoryRetirementProcessSummaryDto))]
    [InlineData(null, nameof(InventoryRetirementImpactDto))]
    public void Missing_parent_properties_or_components_fail_loudly(string? property, string? component)
    {
        SchemaRepository repository = GenerateSchemas(useFilter: false);
        OpenApiSchema schema = Assert.IsType<OpenApiSchema>(repository.Schemas[nameof(InventoryRetirementContextDto)]);
        SchemaGenerator generator = new(new SchemaGeneratorOptions(), new JsonSerializerDataContractResolver(new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        SchemaFilterContext context = new(typeof(InventoryRetirementContextDto), generator, repository);
        RetirementContextSchemaFilter filter = new();
        if (property is not null)
        {
            Assert.True(schema.Properties!.Remove(property));
            Assert.Throws<InvalidOperationException>(() => filter.Apply(schema, context));
            Assert.False(schema.Properties.ContainsKey(property));
        }
        else
        {
            Assert.True(repository.Schemas.Remove(component!));
            Assert.Throws<KeyNotFoundException>(() => filter.Apply(schema, context));
        }
    }

    private static SchemaRepository GenerateSchemas(bool useFilter)
    {
        SchemaGeneratorOptions options = new();
        if (useFilter)
        {
            options.SchemaFilters.Add(new RetirementContextSchemaFilter());
        }

        SchemaGenerator generator = new(options, new JsonSerializerDataContractResolver(new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        SchemaRepository repository = new();
        foreach (Type type in new[]
                 {
                     typeof(InventoryRetirementContextDto), typeof(InventoryModule.RequestRoomRetirementRequest),
                     typeof(InventoryModule.RequestBedRetirementRequest), typeof(InventoryModule.RetryRetirementRequest), typeof(InventoryModule.CancelRetirementRequest),
                     typeof(RoomRetirementDto), typeof(BedRetirementDto),
                 })
        {
            generator.GenerateSchema(type, repository);
        }

        return repository;
    }

    private static string Serialize(SchemaRepository repository)
    {
        OpenApiDocument document = new()
        {
            Info = new OpenApiInfo { Title = "Retirement contract test", Version = "1" },
            Paths = [],
            Components = new OpenApiComponents { Schemas = repository.Schemas },
        };
        using StringWriter text = new(CultureInfo.InvariantCulture);
        document.SerializeAsV3(new OpenApiJsonWriter(text));
        return text.ToString();
    }

    private static string SerializeSchema(IOpenApiSchema schema)
    {
        using StringWriter text = new(CultureInfo.InvariantCulture);
        schema.SerializeAsV3(new OpenApiJsonWriter(text));
        return text.ToString();
    }
}
