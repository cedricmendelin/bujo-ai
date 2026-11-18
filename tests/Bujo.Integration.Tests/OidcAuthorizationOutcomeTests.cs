using Bujo.Host.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Bujo.Integration.Tests;

public sealed class OidcAuthorizationOutcomeTests
{
    [Theory]
    [InlineData("approved", null)]
    [InlineData("denied", "/auth/denied")]
    [InlineData("invalid-token", "/auth/error")]
    [InlineData("mismatched-identity", "/auth/error")]
    public async Task ProductionOidcEventDistinguishesDenialFromSignInFailure(string condition, string? destination)
    {
        await using var factory = new BffTestFactory();
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        if (condition == "denied")
            services.GetRequiredService<IConfiguration>()["Auth:AllowedPrincipals"] = "";

        var options = services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(BffAuthentication.OidcScheme);
        var scheme = new AuthenticationScheme(BffAuthentication.OidcScheme, null, typeof(OpenIdConnectHandler));
        var http = new DefaultHttpContext { RequestServices = services };
        var principal = BffAuthorizationTests.Principal();
        if (condition == "mismatched-identity")
        {
            var identity = (System.Security.Claims.ClaimsIdentity)principal.Identity!;
            identity.RemoveClaim(identity.FindFirst("oid")!);
            identity.AddClaim(new("oid", "00000000-0000-0000-0000-000000000003"));
        }

        var context = new TokenValidatedContext(http, scheme, options, principal, new AuthenticationProperties())
        {
            TokenEndpointResponse = new OpenIdConnectMessage
            {
                AccessToken = condition == "invalid-token" ? "invalid-token" : factory.CreateAccessToken()
            }
        };
        await options.Events.TokenValidated(context);
        if (context.Result?.Failure is { } failure)
        {
            var remoteFailure = new RemoteFailureContext(http, scheme, options, failure);
            await options.Events.RemoteFailure(remoteFailure);
            Assert.True(remoteFailure.Result!.Handled);
        }

        if (destination is null)
        {
            Assert.Null(context.Result);
            Assert.False(http.Response.Headers.ContainsKey("Location"));
            Assert.True(await services.GetRequiredService<IBffSessionValidator>().IsValidAsync(principal));
        }
        else
        {
            Assert.Equal(StatusCodes.Status302Found, http.Response.StatusCode);
            Assert.Equal(destination, http.Response.Headers.Location.ToString());
            Assert.Null(principal.FindFirst(BffSessions.SessionClaim));
            Assert.False(await services.GetRequiredService<IBffSessionValidator>().IsValidAsync(principal));
        }
    }
}
