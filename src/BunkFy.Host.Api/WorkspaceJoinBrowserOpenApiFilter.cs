namespace BunkFy.Host.Api;

using Gma.Modules.Organizations.Contracts;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

public sealed class WorkspaceJoinBrowserOpenApiFilter : IOperationFilter, ISchemaFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);
        string? path = context.ApiDescription.RelativePath?.TrimStart('/');
        if (path is null || !path.StartsWith("api/workspace-join/", StringComparison.Ordinal))
        {
            return;
        }

        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = WorkspaceJoinBrowserContinuation.HeaderName,
            In = ParameterLocation.Header,
            Required = true,
            Description = "The public context ID must match the route. The HttpOnly context cookie is also required after preparation; it does not replace user authentication.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "uuid" },
        });

        Type[] responses = path.EndsWith("/preview", StringComparison.Ordinal)
            ? [typeof(OrganizationInvitationPreviewDto), typeof(OrganizationEnrollmentPreviewDto)]
            : path.EndsWith("/join", StringComparison.Ordinal)
                ? [typeof(OrganizationInvitationAcceptanceDto), typeof(OrganizationEnrollmentOutcomeDto)]
                : [];
        if (responses.Length == 0)
        {
            return;
        }

        operation.Responses ??= [];
        operation.Responses["200"] = new OpenApiResponse
        {
            Description = "The original Organizations response for the protected context's source kind.",
            Content = new Dictionary<string, OpenApiMediaType>(StringComparer.Ordinal)
            {
                ["application/json"] = new OpenApiMediaType
                {
                    Schema = new OpenApiSchema
                    {
                        OneOf = responses.Select(type => context.SchemaGenerator.GenerateSchema(type, context.SchemaRepository)).ToList(),
                    },
                },
            },
        };
    }

    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(context);
        if (context.Type == typeof(PrepareWorkspaceJoinBrowserRequest) && schema is OpenApiSchema request &&
            request.Properties?.TryGetValue("token", out IOpenApiSchema? field) == true && field is OpenApiSchema token)
        {
            token.WriteOnly = true;
            token.Format = "password";
        }
    }
}
