using System.Security.Claims;
using System.Text.Encodings.Web;
using Bujo.Host.Authentication;
using Bujo.Domain;
using Bujo.Infrastructure.Persistence;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Bujo.E2E.Tests;

public sealed class BrowserFixture : IAsyncLifetime
{
    // Redirects must never reach Entra, even when browser request routing misses a redirect hop.
    internal const string IdentityHostDenialRule = "--host-resolver-rules=MAP login.microsoftonline.com ~NOTFOUND";
    private readonly ProofHostFactory _factory = new();
    private IPlaywright _playwright = null!;
    private HttpClient _client = null!;

    public IBrowser Browser { get; private set; } = null!;
    public string BaseUrl => _client.BaseAddress!.ToString().TrimEnd('/');
    internal BrowserJournalRepository Journal => _factory.Journal;
    internal BrowserClock Clock => _factory.Clock;
    internal InMemoryKnowledgeRepository Knowledge => _factory.Knowledge;

    public async Task InitializeAsync()
    {
        _factory.UseKestrel(0);
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var addresses = _factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        _client.BaseAddress = new Uri(addresses.Addresses.Single());
        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new()
        {
            Headless = true,
            ExecutablePath = Environment.GetEnvironmentVariable("BUJO_PLAYWRIGHT_CHROMIUM"),
            Args = ["--no-sandbox", "--enable-automation", "--no-proxy-server", IdentityHostDenialRule]
        });
    }

    public async Task<IBrowserContext> CreateContextAsync(string? identity = "allow", int width = 1280, int height = 900)
    {
        var context = await Browser.NewContextAsync(new()
        {
            BaseURL = BaseUrl,
            ViewportSize = new() { Width = width, Height = height },
            TimezoneId = "Europe/Zurich"
        });
        if (identity is not null)
        {
            await context.AddCookiesAsync([new()
            {
                Name = ProofUiAuthentication.CookieName,
                Value = identity,
                Url = BaseUrl,
                HttpOnly = true,
                SameSite = SameSiteAttribute.Strict
            }]);
        }

        return context;
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null) await Browser.DisposeAsync();
        _playwright?.Dispose();
        _client?.Dispose();
        await _factory.DisposeAsync();
    }
}

internal sealed class ProofHostFactory : WebApplicationFactory<Program>
{
    private readonly string _tenant = Guid.NewGuid().ToString("D");
    private readonly string _approvedObject = Guid.NewGuid().ToString("D");
    private readonly string _clientId = Guid.NewGuid().ToString("D");
    internal BrowserJournalRepository Journal { get; } = new();
    internal BrowserClock Clock { get; } = new();
    internal InMemoryKnowledgeRepository Knowledge { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:journal",
            "Server=127.0.0.1,1;Database=unused-proof;Integrated Security=true;Encrypt=true;Connect Timeout=1");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Auth:TenantId"] = _tenant,
                ["Auth:Audience"] = "urn:bujo:e2e",
                ["Auth:AllowedPrincipals"] = _approvedObject,
                ["Auth:Pwa:ClientId"] = _clientId,
                ["Auth:Pwa:ClientSecret"] = Guid.NewGuid().ToString("N"),
                ["Auth:Pwa:Scope"] = "urn:bujo:e2e/journal.access",
                ["ConnectionStrings:journal"] = "Server=127.0.0.1,1;Database=unused-proof;Integrated Security=true;Encrypt=true;Connect Timeout=1"
            }));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IJournalRepository>();
            services.AddSingleton<IJournalRepository>(Journal);
            services.RemoveAll<IKnowledgeRepository>();
            services.AddSingleton<IKnowledgeRepository>(Knowledge);
            services.RemoveAll<IJournalBackupRepository>();
            services.AddScoped<IJournalBackupRepository>(provider => new InMemoryJournalBackupRepository(
                provider.GetRequiredService<IJournalRepository>(),
                provider.GetRequiredService<IKnowledgeRepository>()));
            services.AddSingleton<TimeProvider>(Clock);
            services.PostConfigure<OpenIdConnectOptions>(BffAuthentication.OidcScheme, options =>
            {
                options.ClientId = _clientId;
                options.Configuration = new OpenIdConnectConfiguration
                {
                    Issuer = $"https://login.microsoftonline.com/{_tenant}/v2.0",
                    AuthorizationEndpoint = $"https://login.microsoftonline.com/{_tenant}/oauth2/v2.0/authorize",
                    TokenEndpoint = $"https://login.microsoftonline.com/{_tenant}/oauth2/v2.0/token"
                };
                options.ConfigurationManager =
                    new StaticConfigurationManager<OpenIdConnectConfiguration>(options.Configuration);
            });
            services.PostConfigure<AntiforgeryOptions>(options =>
            {
                options.Cookie.Name = "bujo-e2e-antiforgery";
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            });
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, ProofUiAuthentication>(ProofUiAuthentication.Scheme, _ => { });
            services.PostConfigure<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = ProofUiAuthentication.Scheme;
                options.DefaultChallengeScheme = ProofUiAuthentication.Scheme;
                options.DefaultForbidScheme = ProofUiAuthentication.Scheme;
            });
            services.PostConfigure<AuthorizationOptions>(options =>
            {
                var existing = options.GetPolicy(BffAuthentication.UiPolicy)!;
                options.AddPolicy(BffAuthentication.UiPolicy,
                    new AuthorizationPolicy(existing.Requirements, [ProofUiAuthentication.Scheme]));
            });
            services.RemoveAll<IBffSessionValidator>();
            services.AddSingleton<IBffSessionValidator, SyntheticSessionValidator>();
            services.AddSingleton(new ProofIdentity(_tenant, _approvedObject));
        });
    }
}

internal sealed class BrowserClock : TimeProvider
{
    private long _utcNowTicks = new DateTimeOffset(2026, 11, 18, 23, 30, 0, TimeSpan.Zero).Ticks;

    public DateTimeOffset UtcNow
    {
        get => new(Interlocked.Read(ref _utcNowTicks), TimeSpan.Zero);
        set => Interlocked.Exchange(ref _utcNowTicks, value.ToUniversalTime().Ticks);
    }

    public override DateTimeOffset GetUtcNow() => UtcNow;
}

internal sealed record ProofIdentity(string Tenant, string ApprovedObject);

internal sealed class SyntheticSessionValidator : IBffSessionValidator
{
    public Task<bool> IsValidAsync(ClaimsPrincipal principal, CancellationToken cancellationToken) =>
        Task.FromResult(true);
}

internal sealed class ProofUiAuthentication(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ProofIdentity identity) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal new const string Scheme = "E2eUi";
    internal const string CookieName = "bujo-e2e-ui";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(CookieName, out var value))
            return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new[]
        {
            new Claim("tid", identity.Tenant),
            new Claim("oid", value == "allow" ? identity.ApprovedObject : Guid.NewGuid().ToString("D")),
            new Claim("scp", "journal.access"),
            new Claim(ClaimTypes.Name, "Synthetic browser proof account")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Redirect("/auth/sign-in");
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.Redirect("/auth/denied");
        return Task.CompletedTask;
    }
}
