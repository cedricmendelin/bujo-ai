using Bujo.Domain;
using Bujo.Host;
using Bujo.Infrastructure;
using Bujo.Infrastructure.Persistence;
using Bujo.ServiceDefaults;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var shouldMigrate = args.Contains("--migrate", StringComparer.Ordinal);
var builder = WebApplication.CreateBuilder(args.Where(argument => argument != "--migrate").ToArray());
builder.Services.AddServiceDefaults();

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
    .AddCheck<JournalDatabaseHealthCheck>("journal-database", tags: ["ready"]);

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapServiceDefaults();

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

app.MapJournalEndpoints();

app.Run();

public partial class Program { }
