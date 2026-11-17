using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bujo.Domain;
using Bujo.Host;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Bujo.Integration.Tests.Mcp;

public sealed class JournalMcpContractTests : IClassFixture<JournalApiFactory>
{
    private static readonly IReadOnlyDictionary<string, string[]> ApiToolMappings =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["GET /api/journal"] = ["get_journal"],
            ["GET /api/daily-start"] = ["get_daily_start"],
            ["GET /api/planning/upcoming"] = ["get_upcoming"],
            ["POST /api/lists"] = ["create_list"],
            ["POST /api/items"] = ["capture_item"],
            ["POST /api/items/{itemId:guid}/move"] = ["move_item"],
            ["POST /api/occurrences/{occurrenceId:guid}/transition"] =
                ["complete_occurrence", "abandon_occurrence", "reopen_occurrence"],
            ["PUT /api/items/{itemId:guid}"] = ["edit_item"],
            ["PUT /api/items/{itemId:guid}/planned-period"] = ["set_planned_period"],
            ["PUT /api/lists/{listId:guid}/goal"] = ["set_list_goal"],
            ["GET /api/occurrences/{occurrenceId:guid}/resolution"] = ["resolve_occurrence"]
        };

    private static readonly IReadOnlyDictionary<string, string> ApiParityExceptions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["POST /api/lists/{listId:guid}/close"] =
                "Deliberately excluded because closing can disposition multiple items; no proposal approval flow exists.",
            ["POST /api/lists/{listId:guid}/reopen"] =
                "Deliberately excluded because list reopening accompanies bulk closure workflows; no proposal approval flow exists."
        };

    private static readonly string[] ExpectedTools =
    [
        "get_journal",
        "get_daily_start",
        "get_upcoming",
        "find_lists",
        "capture_item",
        "create_list",
        "resolve_occurrence",
        "edit_item",
        "set_planned_period",
        "set_list_goal",
        "move_item",
        "complete_occurrence",
        "abandon_occurrence",
        "reopen_occurrence"
    ];

    private readonly JournalApiFactory _factory;

    public JournalMcpContractTests(JournalApiFactory factory)
    {
        _factory = factory;
        _factory.ShareInMemoryRepository = true;
        _factory.FixedUtcNow = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    }

    [Fact]
    public async Task McpEndpointRequiresApprovedPrincipalAndPublishesProtectedResourceMetadata()
    {
        using var client = _factory.CreateClient();
        using var metadata = await client.GetAsync("/.well-known/oauth-protected-resource/mcp");
        using var metadataJson = JsonDocument.Parse(await metadata.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
        Assert.Equal("http://localhost/mcp", metadataJson.RootElement.GetProperty("resource").GetString());
        Assert.Contains(
            "https://login.microsoftonline.com/b3f84e9d-2dfb-4e8d-a1c3-91f7d8f59ca7/v2.0",
            metadataJson.RootElement.GetProperty("authorization_servers").EnumerateArray()
                .Select(value => value.GetString()));
        Assert.Contains(
            "http://localhost/mcp/journal.access",
            metadataJson.RootElement.GetProperty("scopes_supported").EnumerateArray()
                .Select(value => value.GetString()));

        var unauthenticatedRequests = new[]
        {
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""",
            """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""",
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_journal","arguments":{}}}""",
            """{"jsonrpc":"2.0","id":1,"method":"prompts/list","params":{}}""",
            """{"jsonrpc":"2.0","id":1,"method":"resources/list","params":{}}"""
        };
        foreach (var requestBody in unauthenticatedRequests)
        {
            using var anonymous = await client.SendAsync(McpPost(requestBody));
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.Contains(
                "resource_metadata=\"http://localhost/.well-known/oauth-protected-resource/mcp\"",
                string.Join(", ", anonymous.Headers.WwwAuthenticate));
        }

        using var denied = McpPost("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""");
        denied.Headers.Add("Authorization", "Test deny");
        using var deniedResponse = await client.SendAsync(denied);
        Assert.Equal(HttpStatusCode.Forbidden, deniedResponse.StatusCode);

        var mcpEndpoints = GetEndpoints(_factory.Services)
            .Where(endpoint => endpoint.DisplayName?.Contains("MCP Streamable HTTP", StringComparison.Ordinal) == true)
            .ToArray();
        Assert.Single(mcpEndpoints);
        Assert.Equal(["POST"], mcpEndpoints[0].Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods);
        Assert.NotNull(mcpEndpoints[0].Metadata.GetMetadata<IAuthorizeData>());
        Assert.Null(mcpEndpoints[0].Metadata.GetMetadata<IAllowAnonymous>());
    }

    [Fact]
    public async Task InvalidOriginIsRejectedBeforeCallingMcpTools()
    {
        using var factory = new JournalApiFactory { FailQueries = true };
        using var client = factory.CreateClient();
        using var request = McpPost(
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_journal","arguments":{}}}""");
        request.Headers.Add("Authorization", "Test allow");
        request.Headers.Add("Origin", "https://unapproved.example");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ToolsExposeSharedJournalOperationsAndDailyQueryParity()
    {
        using var httpClient = _factory.CreateClient();
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Test allow");
        await using var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp
            },
            httpClient,
            NullLoggerFactory.Instance);
        await using var mcp = await McpClient.CreateAsync(transport);

        var tools = await mcp.ListToolsAsync();
        Assert.Equal(ExpectedTools.Order(), tools.Select(tool => tool.Name).Order());
        Assert.DoesNotContain(tools, tool => tool.Name is "close_list" or "reopen_list");
        var dailyStartSchema = tools.Single(tool => tool.Name == "get_daily_start").ProtocolTool.InputSchema;
        Assert.Equal("string", dailyStartSchema.GetProperty("properties").GetProperty("timeZone").GetProperty("type").GetString());
        Assert.Contains(
            "timeZone",
            dailyStartSchema.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        var moveSchema = tools.Single(tool => tool.Name == "move_item").ProtocolTool.InputSchema;
        Assert.Equal(
            "uuid",
            moveSchema.GetProperty("properties").GetProperty("operationId").GetProperty("format").GetString());
        Assert.Equal(
            ["capture_item", "daily_start", "explicit_edit"],
            (await mcp.ListPromptsAsync()).Select(prompt => prompt.Name).Order());
        Assert.Contains(
            (await mcp.ListResourcesAsync()),
            resource => resource.Uri.ToString() == "bujo://journal/capabilities");

        var capture = await mcp.CallToolAsync("capture_item", new Dictionary<string, object?>
        {
            ["name"] = "Review MCP retry behavior",
            ["plannedPeriod"] = new { kind = "day", day = "2026-11-18" }
        });
        Assert.False(capture.IsError);
        var capturedItem = capture.StructuredContent!.Value.GetProperty("item");
        var itemId = capturedItem.GetProperty("id").GetGuid();
        var sourceOccurrenceId = capturedItem.GetProperty("currentOccurrenceId").GetGuid();

        var edited = await mcp.CallToolAsync("edit_item", new Dictionary<string, object?>
        {
            ["itemId"] = itemId,
            ["name"] = "Review MCP move retries",
            ["description"] = "Exercise the operation ID contract.",
            ["tags"] = new[] { "MCP" }
        });
        Assert.False(edited.IsError);
        var snapshot = await mcp.CallToolAsync("get_journal", new Dictionary<string, object?>());
        Assert.False(snapshot.IsError);
        Assert.Equal(
            "Review MCP move retries",
            snapshot.StructuredContent!.Value.GetProperty("items").EnumerateArray()
                .Single(item => item.GetProperty("id").GetGuid() == itemId)
                .GetProperty("name").GetString());

        var destination = await mcp.CallToolAsync("create_list", new Dictionary<string, object?>
        {
            ["name"] = "MCP destination",
            ["kind"] = JournalListKind.Custom
        });
        Assert.False(destination.IsError);
        var destinationId = destination.StructuredContent!.Value.GetProperty("id").GetGuid();
        var goal = await mcp.CallToolAsync("set_list_goal", new Dictionary<string, object?>
        {
            ["listId"] = destinationId,
            ["goalText"] = "Use explicit moves"
        });
        Assert.False(goal.IsError);
        var plannedPeriod = await mcp.CallToolAsync("set_planned_period", new Dictionary<string, object?>
        {
            ["itemId"] = itemId,
            ["plannedPeriod"] = new { kind = "day", day = "2026-11-19" }
        });
        Assert.False(plannedPeriod.IsError);

        foreach (var listName in new[] { "MCP alpha", "MCP beta" })
        {
            var list = await mcp.CallToolAsync("create_list", new Dictionary<string, object?>
            {
                ["name"] = listName,
                ["kind"] = JournalListKind.Custom
            });
            Assert.False(list.IsError);
        }

        var ambiguous = await mcp.CallToolAsync(
            "find_lists",
            new Dictionary<string, object?> { ["name"] = "MCP" });
        Assert.True(ambiguous.IsError);
        Assert.Equal("AmbiguousList", GetErrorCode(ambiguous));
        Assert.True(ambiguous.StructuredContent!.Value.GetProperty("candidates").GetArrayLength() >= 3);

        var unknown = await mcp.CallToolAsync(
            "find_lists",
            new Dictionary<string, object?> { ["name"] = "No matching list" });
        Assert.True(unknown.IsError);
        Assert.Equal("NotFound", GetErrorCode(unknown));
        Assert.Equal(0, unknown.StructuredContent!.Value.GetProperty("candidates").GetArrayLength());

        var relativePeriod = await mcp.CallToolAsync("capture_item", new Dictionary<string, object?>
        {
            ["name"] = "Invalid relative date",
            ["plannedPeriod"] = new { kind = "day", day = "today" }
        });
        Assert.True(relativePeriod.IsError);
        Assert.Equal("InvalidInput", GetErrorCode(relativePeriod));

        var operationId = Guid.NewGuid();
        var moveArguments = new Dictionary<string, object?>
        {
            ["operationId"] = operationId,
            ["itemId"] = itemId,
            ["expectedOccurrenceId"] = sourceOccurrenceId,
            ["destinationListId"] = destinationId
        };
        var moved = await mcp.CallToolAsync("move_item", moveArguments);
        Assert.False(moved.IsError);
        var movedOccurrenceId = moved.StructuredContent!.Value
            .GetProperty("resolution").GetProperty("currentOccurrence").GetProperty("id").GetGuid();

        var retriedMove = await mcp.CallToolAsync("move_item", moveArguments);
        Assert.False(retriedMove.IsError);
        Assert.Equal(
            movedOccurrenceId,
            retriedMove.StructuredContent!.Value.GetProperty("resolution")
                .GetProperty("currentOccurrence").GetProperty("id").GetGuid());

        var staleMove = await mcp.CallToolAsync("move_item", new Dictionary<string, object?>
        {
            ["operationId"] = Guid.NewGuid(),
            ["itemId"] = itemId,
            ["expectedOccurrenceId"] = sourceOccurrenceId,
            ["destinationListId"] = destinationId
        });
        Assert.True(staleMove.IsError);
        Assert.Contains(GetErrorCode(staleMove), new[] { "StaleOccurrence", "ConcurrencyConflict" });

        var resolution = await mcp.CallToolAsync(
            "resolve_occurrence",
            new Dictionary<string, object?> { ["occurrenceId"] = sourceOccurrenceId });
        Assert.False(resolution.IsError);
        var resolvedChain = resolution.StructuredContent!.Value.GetProperty("chain");
        Assert.Equal(2, resolvedChain.GetArrayLength());
        Assert.Equal(
            movedOccurrenceId,
            resolution.StructuredContent.Value.GetProperty("currentOccurrence").GetProperty("id").GetGuid());
        var history = await mcp.CallToolAsync("get_journal", new Dictionary<string, object?>());
        Assert.Equal(
            1,
            history.StructuredContent!.Value.GetProperty("moveHistory").GetArrayLength());

        foreach (var action in new[] { "complete_occurrence", "reopen_occurrence", "abandon_occurrence", "reopen_occurrence" })
        {
            var transition = await mcp.CallToolAsync(action, new Dictionary<string, object?>
            {
                ["occurrenceId"] = movedOccurrenceId
            });
            Assert.False(transition.IsError);
        }

        var dailyStart = await mcp.CallToolAsync(
            "get_daily_start",
            new Dictionary<string, object?> { ["timeZone"] = "Europe/Zurich" });
        Assert.False(dailyStart.IsError);
        using var apiClient = _factory.CreateClient();
        using var apiDailyRequest = new HttpRequestMessage(HttpMethod.Get, "/api/daily-start?timeZone=Europe%2FZurich");
        apiDailyRequest.Headers.Add("Authorization", "Test allow");
        using var apiDailyResponse = await apiClient.SendAsync(apiDailyRequest);
        using var apiDailyJson = JsonDocument.Parse(await apiDailyResponse.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, apiDailyResponse.StatusCode);
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(apiDailyJson.RootElement.GetRawText()),
            JsonNode.Parse(dailyStart.StructuredContent!.Value.GetRawText())));

        var upcoming = await mcp.CallToolAsync(
            "get_upcoming",
            new Dictionary<string, object?> { ["timeZone"] = "Europe/Zurich" });
        Assert.False(upcoming.IsError);
        using var apiUpcomingRequest = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/planning/upcoming?timeZone=Europe%2FZurich");
        apiUpcomingRequest.Headers.Add("Authorization", "Test allow");
        using var apiUpcomingResponse = await apiClient.SendAsync(apiUpcomingRequest);
        using var apiUpcomingJson = JsonDocument.Parse(await apiUpcomingResponse.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, apiUpcomingResponse.StatusCode);
        var apiUpcoming = apiUpcomingJson.RootElement.GetRawText();
        var mcpUpcoming = upcoming.StructuredContent!.Value.GetRawText();
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(apiUpcoming), JsonNode.Parse(mcpUpcoming)),
            $"API: {apiUpcoming}{Environment.NewLine}MCP: {mcpUpcoming}");

        var missingTimeZone = await mcp.CallToolAsync(
            "get_daily_start",
            new Dictionary<string, object?> { ["timeZone"] = "" });
        Assert.True(missingTimeZone.IsError);
        Assert.Equal("InvalidTimeZone", GetErrorCode(missingTimeZone));
    }

    [Fact]
    public async Task ApiEndpointsHaveMcpToolsOrExplicitDocumentedExceptions()
    {
        using var httpClient = _factory.CreateClient();
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Test allow");
        await using var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp
            },
            httpClient,
            NullLoggerFactory.Instance);
        await using var mcp = await McpClient.CreateAsync(transport);
        var toolNames = (await mcp.ListToolsAsync()).Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);

        var apiOperations = GetEndpoints(_factory.Services)
            .SelectMany(endpoint =>
            {
                var route = Normalize(endpoint.RoutePattern.RawText);
                var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [];
                return methods.Select(method => $"{method} {route}");
            })
            .Where(operation => operation.Contains(" /api/", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        var missingParity = apiOperations
            .Where(operation => !ApiToolMappings.ContainsKey(operation) && !ApiParityExceptions.ContainsKey(operation))
            .ToArray();
        Assert.Empty(missingParity);
        Assert.All(ApiParityExceptions.Keys, exception => Assert.Contains(exception, apiOperations));
        Assert.All(ApiToolMappings.Keys, mappedOperation => Assert.Contains(mappedOperation, apiOperations));
        Assert.All(ApiToolMappings.Values.SelectMany(names => names), tool => Assert.Contains(tool, toolNames));
    }

    private static HttpRequestMessage McpPost(string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(body)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return request;
    }

    private static string GetErrorCode(CallToolResult result) =>
        result.StructuredContent!.Value.GetProperty("code").GetString()!;

    private static IReadOnlyList<RouteEndpoint> GetEndpoints(IServiceProvider services) =>
        services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();

    private static string Normalize(string? route) =>
        $"/{(route ?? string.Empty).TrimStart('/')}";
}
