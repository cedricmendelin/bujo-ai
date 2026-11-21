using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Claims;
using Bujo.Application;
using Bujo.Application.Chat;
using Bujo.Domain;
using Bujo.Host;
using Bujo.Host.Mcp;
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
            ["GET /api/journal/export"] = ["export_journal"],
            ["GET /api/journal/backup/export"] = ["export_journal_backup"],
            ["POST /api/journal/restore"] = ["restore_journal_backup"],
            ["GET /api/daily-start"] = ["get_daily_start"],
            ["GET /api/planning/upcoming"] = ["get_upcoming"],
            ["POST /api/lists"] = ["create_list"],
            ["POST /api/items"] = ["capture_item"],
            ["POST /api/items/{itemId:guid}/move"] = ["move_item"],
            ["POST /api/occurrences/{occurrenceId:guid}/transition"] =
                ["complete_occurrence", "abandon_occurrence", "reopen_occurrence"],
            ["POST /api/lists/{listId:guid}/close"] = ["close_list"],
            ["POST /api/lists/{listId:guid}/reopen"] = ["reopen_list"],
            ["PUT /api/items/{itemId:guid}"] = ["edit_item"],
            ["PUT /api/items/{itemId:guid}/planned-period"] = ["set_planned_period"],
            ["PUT /api/lists/{listId:guid}/goal"] = ["set_list_goal"],
            ["GET /api/occurrences/{occurrenceId:guid}/resolution"] = ["resolve_occurrence"],
            ["GET /api/lists/{listId:guid}/review"] = ["get_list_review"],
            ["POST /api/review-proposals"] = ["create_review_proposal"],
            ["GET /api/review-proposals/{proposalId:guid}"] = ["get_review_proposal"],
            ["POST /api/review-proposals/{proposalId:guid}/cancel"] = ["cancel_review_proposal"],
            ["POST /api/review-proposals/{proposalId:guid}/reject"] = ["reject_review_proposal"],
            ["POST /api/review-proposals/{proposalId:guid}/apply"] = ["apply_review_proposal"],
            ["POST /api/knowledge/"] = ["capture_knowledge"],
            ["GET /api/knowledge/export"] = ["export_knowledge_backup"],
            ["POST /api/knowledge/restore"] = ["restore_knowledge_backup"],
            ["GET /api/knowledge/{id:guid}"] = ["get_knowledge_entry"],
            ["PUT /api/knowledge/{id:guid}"] = ["update_knowledge_entry"],
            ["POST /api/knowledge/{id:guid}/archive"] = ["set_knowledge_archived"],
            ["DELETE /api/knowledge/{id:guid}"] = ["delete_knowledge_entry"],
            ["GET /api/knowledge/search"] = ["search_knowledge"],
            ["GET /api/knowledge/tags"] = ["list_knowledge_tags"],
            ["GET /api/knowledge/search-status"] = ["get_knowledge_search_status"]
        };

    private static readonly string[] ExpectedTools =
    [
        "get_journal",
        "export_journal",
        "export_journal_backup",
        "restore_journal_backup",
        "get_daily_start",
        "get_upcoming",
        "find_lists",
        "capture_item",
        "create_list",
        "resolve_occurrence",
        "edit_item",
        "set_planned_period",
        "set_list_goal",
        "close_list",
        "reopen_list",
        "move_item",
        "complete_occurrence",
        "abandon_occurrence",
        "reopen_occurrence",
        "get_list_review",
        "create_review_proposal",
        "get_review_proposal",
        "cancel_review_proposal",
        "reject_review_proposal",
        "apply_review_proposal",
        "capture_knowledge",
        "get_knowledge_entry",
        "update_knowledge_entry",
        "set_knowledge_archived",
        "delete_knowledge_entry",
        "search_knowledge",
        "list_knowledge_tags",
        "get_knowledge_search_status",
        "export_knowledge_backup",
        "restore_knowledge_backup"
    ];

    private readonly JournalApiFactory _factory;

    public JournalMcpContractTests(JournalApiFactory factory)
    {
        _factory = factory;
        _factory.ShareInMemoryRepository = true;
        _factory.FixedUtcNow = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
    }

    [Fact]
    public async Task CaptureTypeSchemaValidationAndReadsMatchTheSharedContract()
    {
        using var httpClient = _factory.CreateClient();
        httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Test allow");
        await using var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp
            }, httpClient, NullLoggerFactory.Instance);
        await using var mcp = await McpClient.CreateAsync(transport);
        var schema = (await mcp.ListToolsAsync()).Single(tool => tool.Name == "capture_item").ProtocolTool.InputSchema;
        var prompt = JournalMcpPrompts.CaptureItem("I learnt about XY");
        Assert.Contains("Thought for learning or reflection", prompt);
        Assert.Contains("mixed or ambiguous", prompt);
        Assert.Contains("successfully persisted", prompt);
        var typeSchema = schema.GetProperty("properties").GetProperty("type");
        Assert.Contains("string", typeSchema.GetProperty("type").ToString());
        Assert.Contains("Thought", typeSchema.GetProperty("description").GetString());
        if (schema.TryGetProperty("required", out var required))
        {
            Assert.DoesNotContain("type", required.EnumerateArray().Select(value => value.GetString()));
        }

        var name = $"Reflection {Guid.NewGuid()}";
        var thought = await mcp.CallToolAsync("capture_item", new Dictionary<string, object?>
        {
            ["name"] = name, ["type"] = "tHoUgHt"
        });
        Assert.False(thought.IsError);
        var item = thought.StructuredContent!.Value.GetProperty("item");
        var id = item.GetProperty("id").GetGuid();
        Assert.Equal((int)ItemType.Thought, item.GetProperty("type").GetInt32());

        foreach (var type in new object?[] { null, "tAsK" })
        {
            var task = await mcp.CallToolAsync("capture_item", new Dictionary<string, object?>
            {
                ["name"] = $"Task {Guid.NewGuid()}", ["type"] = type
            });
            Assert.False(task.IsError);
            Assert.Equal((int)ItemType.Task, task.StructuredContent!.Value.GetProperty("item").GetProperty("type").GetInt32());
        }

        var legacy = await mcp.CallToolAsync("capture_item", new Dictionary<string, object?>
        {
            ["name"] = $"Legacy {Guid.NewGuid()}"
        });
        Assert.False(legacy.IsError);
        Assert.Equal((int)ItemType.Task, legacy.StructuredContent!.Value.GetProperty("item").GetProperty("type").GetInt32());

        foreach (var invalid in new[] { "", "1", "other" })
        {
            var failed = await mcp.CallToolAsync("capture_item", new Dictionary<string, object?>
            {
                ["name"] = $"Invalid {Guid.NewGuid()}", ["type"] = invalid
            });
            Assert.True(failed.IsError);
            Assert.Equal("InvalidInput", GetErrorCode(failed));
        }

        var snapshot = await mcp.CallToolAsync("get_journal");
        var read = snapshot.StructuredContent!.Value.GetProperty("items").EnumerateArray()
            .Single(entry => entry.GetProperty("id").GetGuid() == id);
        Assert.Equal((int)ItemType.Thought, read.GetProperty("type").GetInt32());
        var complete = await mcp.CallToolAsync("complete_occurrence", new Dictionary<string, object?>
        {
            ["occurrenceId"] = item.GetProperty("currentOccurrenceId").GetGuid()
        });
        Assert.True(complete.IsError);
    }

    [Fact]
    public async Task McpEndpointRequiresApprovedPrincipalAndPublishesProtectedResourceMetadata()
    {
        using var client = _factory.CreateClient();
        using var metadataRequest = new HttpRequestMessage(HttpMethod.Get, "/.well-known/oauth-protected-resource/mcp");
        metadataRequest.Headers.Host = "attacker.example";
        using var metadata = await client.SendAsync(metadataRequest);
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
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"export_journal","arguments":{}}}""",
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"close_list","arguments":{"listId":"00000000-0000-0000-0000-000000000101"}}}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"capture_knowledge","arguments":{"content":"note"}}}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"get_knowledge_entry","arguments":{"id":"00000000-0000-0000-0000-000000000001"}}}""",
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"update_knowledge_entry","arguments":{"id":"00000000-0000-0000-0000-000000000001","content":"note","expectedVersion":1}}}""",
            """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"set_knowledge_archived","arguments":{"id":"00000000-0000-0000-0000-000000000001","archived":true,"expectedVersion":1}}}""",
            """{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"delete_knowledge_entry","arguments":{"id":"00000000-0000-0000-0000-000000000001","expectedVersion":1}}}""",
            """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"search_knowledge","arguments":{"text":"note"}}}""",
            """{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"list_knowledge_tags","arguments":{}}}""",
            """{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"get_knowledge_search_status","arguments":{}}}""",
            """{"jsonrpc":"2.0","id":10,"method":"tools/call","params":{"name":"export_knowledge_backup","arguments":{}}}""",
            """{"jsonrpc":"2.0","id":11,"method":"tools/call","params":{"name":"restore_knowledge_backup","arguments":{"backup":{"schemaVersion":1,"entries":[]}}}}""",
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
    public async Task ConfiguredOriginCanCallMcpEndpoint()
    {
        using var client = _factory.CreateClient();
        using var request = McpPost("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""");
        request.Headers.Add("Authorization", "Test allow");
        request.Headers.Add("Origin", "https://approved.example");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task KnowledgeMcpToolsShareTheApiStoreAndPreserveOriginalContent()
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
        const string content = "“Saved exactly”\r\nand returned with its quote.";

        var saved = await mcp.CallToolAsync("capture_knowledge", new Dictionary<string, object?>
        {
            ["content"] = content,
            ["title"] = "Exact quote",
            ["sourceReference"] = "https://example.invalid/quote",
            ["tags"] = new[] { "Quotes", "Research" }
        });
        Assert.False(saved.IsError);
        var id = saved.StructuredContent!.Value.GetProperty("id").GetGuid();

        using var api = _factory.CreateClient();
        using var read = await api.SendAsync(AuthorizedGet($"/api/knowledge/{id}"));
        using var readBody = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        var search = await mcp.CallToolAsync("search_knowledge", new Dictionary<string, object?>
        {
            ["text"] = "\"Saved exactly\"",
            ["tags"] = new[] { "Quotes", "Research" }
        });
        var backup = await mcp.CallToolAsync("export_knowledge_backup");
        var backupSnapshot = JsonSerializer.Deserialize<KnowledgeBackupSnapshot>(
            backup.StructuredContent!.Value.GetRawText(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var deleted = await mcp.CallToolAsync("delete_knowledge_entry", new Dictionary<string, object?>
        {
            ["id"] = id,
            ["expectedVersion"] = 1
        });
        var restored = await mcp.CallToolAsync("restore_knowledge_backup", new Dictionary<string, object?>
        {
            ["backup"] = backupSnapshot
        });

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(content, readBody.RootElement.GetProperty("content").GetString());
        Assert.False(search.IsError);
        var result = Assert.Single(search.StructuredContent!.Value.GetProperty("results").EnumerateArray());
        Assert.Equal(id, result.GetProperty("id").GetGuid());
        Assert.Equal(content, result.GetProperty("passage").GetString());
        Assert.Equal("https://example.invalid/quote", result.GetProperty("sourceReference").GetString());
        Assert.Equal((int)KnowledgeSearchMode.Fallback,
            search.StructuredContent.Value.GetProperty("searchMode").GetInt32());
        Assert.False(backup.IsError);
        Assert.Single(backupSnapshot!.Entries);
        Assert.False(deleted.IsError);
        Assert.False(restored.IsError);
    }

    private static HttpRequestMessage AuthorizedGet(string uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("Authorization", "Test allow");
        return request;
    }

    [Fact]
    public async Task ReviewToolsRequirePwaApprovalAndCannotClaimApproval()
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
        Assert.DoesNotContain("approve_review_proposal", tools.Select(tool => tool.Name));
        Assert.DoesNotContain("approved", tools.SelectMany(tool =>
            tool.ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject()
                .Select(property => property.Name)));
        Assert.DoesNotContain(
            "approved",
            tools.Single(tool => tool.Name == "create_review_proposal").ProtocolTool.InputSchema.ToString(),
            StringComparison.OrdinalIgnoreCase);

        var listResult = await mcp.CallToolAsync("create_list", new Dictionary<string, object?>
        {
            ["name"] = "MCP review approval",
            ["kind"] = JournalListKind.Custom
        });
        Assert.False(listResult.IsError);
        var listId = listResult.StructuredContent!.Value.GetProperty("id").GetGuid();
        var first = await mcp.CallToolAsync("capture_item", new Dictionary<string, object?>
        {
            ["name"] = "MCP review done",
            ["listId"] = listId
        });
        var second = await mcp.CallToolAsync("capture_item", new Dictionary<string, object?>
        {
            ["name"] = "MCP review abandon",
            ["listId"] = listId
        });
        Assert.False(first.IsError);
        Assert.False(second.IsError);
        var firstItem = first.StructuredContent!.Value.GetProperty("item");
        var secondItem = second.StructuredContent!.Value.GetProperty("item");
        var review = await mcp.CallToolAsync("get_list_review", new Dictionary<string, object?>
        {
            ["listId"] = listId
        });
        Assert.False(review.IsError);
        Assert.Equal(2, review.StructuredContent!.Value.GetProperty("unfinished").GetArrayLength());

        var created = await mcp.CallToolAsync("create_review_proposal", new Dictionary<string, object?>
        {
            ["request"] = new
            {
                listId,
                expectedListVersion = review.StructuredContent.Value.GetProperty("list").GetProperty("version").GetInt64(),
                requestCloseList = true,
                changes = new[]
                {
                    new
                    {
                        itemId = firstItem.GetProperty("id").GetGuid(),
                        occurrenceId = firstItem.GetProperty("currentOccurrenceId").GetGuid(),
                        expectedItemVersion = firstItem.GetProperty("version").GetInt64(),
                        action = ReviewDisposition.Done
                    },
                    new
                    {
                        itemId = secondItem.GetProperty("id").GetGuid(),
                        occurrenceId = secondItem.GetProperty("currentOccurrenceId").GetGuid(),
                        expectedItemVersion = secondItem.GetProperty("version").GetInt64(),
                        action = ReviewDisposition.Abandon
                    }
                }
            }
        });
        Assert.False(created.IsError);
        var proposalId = created.StructuredContent!.Value.GetProperty("id").GetGuid();
        Assert.Contains(
            created.Content,
            block => block is TextContentBlock text &&
                     text.Text.Contains($"/lists/{listId}/review?proposalId={proposalId}", StringComparison.Ordinal));
        var digest = created.StructuredContent.Value.GetProperty("digest").GetString()!;
        var unapprovedApply = await mcp.CallToolAsync("apply_review_proposal", new Dictionary<string, object?>
        {
            ["proposalId"] = proposalId
        });
        Assert.True(unapprovedApply.IsError);
        Assert.Equal("ApprovalRequired", GetErrorCode(unapprovedApply));

        var claims = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("tid", "b3f84e9d-2dfb-4e8d-a1c3-91f7d8f59ca7"),
            new Claim("oid", "2b4d69e1-46a1-4bd0-9a9e-c47be64c6d72"),
            new Claim("scp", "journal.access")
        ]));
        await using var scope = _factory.Services.CreateAsyncScope();
        var reviewService = scope.ServiceProvider.GetRequiredService<ReviewApplicationService>();
        var approved = reviewService.ApproveProposal(
            proposalId,
            digest,
            ReviewActor.FromPrincipal(claims, ReviewProposalSource.Pwa, interactive: true));
        Assert.True(approved.IsSuccess, approved.Error?.Message);

        var applied = await mcp.CallToolAsync("apply_review_proposal", new Dictionary<string, object?>
        {
            ["proposalId"] = proposalId
        });
        Assert.False(applied.IsError);
        Assert.Equal((int)ReviewProposalStatus.Applied, applied.StructuredContent!.Value.GetProperty("status").GetInt32());

        var staleList = await mcp.CallToolAsync("create_list", new Dictionary<string, object?>
        {
            ["name"] = "MCP stale review",
            ["kind"] = JournalListKind.Custom
        });
        var staleListId = staleList.StructuredContent!.Value.GetProperty("id").GetGuid();
        var staleItem = await mcp.CallToolAsync("capture_item", new Dictionary<string, object?>
        {
            ["name"] = "MCP stale item",
            ["listId"] = staleListId
        });
        var staleItemValue = staleItem.StructuredContent!.Value.GetProperty("item");
        var staleOccurrenceValue = staleItem.StructuredContent.Value.GetProperty("occurrence");
        var staleReview = await mcp.CallToolAsync("get_list_review", new Dictionary<string, object?>
        {
            ["listId"] = staleListId
        });
        var staleProposal = await mcp.CallToolAsync("create_review_proposal", new Dictionary<string, object?>
        {
            ["request"] = new
            {
                listId = staleListId,
                expectedListVersion = staleReview.StructuredContent!.Value.GetProperty("list").GetProperty("version").GetInt64(),
                requestCloseList = true,
                changes = new[]
                {
                    new
                    {
                        itemId = staleItemValue.GetProperty("id").GetGuid(),
                        occurrenceId = staleOccurrenceValue.GetProperty("id").GetGuid(),
                        expectedItemVersion = staleItemValue.GetProperty("version").GetInt64(),
                        action = ReviewDisposition.Done
                    }
                }
            }
        });
        var staleProposalId = staleProposal.StructuredContent!.Value.GetProperty("id").GetGuid();
        Assert.True(reviewService.ApproveProposal(
            staleProposalId,
            staleProposal.StructuredContent.Value.GetProperty("digest").GetString()!,
            ReviewActor.FromPrincipal(claims, ReviewProposalSource.Pwa, interactive: true)).IsSuccess);
        var concurrentItem = await mcp.CallToolAsync("capture_item", new Dictionary<string, object?>
        {
            ["name"] = "MCP concurrent item",
            ["listId"] = staleListId
        });
        Assert.False(concurrentItem.IsError);

        var conflict = await mcp.CallToolAsync("apply_review_proposal", new Dictionary<string, object?>
        {
            ["proposalId"] = staleProposalId
        });
        Assert.True(conflict.IsError);
        Assert.Equal("ConcurrencyConflict", GetErrorCode(conflict));
        Assert.Contains("expected version", conflict.StructuredContent!.Value.GetProperty("message").GetString());
        Assert.Contains("Unresolved occurrence", conflict.StructuredContent.Value.GetProperty("message").GetString());
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
        var mcpNames = tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        Assert.All(
            ChatToolRegistry.All.Select(tool => tool.Name),
            name => Assert.Contains(name, mcpNames));
        Assert.DoesNotContain("run_chat", mcpNames);
        Assert.DoesNotContain("get_chat_speech_status", mcpNames);
        Assert.DoesNotContain("approve_review_proposal", tools.Select(tool => tool.Name));
        Assert.DoesNotContain("approved", tools.SelectMany(tool =>
            tool.ProtocolTool.InputSchema.GetProperty("properties").EnumerateObject()
                .Select(property => property.Name)));
        var proposalSchema = tools.Single(tool => tool.Name == "create_review_proposal")
            .ProtocolTool.InputSchema.GetProperty("properties");
        Assert.DoesNotContain("approved", proposalSchema.ToString(), StringComparison.OrdinalIgnoreCase);
        var dailyStartSchema = tools.Single(tool => tool.Name == "get_daily_start").ProtocolTool.InputSchema;
        Assert.Equal("string", dailyStartSchema.GetProperty("properties").GetProperty("timeZone").GetProperty("type").GetString());
        Assert.Contains(
            "timeZone",
            dailyStartSchema.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        var moveSchema = tools.Single(tool => tool.Name == "move_item").ProtocolTool.InputSchema;
        Assert.Equal(
            "uuid",
            moveSchema.GetProperty("properties").GetProperty("operationId").GetProperty("format").GetString());
        foreach (var toolName in new[] { "close_list", "reopen_list" })
        {
            var lifecycleTool = tools.Single(tool => tool.Name == toolName);
            var listIdSchema = lifecycleTool.ProtocolTool.InputSchema.GetProperty("properties").GetProperty("listId");
            Assert.Equal("string", listIdSchema.GetProperty("type").GetString());
            Assert.Equal("uuid", listIdSchema.GetProperty("format").GetString());
            Assert.Contains(
                "listId",
                lifecycleTool.ProtocolTool.InputSchema.GetProperty("required").EnumerateArray()
                    .Select(value => value.GetString()));
            Assert.False(string.IsNullOrWhiteSpace(lifecycleTool.Description));
        }
        Assert.Equal(
            ["capture_item", "daily_start", "explicit_edit"],
            (await mcp.ListPromptsAsync()).Select(prompt => prompt.Name).Order());
        Assert.Contains(
            (await mcp.ListResourcesAsync()),
            resource => resource.Uri.ToString() == "bujo://journal/capabilities");
        var capabilitiesText = JournalMcpResources.GetCapabilities();
        Assert.Contains("close_list", capabilitiesText);
        Assert.Contains("reopen_list", capabilitiesText);
        Assert.Contains("export_journal", capabilitiesText);
        Assert.DoesNotContain("list-closure operations", capabilitiesText, StringComparison.OrdinalIgnoreCase);

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
        var duplicateName = await mcp.CallToolAsync("create_list", new Dictionary<string, object?>
        {
            ["name"] = "mcp DESTINATION",
            ["kind"] = JournalListKind.Custom
        });
        Assert.True(duplicateName.IsError);
        Assert.Equal("DuplicateListName", GetErrorCode(duplicateName));

        var weekly = await mcp.CallToolAsync("create_list", new Dictionary<string, object?>
        {
            ["name"] = "MCP week 48",
            ["kind"] = JournalListKind.Weekly,
            ["weekKey"] = new { year = 2026, week = 48 }
        });
        Assert.False(weekly.IsError);
        var anotherWeeklyList = await mcp.CallToolAsync("create_list", new Dictionary<string, object?>
        {
            ["name"] = "MCP planning week 48",
            ["kind"] = JournalListKind.Weekly,
            ["weekKey"] = new { year = 2026, week = 48 }
        });
        Assert.False(anotherWeeklyList.IsError);

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

        using var apiExportRequest = new HttpRequestMessage(HttpMethod.Get, "/api/journal/export");
        apiExportRequest.Headers.Add("Authorization", "Test allow");
        using var apiExportResponse = await apiClient.SendAsync(apiExportRequest);
        using var apiExportJson = JsonDocument.Parse(await apiExportResponse.Content.ReadAsStringAsync());
        var mcpExport = await mcp.CallToolAsync("export_journal", new Dictionary<string, object?>());
        Assert.Equal(HttpStatusCode.OK, apiExportResponse.StatusCode);
        Assert.False(mcpExport.IsError);
        var apiExportNode = JsonNode.Parse(apiExportJson.RootElement.GetRawText())!.AsObject();
        var mcpExportNode = JsonNode.Parse(mcpExport.StructuredContent!.Value.GetRawText())!.AsObject();
        apiExportNode.Remove("exportedAt");
        mcpExportNode.Remove("exportedAt");
        Assert.True(
            JsonNode.DeepEquals(apiExportNode, mcpExportNode),
            $"API: {apiExportNode}{Environment.NewLine}MCP: {mcpExportNode}");

        var missingTimeZone = await mcp.CallToolAsync(
            "get_daily_start",
            new Dictionary<string, object?> { ["timeZone"] = "" });
        Assert.True(missingTimeZone.IsError);
        Assert.Equal("InvalidTimeZone", GetErrorCode(missingTimeZone));
    }

    [Fact]
    public async Task JournalAndExportMcpToolsReturnErrorsWhenTheRepositoryIsUnavailable()
    {
        using var factory = new JournalApiFactory { FailQueries = true };
        using var httpClient = factory.CreateClient();
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

        foreach (var toolName in new[] { "get_journal", "export_journal" })
        {
            var result = await mcp.CallToolAsync(toolName, new Dictionary<string, object?>());
            Assert.True(result.IsError);
            Assert.Equal("QueryFailed", GetErrorCode(result));
            Assert.DoesNotContain("Simulated repository failure", result.Content.OfType<TextContentBlock>()
                .Select(block => block.Text).Single());
        }
    }

    [Fact]
    public async Task ListLifecycleToolsUseTheSharedServiceAndPreserveDomainErrors()
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

        var snapshot = await mcp.CallToolAsync("get_journal", new Dictionary<string, object?>());
        Assert.False(snapshot.IsError);
        var futureLogId = snapshot.StructuredContent!.Value.GetProperty("lists").EnumerateArray()
            .Single(list => list.GetProperty("name").GetString() == "Future Log")
            .GetProperty("id").GetGuid();
        var futureLogClose = await mcp.CallToolAsync("close_list", new Dictionary<string, object?>
        {
            ["listId"] = futureLogId
        });
        Assert.True(futureLogClose.IsError);
        Assert.Equal("FutureLogCannotClose", GetErrorCode(futureLogClose));

        var missingListId = Guid.NewGuid();
        foreach (var toolName in new[] { "close_list", "reopen_list" })
        {
            var missingList = await mcp.CallToolAsync(toolName, new Dictionary<string, object?>
            {
                ["listId"] = missingListId
            });
            Assert.True(missingList.IsError);
            Assert.Equal("NotFound", GetErrorCode(missingList));
        }

        var created = await mcp.CallToolAsync("create_list", new Dictionary<string, object?>
        {
            ["name"] = $"MCP lifecycle {Guid.NewGuid():N}",
            ["kind"] = JournalListKind.Custom
        });
        Assert.False(created.IsError);
        var listId = created.StructuredContent!.Value.GetProperty("id").GetGuid();
        var capture = await mcp.CallToolAsync("capture_item", new Dictionary<string, object?>
        {
            ["name"] = "Resolve before closing",
            ["listId"] = listId
        });
        Assert.False(capture.IsError);
        var occurrenceId = capture.StructuredContent!.Value.GetProperty("item")
            .GetProperty("currentOccurrenceId").GetGuid();

        var unresolvedClose = await mcp.CallToolAsync("close_list", new Dictionary<string, object?>
        {
            ["listId"] = listId
        });
        Assert.True(unresolvedClose.IsError);
        Assert.Equal("UnresolvedOccurrences", GetErrorCode(unresolvedClose));

        var completed = await mcp.CallToolAsync("complete_occurrence", new Dictionary<string, object?>
        {
            ["occurrenceId"] = occurrenceId
        });
        Assert.False(completed.IsError);
        var closed = await mcp.CallToolAsync("close_list", new Dictionary<string, object?>
        {
            ["listId"] = listId
        });
        Assert.False(closed.IsError);
        Assert.Equal("Closed", GetListState(closed.StructuredContent!.Value.GetProperty("state")));

        using var closedSnapshotResponse = await httpClient.GetAsync("/api/journal");
        Assert.Equal(HttpStatusCode.OK, closedSnapshotResponse.StatusCode);
        using var closedSnapshot = JsonDocument.Parse(await closedSnapshotResponse.Content.ReadAsStringAsync());
        Assert.Equal(
            "Closed",
            GetListState(closedSnapshot.RootElement.GetProperty("lists").EnumerateArray()
                .Single(list => list.GetProperty("id").GetGuid() == listId).GetProperty("state")));

        var reopened = await mcp.CallToolAsync("reopen_list", new Dictionary<string, object?>
        {
            ["listId"] = listId
        });
        Assert.False(reopened.IsError);
        Assert.Equal("Open", GetListState(reopened.StructuredContent!.Value.GetProperty("state")));

        using var reopenedSnapshotResponse = await httpClient.GetAsync("/api/journal");
        Assert.Equal(HttpStatusCode.OK, reopenedSnapshotResponse.StatusCode);
        using var reopenedSnapshot = JsonDocument.Parse(await reopenedSnapshotResponse.Content.ReadAsStringAsync());
        Assert.Equal(
            "Open",
            GetListState(reopenedSnapshot.RootElement.GetProperty("lists").EnumerateArray()
                .Single(list => list.GetProperty("id").GetGuid() == listId).GetProperty("state")));
    }

    [Fact]
    public async Task ApiEndpointsHaveMcpTools()
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
            .Where(operation => !operation.Contains("/api/chat/", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        var missingParity = apiOperations
            .Where(operation => !ApiToolMappings.ContainsKey(operation))
            .ToArray();
        Assert.Empty(missingParity);
        Assert.All(ApiToolMappings.Keys, mappedOperation => Assert.Contains(mappedOperation, apiOperations));
        Assert.All(ApiToolMappings.Values.SelectMany(names => names), tool => Assert.Contains(tool, toolNames));
    }

    private static string GetListState(JsonElement state) =>
        state.ValueKind == JsonValueKind.String
            ? state.GetString()!
            : Enum.GetName(typeof(JournalListState), state.GetInt32())!;

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
