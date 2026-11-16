using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Bujo.ServiceDefaults;

public static class ServiceDefaultsExtensions
{
    public static IServiceCollection AddServiceDefaults(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"]);
        return services;
    }

    public static IEndpointRouteBuilder MapServiceDefaults(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health/live", Options("live")).AllowAnonymous();
        endpoints.MapHealthChecks("/health/ready", Options("ready"));
        return endpoints;
    }

    private static HealthCheckOptions Options(string tag) => new()
    {
        Predicate = check => check.Tags.Contains(tag),
        ResponseWriter = static (_, _) => Task.CompletedTask
    };
}
