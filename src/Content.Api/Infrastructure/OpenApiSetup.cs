using Content.Api.Auth;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Content.Api.Infrastructure;

/// <summary>Describes the API key scheme and the document metadata, and keeps the generated document deterministic.</summary>
internal sealed class ContentDocumentTransformer : IOpenApiDocumentTransformer
{
    public const string SecuritySchemeName = "ApiKey";

    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info = new OpenApiInfo
        {
            Title = "Mongo Content Store API",
            Version = "v1",
            Description =
                "Hotel static content served from per-supplier MongoDB collections, plus control of the checkpointed "
                + "Mongo to OpenSearch sync. Send an API key in the X-Api-Key header; the sync routes need an admin key.",
        };

        document.Servers = [];
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
        {
            [SecuritySchemeName] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                Name = ApiKeyAuthenticationHandler.HeaderName,
                In = ParameterLocation.Header,
                Description = "API key. Content routes accept any configured key; /api/v1/sync routes require an admin key.",
            },
        };
        document.Security =
        [
            new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(SecuritySchemeName, document)] = [] },
        ];

        return Task.CompletedTask;
    }
}
