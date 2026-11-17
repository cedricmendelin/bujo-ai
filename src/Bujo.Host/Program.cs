using Bujo.Domain;
using Bujo.Host;
using Bujo.Host.Authentication;
using Bujo.Web;
using Bujo.Infrastructure;
using Bujo.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;

var shouldMigrate = args.Contains("--migrate", StringComparer.Ordinal);
var shouldMigrateOnly = args.Contains("--migrate-only", StringComparer.Ordinal);
var shouldVerifyDatabase = args.Contains("--verify-database", StringComparer.Ordinal);
if (new[] { shouldMigrate, shouldMigrateOnly, shouldVerifyDatabase }.Count(enabled => enabled) > 1)
{
    throw new InvalidOperationException("Migration and database verification modes cannot be combined.");
}

var builder = WebApplication.CreateBuilder(args.Where(argument =>
    argument is not "--migrate" and not "--migrate-only" and not "--verify-database").ToArray());
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
builder.Services.AddSingleton(approvedPrincipal);
builder.Services.AddSingleton<IAuthorizationHandler, ApprovedPrincipalHandler>();
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()
        .AddRequirements(approvedPrincipal)
        .Build();
});
builder.Services.AddBff(builder.Configuration, tenantId);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
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
    // Never trust arbitrary clients' forwarded scheme. Deployment must supply actual ingress IPs.
    foreach (var value in (builder.Configuration["Hosting:KnownProxies"] ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
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
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
    .AddCheck<JournalDatabaseHealthCheck>("journal-database", tags: ["ready"]);

var app = builder.Build();
app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/auth/error");
    app.UseHsts();
}
app.Use(async (context, next) =>
{
    context.Response.Headers.ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; " +
        "connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    if (!context.Request.Path.StartsWithSegments("/_content") &&
        !context.Request.Path.StartsWithSegments("/_framework"))
        context.Response.Headers.CacheControl = "no-store, private";
    await next();
});
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
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

if (shouldMigrate || shouldMigrateOnly)
{
    if (shouldMigrate && !app.Environment.IsDevelopment())
    {
        throw new InvalidOperationException("Database migrations can only run in Development.");
    }

    if (shouldMigrateOnly && !app.Environment.IsProduction())
    {
        throw new InvalidOperationException("The migration job can only run in Production.");
    }

    await using var scope = app.Services.CreateAsyncScope();
    var contextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<JournalDbContext>>();
    await using var context = await contextFactory.CreateDbContextAsync();
    await context.Database.MigrateAsync();

    if (shouldMigrateOnly)
    {
        await app.DisposeAsync();
        return;
    }
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
        return;
    }
}

app.MapJournalEndpoints();

app.Run();

static HealthCheckOptions HealthOptions(string tag) => new()
{
    Predicate = check => check.Tags.Contains(tag),
    ResponseWriter = static (_, _) => Task.CompletedTask
};

public partial class Program { }
