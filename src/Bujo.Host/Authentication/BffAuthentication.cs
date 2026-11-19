using System.Security.Claims;
using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Bujo.Host.Authentication;

public static class BffAuthentication
{
    public const string CookieScheme = "Bujo.Cookie";
    public const string OidcScheme = "Bujo.Oidc";
    public const string UiPolicy = "ApprovedUi";

    private const string ManagedIdentitySetting = "Auth:Pwa:ManagedIdentityClientId";

    internal static bool HasClientCredential(IConfiguration configuration) =>
        string.IsNullOrWhiteSpace(configuration[ManagedIdentitySetting])
            ? !string.IsNullOrWhiteSpace(configuration["Auth:Pwa:ClientSecret"])
            : Guid.TryParse(configuration[ManagedIdentitySetting], out _);

    public static void AddBff(this IServiceCollection services, IConfiguration configuration, string? tenantId)
    {
        var managedIdentityId = configuration[ManagedIdentitySetting];
        var useManagedIdentity = !string.IsNullOrWhiteSpace(managedIdentityId);
        if (Guid.TryParse(managedIdentityId, out var identityId))
            services.AddKeyedSingleton<TokenCredential>(OidcScheme,
                new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(identityId.ToString("D"))));

        services.AddSingleton<BffSessions>();
        services.AddSingleton<IBffSessionValidator>(provider => provider.GetRequiredService<BffSessions>());
        services.AddScoped<IAuthorizationHandler, BffSessionHandler>();
        services.AddAuthentication()
            .AddCookie(CookieScheme, options =>
            {
                options.Cookie.Name = "__Host-Bujo";
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.Path = "/";
                options.ExpireTimeSpan = TimeSpan.FromHours(1);
                options.SlidingExpiration = false;
                options.LoginPath = "/";
                options.AccessDeniedPath = "/auth/denied";
                options.Events.OnValidatePrincipal = async context =>
                {
                    var validator = context.HttpContext.RequestServices.GetRequiredService<IBffSessionValidator>();
                    var authorization = context.HttpContext.RequestServices.GetRequiredService<IAuthorizationService>();
                    if (context.Principal is null ||
                        !await validator.IsValidAsync(context.Principal, context.HttpContext.RequestAborted) ||
                        !(await authorization.AuthorizeAsync(context.Principal, null,
                            new ApprovedPrincipalRequirement(configuration))).Succeeded)
                    {
                        context.RejectPrincipal();
                        await context.HttpContext.SignOutAsync(CookieScheme);
                    }
                };
            })
            .AddOpenIdConnect(OidcScheme, options =>
            {
                options.SignInScheme = CookieScheme;
                options.Authority = $"https://login.microsoftonline.com/{tenantId ?? "invalid"}/v2.0";
                options.ClientId = configuration["Auth:Pwa:ClientId"] ?? "unconfigured";
                options.ClientSecret = useManagedIdentity ? null : configuration["Auth:Pwa:ClientSecret"];
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.UsePkce = true;
                options.MapInboundClaims = false;
                options.RequireHttpsMetadata = true;
                options.SaveTokens = false;
                options.GetClaimsFromUserInfoEndpoint = false;
                options.CallbackPath = "/signin-oidc";
                options.SignedOutCallbackPath = "/signout-callback-oidc";
                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("profile");
                options.Scope.Add(configuration["Auth:Pwa:Scope"] ?? "urn:bujo:unconfigured/journal.access");
                if (useManagedIdentity)
                    options.Events.OnAuthorizationCodeReceived = async context =>
                    {
                        var credential = context.HttpContext.RequestServices.GetRequiredKeyedService<TokenCredential>(OidcScheme);
                        var assertion = await credential.GetTokenAsync(
                            new TokenRequestContext(["api://AzureADTokenExchange/.default"]),
                            context.HttpContext.RequestAborted);
                        context.TokenEndpointRequest!.SetParameter("client_assertion_type",
                            "urn:ietf:params:oauth:client-assertion-type:jwt-bearer");
                        context.TokenEndpointRequest.SetParameter("client_assertion", assertion.Token);
                    };
                options.Events.OnTokenValidated = async context =>
                {
                    var sessions = context.HttpContext.RequestServices.GetRequiredService<BffSessions>();
                    var token = context.TokenEndpointResponse?.AccessToken;
                    var delegated = token is null ? null :
                        await sessions.ValidateTokenAsync(token, context.HttpContext.RequestAborted);
                    if (delegated is null || context.Principal is null ||
                        !BffSessions.SameIdentity(context.Principal, delegated))
                    {
                        context.Fail("Sign-in could not be validated.");
                        return;
                    }
                    var authorization = context.HttpContext.RequestServices.GetRequiredService<IAuthorizationService>();
                    if (!(await authorization.AuthorizeAsync(delegated, null,
                        new ApprovedPrincipalRequirement(configuration))).Succeeded)
                    {
                        context.HandleResponse();
                        context.Response.Redirect("/auth/denied");
                        return;
                    }
                    // Copy only validated delegated permission; never trust an ID-token scope.
                    var identity = (ClaimsIdentity)context.Principal.Identity!;
                    foreach (var claim in identity.FindAll("scp").ToArray())
                        identity.RemoveClaim(claim);
                    identity.AddClaim(new Claim("scp", delegated.FindFirstValue("scp")!));
                    identity.AddClaim(new Claim(BffSessions.SessionClaim, sessions.Create(token!, delegated)));
                };
                options.Events.OnRemoteFailure = context =>
                {
                    context.HandleResponse();
                    context.Response.Redirect("/auth/error");
                    return Task.CompletedTask;
                };
                options.Events.OnAccessDenied = context =>
                {
                    context.HandleResponse();
                    context.Response.Redirect("/auth/denied");
                    return Task.CompletedTask;
                };
            });
        services.AddAuthorization(options =>
            options.AddPolicy(UiPolicy, policy => policy
                .AddAuthenticationSchemes(CookieScheme)
                .RequireAuthenticatedUser()
                .AddRequirements(new ApprovedPrincipalRequirement(configuration), new BffSessionRequirement())));
    }
}

public sealed class BffSessionRequirement : IAuthorizationRequirement;

public sealed class BffSessionHandler(IBffSessionValidator sessions) : AuthorizationHandler<BffSessionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, BffSessionRequirement requirement)
    {
        if (await sessions.IsValidAsync(context.User))
            context.Succeed(requirement);
    }
}
