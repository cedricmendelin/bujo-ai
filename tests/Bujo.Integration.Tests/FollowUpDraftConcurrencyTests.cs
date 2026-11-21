using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bujo.Domain;
using Bujo.Infrastructure.Persistence;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

namespace Bujo.Integration.Tests;

public sealed class FollowUpDraftConcurrencyTests
{
    [Fact]
    public async Task ApiAndMcpRequireAnExplicitExpectedDraftAndRejectStaleEdits()
    {
        var journal = InMemoryJournalRepository.CreateWithFutureLog(Guid.NewGuid());
        using var root = new JournalApiFactory();
        using var factory = root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IJournalRepository>();
            services.AddSingleton<IJournalRepository>(journal);
            services.RemoveAll<IFollowUpRepository>();
            services.AddSingleton<IFollowUpRepository>(new InMemoryFollowUpRepository(journal));
        }));
        using var first = factory.CreateClient();
        using var second = factory.CreateClient();
        first.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Test allow");
        second.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Test allow");
        using var capture = await first.PostAsJsonAsync("/api/followups", new
        {
            title = "Concurrent draft", condition = "if no reply", dueAtUtc = DateTimeOffset.UtcNow,
            timeZoneId = "Etc/UTC", source = 1
        });
        Assert.Equal(HttpStatusCode.OK, capture.StatusCode);
        var id = (await Body(capture)).GetProperty("id").GetGuid();
        var path = $"/api/followups/{id}";
        using var firstRead = await first.GetAsync(path);
        using var secondRead = await second.GetAsync(path);
        Assert.Equal(JsonValueKind.Null, (await Body(firstRead)).GetProperty("draft").ValueKind);
        Assert.Equal(JsonValueKind.Null, (await Body(secondRead)).GetProperty("draft").ValueKind);

        using var omitted = await first.PutAsJsonAsync($"{path}/draft", new { draft = "Must not save" });
        Assert.Equal(HttpStatusCode.BadRequest, omitted.StatusCode);
        using var explicitNull = await first.PutAsJsonAsync($"{path}/draft",
            new { draft = "First client", expectedDraft = (string?)null });
        Assert.Equal(HttpStatusCode.OK, explicitNull.StatusCode);
        using var stale = await second.PutAsJsonAsync($"{path}/draft",
            new { draft = "Stale client", expectedDraft = (string?)null });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("ConcurrencyConflict", (await Body(stale)).GetProperty("code").GetString());
        using var retained = await second.GetAsync(path);
        Assert.Equal("First client", (await Body(retained)).GetProperty("draft").GetString());

        using var clear = await first.PutAsJsonAsync($"{path}/draft",
            new { draft = (string?)null, expectedDraft = "First client" });
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);

        await using var firstTransport = Transport(first);
        await using var secondTransport = Transport(second);
        await using var firstMcp = await McpClient.CreateAsync(firstTransport);
        await using var secondMcp = await McpClient.CreateAsync(secondTransport);
        var schema = (await firstMcp.ListToolsAsync()).Single(tool => tool.Name == "set_followup_draft")
            .ProtocolTool.InputSchema;
        Assert.Contains("expectedDraft", schema.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        var args = new Dictionary<string, object?> { ["id"] = id };
        var mcpFirstRead = await firstMcp.CallToolAsync("get_followup", args);
        var mcpSecondRead = await secondMcp.CallToolAsync("get_followup", args);
        Assert.Equal(JsonValueKind.Null, mcpFirstRead.StructuredContent!.Value.GetProperty("draft").ValueKind);
        Assert.Equal(JsonValueKind.Null, mcpSecondRead.StructuredContent!.Value.GetProperty("draft").ValueKind);

        var mcpOmitted = await firstMcp.CallToolAsync("set_followup_draft", new Dictionary<string, object?>
        {
            ["id"] = id, ["draft"] = "Must not save"
        });
        Assert.True(mcpOmitted.IsError);
        var invalid = await firstMcp.CallToolAsync("set_followup_draft", new Dictionary<string, object?>
        {
            ["id"] = id, ["draft"] = "Must not save", ["expectedDraft"] = 42
        });
        Assert.True(invalid.IsError);
        var mcpExplicitNull = await firstMcp.CallToolAsync("set_followup_draft", new Dictionary<string, object?>
        {
            ["id"] = id, ["draft"] = "MCP first client", ["expectedDraft"] = null
        });
        Assert.False(mcpExplicitNull.IsError);
        var mcpStale = await secondMcp.CallToolAsync("set_followup_draft", new Dictionary<string, object?>
        {
            ["id"] = id, ["draft"] = "MCP stale client", ["expectedDraft"] = null
        });
        Assert.True(mcpStale.IsError);
        Assert.Equal("ConcurrencyConflict", mcpStale.StructuredContent!.Value.GetProperty("code").GetString());
        using var finalRead = await second.GetAsync(path);
        Assert.Equal("MCP first client", (await Body(finalRead)).GetProperty("draft").GetString());
        var mcpUpdate = await firstMcp.CallToolAsync("set_followup_draft", new Dictionary<string, object?>
        {
            ["id"] = id, ["draft"] = "MCP updated", ["expectedDraft"] = "MCP first client"
        });
        Assert.False(mcpUpdate.IsError);
        var mcpStaleString = await secondMcp.CallToolAsync("set_followup_draft", new Dictionary<string, object?>
        {
            ["id"] = id, ["draft"] = "Stale string", ["expectedDraft"] = "MCP first client"
        });
        Assert.True(mcpStaleString.IsError);
        Assert.Equal("ConcurrencyConflict", mcpStaleString.StructuredContent!.Value.GetProperty("code").GetString());
        using var updatedRead = await second.GetAsync(path);
        Assert.Equal("MCP updated", (await Body(updatedRead)).GetProperty("draft").GetString());
    }

    private static HttpClientTransport Transport(HttpClient client) => new(new HttpClientTransportOptions
    {
        Endpoint = new Uri("http://localhost/mcp"),
        TransportMode = HttpTransportMode.StreamableHttp
    }, client, NullLoggerFactory.Instance);

    private static async Task<JsonElement> Body(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}
