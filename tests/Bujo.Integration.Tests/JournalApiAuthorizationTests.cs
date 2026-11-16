using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Net;
using System.Net.Http.Json;
using Bujo.Domain;
using Bujo.Host;
using Bujo.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bujo.Integration.Tests;

public sealed class JournalApiAuthorizationTests : IClassFixture<JournalApiFactory>
{
    private readonly HttpClient _client;

    public JournalApiAuthorizationTests(JournalApiFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task DataEndpointRequiresAuthentication()
    {
        using var response = await _client.GetAsync("/api/journal");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedPrincipalOutsideAllowlistIsForbidden()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/journal");
        request.Headers.Add("Authorization", "Test deny");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ApprovedPrincipalCanAccessDataEndpoint()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/journal");
        request.Headers.Add("Authorization", "Test allow");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task EveryJournalDataEndpointRequiresAuthentication()
    {
        var id = Guid.NewGuid();
        var requests = new[]
        {
            new HttpRequestMessage(HttpMethod.Get, "/api/journal"),
            new HttpRequestMessage(HttpMethod.Post, "/api/lists")
            {
                Content = JsonContent.Create(new { name = "Custom", kind = JournalListKind.Custom })
            },
            new HttpRequestMessage(HttpMethod.Post, "/api/items")
            {
                Content = JsonContent.Create(new { name = "Item", plannedPeriod = new { kind = "unplanned" } })
            },
            new HttpRequestMessage(HttpMethod.Post, $"/api/items/{id}/move")
            {
                Content = JsonContent.Create(new { operationId = Guid.NewGuid(), expectedOccurrenceId = id, destinationListId = Guid.NewGuid() })
            },
            new HttpRequestMessage(HttpMethod.Post, $"/api/occurrences/{id}/transition")
            {
                Content = JsonContent.Create(new { action = OccurrenceAction.Done })
            },
            new HttpRequestMessage(HttpMethod.Post, $"/api/lists/{id}/close"),
            new HttpRequestMessage(HttpMethod.Post, $"/api/lists/{id}/reopen"),
            new HttpRequestMessage(HttpMethod.Put, $"/api/items/{id}")
            {
                Content = JsonContent.Create(new { name = "Edited" })
            },
            new HttpRequestMessage(HttpMethod.Put, $"/api/items/{id}/planned-period")
            {
                Content = JsonContent.Create(new { kind = "unplanned" })
            },
            new HttpRequestMessage(HttpMethod.Put, $"/api/lists/{id}/goal")
            {
                Content = JsonContent.Create(new { goalText = "Goal" })
            },
            new HttpRequestMessage(HttpMethod.Get, $"/api/occurrences/{id}/resolution")
        };

        foreach (var request in requests)
        {
            using (request)
            using (var response = await _client.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }
        }
    }

    [Fact]
    public async Task LivenessIsAnonymousButReadinessRequiresAuthentication()
    {
        using var live = await _client.GetAsync("/health/live");
        using var ready = await _client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, ready.StatusCode);
    }

    [Fact]
    public async Task DomainErrorsMapToValidationNotFoundAndConflictStatuses()
    {
        using var invalid = AuthenticatedJsonRequest(HttpMethod.Post, "/api/items", new { name = "" });
        using var invalidResponse = await _client.SendAsync(invalid);
        using var missing = AuthenticatedJsonRequest(
            HttpMethod.Put,
            $"/api/items/{Guid.NewGuid()}",
            new { name = "Missing" });
        using var missingResponse = await _client.SendAsync(missing);
        using var conflict = AuthenticatedJsonRequest(
            HttpMethod.Post,
            "/api/lists",
            new { name = "Duplicate Future Log", kind = JournalListKind.FutureLog });
        using var conflictResponse = await _client.SendAsync(conflict);

        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode);
    }

    private static HttpRequestMessage AuthenticatedJsonRequest(HttpMethod method, string uri, object body)
    {
        var request = new HttpRequestMessage(method, uri)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("Authorization", "Test allow");
        return request;
    }
}

public sealed class JournalApiFactory : WebApplicationFactory<JournalDatabaseHealthCheck>
{
    private static readonly string TenantId = "b3f84e9d-2dfb-4e8d-a1c3-91f7d8f59ca7";
    private static readonly string AllowedObjectId = "2b4d69e1-46a1-4bd0-9a9e-c47be64c6d72";

    public JournalApiFactory()
    {
        Environment.SetEnvironmentVariable("Auth__TenantId", TenantId);
        Environment.SetEnvironmentVariable("Auth__Audience", "journal-api");
        Environment.SetEnvironmentVariable("Auth__AllowedPrincipals", $"{TenantId}:{AllowedObjectId}");
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__journal",
            "Server=localhost;Database=journal;Integrated Security=true;TrustServerCertificate=true");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
                options.DefaultForbidScheme = TestAuthenticationHandler.SchemeName;
            }).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                TestAuthenticationHandler.SchemeName,
                _ => { });
            services.RemoveAll<IJournalRepository>();
            services.AddScoped<IJournalRepository>(_ => InMemoryJournalRepository.CreateWithFutureLog(
                Guid.Parse("00000000-0000-0000-0000-000000000101")));
        });
    }
}

public sealed class TestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    private const string TenantId = "b3f84e9d-2dfb-4e8d-a1c3-91f7d8f59ca7";
    private const string AllowedObjectId = "2b4d69e1-46a1-4bd0-9a9e-c47be64c6d72";
    private const string DeniedObjectId = "f7a16bf2-f207-428a-83e4-d398d01a4276";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (authorization is not ("Test allow" or "Test deny"))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var objectId = authorization == "Test allow" ? AllowedObjectId : DeniedObjectId;
        var identity = new ClaimsIdentity(
        [
            new Claim("tid", TenantId),
            new Claim("oid", objectId),
            new Claim("scp", "journal.access")
        ], SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
