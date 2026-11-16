using Bujo.Domain;
using Bujo.Host;
using Bujo.Infrastructure;
using Bujo.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;

var shouldMigrate = args.Contains("--migrate", StringComparer.Ordinal);
var shouldMigrateOnly = args.Contains("--migrate-only", StringComparer.Ordinal);
var builder = WebApplication.CreateBuilder(args.Where(argument =>
    argument is not "--migrate" and not "--migrate-only").ToArray());

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
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .AddRequirements(approvedPrincipal)
        .Build();
});

var connectionString = builder.Configuration.GetConnectionString("journal")
    ?? throw new InvalidOperationException("ConnectionStrings:journal must be configured.");
builder.Services.AddJournalPersistence(connectionString);
builder.Services.AddJournalApplication();
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
    .AddCheck<JournalDatabaseHealthCheck>("journal-database", tags: ["ready"]);

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
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

app.MapJournalEndpoints();

app.Run();

static HealthCheckOptions HealthOptions(string tag) => new()
{
    Predicate = check => check.Tags.Contains(tag),
    ResponseWriter = static (_, _) => Task.CompletedTask
};

public partial class Program { }
