namespace BunkFy.Host.Api;

using Gma.Modules.Auth.Contracts;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

/// <summary>Describes the owner's non-null collections and nullable active identity exactly.</summary>
public sealed class RememberedBrowserSchemaFilter : ISchemaFilter
{
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(context);
        if (schema is not OpenApiSchema definition || schema is OpenApiSchemaReference)
        {
            return;
        }

        string[] required = context.Type == typeof(RememberedBrowserCurrentResponse)
            ? ["state", "generation", "pinDigits", "accounts"]
            : context.Type == typeof(RememberedBrowserOperationResponse)
                ? ["outcome", "current"]
                : context.Type == typeof(RememberedBrowserAccount)
                    ? ["proofId", "username", "expiresAtUtc", "requiresSignIn"]
                    : context.Type == typeof(RememberedBrowserActiveAccount)
                        ? ["memberId", "sessionId", "username"]
                        : [];
        foreach (string name in required)
        {
            if (definition.Properties?.TryGetValue(name, out IOpenApiSchema? property) != true)
            {
                throw new InvalidOperationException("The remembered-browser schema is missing an owner-required property.");
            }

            definition.Required ??= new HashSet<string>(StringComparer.Ordinal);
            definition.Required.Add(name);
            if (property is OpenApiSchema value && value.Type is { } type)
            {
                value.Type = type & ~JsonSchemaType.Null;
            }
        }

        if (context.Type == typeof(RememberedBrowserCurrentResponse))
        {
            // OAS 3.0 ignores nullable next to $ref. Inline only this nullable
            // use, keeping the shared active-account definition non-null.
            OpenApiSchema active = (OpenApiSchema)context.SchemaRepository.Schemas[
                nameof(RememberedBrowserActiveAccount)].CreateShallowCopy();
            active.Type = JsonSchemaType.Object | JsonSchemaType.Null;
            definition.Properties!["active"] = active;
        }
    }
}
