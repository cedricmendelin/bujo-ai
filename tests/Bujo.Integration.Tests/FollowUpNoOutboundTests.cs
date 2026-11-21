using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bujo.Application;
using Bujo.Domain;
using Bujo.Infrastructure.Persistence;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

namespace Bujo.Integration.Tests;

public sealed class FollowUpNoOutboundTests
{
    [Fact]
    public async Task FollowUpApiAndMcpDoNotUseRegisteredOutboundHttpOrMessageSenders()
    {
        var now = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        var journal = InMemoryJournalRepository.CreateWithFutureLog(Guid.NewGuid());
        var outbound = new OutboundHttpProbe();
        using var root = new JournalApiFactory { FixedUtcNow = now };
        using var factory = root.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            // Inspect the actual host registrations, not an empty or fabricated service list.
            Assert.Contains(services, registration => registration.ServiceType == typeof(FollowUpApplicationService));
            Assert.Contains(services, registration => registration.ServiceType == typeof(IFollowUpRepository) &&
                registration.ImplementationType == typeof(EfFollowUpRepository));
            Assert.DoesNotContain(services, IsMessageSender);
            Assert.Equal(new[] { typeof(IFollowUpRepository), typeof(TimeProvider) },
                typeof(FollowUpApplicationService).GetConstructors().Single().GetParameters().Select(parameter => parameter.ParameterType));
            Assert.Equal(new[] { typeof(IDbContextFactory<JournalDbContext>) },
                typeof(EfFollowUpRepository).GetConstructors().Single().GetParameters().Select(parameter => parameter.ParameterType));

            // Prove the inventory gate would catch a real sender registration.
            var mutated = services.ToList();
            mutated.Add(ServiceDescriptor.Singleton<IMessageSender, SentinelMessageSender>());
            Assert.Contains(mutated, IsMessageSender);

            services.RemoveAll<IJournalRepository>();
            services.AddSingleton<IJournalRepository>(journal);
            services.RemoveAll<IFollowUpRepository>();
            services.AddSingleton<IFollowUpRepository>(new InMemoryFollowUpRepository(journal));
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(outbound);
            services.RemoveAll<HttpClient>();
            services.AddTransient(_ => outbound.CreateClient("direct"));
        }));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Test allow");

        // Positive control: the actual host-resolved factory and client hit the probe.
        using (var probeClient = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient("control"))
        using (var response = await probeClient.GetAsync("http://outbound-probe.invalid/"))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var probeClient = factory.Services.GetRequiredService<HttpClient>())
        using (var response = await probeClient.GetAsync("http://outbound-probe.invalid/"))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, outbound.CreatedClients);
        Assert.Equal(2, outbound.Requests);
        outbound.Reset();

        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri("http://localhost/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp
        }, client, NullLoggerFactory.Instance);
        await using var mcp = await McpClient.CreateAsync(transport);

        using var capture = await client.PostAsJsonAsync("/api/followups", new
        {
            title = "API reminder", condition = "if no reply", dueAtUtc = now,
            timeZoneId = "Etc/UTC", source = 1
        });
        Assert.Equal(HttpStatusCode.OK, capture.StatusCode);
        using var body = JsonDocument.Parse(await capture.Content.ReadAsStringAsync());
        var apiId = body.RootElement.GetProperty("id").GetGuid();
        using var due = await client.GetAsync("/api/followups?due=true");
        using var get = await client.GetAsync($"/api/followups/{apiId}");
        using var evidence = await client.PostAsJsonAsync($"/api/followups/{apiId}/evidence",
            new { source = 1, observation = 2, checkedScope = "inbox" });
        using var draft = await client.PutAsJsonAsync($"/api/followups/{apiId}/draft", new { draft = "Review only", expectedDraft = (string?)null });
        using var defer = await client.PostAsJsonAsync($"/api/followups/{apiId}/defer", new { dueAtUtc = now.AddDays(1) });
        using var dismiss = await client.PostAsync($"/api/followups/{apiId}/dismiss", null);
        Assert.All(new[] { due, get, evidence, draft, defer, dismiss },
            response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));

        var mcpCapture = await mcp.CallToolAsync("capture_followup", new Dictionary<string, object?>
        {
            ["title"] = "MCP reminder", ["condition"] = "if no reply", ["dueAtUtc"] = now,
            ["timeZoneId"] = "Etc/UTC", ["source"] = EvidenceSource.Manual
        });
        Assert.False(mcpCapture.IsError);
        var mcpId = mcpCapture.StructuredContent!.Value.GetProperty("id").GetGuid();
        var id = new Dictionary<string, object?> { ["id"] = mcpId };
        var results = new[]
        {
            await mcp.CallToolAsync("list_due_followups"),
            await mcp.CallToolAsync("get_followup", id),
            await mcp.CallToolAsync("record_followup_evidence", new Dictionary<string, object?>
            {
                ["id"] = mcpId, ["source"] = EvidenceSource.Manual,
                ["observation"] = FollowUpObservation.NoReplySeen, ["checkedScope"] = "inbox"
            }),
            await mcp.CallToolAsync("set_followup_draft", new Dictionary<string, object?>
            {
                ["id"] = mcpId, ["draft"] = "Review only", ["expectedDraft"] = null
            }),
            await mcp.CallToolAsync("defer_followup", new Dictionary<string, object?>
            {
                ["id"] = mcpId, ["dueAtUtc"] = now.AddDays(1)
            }),
            await mcp.CallToolAsync("dismiss_followup", id)
        };
        Assert.All(results, result => Assert.False(result.IsError));
        Assert.Equal(0, outbound.CreatedClients);
        Assert.Equal(0, outbound.Requests);
    }

    private static bool IsMessageSender(ServiceDescriptor descriptor)
    {
        static bool Matches(Type? type) => type?.Name is
            "IMessageSender" or "IEmailSender" or "IMailSender" or "ISmsSender";
        return Matches(descriptor.ServiceType) || Matches(descriptor.ImplementationType) ||
            Matches(descriptor.ImplementationInstance?.GetType());
    }

    private interface IMessageSender;
    private sealed class SentinelMessageSender : IMessageSender;

    private sealed class OutboundHttpProbe : HttpMessageHandler, IHttpClientFactory
    {
        private int _createdClients;
        private int _requests;
        public int CreatedClients => Volatile.Read(ref _createdClients);
        public int Requests => Volatile.Read(ref _requests);

        public HttpClient CreateClient(string name)
        {
            Interlocked.Increment(ref _createdClients);
            return new HttpClient(this, disposeHandler: false);
        }

        public void Reset()
        {
            Interlocked.Exchange(ref _createdClients, 0);
            Interlocked.Exchange(ref _requests, 0);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
