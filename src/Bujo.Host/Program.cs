using Bujo.Domain;
using Bujo.Host;
using Bujo.Host.Authentication;
using Bujo.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Bujo.Infrastructure;
using Bujo.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.AspNetCore;
using Bujo.Host.Mcp;
using Bujo.Host.Chat;
using Bujo.Application.Chat;
using System.Threading.RateLimiting;

var shouldMigrate = args.Contains("--migrate", StringComparer.Ordinal);
var shouldMigrateOnly = args.Contains("--migrate-only", StringComparer.Ordinal);
var shouldVerifyDatabase = args.Contains("--verify-database", StringComparer.Ordinal);
if (new[] { shouldMigrate, shouldMigrateOnly, shouldVerifyDatabase }.Count(enabled => enabled) > 1)
{
    throw new InvalidOperationException("Migration and database verification modes cannot be combined.");
}

var hostArgs = args.Where(argument =>
    argument is not "--migrate" and not "--migrate-only" and not "--verify-database").ToArray();
if (shouldMigrateOnly)
{
    return await MigrationJob.RunAsync(hostArgs);
}

var builder = WebApplication.CreateBuilder(hostArgs);
builder.WebHost.UseStaticWebAssets();

var tenantSetting = builder.Configuration["Auth:TenantId"];
var tenantId = Guid.TryParse(tenantSetting, out var parsedTenantId)
    ? parsedTenantId.ToString("D")
    : null;
var audience = builder.Configuration["Auth:Audience"];
var validConfiguration = tenantId is not null && !string.IsNullOrWhiteSpace(audience);
var issuer = validConfiguration
    ? $"https://login.microsoftonline.com/{tenantId}/v2.0"
    : "urn:bujo-ai:invalid-auth-configuration";

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.Authority = tenantId is null
            ? null
            : $"https://login.microsoftonline.com/{tenantId}/v2.0";
        options.RequireHttpsMetadata = true;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = issuer,
            ValidateAudience = true,
            ValidAudience = string.IsNullOrWhiteSpace(audience) ? "urn:bujo-ai:invalid-audience" : audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromMinutes(2)
        };
    });

var approvedPrincipal = new ApprovedPrincipalRequirement(builder.Configuration);
var approvedPrincipalPolicy = new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
    .RequireAuthenticatedUser()
    .AddRequirements(approvedPrincipal)
    .Build();
builder.Services.AddSingleton(approvedPrincipal);
builder.Services.AddSingleton<IAuthorizationHandler, ApprovedPrincipalHandler>();
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = approvedPrincipalPolicy;
    options.AddPolicy("ApprovedPrincipal", approvedPrincipalPolicy);
});
builder.Services.AddBff(builder.Configuration, tenantId);
builder.Services.Configure<AuthenticationOptions>(options =>
{
    // Populate SSR account chrome from the UI cookie. API fallback explicitly selects bearer.
    options.DefaultAuthenticateScheme = BffAuthentication.CookieScheme;
    options.DefaultChallengeScheme = BffAuthentication.CookieScheme;
});
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddHttpContextAccessor();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<CircuitHandler, ApprovedCircuitHandler>();
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "__Host-Bujo.Antiforgery";
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
});
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    var knownProxies = (builder.Configuration["Hosting:KnownProxies"] ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (knownProxies.Length == 0 && !string.IsNullOrWhiteSpace(builder.Configuration["CONTAINER_APP_NAME"]))
    {
        // Container Apps HTTP ingress overwrites X-Forwarded-Proto and is the only path to the app port.
        // Do not trust any other forwarded headers or depend on the platform proxy's IP staying fixed.
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    }

    foreach (var value in knownProxies)
    {
        if (System.Net.IPAddress.TryParse(value, out var address))
            options.KnownProxies.Add(address);
        else
            throw new InvalidOperationException("Hosting:KnownProxies must contain only IP addresses.");
    }
});

var connectionString = builder.Configuration.GetConnectionString("journal")
    ?? throw new InvalidOperationException("ConnectionStrings:journal must be configured.");
builder.Services.AddJournalPersistence(connectionString);
builder.Services.AddJournalApplication();
builder.Services.Configure<ChatOptions>(builder.Configuration.GetSection(ChatOptions.SectionName));
builder.Services.AddSingleton<ChatConversationStateStore>();
builder.Services.AddHttpClient("ChatProvider");
builder.Services.AddHttpClient("ChatSpeech");
builder.Services.AddScoped<IChatRuntime, ChatRuntime>();
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("chat", httpContext =>
    {
        var objectId = httpContext.User.FindFirst("oid")?.Value;
        var partition = string.IsNullOrWhiteSpace(objectId)
            ? httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"
            : objectId;
        return RateLimitPartition.GetFixedWindowLimiter(partition, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });
    });
});
builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
    .WithTools<JournalMcpTools>()
    .WithTools<KnowledgeMcpTools>()
    .WithPrompts<JournalMcpPrompts>()
    .WithResources<JournalMcpResources>();
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
    .AddCheck<JournalDatabaseHealthCheck>("journal-database", tags: ["ready"]);

var app = builder.Build();
if (approvedPrincipal.TenantId is null)
{
    app.Logger.LogWarning("Auth:TenantId is missing or invalid; approved-account authorization will deny all principals.");
}

if (approvedPrincipal.InvalidAllowedPrincipalEntryCount > 0)
{
    app.Logger.LogWarning(
        "Auth:AllowedPrincipals contains {InvalidEntryCount} invalid entries; use comma-separated bare Entra object ID GUIDs. Invalid entries cannot authorize an account.",
        approvedPrincipal.InvalidAllowedPrincipalEntryCount);
}
else if (approvedPrincipal.AllowedObjectIds.Count == 0)
{
    app.Logger.LogWarning("Auth:AllowedPrincipals contains no valid object IDs; approved-account authorization will deny all principals.");
}

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/auth/error");
    app.UseHsts();
}
app.UseMcpRequestSecurity();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    // Chromium applies form-action from Home to redirects of its sign-in POST.
    var formAction = "'self'";
    if (context.Request.Path.Equals("/", StringComparison.Ordinal) &&
        HttpMethods.IsGet(context.Request.Method) &&
        context.User.Identity?.IsAuthenticated != true)
    {
        var authority = context.RequestServices.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(BffAuthentication.OidcScheme).Authority;
        if (Uri.TryCreate(authority, UriKind.Absolute, out var providerUri) &&
            providerUri.Scheme == Uri.UriSchemeHttps)
        {
            formAction += $" {providerUri.GetLeftPart(UriPartial.Authority)}";
        }
    }
    context.Response.Headers.ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; " +
        $"connect-src 'self'; object-src 'none'; base-uri 'self'; form-action {formAction}; frame-ancestors 'none'";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(self), geolocation=()";
    if (!context.Request.Path.StartsWithSegments("/_content") &&
        !context.Request.Path.StartsWithSegments("/_framework"))
        context.Response.Headers.CacheControl = "no-store, private";
    await next();
});
app.UseAuthorization();
app.UseAntiforgery();
app.UseRateLimiter();
app.MapStaticAssets().AllowAnonymous();
app.MapGet("/service-worker.js", (HttpContext context, IWebHostEnvironment environment) =>
{
    var file = environment.WebRootFileProvider.GetFileInfo("_content/Bujo.Web/service-worker.js");
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers["Service-Worker-Allowed"] = "/";
    return file.Exists ? Results.Stream(file.CreateReadStream(), "text/javascript") : Results.NotFound();
}).AllowAnonymous();
app.MapAccountEndpoints();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode()
    .RequireAuthorization(BffAuthentication.UiPolicy);
app.MapHealthChecks("/health/live", HealthOptions("live")).AllowAnonymous();
app.MapHealthChecks("/health/ready", HealthOptions("ready"));

if (shouldMigrate)
{
    if (!app.Environment.IsDevelopment())
    {
        throw new InvalidOperationException("Database migrations can only run in Development.");
    }

    await using var scope = app.Services.CreateAsyncScope();
    var contextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<JournalDbContext>>();
    await using var context = await contextFactory.CreateDbContextAsync();
    await context.Database.MigrateAsync();
}

if (shouldVerifyDatabase || builder.Configuration.GetValue<bool>("Deployment:VerifyDatabaseOnStart"))
{
    var result = await app.Services.GetRequiredService<HealthCheckService>()
        .CheckHealthAsync(registration => registration.Tags.Contains("ready"));
    if (result.Status != HealthStatus.Healthy)
    {
        throw new InvalidOperationException(
            "Journal database verification failed. Verify the release migrations and runtime SQL data permissions.");
    }

    if (shouldVerifyDatabase)
    {
        await app.DisposeAsync();
        return 0;
    }
}

app.MapJournalEndpoints();
app.MapKnowledgeEndpoints();
app.MapProtectedResourceMetadata();
app.MapMcp("/mcp").RequireAuthorization("ApprovedPrincipal");
app.MapChatEndpoints();

app.Run();
return 0;

static HealthCheckOptions HealthOptions(string tag) => new()
{
    Predicate = check => check.Tags.Contains(tag),
    ResponseWriter = static async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        await System.Text.Json.JsonSerializer.SerializeAsync(
            context.Response.Body,
            new
            {
                status = report.Status.ToString(),
                checks = report.Entries.Select(entry => new
                {
                    name = entry.Key,
                    status = entry.Value.Status.ToString(),
                    data = entry.Value.Data
                })
            });
    }
};

public partial class Program { }
