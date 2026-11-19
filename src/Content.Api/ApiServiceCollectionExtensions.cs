using System.Text.Json;
using System.Text.Json.Serialization;
using Content.Api.Auth;
using Content.Api.Endpoints;
using Content.Api.Infrastructure;
using Content.Migration;
using Content.Mongo;
using Content.Reads;
using Content.Store;
using Content.Sync;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Content.Api;

public static class ApiServiceCollectionExtensions
{
    public static IServiceCollection AddContentApi(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddContentMongo(configuration).AddContentStore(configuration).AddContentSync(configuration).AddContentReads(configuration).AddContentMigration(configuration);

        services.AddOptions<ApiAuthOptions>().Bind(configuration.GetSection(ApiAuthOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<ApiAuthOptions>, ApiAuthOptionsValidator>();
        services.AddSingleton<ApiKeyStore>();
        services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddPolicy(SyncEndpoints.AdminPolicy, policy => policy.RequireRole(ApiRoles.Admin));

        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });

        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
        {
            var correlationId = CorrelationIdMiddleware.Get(context.HttpContext);
            if (correlationId is not null)
            {
                context.ProblemDetails.Extensions["correlationId"] = correlationId;
            }
        });
        services.AddExceptionHandler<ApiExceptionHandler>();

        services.AddOpenApi("v1", options => options.AddDocumentTransformer<ContentDocumentTransformer>());

        services.AddHealthChecks()
            .AddCheck<MongoReadinessCheck>("mongo", HealthStatus.Unhealthy, [Readiness.Tag])
            .AddCheck<SearchReadinessCheck>("opensearch", HealthStatus.Unhealthy, [Readiness.Tag]);
        return services;
    }

    public static WebApplication UseContentApi(this WebApplication app)
    {
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapOpenApi("/openapi/{documentName}.json").AllowAnonymous();
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
        app.MapHealthChecks(
                "/health/ready",
                new HealthCheckOptions { Predicate = registration => registration.Tags.Contains(Readiness.Tag), ResponseWriter = WriteReadiness })
            .AllowAnonymous();

        app.MapContentEndpoints();
        app.MapSyncEndpoints();
        app.MapIndexEndpoints();
        app.MapMigrationEndpoints();
        return app;
    }

    private static Task WriteReadiness(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        var body = new
        {
            status = report.Status.ToString().ToLowerInvariant(),
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString().ToLowerInvariant(),
                description = entry.Value.Description,
            }),
        };
        return context.Response.WriteAsJsonAsync(body, context.RequestAborted);
    }
}
