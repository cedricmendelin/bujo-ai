using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bujo.Application;
using Bujo.Domain;
using Bujo.Host;
using Bujo.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

namespace Bujo.Integration.Tests;

[Trait("Category", "SqlDurability")]
public sealed class SqlDurabilityTests
{
    [SqlDurabilityFact]
    public async Task SqlJournalSurvivesHostRestartAndExportRestoresTheCanonicalSnapshot()
    {
        var adminConnectionString = Environment.GetEnvironmentVariable("BUJO_SQL_DURABILITY_CONNECTION")!;
        var databaseName = $"BujoDurability_{Guid.NewGuid():N}";
        var restoreDatabaseName = $"BujoRestore_{Guid.NewGuid():N}";
        var databaseCreated = false;
        var restoreDatabaseCreated = false;

        try
        {
            var databaseConnectionString = await CreateDatabaseAsync(adminConnectionString, databaseName);
            databaseCreated = true;
            var restoreConnectionString = await CreateDatabaseAsync(adminConnectionString, restoreDatabaseName);
            restoreDatabaseCreated = true;
            await MigrateAsync(databaseConnectionString);
            JsonNode initialExport;
            Guid itemId;
            Guid originalOccurrenceId;
            Guid weeklyListId;

            using (var factory = new SqlJournalApiFactory(databaseConnectionString))
            using (var client = factory.CreateClient())
            {
                using var ready = await GetAsync(client, "/health/ready");
                Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

                var customList = await CreateListAsync(client, "Durability custom", JournalListKind.Custom, "Keep this goal");
                var weeklyList = await CreateListAsync(
                    client,
                    "Durability weekly",
                    JournalListKind.Weekly,
                    weekYear: 2026,
                    weekNumber: 48);
                var capture = await SendJsonAsync(
                    client,
                    HttpMethod.Post,
                    "/api/items",
                    new
                    {
                        name = "Synthetic durability item",
                        description = "Never use real journal text in this test.",
                        tags = new[] { "durability", "synthetic" },
                        plannedPeriod = new { kind = "week", weekYear = 2026, week = 48 }
                    });
                Assert.Equal(HttpStatusCode.OK, capture.StatusCode);
                using var captured = await JsonDocument.ParseAsync(await capture.Content.ReadAsStreamAsync());
                var item = captured.RootElement.GetProperty("item");
                itemId = item.GetProperty("id").GetGuid();
                originalOccurrenceId = item.GetProperty("currentOccurrenceId").GetGuid();
                weeklyListId = weeklyList;
                var retryOperationId = Guid.NewGuid();

                using var failedMove = await SendJsonAsync(
                    client,
                    HttpMethod.Post,
                    $"/api/items/{itemId}/move",
                    new
                    {
                        operationId = retryOperationId,
                        expectedOccurrenceId = Guid.NewGuid(),
                        destinationListId = customList
                    });
                Assert.Equal(HttpStatusCode.Conflict, failedMove.StatusCode);

                using var move = await SendJsonAsync(
                    client,
                    HttpMethod.Post,
                    $"/api/items/{itemId}/move",
                    new
                    {
                        operationId = retryOperationId,
                        expectedOccurrenceId = originalOccurrenceId,
                        destinationListId = customList
                    });
                Assert.Equal(HttpStatusCode.OK, move.StatusCode);
                using var moved = await JsonDocument.ParseAsync(await move.Content.ReadAsStreamAsync());
                var customOccurrenceId = moved.RootElement.GetProperty("resolution")
                    .GetProperty("currentOccurrence").GetProperty("id").GetGuid();

                using var retriedMove = await SendJsonAsync(
                    client,
                    HttpMethod.Post,
                    $"/api/items/{itemId}/move",
                    new
                    {
                        operationId = retryOperationId,
                        expectedOccurrenceId = originalOccurrenceId,
                        destinationListId = customList
                    });
                Assert.Equal(HttpStatusCode.OK, retriedMove.StatusCode);
                using var retried = await JsonDocument.ParseAsync(await retriedMove.Content.ReadAsStreamAsync());
                Assert.Equal(
                    customOccurrenceId,
                    retried.RootElement.GetProperty("resolution").GetProperty("currentOccurrence").GetProperty("id").GetGuid());

                using var secondMove = await SendJsonAsync(
                    client,
                    HttpMethod.Post,
                    $"/api/items/{itemId}/move",
                    new
                    {
                        operationId = Guid.NewGuid(),
                        expectedOccurrenceId = customOccurrenceId,
                        destinationListId = weeklyList
                    });
                Assert.Equal(HttpStatusCode.OK, secondMove.StatusCode);
                using var movedAgain = await JsonDocument.ParseAsync(await secondMove.Content.ReadAsStreamAsync());
                var weeklyOccurrenceId = movedAgain.RootElement.GetProperty("resolution")
                    .GetProperty("currentOccurrence").GetProperty("id").GetGuid();

                using var completed = await SendJsonAsync(
                    client,
                    HttpMethod.Post,
                    $"/api/occurrences/{weeklyOccurrenceId}/transition",
                    new { action = OccurrenceAction.Done });
                Assert.Equal(HttpStatusCode.OK, completed.StatusCode);

                using var dailyStart = await GetAsync(client, "/api/daily-start?timeZone=Europe%2FZurich");
                Assert.Equal(HttpStatusCode.OK, dailyStart.StatusCode);
                using var resolution = await GetAsync(client, $"/api/occurrences/{originalOccurrenceId}/resolution");
                using var resolved = await JsonDocument.ParseAsync(await resolution.Content.ReadAsStreamAsync());
                Assert.Equal(
                    weeklyOccurrenceId,
                    resolved.RootElement.GetProperty("currentOccurrence").GetProperty("id").GetGuid());
                Assert.Equal(
                    "Done",
                    GetEnumName(
                        resolved.RootElement.GetProperty("currentOccurrence").GetProperty("state"),
                        typeof(OccurrenceState)));

                initialExport = await GetExportAsync(client);
                var exportedItem = initialExport["items"]!.AsArray()
                    .Single(exported => exported!["id"]!.GetValue<Guid>() == itemId);
                Assert.Equal("Keep this goal", initialExport["lists"]!.AsArray()
                    .Single(list => list!["id"]!.GetValue<Guid>() == customList)!["goalText"]!.GetValue<string>());
                Assert.Contains(exportedItem!["tags"]!.AsArray(),
                    tag => tag!.GetValue<string>() == "synthetic");
                Assert.Equal(3, initialExport["occurrences"]!.AsArray()
                    .Count(occurrence => occurrence!["itemId"]!.GetValue<Guid>() == itemId));

                var concurrentItemResponse = await SendJsonAsync(
                    client,
                    HttpMethod.Post,
                    "/api/items",
                    new { name = "Synthetic concurrent item" });
                Assert.Equal(HttpStatusCode.OK, concurrentItemResponse.StatusCode);
                using var concurrentCapture = await JsonDocument.ParseAsync(
                    await concurrentItemResponse.Content.ReadAsStreamAsync());
                var concurrentItem = concurrentCapture.RootElement.GetProperty("item");
                var concurrentItemId = concurrentItem.GetProperty("id").GetGuid();
                var concurrentOccurrenceId = concurrentItem.GetProperty("currentOccurrenceId").GetGuid();
                var alternateList = await CreateListAsync(client, "Durability alternate", JournalListKind.Custom);
                var concurrentMoves = await Task.WhenAll(
                    SendJsonAsync(client, HttpMethod.Post, $"/api/items/{concurrentItemId}/move", new
                    {
                        operationId = Guid.NewGuid(),
                        expectedOccurrenceId = concurrentOccurrenceId,
                        destinationListId = customList
                    }),
                    SendJsonAsync(client, HttpMethod.Post, $"/api/items/{concurrentItemId}/move", new
                    {
                        operationId = Guid.NewGuid(),
                        expectedOccurrenceId = concurrentOccurrenceId,
                        destinationListId = alternateList
                    }));
                try
                {
                    Assert.Single(concurrentMoves, response => response.IsSuccessStatusCode);
                    Assert.All(concurrentMoves.Where(response => !response.IsSuccessStatusCode),
                        response => Assert.Equal(HttpStatusCode.Conflict, response.StatusCode));
                }
                finally
                {
                    foreach (var response in concurrentMoves)
                    {
                        response.Dispose();
                    }
                }

                using var concurrentSnapshotResponse = await GetAsync(client, "/api/journal");
                using var concurrentSnapshot = await JsonDocument.ParseAsync(
                    await concurrentSnapshotResponse.Content.ReadAsStreamAsync());
                Assert.Single(
                    concurrentSnapshot.RootElement.GetProperty("occurrences").EnumerateArray(),
                    occurrence =>
                        occurrence.GetProperty("itemId").GetGuid() == concurrentItemId &&
                        GetEnumName(occurrence.GetProperty("state"), typeof(OccurrenceState)) == "Open");
            }

            await MigrateAsync(restoreConnectionString);
            await RestoreExportAsync(restoreConnectionString, initialExport);
            using (var restoredFactory = new SqlJournalApiFactory(restoreConnectionString))
            using (var restoredClient = restoredFactory.CreateClient())
            {
                var restoredExport = await GetExportAsync(restoredClient);
                initialExport.AsObject().Remove("exportedAt");
                restoredExport.AsObject().Remove("exportedAt");
                Assert.True(
                    JsonNode.DeepEquals(initialExport, restoredExport),
                    $"Original: {initialExport}{Environment.NewLine}Restored: {restoredExport}");

                using var snapshotResponse = await GetAsync(restoredClient, "/api/journal");
                using var snapshot = await JsonDocument.ParseAsync(await snapshotResponse.Content.ReadAsStreamAsync());
                Assert.Contains(snapshot.RootElement.GetProperty("items").EnumerateArray(),
                    restoredItem => restoredItem.GetProperty("id").GetGuid() == itemId);
                using var restoredResolutionResponse = await GetAsync(
                    restoredClient,
                    $"/api/occurrences/{originalOccurrenceId}/resolution");
                using var restoredResolution = await JsonDocument.ParseAsync(
                    await restoredResolutionResponse.Content.ReadAsStreamAsync());
                Assert.Equal(HttpStatusCode.OK, restoredResolutionResponse.StatusCode);
                Assert.Equal(
                    weeklyListId,
                    restoredResolution.RootElement.GetProperty("currentList").GetProperty("id").GetGuid());
                Assert.Equal(
                    "Done",
                    GetEnumName(
                        restoredResolution.RootElement.GetProperty("currentOccurrence").GetProperty("state"),
                        typeof(OccurrenceState)));

                using var mcpHttpClient = restoredFactory.CreateClient();
                mcpHttpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Test allow");
                await using var transport = new HttpClientTransport(
                    new HttpClientTransportOptions
                    {
                        Endpoint = new Uri("http://localhost/mcp"),
                        TransportMode = HttpTransportMode.StreamableHttp
                    },
                    mcpHttpClient,
                    NullLoggerFactory.Instance);
                await using var mcp = await McpClient.CreateAsync(transport);
                var mcpExport = await mcp.CallToolAsync("export_journal", new Dictionary<string, object?>());
                Assert.False(mcpExport.IsError);
                var mcpExportNode = JsonNode.Parse(mcpExport.StructuredContent!.Value.GetRawText())!.AsObject();
                mcpExportNode.Remove("exportedAt");
                Assert.True(JsonNode.DeepEquals(restoredExport, mcpExportNode));
            }

            using (var restartedFactory = new SqlJournalApiFactory(databaseConnectionString))
            using (var restartedClient = restartedFactory.CreateClient())
            {
                using var restartedSnapshotResponse = await GetAsync(restartedClient, "/api/journal");
                using var restartedSnapshot = await JsonDocument.ParseAsync(
                    await restartedSnapshotResponse.Content.ReadAsStreamAsync());
                var restartedItem = restartedSnapshot.RootElement.GetProperty("items").EnumerateArray()
                    .Single(candidate => candidate.GetProperty("id").GetGuid() == itemId);
                Assert.Equal("Synthetic durability item", restartedItem.GetProperty("name").GetString());
                Assert.Equal(3, restartedSnapshot.RootElement.GetProperty("occurrences").EnumerateArray()
                    .Count(occurrence => occurrence.GetProperty("itemId").GetGuid() == itemId));
            }
        }
        finally
        {
            try
            {
                if (restoreDatabaseCreated)
                {
                    await DropDatabaseAsync(adminConnectionString, restoreDatabaseName);
                }
            }
            finally
            {
                if (databaseCreated)
                {
                    await DropDatabaseAsync(adminConnectionString, databaseName);
                }
            }
        }
    }

    private static async Task<Guid> CreateListAsync(
        HttpClient client,
        string name,
        JournalListKind kind,
        string? goalText = null,
        int? weekYear = null,
        int? weekNumber = null)
    {
        using var response = await SendJsonAsync(client, HttpMethod.Post, "/api/lists", new
        {
            name,
            kind,
            goalText,
            weekKey = weekYear is { } year && weekNumber is { } week ? new { year, week } : null
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return body.RootElement.GetProperty("id").GetGuid();
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Authorization", "Test allow");
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SendJsonAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        object body)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("Authorization", "Test allow");
        return client.SendAsync(request);
    }

    private static async Task<JsonNode> GetExportAsync(HttpClient client)
    {
        using var response = await GetAsync(client, "/api/journal/export");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private static string GetEnumName(JsonElement value, Type enumType) =>
        value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : Enum.GetName(enumType, value.GetInt32())!;

    private static async Task<string> CreateDatabaseAsync(string adminConnectionString, string databaseName)
    {
        var builder = new SqlConnectionStringBuilder(adminConnectionString) { InitialCatalog = "master" };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE [{databaseName}]";
        await command.ExecuteNonQueryAsync();
        return new SqlConnectionStringBuilder(adminConnectionString)
        {
            InitialCatalog = databaseName,
            ConnectRetryCount = 0
        }.ConnectionString;
    }

    private static async Task DropDatabaseAsync(string adminConnectionString, string databaseName)
    {
        var builder = new SqlConnectionStringBuilder(adminConnectionString) { InitialCatalog = "master" };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}]";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task MigrateAsync(string connectionString)
    {
        var options = new DbContextOptionsBuilder<JournalDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        await using var context = new JournalDbContext(options);
        await context.Database.MigrateAsync();
    }

    private static async Task RestoreExportAsync(string connectionString, JsonNode exported)
    {
        var document = JsonSerializer.Deserialize<JournalExportDocument>(
            exported.ToJsonString(),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var options = new DbContextOptionsBuilder<JournalDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        await using var context = new JournalDbContext(options);
        context.Lists.AddRange(document.Lists.Select(list => new JournalListEntity
        {
            Id = list.Id,
            Name = list.Name,
            NormalizedName = list.Name,
            Kind = Enum.Parse<JournalListKind>(list.Kind),
            State = Enum.Parse<JournalListState>(list.State),
            GoalText = list.GoalText,
            WeekYear = list.WeekYear,
            WeekNumber = list.WeekNumber,
            Version = list.Version
        }));
        context.Items.AddRange(document.Items.Select(item =>
        {
            var entity = new ItemEntity
            {
                Id = item.Id,
                Name = item.Name,
                Description = item.Description,
                PlannedPeriodKind = Enum.Parse<PlannedPeriodKind>(item.PlannedPeriodKind),
                PlannedDay = item.PlannedDay is null ? null : DateOnly.Parse(item.PlannedDay),
                PlannedWeekYear = item.PlannedWeekYear,
                PlannedWeekNumber = item.PlannedWeekNumber,
                PlannedYear = item.PlannedYear,
                PlannedMonth = item.PlannedMonth,
                PlannedQuarter = item.PlannedQuarter,
                CurrentOccurrenceId = item.CurrentOccurrenceId,
                Version = item.Version
            };
            foreach (var tag in item.Tags)
            {
                var normalized = Tag.Create(tag);
                entity.Tags.Add(new ItemTagEntity
                {
                    ItemId = item.Id,
                    NormalizedValue = normalized.NormalizedValue,
                    Value = normalized.Value
                });
            }

            return entity;
        }));
        context.Occurrences.AddRange(document.Occurrences.Select(occurrence => new OccurrenceEntity
        {
            Id = occurrence.Id,
            ItemId = occurrence.ItemId,
            ListId = occurrence.ListId,
            Sequence = occurrence.Sequence,
            State = Enum.Parse<OccurrenceState>(occurrence.State),
            CreatedAt = occurrence.CreatedAt,
            UpdatedAt = occurrence.UpdatedAt,
            PreviousOccurrenceId = occurrence.PreviousOccurrenceId,
            NextOccurrenceId = occurrence.NextOccurrenceId,
            Version = occurrence.Version
        }));
        context.JournalEvents.AddRange(document.History.Select(entry => new JournalEventEntity
        {
            Sequence = entry.Sequence,
            Kind = Enum.Parse<JournalEventKind>(entry.Kind),
            OccurredAt = entry.OccurredAt,
            ItemId = entry.ItemId,
            OccurrenceId = entry.OccurrenceId,
            PreviousOccurrenceId = entry.PreviousOccurrenceId,
            NextOccurrenceId = entry.NextOccurrenceId,
            ListId = entry.ListId,
            OperationId = entry.OperationId
        }));
        await context.SaveChangesAsync();
    }

    private sealed class SqlJournalApiFactory(string connectionString) : WebApplicationFactory<JournalDatabaseHealthCheck>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:journal"] = connectionString,
                    ["Auth:TenantId"] = "b3f84e9d-2dfb-4e8d-a1c3-91f7d8f59ca7",
                    ["Auth:Audience"] = "journal-api",
                    ["Auth:AllowedPrincipals"] = "2b4d69e1-46a1-4bd0-9a9e-c47be64c6d72",
                    ["Mcp:PublicBaseUrl"] = "http://localhost"
                }));
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
                services.PostConfigure<AuthenticationOptions>(options =>
                    options.Schemes.Single(scheme => scheme.Name == "Bearer").HandlerType = typeof(TestAuthenticationHandler));
            });
        }
    }
}

public sealed class SqlDurabilityFactAttribute : FactAttribute
{
    public SqlDurabilityFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BUJO_SQL_DURABILITY_CONNECTION")))
        {
            Skip = "Set BUJO_SQL_DURABILITY_CONNECTION to a local SQL Server connection string to run this opt-in test.";
        }
    }
}
