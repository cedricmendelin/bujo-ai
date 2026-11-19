using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using Azure.Core;
using Bujo.Host;
using Bujo.Host.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Bujo.Integration.Tests;

public sealed class BffAuthorizationTests : IClassFixture<BffTestFactory>
{
    private readonly BffTestFactory _factory;
    private readonly HttpClient _client;

    public BffAuthorizationTests(BffTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });
    }

    [Theory]
    [InlineData("/auth/sign-in")]
    [InlineData("/auth/signed-out")]
    [InlineData("/auth/denied")]
    [InlineData("/auth/error")]
    public async Task SafeAccountPagesAreAnonymousUncachedAndRevealNoIdentity(string path)
    {
        using var response = await _client.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<h1>", html);
        Assert.DoesNotContain(BffTestFactory.Tenant, html);
        Assert.DoesNotContain(BffTestFactory.Subject, html);
        Assert.DoesNotContain("access_token", html);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        if (path == "/auth/sign-in")
            Assert.Contains("form-action 'self' https://login.microsoftonline.com;", csp);
        else
        {
            Assert.Contains("form-action 'self';", csp);
            Assert.DoesNotContain("login.microsoftonline.com", csp);
        }
    }

    [Fact]
    public async Task ProtectedUiRedirectsToSafeSignInAndApiDoesNotRedirect()
    {
        using var api = await _client.GetAsync("/api/journal");
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
        Assert.Null(api.Headers.Location);

        foreach (var path in new[] { "/rendering-proof", "/today", "/capture", "/lists", "/upcoming" })
        {
            using var ui = await _client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, ui.StatusCode);
            Assert.StartsWith("https://localhost/auth/sign-in", ui.Headers.Location!.ToString());
        }
    }

    [Theory]
    [InlineData(true, "https", true)]
    [InlineData(true, "http", false)]
    [InlineData(false, "https", false)]
    public async Task ForwardedHttpsEnablesSecureSignInOnlyBehindContainerAppsIngress(
        bool containerApps, string forwardedScheme, bool expectHttps)
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("CONTAINER_APP_NAME", containerApps ? "test-bujo" : ""));

        Task<Microsoft.AspNetCore.Http.HttpContext> GetAsync(string path) =>
            factory.Server.SendAsync(context =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
                context.Request.Method = "GET";
                context.Request.Scheme = "http";
                context.Request.Path = path;
                context.Request.Host = new Microsoft.AspNetCore.Http.HostString("bujo.example.test");
                context.Request.Headers["X-Forwarded-Proto"] = forwardedScheme;
                context.Request.Headers["X-Forwarded-Host"] = "ignored.example.test";
            });

        var signIn = await GetAsync("/auth/sign-in");
        if (expectHttps)
        {
            Assert.Equal(200, signIn.Response.StatusCode);
            Assert.Contains(signIn.Response.Headers.SetCookie, cookie =>
                cookie is not null && cookie.StartsWith("__Host-Bujo.Antiforgery=", StringComparison.Ordinal)
                && cookie.Contains("; secure", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            Assert.DoesNotContain(signIn.Response.Headers.SetCookie, cookie =>
                cookie is not null && cookie.StartsWith("__Host-Bujo.Antiforgery=", StringComparison.Ordinal));
        }

        var protectedPage = await GetAsync("/rendering-proof");
        Assert.Equal(302, protectedPage.Response.StatusCode);
        Assert.StartsWith($"{(expectHttps ? "https" : "http")}://bujo.example.test/auth/sign-in",
            protectedPage.Response.Headers.Location.ToString());
    }

    [Fact]
    public async Task StateChangingAccountEndpointsRejectMissingAntiforgery()
    {
        using var signIn = await _client.PostAsync("/auth/sign-in", new FormUrlEncodedContent([]));
        Assert.Equal(HttpStatusCode.BadRequest, signIn.StatusCode);
    }

    [Fact]
    public async Task MissingHumanConfigurationDoesNotStartUnconfiguredOidc()
    {
        var html = await _client.GetStringAsync("/auth/sign-in");
        var token = System.Text.RegularExpressions.Regex.Match(html,
            "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);
        using var result = await _client.PostAsync("/auth/sign-in",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.Redirect, result.StatusCode);
        Assert.Equal("/auth/error", result.Headers.Location!.ToString());
    }

    [Fact]
    public async Task ManagedIdentityAuthenticatesCodeRedemptionWithoutAClientSecret()
    {
        var credential = new TestManagedIdentityCredential();
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Auth:Pwa:ManagedIdentityClientId", BffTestFactory.Subject);
            builder.UseSetting("Auth:Pwa:ClientSecret", "unused-test-secret");
            builder.ConfigureTestServices(services =>
                services.AddKeyedSingleton<TokenCredential>(BffAuthentication.OidcScheme, credential));
        });
        var options = factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(BffAuthentication.OidcScheme);
        Assert.Null(options.ClientSecret);
        var context = new AuthorizationCodeReceivedContext(
            new DefaultHttpContext { RequestServices = factory.Services },
            new AuthenticationScheme(BffAuthentication.OidcScheme, null, typeof(OpenIdConnectHandler)),
            options, new AuthenticationProperties())
        {
            TokenEndpointRequest = new OpenIdConnectMessage { Code = "test-code" }
        };
        context.TokenEndpointRequest.SetParameter("code_verifier", "test-verifier");

        await options.Events.AuthorizationCodeReceived(context);

        Assert.Equal("api://AzureADTokenExchange/.default", Assert.Single(credential.Scopes!));
        Assert.Equal("urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            context.TokenEndpointRequest.GetParameter("client_assertion_type"));
        Assert.Equal("test-assertion", context.TokenEndpointRequest.GetParameter("client_assertion"));
        Assert.Null(context.TokenEndpointRequest.ClientSecret);
        Assert.Equal("test-code", context.TokenEndpointRequest.Code);
        Assert.Equal("test-verifier", context.TokenEndpointRequest.GetParameter("code_verifier"));
        Assert.False(context.HandledCodeRedemption);
    }

    [Fact]
    public async Task ManagedIdentityConfigurationStartsEntraChallengeWithoutAClientSecret()
    {
        const string authorizationEndpoint = "https://login.microsoftonline.com/test/oauth2/v2.0/authorize";
        const string scope = "api://proof/journal.access";
        string? challengeReturnUri = null;
        using var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Auth:Pwa:ClientId", BffTestFactory.Subject);
            builder.UseSetting("Auth:Pwa:ManagedIdentityClientId", BffTestFactory.Subject);
            builder.UseSetting("Auth:Pwa:Scope", scope);
            builder.ConfigureTestServices(services =>
                services.PostConfigure<OpenIdConnectOptions>(BffAuthentication.OidcScheme, options =>
                {
                    options.ConfigurationManager =
                        new Microsoft.IdentityModel.Protocols.StaticConfigurationManager<OpenIdConnectConfiguration>(
                            new OpenIdConnectConfiguration { AuthorizationEndpoint = authorizationEndpoint });
                    options.Events.OnRedirectToIdentityProvider = context =>
                    {
                        challengeReturnUri = context.Properties.RedirectUri;
                        return Task.CompletedTask;
                    };
                }));
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://bujo.example.test"),
            AllowAutoRedirect = false
        });
        var html = await client.GetStringAsync("/auth/sign-in");
        var token = System.Text.RegularExpressions.Regex.Match(html,
            "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
        using var response = await client.PostAsync("/auth/sign-in",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.Equal(authorizationEndpoint, location.GetLeftPart(UriPartial.Path));
        var parameters = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("https://bujo.example.test/signin-oidc", parameters["redirect_uri"].ToString());
        Assert.Equal("S256", parameters["code_challenge_method"].ToString());
        Assert.Contains(scope, parameters["scope"].ToString().Split(' '));
        Assert.Equal("/", challengeReturnUri);
    }

    private sealed class TestManagedIdentityCredential : TokenCredential
    {
        public string[]? Scopes { get; private set; }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Scopes = requestContext.Scopes;
            return ValueTask.FromResult(new AccessToken("test-assertion", DateTimeOffset.UtcNow.AddMinutes(1)));
        }
    }

    [Fact]
    public void CookieAndOidcOptionsKeepTokensOutOfBrowserAndRequireCodePkce()
    {
        var cookie = _factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(BffAuthentication.CookieScheme);
        var oidc = _factory.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(BffAuthentication.OidcScheme);
        Assert.Equal("__Host-Bujo", cookie.Cookie.Name);
        Assert.True(cookie.Cookie.HttpOnly);
        Assert.Equal(Microsoft.AspNetCore.Http.CookieSecurePolicy.Always, cookie.Cookie.SecurePolicy);
        Assert.Equal(Microsoft.AspNetCore.Http.SameSiteMode.Lax, cookie.Cookie.SameSite);
        Assert.False(cookie.SlidingExpiration);
        Assert.Equal("code", oidc.ResponseType);
        Assert.True(oidc.UsePkce);
        Assert.False(oidc.SaveTokens);
        Assert.True(oidc.RequireHttpsMetadata);
        Assert.Equal("/signin-oidc", oidc.CallbackPath.Value);
    }

    [Fact]
    public async Task RealCookieWithNoServerTokenCannotAuthorizeUiOrBearerApi()
    {
        var options = _factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(BffAuthentication.CookieScheme);
        var principal = Principal();
        var ticket = new Microsoft.AspNetCore.Authentication.AuthenticationTicket(principal,
            new Microsoft.AspNetCore.Authentication.AuthenticationProperties(), BffAuthentication.CookieScheme);
        var value = options.TicketDataFormat.Protect(ticket);
        using var apiRequest = new HttpRequestMessage(HttpMethod.Get, "/api/journal");
        apiRequest.Headers.Add("Cookie", $"__Host-Bujo={value}");
        using var api = await _client.SendAsync(apiRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);

        using var uiRequest = new HttpRequestMessage(HttpMethod.Get, "/rendering-proof");
        uiRequest.Headers.Add("Cookie", $"__Host-Bujo={value}");
        using var ui = await _client.SendAsync(uiRequest);
        Assert.Equal(HttpStatusCode.Redirect, ui.StatusCode);
        Assert.StartsWith("https://localhost/auth/sign-in", ui.Headers.Location!.ToString());
    }

    [Fact]
    public async Task ValidServerBackedCookieRendersProofButNeverAuthorizesApiAndLogoutRevokesSession()
    {
        var principal = Principal();
        var token = _factory.CreateAccessToken();
        var sessions = _factory.Services.GetRequiredService<BffSessions>();
        var id = sessions.Create(token, principal);
        ((ClaimsIdentity)principal.Identity!).AddClaim(new(BffSessions.SessionClaim, id));
        var options = _factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(BffAuthentication.CookieScheme);
        var ticket = new Microsoft.AspNetCore.Authentication.AuthenticationTicket(principal,
            new Microsoft.AspNetCore.Authentication.AuthenticationProperties(), BffAuthentication.CookieScheme);
        var cookie = options.TicketDataFormat.Protect(ticket);
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = false
        });
        client.DefaultRequestHeaders.Add("Cookie", $"__Host-Bujo={cookie}");
        using var ui = await client.GetAsync("/rendering-proof");
        var html = await ui.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, ui.StatusCode);
        Assert.Contains("proof-input", html);
        Assert.Contains("<title>Connection proof · Bujo-AI</title>", html);
        Assert.DoesNotContain(token, html);
        using var api = await client.GetAsync("/api/journal");
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
        Assert.Null(api.Headers.Location);
        using var invalidSignOut = await client.PostAsync("/auth/sign-out", new FormUrlEncodedContent([]));
        Assert.Equal(HttpStatusCode.BadRequest, invalidSignOut.StatusCode);
        Assert.True(await sessions.IsValidAsync(principal));

        using var confirmation = await client.GetAsync("/auth/sign-out");
        Assert.Equal(HttpStatusCode.OK, confirmation.StatusCode);
        Assert.True(await sessions.IsValidAsync(principal)); // GET did not revoke.
        var form = await confirmation.Content.ReadAsStringAsync();
        var csrf = System.Text.RegularExpressions.Regex.Match(form,
            "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
        var csrfCookie = confirmation.Headers.GetValues("Set-Cookie")
            .Single(value => value.StartsWith("__Host-Bujo.Antiforgery=", StringComparison.Ordinal)).Split(';')[0];
        using var logout = new HttpRequestMessage(HttpMethod.Post, "/auth/sign-out")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = csrf })
        };
        logout.Headers.Add("Cookie", $"__Host-Bujo={cookie}; {csrfCookie}");
        // Avoid the default Cookie header being combined a second time.
        client.DefaultRequestHeaders.Remove("Cookie");
        using var signedOut = await client.SendAsync(logout);
        Assert.Equal(HttpStatusCode.Redirect, signedOut.StatusCode);
        Assert.Equal("/auth/signed-out", signedOut.Headers.Location!.ToString());
        Assert.False(await sessions.IsValidAsync(principal));
    }

    [Fact]
    public async Task CircuitStartReconnectAndEventsRejectRevokedSessionAndChangedAllowlist()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Auth:TenantId"] = BffTestFactory.Tenant,
            ["Auth:AllowedPrincipals"] = BffTestFactory.Subject
        }).Build();
        var validator = new MutableSessionValidator();
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddAuthorization(options => options.AddPolicy(BffAuthentication.UiPolicy, policy =>
            policy.RequireAuthenticatedUser().AddRequirements(
                new ApprovedPrincipalRequirement(configuration), new BffSessionRequirement())));
        services.AddSingleton<IAuthorizationHandler, ApprovedPrincipalHandler>();
        services.AddSingleton<IAuthorizationHandler, BffSessionHandler>();
        services.AddSingleton<IBffSessionValidator>(validator);
        using var provider = services.BuildServiceProvider();
        var state = new MutableAuthenticationState(Principal());
        using var handler = new ApprovedCircuitHandler(state, provider.GetRequiredService<IAuthorizationService>());
        await handler.OnCircuitOpenedAsync(null!, default);
        await handler.OnConnectionUpAsync(null!, default);
        var invoked = false;
        var activity = handler.CreateInboundActivityHandler(_ => { invoked = true; return Task.CompletedTask; });
        await activity(null!);
        Assert.True(invoked);

        configuration["Auth:AllowedPrincipals"] = "";
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => handler.OnConnectionUpAsync(null!, default));
        invoked = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => activity(null!));
        Assert.False(invoked);
        configuration["Auth:AllowedPrincipals"] = BffTestFactory.Subject;
        validator.Valid = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => handler.OnConnectionUpAsync(null!, default));
        validator.Valid = true;
        state.Change(new ClaimsPrincipal(new ClaimsIdentity()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => activity(null!));
    }

    internal static ClaimsPrincipal Principal() => new(new ClaimsIdentity(
    [
        new("tid", BffTestFactory.Tenant),
        new("oid", BffTestFactory.Subject),
        new("scp", "journal.access")
    ], BffAuthentication.CookieScheme));

    private sealed class MutableSessionValidator : IBffSessionValidator
    {
        public bool Valid { get; set; } = true;
        public Task<bool> IsValidAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default) =>
            Task.FromResult(Valid);
    }

    private sealed class MutableAuthenticationState(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        private Task<AuthenticationState> _state = Task.FromResult(new AuthenticationState(user));
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => _state;
        public void Change(ClaimsPrincipal principal)
        {
            _state = Task.FromResult(new AuthenticationState(principal));
            NotifyAuthenticationStateChanged(_state);
        }
    }
}

public sealed class BffTestFactory : WebApplicationFactory<JournalDatabaseHealthCheck>
{
    internal const string Tenant = "00000000-0000-0000-0000-000000000001";
    internal const string Subject = "00000000-0000-0000-0000-000000000002";
    private readonly RsaSecurityKey _key = new(RSA.Create(2048)) { KeyId = "bff-test-key" };

    internal string CreateAccessToken() => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0",
        Audience = "proof-api",
        Subject = (ClaimsIdentity)BffAuthorizationTests.Principal().Identity!,
        Expires = DateTime.UtcNow.AddMinutes(10),
        SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256)
    });

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:journal", "Server=localhost;Database=proof;Integrated Security=true;TrustServerCertificate=true");
        builder.UseSetting("Auth:TenantId", Tenant);
        builder.UseSetting("Auth:Audience", "proof-api");
        builder.UseSetting("Auth:AllowedPrincipals", Subject);
        builder.UseSetting("Auth:Pwa:ClientSecret", "");
        builder.ConfigureTestServices(services =>
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                var configuration = new OpenIdConnectConfiguration
                {
                    Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0"
                };
                configuration.SigningKeys.Add(_key);
                options.ConfigurationManager =
                    new Microsoft.IdentityModel.Protocols.StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
            }));
    }
}

public sealed class BffTokenValidationTests
{
    [Theory]
    [InlineData("valid", true)]
    [InlineData("audience", false)]
    [InlineData("issuer", false)]
    [InlineData("expired", false)]
    [InlineData("signature", false)]
    [InlineData("malformed", false)]
    public async Task ServerSessionValidatesActualSignedTokenAndBindsIdentity(string condition, bool expected)
    {
        using var rsa = RSA.Create(2048);
        using var otherRsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "test-key" };
        var configuration = new OpenIdConnectConfiguration { Issuer = "https://issuer.example.test" };
        configuration.SigningKeys.Add(key);
        var services = new ServiceCollection().AddLogging();
        services.AddAuthentication().AddJwtBearer(options =>
        {
            options.Configuration = configuration;
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = configuration.Issuer,
                ValidateAudience = true,
                ValidAudience = "proof-api",
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = key,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ClockSkew = TimeSpan.Zero
            };
        });
        using var provider = services.BuildServiceProvider();
        using var sessions = new BffSessions(provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>());
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = condition == "issuer" ? "https://other.example.test" : configuration.Issuer,
            Audience = condition == "audience" ? "other-api" : "proof-api",
            Expires = condition == "expired" ? DateTime.UtcNow.AddMinutes(-1) : DateTime.UtcNow.AddMinutes(10),
            IssuedAt = DateTime.UtcNow.AddMinutes(-10),
            NotBefore = DateTime.UtcNow.AddMinutes(-10),
            Subject = (ClaimsIdentity)BffAuthorizationTests.Principal().Identity!,
            SigningCredentials = new SigningCredentials(
                condition == "signature" ? new RsaSecurityKey(otherRsa) { KeyId = "test-key" } : key,
                SecurityAlgorithms.RsaSha256)
        };
        var token = condition == "malformed" ? "not-a-token" : new JsonWebTokenHandler().CreateToken(descriptor);
        var validated = await sessions.ValidateTokenAsync(token, default);
        Assert.Equal(expected, validated is not null);
        if (!expected)
            return;

        var principal = BffAuthorizationTests.Principal();
        var id = sessions.Create(token, principal);
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(BffSessions.SessionClaim, id));
        Assert.True(await sessions.IsValidAsync(principal));
        var mismatched = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new("tid", BffTestFactory.Tenant),
            new("oid", "00000000-0000-0000-0000-000000000003"),
            new(BffSessions.SessionClaim, id)
        ], "cookie"));
        Assert.False(await sessions.IsValidAsync(mismatched));
        Assert.False(await sessions.IsValidAsync(principal)); // Mismatch revoked the server session.
        var logoutId = sessions.Create(token, principal);
        ((ClaimsIdentity)principal.Identity!).RemoveClaim(principal.FindFirst(BffSessions.SessionClaim)!);
        ((ClaimsIdentity)principal.Identity!).AddClaim(new(BffSessions.SessionClaim, logoutId));
        sessions.Remove(principal);
        Assert.False(await sessions.IsValidAsync(principal));
    }
}
