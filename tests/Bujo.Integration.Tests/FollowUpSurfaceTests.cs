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

public sealed class FollowUpSurfaceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    private const string Id = "00000000-0000-0000-0000-000000000001";

    [Fact]
    public async Task EveryApiOperationAndMcpRequireApprovedPrincipal()
    {
        using var factory = new JournalApiFactory();
        using var client = factory.CreateClient();
        var routes = new (HttpMethod Method, string Route)[]
        {
            (HttpMethod.Post, "/api/followups"),
            (HttpMethod.Get, "/api/followups?due=true"),
            (HttpMethod.Get, $"/api/followups/{Id}"),
            (HttpMethod.Post, $"/api/followups/{Id}/evidence"),
            (HttpMethod.Post, $"/api/followups/{Id}/dismiss"),
            (HttpMethod.Post, $"/api/followups/{Id}/defer"),
            (HttpMethod.Put, $"/api/followups/{Id}/draft")
        };
        foreach (var (method, route) in routes)
        {
            foreach (var (authorization, status) in new[]
            {
                ((string?)null, HttpStatusCode.Unauthorized), ("Test deny", HttpStatusCode.Forbidden)
            })
            {
                using var request = new HttpRequestMessage(method, route);
                if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization);
                using var response = await client.SendAsync(request);
                Assert.True(response.StatusCode == status, $"{method} {route}: {authorization ?? "anonymous"} returned {response.StatusCode}");
            }
        }

        foreach (var name in new[] { "capture_followup", "list_due_followups", "get_followup",
                     "record_followup_evidence", "dismiss_followup", "defer_followup", "set_followup_draft" })
        {
            foreach (var (authorization, status) in new[]
            {
                ((string?)null, HttpStatusCode.Unauthorized), ("Test deny", HttpStatusCode.Forbidden)
            })
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
                {
                    Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name, arguments = new { } } })
                };
                request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
                if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization);
                using var response = await client.SendAsync(request);
                Assert.True(response.StatusCode == status, $"{name}: {authorization ?? "anonymous"} returned {response.StatusCode}");
            }
        }
    }

    [Fact]
    public async Task ApiAndMcpShareAllFollowUpOperationsAndHonestEvidence()
    {
        using var baseFactory = new JournalApiFactory { FixedUtcNow = Now };
        var journal = InMemoryJournalRepository.CreateWithFutureLog(Guid.NewGuid());
        var repository = new InMemoryFollowUpRepository(journal);
        using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IJournalRepository>();
            services.AddSingleton<IJournalRepository>(journal);
            services.RemoveAll<IFollowUpRepository>();
            services.AddSingleton<IFollowUpRepository>(repository);
        }));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Test allow");
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp
        }, client, NullLoggerFactory.Instance);
        await using var mcp = await McpClient.CreateAsync(transport);
        var toolSchema = (await mcp.ListToolsAsync()).Single(tool => tool.Name == "capture_followup")
            .ProtocolTool.InputSchema;
        var required = toolSchema.GetProperty("required").EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Contains("source", required);
        Assert.Contains("dueAtUtc", required);

        using var empty = await client.GetAsync("/api/followups?due=true");
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        Assert.Empty((await Json(empty)).EnumerateArray());
        var noDue = await mcp.CallToolAsync("list_due_followups");
        Assert.False(noDue.IsError);
        Assert.Empty(noDue.StructuredContent!.Value.EnumerateArray());

        using var missingSource = await client.PostAsJsonAsync("/api/followups", new
        {
            title = "Missing source", condition = "if no reply", dueAtUtc = Now, timeZoneId = "Etc/UTC"
        });
        Assert.Equal(HttpStatusCode.BadRequest, missingSource.StatusCode);
        using var badSource = await client.PostAsJsonAsync("/api/followups", new
        {
            title = "Unsupported", condition = "if no reply", dueAtUtc = Now, timeZoneId = "Etc/UTC", source = 42
        });
        Assert.Equal(HttpStatusCode.BadRequest, badSource.StatusCode);
        using var missingDue = await client.PostAsJsonAsync("/api/followups", new
        {
            title = "Missing due", condition = "if no reply", timeZoneId = "Etc/UTC", source = 1
        });
        Assert.Equal(HttpStatusCode.BadRequest, missingDue.StatusCode);
        using var invalidFilter = await client.GetAsync("/api/followups?due=false");
        Assert.Equal(HttpStatusCode.BadRequest, invalidFilter.StatusCode);
        var badMcp = await mcp.CallToolAsync("capture_followup", new Dictionary<string, object?>
        {
            ["title"] = "Unsupported", ["condition"] = "if no reply", ["dueAtUtc"] = Now,
            ["timeZoneId"] = "Etc/UTC", ["source"] = 42
        });
        Assert.True(badMcp.IsError);
        var noSourceMcp = await mcp.CallToolAsync("capture_followup", new Dictionary<string, object?>
        {
            ["title"] = "Missing source", ["condition"] = "if no reply", ["dueAtUtc"] = Now,
            ["timeZoneId"] = "Etc/UTC"
        });
        Assert.True(noSourceMcp.IsError);

        using var captured = await client.PostAsJsonAsync("/api/followups", new
        {
            title = "API follow-up", condition = "if no reply", dueAtUtc = Now, timeZoneId = "Etc/UTC", source = 1
        });
        Assert.Equal(HttpStatusCode.OK, captured.StatusCode);
        var apiItem = await Json(captured);
        var apiId = apiItem.GetProperty("id").GetGuid();
        Assert.Equal((int)FollowUpOutcome.EvidenceUnavailable, apiItem.GetProperty("outcome").GetInt32());
        var mcpItem = await mcp.CallToolAsync("capture_followup", new Dictionary<string, object?>
        {
            ["title"] = "MCP follow-up", ["condition"] = "if no reply", ["dueAtUtc"] = Now,
            ["timeZoneId"] = "Etc/UTC", ["source"] = EvidenceSource.Manual
        });
        Assert.False(mcpItem.IsError);
        var mcpId = mcpItem.StructuredContent!.Value.GetProperty("id").GetGuid();

        using var export = await client.GetAsync("/api/journal/export");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        var exportIds = (await Json(export)).GetProperty("followUps").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid()).ToArray();
        Assert.Contains(apiId, exportIds);
        Assert.Contains(mcpId, exportIds);
        var mcpExport = await mcp.CallToolAsync("export_journal");
        Assert.False(mcpExport.IsError);
        var mcpExportIds = mcpExport.StructuredContent!.Value.GetProperty("followUps").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid()).ToArray();
        Assert.Equal(exportIds.Order(), mcpExportIds.Order());
        using var backup = await client.GetAsync("/api/journal/backup/export");
        Assert.Equal(HttpStatusCode.OK, backup.StatusCode);
        var backupIds = (await Json(backup)).GetProperty("followUps").GetProperty("followUps").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid()).ToArray();
        Assert.Equal(exportIds.Order(), backupIds.Order());

        using var due = await client.GetAsync("/api/followups?due=true");
        Assert.Equal(2, (await Json(due)).GetArrayLength());
        var mcpDue = await mcp.CallToolAsync("list_due_followups");
        Assert.Equal(2, mcpDue.StructuredContent!.Value.GetArrayLength());

        using var evidence = await client.PostAsJsonAsync($"/api/followups/{mcpId}/evidence", new
        {
            source = 1, observation = 2, checkedScope = (string?)null
        });
        Assert.Equal(HttpStatusCode.OK, evidence.StatusCode);
        Assert.Equal((int)FollowUpOutcome.EvidenceUnavailable, (await Json(evidence)).GetProperty("outcome").GetInt32());
        var read = await mcp.CallToolAsync("get_followup", new Dictionary<string, object?> { ["id"] = mcpId });
        Assert.Equal((int)FollowUpOutcome.EvidenceUnavailable, read.StructuredContent!.Value.GetProperty("outcome").GetInt32());
        var corrected = await mcp.CallToolAsync("record_followup_evidence", new Dictionary<string, object?>
        {
            ["id"] = mcpId, ["source"] = EvidenceSource.Manual,
            ["observation"] = FollowUpObservation.NoReplySeen, ["checkedScope"] = "inbox and spam"
        });
        Assert.False(corrected.IsError);
        Assert.Equal((int)FollowUpOutcome.NoReplySeenInCheckedScope, corrected.StructuredContent!.Value.GetProperty("outcome").GetInt32());
        using var apiRead = await client.GetAsync($"/api/followups/{mcpId}");
        var correctedRead = await Json(apiRead);
        Assert.Equal(2, correctedRead.GetProperty("evidence").GetArrayLength());
        Assert.Equal((int)FollowUpOutcome.NoReplySeenInCheckedScope, correctedRead.GetProperty("outcome").GetInt32());

        using var invalidEvidence = await client.PostAsJsonAsync($"/api/followups/{mcpId}/evidence", new
        {
            source = 1, observation = 99, checkedScope = "inbox"
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalidEvidence.StatusCode);
        using var unsupportedEvidence = await client.PostAsJsonAsync($"/api/followups/{mcpId}/evidence", new
        {
            source = 42, observation = 2, checkedScope = "inbox"
        });
        Assert.Equal(HttpStatusCode.BadRequest, unsupportedEvidence.StatusCode);
        var invalidMcpEvidence = await mcp.CallToolAsync("record_followup_evidence", new Dictionary<string, object?>
        {
            ["id"] = apiId, ["source"] = 1, ["observation"] = 99, ["checkedScope"] = "inbox"
        });
        Assert.True(invalidMcpEvidence.IsError);
        var unsupportedMcpEvidence = await mcp.CallToolAsync("record_followup_evidence", new Dictionary<string, object?>
        {
            ["id"] = apiId, ["source"] = 42, ["observation"] = 2, ["checkedScope"] = "inbox"
        });
        Assert.True(unsupportedMcpEvidence.IsError);
        using var stillCorrected = await client.GetAsync($"/api/followups/{mcpId}");
        Assert.Equal((int)FollowUpOutcome.NoReplySeenInCheckedScope, (await Json(stillCorrected)).GetProperty("outcome").GetInt32());
        var replySeen = await mcp.CallToolAsync("record_followup_evidence", new Dictionary<string, object?>
        {
            ["id"] = mcpId, ["source"] = EvidenceSource.Manual, ["observation"] = FollowUpObservation.ReplySeen
        });
        Assert.False(replySeen.IsError);
        using var readReply = await client.GetAsync($"/api/followups/{mcpId}");
        Assert.Equal((int)FollowUpOutcome.ReplySeen, (await Json(readReply)).GetProperty("outcome").GetInt32());

        using var apiDraft = await client.PutAsJsonAsync($"/api/followups/{apiId}/draft", new { draft = "For review only", expectedDraft = (string?)null });
        Assert.Equal("For review only", (await Json(apiDraft)).GetProperty("draft").GetString());
        var mcpDraft = await mcp.CallToolAsync("set_followup_draft", new Dictionary<string, object?>
        {
            ["id"] = mcpId, ["draft"] = "Copy, do not send", ["expectedDraft"] = null
        });
        Assert.Equal("Copy, do not send", mcpDraft.StructuredContent!.Value.GetProperty("draft").GetString());
        using var deferred = await client.PostAsJsonAsync($"/api/followups/{apiId}/defer", new { dueAtUtc = Now.AddDays(1) });
        Assert.Equal(HttpStatusCode.OK, deferred.StatusCode);
        var mcpDefer = await mcp.CallToolAsync("defer_followup", new Dictionary<string, object?>
        {
            ["id"] = mcpId, ["dueAtUtc"] = Now.AddDays(1)
        });
        Assert.False(mcpDefer.IsError);
        using var afterDefer = await client.GetAsync("/api/followups?due=true");
        Assert.Empty((await Json(afterDefer)).EnumerateArray());

        using var dismissed = await client.PostAsync($"/api/followups/{apiId}/dismiss", null);
        Assert.Equal((int)FollowUpStatus.Dismissed, (await Json(dismissed)).GetProperty("status").GetInt32());
        var mcpDismiss = await mcp.CallToolAsync("dismiss_followup", new Dictionary<string, object?> { ["id"] = mcpId });
        Assert.Equal((int)FollowUpStatus.Dismissed, mcpDismiss.StructuredContent!.Value.GetProperty("status").GetInt32());
        using var missing = await client.GetAsync($"/api/followups/{Id}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var missingMcp = await mcp.CallToolAsync("get_followup", new Dictionary<string, object?> { ["id"] = Guid.Parse(Id) });
        Assert.True(missingMcp.IsError);
        Assert.Equal("NotFound", missingMcp.StructuredContent!.Value.GetProperty("code").GetString());
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}
