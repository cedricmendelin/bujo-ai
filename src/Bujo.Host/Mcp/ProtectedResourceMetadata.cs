using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Bujo.Host.Mcp;

internal sealed record ProtectedResourceMetadata(
    [property: JsonPropertyName("resource")] string Resource,
    [property: JsonPropertyName("authorization_servers")] IReadOnlyList<string> AuthorizationServers,
    [property: JsonPropertyName("scopes_supported")] IReadOnlyList<string> ScopesSupported,
    [property: JsonPropertyName("bearer_methods_supported")] IReadOnlyList<string> BearerMethodsSupported);

internal static class McpEndpoints
{
    private const string DiscoveryPath = "/.well-known/oauth-protected-resource/mcp";
    private const string ScopeName = "journal.access";

    public static void MapProtectedResourceMetadata(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/.well-known/oauth-protected-resource", GetMetadata).AllowAnonymous();
        endpoints.MapGet(DiscoveryPath, GetMetadata).AllowAnonymous();
    }

    public static void UseMcpRequestSecurity(this IApplicationBuilder app)
    {
        var allowedOrigins = ParseAllowedOrigins(app.ApplicationServices.GetRequiredService<IConfiguration>());

        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/mcp") &&
                context.Request.Headers.TryGetValue("Origin", out var origins) &&
                (origins.Count != 1 || !IsAllowedOrigin(origins[0], allowedOrigins)))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next();

            if (context.Request.Path.StartsWithSegments("/mcp") &&
                context.Response.StatusCode == StatusCodes.Status401Unauthorized)
            {
                var resource = ResourceUri(context);
                var metadata = AbsoluteUri(context, DiscoveryPath);
                const char quote = '"';
                context.Response.Headers.WWWAuthenticate =
                    string.Concat("Bearer ", "resource_metadata=", quote, metadata, quote, ", scope=", quote, resource, "/", ScopeName, quote);
            }
        });
    }

    private static IResult GetMetadata(HttpContext context)
    {
        var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
        var resource = ResourceUri(context);
        var tenantId = configuration["Auth:TenantId"];
        var authorizationServers = Guid.TryParse(tenantId, out var parsedTenantId)
            ? new[] { $"https://login.microsoftonline.com/{parsedTenantId:D}/v2.0" }
            : [];

        return Results.Json(new ProtectedResourceMetadata(
            resource,
            authorizationServers,
            [$"{resource}/{ScopeName}"],
            ["header"]));
    }

    private static HashSet<string> ParseAllowedOrigins(IConfiguration configuration)
    {
        var configuredOrigins = configuration.GetSection("Mcp:AllowedOrigins").Get<string[]>() ?? [];
        var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var configuredOrigin in configuredOrigins)
        {
            if (!TryNormalizeOrigin(configuredOrigin, out var normalizedOrigin))
            {
                throw new InvalidOperationException("Mcp:AllowedOrigins must contain only absolute HTTP or HTTPS origins.");
            }

            origins.Add(normalizedOrigin);
        }

        return origins;
    }

    private static bool IsAllowedOrigin(string? origin, IReadOnlySet<string> allowedOrigins) =>
        TryNormalizeOrigin(origin, out var normalizedOrigin) && allowedOrigins.Contains(normalizedOrigin);

    private static bool TryNormalizeOrigin(string? origin, out string normalizedOrigin)
    {
        normalizedOrigin = string.Empty;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length != 0 ||
            uri.AbsolutePath != "/" ||
            uri.Query.Length != 0 ||
            uri.Fragment.Length != 0)
        {
            return false;
        }

        normalizedOrigin = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }

    private static string ResourceUri(HttpContext context) => $"{PublicOrigin(context)}/mcp";

    private static string AbsoluteUri(HttpContext context, string path) => $"{PublicOrigin(context)}{path}";

    private static string PublicOrigin(HttpContext context)
    {
        var configured = context.RequestServices.GetRequiredService<IConfiguration>()["Mcp:PublicBaseUrl"];
        if (string.IsNullOrWhiteSpace(configured) ||
            !Uri.TryCreate(configured, UriKind.Absolute, out var configuredUri) ||
            configuredUri.Scheme is not ("http" or "https") ||
            configuredUri.UserInfo.Length != 0 ||
            configuredUri.AbsolutePath != "/" ||
            configuredUri.Query.Length != 0 ||
            configuredUri.Fragment.Length != 0)
        {
            throw new InvalidOperationException("Mcp:PublicBaseUrl must be configured as an absolute HTTP or HTTPS origin.");
        }

        return configuredUri.GetLeftPart(UriPartial.Authority);
    }
}
