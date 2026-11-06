namespace BunkFy.Host.Api;

using BunkFy.Modules.Inventory.Contracts;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

public sealed class RetirementContextSchemaFilter : ISchemaFilter
{
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(context);

        // Swashbuckle invokes filters for the definition and then its reference.
        // The definition owns the two fields; never alter its later reference.
        if (context.Type != typeof(InventoryRetirementContextDto) || schema is OpenApiSchemaReference)
        {
            return;
        }

        if (schema is not OpenApiSchema contextSchema
            || contextSchema.Properties?.ContainsKey("process") != true
            || !contextSchema.Properties.ContainsKey("impact"))
        {
            throw new InvalidOperationException("The retirement context schema must declare both process and impact before nullability is applied.");
        }

        // OAS 3.0 ignores nullable siblings on $ref. Inline only these two
        // nullable properties without changing their shared definitions.
        OpenApiSchema process = (OpenApiSchema)context.SchemaRepository.Schemas[
            nameof(InventoryRetirementProcessSummaryDto)].CreateShallowCopy();
        process.Type = JsonSchemaType.Object | JsonSchemaType.Null;
        contextSchema.Properties["process"] = process;

        OpenApiSchema impact = (OpenApiSchema)context.SchemaRepository.Schemas[
            nameof(InventoryRetirementImpactDto)].CreateShallowCopy();
        impact.Type = JsonSchemaType.Object | JsonSchemaType.Null;
        contextSchema.Properties["impact"] = impact;
    }
}
