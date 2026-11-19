using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bujo.Application;
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
    public async Task ChatEndpointsRequireAuthenticationAndFeatureFlag()
    {
        using var aguiAnonymous = await _client.PostAsJsonAsync("/api/chat/agui", new
        {
            message = "show today"
        });
        using var speechAnonymous = await _client.PostAsync("/api/chat/speech-token", null);

        Assert.Equal(HttpStatusCode.Unauthorized, aguiAnonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, speechAnonymous.StatusCode);

        using var aguiDenied = new HttpRequestMessage(HttpMethod.Post, "/api/chat/agui")
        {
            Content = JsonContent.Create(new { message = "show today" })
        };
        aguiDenied.Headers.Add("Authorization", "Test deny");
        using var aguiDeniedResponse = await _client.SendAsync(aguiDenied);

        using var speechDenied = new HttpRequestMessage(HttpMethod.Post, "/api/chat/speech-token");
        speechDenied.Headers.Add("Authorization", "Test deny");
        using var speechDeniedResponse = await _client.SendAsync(speechDenied);

        Assert.Equal(HttpStatusCode.Forbidden, aguiDeniedResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, speechDeniedResponse.StatusCode);

        using var aguiOff = new HttpRequestMessage(HttpMethod.Post, "/api/chat/agui")
        {
            Content = JsonContent.Create(new { message = "show today" })
        };
        aguiOff.Headers.Add("Authorization", "Test allow");
        using var aguiOffResponse = await _client.SendAsync(aguiOff);

        using var speechOff = new HttpRequestMessage(HttpMethod.Post, "/api/chat/speech-token");
        speechOff.Headers.Add("Authorization", "Test allow");
        using var speechOffResponse = await _client.SendAsync(speechOff);

        Assert.Equal(HttpStatusCode.NotFound, aguiOffResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, speechOffResponse.StatusCode);
    }

    [Fact]
    public async Task EnabledChatUsesMockProviderAndSpeechTokenFailsClosedWhenUnconfigured()
    {
        using var factory = new JournalApiFactory();
        using var enabled = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Chat:Enabled", "true");
            builder.UseSetting("Chat:Provider", "Mock");
        });
        using var client = enabled.CreateClient();

        using var chatRequest = new HttpRequestMessage(HttpMethod.Post, "/api/chat/agui")
        {
            Content = JsonContent.Create(new
            {
                message = "show journal",
                timeZone = "Europe/Zurich",
                history = Array.Empty<object>(),
                threadId = "test-thread"
            })
        };
        chatRequest.Headers.Add("Authorization", "Test allow");
        using var chatResponse = await client.SendAsync(chatRequest);

        Assert.Equal(HttpStatusCode.OK, chatResponse.StatusCode);
        Assert.Equal("text/event-stream", chatResponse.Content.Headers.ContentType!.MediaType);
        var chatBody = await chatResponse.Content.ReadAsStringAsync();
        Assert.Contains("event: run_started", chatBody);
        Assert.Contains("event: text_message_content", chatBody);
        Assert.Contains("event: run_finished", chatBody);

        using var speechRequest = new HttpRequestMessage(HttpMethod.Post, "/api/chat/speech-token");
        speechRequest.Headers.Add("Authorization", "Test allow");
        using var speechResponse = await client.SendAsync(speechRequest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, speechResponse.StatusCode);
    }

    [Fact]
    public async Task SpeechAssetUsesEnUsAndHasSingleFinalSubmissionGuard()
    {
        using var response = await _client.GetAsync("/_content/Bujo.Web/js/speech.js");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var source = await response.Content.ReadAsStringAsync();
        Assert.Contains("recognizer.lang", source);
        Assert.Contains("\"en-US\"", source);
        Assert.Contains("finalSubmitted", source);
        Assert.Contains("finalSubmitted = true", source);
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
            new HttpRequestMessage(HttpMethod.Get, "/api/journal/export"),
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
            new HttpRequestMessage(HttpMethod.Get, $"/api/occurrences/{id}/resolution"),
            new HttpRequestMessage(HttpMethod.Get, $"/api/lists/{id}/review"),
            new HttpRequestMessage(HttpMethod.Post, "/api/review-proposals")
            {
                Content = JsonContent.Create(new { listId = id, expectedListVersion = 0, requestCloseList = false, changes = Array.Empty<object>() })
            },
            new HttpRequestMessage(HttpMethod.Get, $"/api/review-proposals/{id}"),
            new HttpRequestMessage(HttpMethod.Post, $"/api/review-proposals/{id}/cancel"),
            new HttpRequestMessage(HttpMethod.Post, $"/api/review-proposals/{id}/reject"),
            new HttpRequestMessage(HttpMethod.Post, $"/api/review-proposals/{id}/apply"),
            new HttpRequestMessage(HttpMethod.Get, "/api/daily-start"),
            new HttpRequestMessage(HttpMethod.Get, "/api/planning/upcoming")
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

    [Theory]
    [InlineData(2026, 48, HttpStatusCode.OK)]
    [InlineData(2026, 54, HttpStatusCode.BadRequest)]
    [InlineData(0, 1, HttpStatusCode.BadRequest)]
    public async Task WeeklyListValidatesAndPreservesTheRequestedIsoWeek(
        int year,
        int week,
        HttpStatusCode expectedStatus)
    {
        using var request = AuthenticatedJsonRequest(HttpMethod.Post, "/api/lists", new
        {
            name = "Weekly list",
            kind = JournalListKind.Weekly,
            weekKey = new { year, week }
        });
        using var response = await _client.SendAsync(request);
        Assert.Equal(expectedStatus, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (expectedStatus == HttpStatusCode.OK)
        {
            var weekKey = body.RootElement.GetProperty("weekKey");
            Assert.Equal(year, weekKey.GetProperty("year").GetInt32());
            Assert.Equal(week, weekKey.GetProperty("week").GetInt32());
        }
        else
        {
            Assert.Equal("InvalidInput", body.RootElement.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task ListCreationRejectsDuplicateNamesAndAllowsMultipleWeeklyListsForAWeek()
    {
        using var factory = new JournalApiFactory { ShareInMemoryRepository = true };
        using var client = factory.CreateClient();
        var suffix = Guid.NewGuid().ToString("N");
        var listName = $"Projects {suffix}";
        using var created = await client.SendAsync(AuthenticatedJsonRequest(
            HttpMethod.Post,
            "/api/lists",
            new { name = listName, kind = JournalListKind.Custom }));
        using var duplicateName = await client.SendAsync(AuthenticatedJsonRequest(
            HttpMethod.Post,
            "/api/lists",
            new { name = listName.ToUpperInvariant(), kind = JournalListKind.Custom }));
        using var duplicateNameBody = JsonDocument.Parse(await duplicateName.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, duplicateName.StatusCode);
        Assert.Equal("DuplicateListName", duplicateNameBody.RootElement.GetProperty("code").GetString());

        using var weekly = await client.SendAsync(AuthenticatedJsonRequest(
            HttpMethod.Post,
            "/api/lists",
            new { name = $"Week 48 {suffix}", kind = JournalListKind.Weekly, weekKey = new { year = 2026, week = 48 } }));
        using var anotherWeeklyList = await client.SendAsync(AuthenticatedJsonRequest(
            HttpMethod.Post,
            "/api/lists",
            new { name = $"Planning 48 {suffix}", kind = JournalListKind.Weekly, weekKey = new { year = 2026, week = 48 } }));

        Assert.Equal(HttpStatusCode.OK, weekly.StatusCode);
        Assert.Equal(HttpStatusCode.OK, anotherWeeklyList.StatusCode);
    }

    [Fact]
    public async Task ListReviewShowsUnfinishedOccurrencesAndBlocksUnapprovedClosure()
    {
        using var factory = new JournalApiFactory { ShareInMemoryRepository = true };
        using var client = factory.CreateClient();
        using var listResponse = await client.SendAsync(AuthenticatedJsonRequest(
            HttpMethod.Post,
            "/api/lists",
            new { name = "Review API list", kind = JournalListKind.Custom }));
        using var listBody = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        var listId = listBody.RootElement.GetProperty("id").GetGuid();
        using var captureResponse = await client.SendAsync(AuthenticatedJsonRequest(
            HttpMethod.Post,
            "/api/items",
            new { name = "Review API item", listId }));
        using var captureBody = JsonDocument.Parse(await captureResponse.Content.ReadAsStringAsync());
        var item = captureBody.RootElement.GetProperty("item");
        var occurrence = captureBody.RootElement.GetProperty("occurrence");
        using var reviewResponse = await client.SendAsync(AuthenticatedGet($"/api/lists/{listId}/review"));
        using var reviewBody = JsonDocument.Parse(await reviewResponse.Content.ReadAsStringAsync());
        using var closeResponse = await client.SendAsync(
            AuthenticatedJsonRequest(HttpMethod.Post, $"/api/lists/{listId}/close", new { }));
        using var closeBody = JsonDocument.Parse(await closeResponse.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, reviewResponse.StatusCode);
        Assert.Single(reviewBody.RootElement.GetProperty("unfinished").EnumerateArray());
        Assert.Empty(reviewBody.RootElement.GetProperty("moved").EnumerateArray());
        Assert.Equal(HttpStatusCode.Conflict, closeResponse.StatusCode);
        Assert.Equal(nameof(DomainErrorCode.UnresolvedOccurrences), closeBody.RootElement.GetProperty("code").GetString());

        using var proposalResponse = await client.SendAsync(AuthenticatedJsonRequest(
            HttpMethod.Post,
            "/api/review-proposals",
            new
            {
                listId,
                expectedListVersion = 1,
                requestCloseList = true,
                changes = new[]
                {
                    new
                    {
                        itemId = item.GetProperty("id").GetGuid(),
                        occurrenceId = occurrence.GetProperty("id").GetGuid(),
                        expectedItemVersion = item.GetProperty("version").GetInt64(),
                        action = ReviewDisposition.Done
                    }
                }
            }));
        using var proposalBody = JsonDocument.Parse(await proposalResponse.Content.ReadAsStringAsync());
        var proposalId = proposalBody.RootElement.GetProperty("id").GetGuid();
        using var unapprovedApply = await client.SendAsync(
            AuthenticatedJsonRequest(HttpMethod.Post, $"/api/review-proposals/{proposalId}/apply", new { }));
        using var noApiApproval = await client.SendAsync(
            AuthenticatedJsonRequest(HttpMethod.Post, $"/api/review-proposals/{proposalId}/approve", new { digest = proposalBody.RootElement.GetProperty("digest").GetString() }));
        using var afterApply = await client.SendAsync(AuthenticatedGet($"/api/lists/{listId}/review"));

        Assert.Equal(HttpStatusCode.OK, proposalResponse.StatusCode);
        Assert.Equal(64, proposalBody.RootElement.GetProperty("digest").GetString()!.Length);
        Assert.Equal(HttpStatusCode.Conflict, unapprovedApply.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, noApiApproval.StatusCode);
        using (var afterApplyBody = JsonDocument.Parse(await afterApply.Content.ReadAsStringAsync()))
        {
            Assert.Single(afterApplyBody.RootElement.GetProperty("unfinished").EnumerateArray());
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var reviewService = scope.ServiceProvider.GetRequiredService<ReviewApplicationService>();
        var actor = new ReviewActor(
            "b3f84e9d-2dfb-4e8d-a1c3-91f7d8f59ca7:2b4d69e1-46a1-4bd0-9a9e-c47be64c6d72",
            ReviewProposalSource.Pwa,
            Interactive: true);
        var approved = reviewService.ApproveProposal(
            proposalId,
            proposalBody.RootElement.GetProperty("digest").GetString()!,
            actor);
        using var applied = await client.SendAsync(
            AuthenticatedJsonRequest(HttpMethod.Post, $"/api/review-proposals/{proposalId}/apply", new { }));
        using var completedReview = await client.SendAsync(AuthenticatedGet($"/api/lists/{listId}/review"));
        using var completedReviewBody = JsonDocument.Parse(await completedReview.Content.ReadAsStringAsync());

        Assert.True(approved.IsSuccess, approved.Error?.Message);
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.Empty(completedReviewBody.RootElement.GetProperty("unfinished").EnumerateArray());
        Assert.Single(completedReviewBody.RootElement.GetProperty("resolved").EnumerateArray());
    }

    [Fact]
    public async Task StaleReviewApplyReturnsPerChangeConflictDetails()
    {
        using var factory = new JournalApiFactory { ShareInMemoryRepository = true };
        using var client = factory.CreateClient();
        using var listResponse = await client.SendAsync(AuthenticatedJsonRequest(
            HttpMethod.Post,
            "/api/lists",
            new { name = "Stale review API list", kind = JournalListKind.Custom }));
        using var listBody = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        var listId = listBody.RootElement.GetProperty("id").GetGuid();
        using var captureResponse = await client.SendAsync(AuthenticatedJsonRequest(
            HttpMethod.Post,
            "/api/items",
            new { name = "Stale review API item", listId }));
        using var captureBody = JsonDocument.Parse(await captureResponse.Content.ReadAsStringAsync());
        var item = captureBody.RootElement.GetProperty("item");
        var occurrence = captureBody.RootElement.GetProperty("occurrence");
        using var reviewResponse = await client.SendAsync(AuthenticatedGet($"/api/lists/{listId}/review"));
        using var reviewBody = JsonDocument.Parse(await reviewResponse.Content.ReadAsStringAsync());
        using var proposalResponse = await client.SendAsync(AuthenticatedJsonRequest(
            HttpMethod.Post,
            "/api/review-proposals",
            new
            {
                listId,
                expectedListVersion = reviewBody.RootElement.GetProperty("list").GetProperty("version").GetInt64(),
                requestCloseList = true,
                changes = new[]
                {
                    new
                    {
                        itemId = item.GetProperty("id").GetGuid(),
                        occurrenceId = occurrence.GetProperty("id").GetGuid(),
                        expectedItemVersion = item.GetProperty("version").GetInt64(),
                        action = ReviewDisposition.Done
                    }
                }
            }));
        using var proposalBody = JsonDocument.Parse(await proposalResponse.Content.ReadAsStringAsync());
        var proposalId = proposalBody.RootElement.GetProperty("id").GetGuid();
        var digest = proposalBody.RootElement.GetProperty("digest").GetString()!;
        await using var scope = factory.Services.CreateAsyncScope();
        var reviewService = scope.ServiceProvider.GetRequiredService<ReviewApplicationService>();
        var actor = new ReviewActor(
            "b3f84e9d-2dfb-4e8d-a1c3-91f7d8f59ca7:2b4d69e1-46a1-4bd0-9a9e-c47be64c6d72",
            ReviewProposalSource.Pwa,
            Interactive: true);
        Assert.True(reviewService.ApproveProposal(proposalId, digest, actor).IsSuccess);
        using var concurrentCapture = await client.SendAsync(AuthenticatedJsonRequest(
            HttpMethod.Post,
            "/api/items",
            new { name = "Concurrent API item", listId }));
        Assert.Equal(HttpStatusCode.OK, concurrentCapture.StatusCode);

        using var applyResponse = await client.SendAsync(AuthenticatedJsonRequest(
            HttpMethod.Post,
            $"/api/review-proposals/{proposalId}/apply",
            new { }));
        using var applyBody = JsonDocument.Parse(await applyResponse.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Conflict, applyResponse.StatusCode);
        Assert.Contains("expected version", applyBody.RootElement.GetProperty("detail").GetString());
        Assert.Contains("Unresolved occurrence", applyBody.RootElement.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task PlannedQueryEndpointsValidateTimeZoneAndReturnStructuredResults()
    {
        using var dailyRequest = new HttpRequestMessage(HttpMethod.Get, "/api/daily-start?timeZone=Europe%2FZurich");
        dailyRequest.Headers.Add("Authorization", "Test allow");
        using var dailyResponse = await _client.SendAsync(dailyRequest);
        using var dailyBody = JsonDocument.Parse(await dailyResponse.Content.ReadAsStringAsync());
        using var upcomingRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/planning/upcoming?timeZone=Europe%2FZurich&futureLog=true&limit=5");
        upcomingRequest.Headers.Add("Authorization", "Test allow");
        using var upcomingResponse = await _client.SendAsync(upcomingRequest);
        using var missingZone = AuthenticatedGet("/api/daily-start");
        using var missingZoneResponse = await _client.SendAsync(missingZone);
        using var invalidZone = AuthenticatedGet("/api/planning/upcoming?timeZone=Not%2FA_Time_Zone");
        using var invalidZoneResponse = await _client.SendAsync(invalidZone);

        Assert.Equal(HttpStatusCode.OK, dailyResponse.StatusCode);
        Assert.Equal(
            new[] { "Overdue", "Today", "ThisWeek" },
            dailyBody.RootElement.GetProperty("groups").EnumerateArray()
                .Select(group => group.GetProperty("code").GetString()));
        Assert.Equal(HttpStatusCode.OK, upcomingResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, missingZoneResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidZoneResponse.StatusCode);
    }

    [Fact]
    public async Task PlannedQueryRepositoryFailureReturnsAnExplicitServerError()
    {
        using var factory = new JournalApiFactory { FailQueries = true };
        using var client = factory.CreateClient();
        using var request = AuthenticatedGet("/api/daily-start?timeZone=Europe%2FZurich");

        using var response = await client.SendAsync(request);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("QueryFailed", body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task JournalAndExportRepositoryFailuresReturnExplicitServerErrors()
    {
        using var factory = new JournalApiFactory { FailQueries = true };
        var capturedLogs = new List<string>();
        factory.CapturedLogs = capturedLogs;
        using var client = factory.CreateClient();

        foreach (var path in new[] { "/api/journal", "/api/journal/export" })
        {
            using var request = AuthenticatedGet(path);
            using var response = await client.SendAsync(request);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal("QueryFailed", body.RootElement.GetProperty("code").GetString());
        }

        Assert.DoesNotContain(capturedLogs, message =>
            message.Contains("fake-access-token", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("private-entry", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExportEndpointReturnsVersionedNoStoreJournalData()
    {
        using var factory = new JournalApiFactory { ShareInMemoryRepository = true };
        using var client = factory.CreateClient();
        using var createList = await client.SendAsync(AuthenticatedJsonRequest(
            HttpMethod.Post,
            "/api/lists",
            new { name = "Export list", kind = JournalListKind.Custom, goalText = "Keep the goal" }));
        using var listJson = JsonDocument.Parse(await createList.Content.ReadAsStringAsync());
        var listId = listJson.RootElement.GetProperty("id").GetGuid();
        using var capture = await client.SendAsync(AuthenticatedJsonRequest(
            HttpMethod.Post,
            "/api/items",
            new
            {
                name = "Export item",
                description = "Synthetic export test",
                tags = new[] { "personal" },
                listId,
                plannedPeriod = new { kind = "week", weekYear = 2026, week = 48 }
            }));
        using var captureJson = JsonDocument.Parse(await capture.Content.ReadAsStringAsync());
        var itemId = captureJson.RootElement.GetProperty("item").GetProperty("id").GetGuid();

        using var response = await client.SendAsync(AuthenticatedGet("/api/journal/export"));
        using var export = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = export.RootElement;
        var exportedItem = root.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("id").GetGuid() == itemId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("Keep the goal", root.GetProperty("lists").EnumerateArray()
            .Single(list => list.GetProperty("id").GetGuid() == listId)
            .GetProperty("goalText").GetString());
        Assert.Equal("Export item", exportedItem.GetProperty("name").GetString());
        Assert.Equal("Week", exportedItem.GetProperty("plannedPeriodKind").GetString());
        Assert.Equal("personal", exportedItem.GetProperty("tags")[0].GetString());
        Assert.Empty(root.GetProperty("knowledgeEntries").EnumerateArray());
        Assert.DoesNotContain("token", root.GetRawText(), StringComparison.OrdinalIgnoreCase);
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

    private static HttpRequestMessage AuthenticatedGet(string uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("Authorization", "Test allow");
        return request;
    }
}

public sealed class JournalApiFactory : WebApplicationFactory<JournalDatabaseHealthCheck>
{
    private readonly InMemoryJournalRepository _sharedRepository =
        InMemoryJournalRepository.CreateWithFutureLog(Guid.Parse("00000000-0000-0000-0000-000000000101"));

    public bool FailQueries { get; set; }

    public bool ShareInMemoryRepository { get; set; }

    public DateTimeOffset? FixedUtcNow { get; set; }

    public ICollection<string>? CapturedLogs { get; set; }

    private static readonly string TenantId = "b3f84e9d-2dfb-4e8d-a1c3-91f7d8f59ca7";
    private static readonly string AllowedObjectId = "2b4d69e1-46a1-4bd0-9a9e-c47be64c6d72";

    public JournalApiFactory()
    {
        Environment.SetEnvironmentVariable("Auth__TenantId", TenantId);
        Environment.SetEnvironmentVariable("Auth__Audience", "journal-api");
        Environment.SetEnvironmentVariable("Auth__AllowedPrincipals", AllowedObjectId);
        Environment.SetEnvironmentVariable("Mcp__PublicBaseUrl", "http://localhost");
        Environment.SetEnvironmentVariable("Mcp__AllowedOrigins__0", "https://approved.example");
        Environment.SetEnvironmentVariable("Chat__Enabled", "false");
        Environment.SetEnvironmentVariable("Chat__Provider", "Mock");
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__journal",
            "Server=localhost;Database=journal;Integrated Security=true;TrustServerCertificate=true");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            if (CapturedLogs is { } capturedLogs)
            {
                services.AddLogging(logging =>
                    logging.AddProvider(new CapturingLoggerProvider(capturedLogs)));
            }

            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
                options.DefaultForbidScheme = TestAuthenticationHandler.SchemeName;
            }).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                TestAuthenticationHandler.SchemeName,
                _ => { });
            services.PostConfigure<AuthenticationOptions>(options =>
                options.Schemes.Single(scheme => scheme.Name == "Bearer").HandlerType = typeof(TestAuthenticationHandler));
            services.RemoveAll<IJournalRepository>();
            if (FailQueries)
            {
                services.AddScoped<IJournalRepository, FailingReadJournalRepository>();
            }
            else if (ShareInMemoryRepository)
            {
                services.AddSingleton<IJournalRepository>(_sharedRepository);
            }
            else
            {
                services.AddScoped<IJournalRepository>(_ => InMemoryJournalRepository.CreateWithFutureLog(
                    Guid.Parse("00000000-0000-0000-0000-000000000101")));
            }

            if (FixedUtcNow is { } fixedUtcNow)
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FixedTimeProvider(fixedUtcNow));
            }
        });
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}

internal sealed class FailingReadJournalRepository : IJournalRepository
{
    public JournalSnapshot GetSnapshot() =>
        throw new InvalidOperationException("Authorization: fake-access-token; journal text: private-entry");

    public DomainResult<JournalListSnapshot> CreateList(CreateListCommand command) => throw new NotSupportedException();

    public DomainResult<CaptureResult> Capture(CaptureCommand command) => throw new NotSupportedException();

    public DomainResult<MoveResult> Move(MoveCommand command) => throw new NotSupportedException();

    public DomainResult<OccurrenceSnapshot> Transition(TransitionCommand command) => throw new NotSupportedException();

    public DomainResult<JournalListSnapshot> CloseList(ListVersionCommand command) => throw new NotSupportedException();

    public DomainResult<JournalListSnapshot> ReopenList(ListVersionCommand command) => throw new NotSupportedException();

    public DomainResult<ItemSnapshot> EditItem(EditItemCommand command) => throw new NotSupportedException();

    public DomainResult<ItemSnapshot> ChangePlannedPeriod(ChangePeriodCommand command) => throw new NotSupportedException();

    public DomainResult<JournalListSnapshot> SetListGoal(SetListGoalCommand command) => throw new NotSupportedException();

    public DomainResult<ReviewProposalSnapshot> CreateReviewProposal(CreateReviewProposalCommand command) => throw new NotSupportedException();

    public DomainResult<ReviewProposalSnapshot> GetReviewProposal(Guid id) => throw new NotSupportedException();

    public DomainResult<ReviewProposalSnapshot> SetReviewProposalStatus(ReviewProposalStatusCommand command) => throw new NotSupportedException();

    public DomainResult<ReviewProposalSnapshot> ApplyReviewProposal(ApplyReviewProposalCommand command) => throw new NotSupportedException();
}

internal sealed class CapturingLoggerProvider(ICollection<string> messages) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new CapturingLogger(messages);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(ICollection<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            messages.Add(formatter(state, exception));
            if (exception is not null)
            {
                messages.Add(exception.ToString());
            }
        }
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
