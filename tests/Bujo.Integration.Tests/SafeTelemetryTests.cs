using System.Collections.Concurrent;
using System.Diagnostics;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Headers;
using System.Security.Claims;
using Azure.Core;
using Azure.Monitor.OpenTelemetry.Exporter;
using Bujo.Application;
using Bujo.Application.Chat;
using Bujo.Domain;
using Bujo.Host.Chat;
using Bujo.Host.Telemetry;
using Bujo.Infrastructure.Persistence;
using Bujo.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Bujo.Integration.Tests;

[CollectionDefinition("Telemetry environment", DisableParallelization = true)]
public sealed class TelemetryEnvironmentCollection;

[Collection("Telemetry environment")]
public sealed class SafeTelemetryTests
{
    [Fact]
    public async Task Request_and_dependency_export_only_safe_metadata_and_keep_correlation()
    {
        using var overrides = new UnsafeEnvironmentOverrides();
        var exported = new ConcurrentBag<Activity>();
        using var provider = Provider(exported);
        var app = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
        app.UseSafeRequestTelemetry();
        app.Run(async context =>
        {
            using var client = new HttpClient(new SafeHttpTelemetryHandler { InnerHandler = new ReplyHandler() });
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.invalid/private-secret?query=private-secret");
            request.Headers.Add("Authorization", "Bearer private-secret");
            request.Content = new StringContent("private-secret");
            using var response = await client.SendAsync(request);
            context.Response.StatusCode = 204;
        });

        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/journal/private-secret";
        context.Request.QueryString = new QueryString("?token=private-secret");
        context.Request.Headers.TraceParent = "00-0123456789abcdef0123456789abcdef-0123456789abcdef-01";
        context.Request.Headers.TraceState = "secret=private-secret";
        context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask,
            RoutePatternFactory.Parse("/journal/{id}"), 0, EndpointMetadataCollection.Empty, "route"));
        await app.Build()(context);
        Assert.True(provider.ForceFlush());

        var server = Assert.Single(exported, a => a.DisplayName == "http.server" && a.TraceId.ToString() == "0123456789abcdef0123456789abcdef");
        var dependency = Assert.Single(exported, a => a.DisplayName == "http.client" && a.TraceId == server.TraceId);
        Assert.Equal(ActivityKind.Server, server.Kind);
        Assert.Equal(ActivityKind.Client, dependency.Kind);
        Assert.Equal(server.SpanId, dependency.ParentSpanId);
        Assert.Equal("/journal/{id}", server.GetTagItem("http.route"));
        Assert.Equal(204, server.GetTagItem("http.response.status_code"));
        Assert.Equal(200, dependency.GetTagItem("http.response.status_code"));
        Assert.Null(server.TraceStateString);
        AssertSafe(server, dependency);
    }

    [Fact]
    public async Task Unsampled_remote_parent_suppresses_metadata_export_but_preserves_trace_propagation()
    {
        var exported = new ConcurrentBag<Activity>();
        using var provider = Provider(exported);
        var app = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
        app.UseSafeRequestTelemetry();
        ActivityTraceId traceId = default;
        ActivityTraceFlags flags = ActivityTraceFlags.Recorded;
        app.Run(context =>
        {
            traceId = Activity.Current!.TraceId;
            flags = Activity.Current.ActivityTraceFlags;
            using var child = SafeTelemetry.Source.StartActivity("chat.run");
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Headers.TraceParent = "00-eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee-bbbbbbbbbbbbbbbb-00";
        await app.Build()(context);
        Assert.True(provider.ForceFlush());
        Assert.Equal("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", traceId.ToString());
        Assert.Equal(ActivityTraceFlags.None, flags);
        Assert.DoesNotContain(exported, span => span.TraceId == traceId);
    }

    [Fact]
    public async Task Without_listener_middleware_preserves_framework_activity()
    {
        var app = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
        app.UseSafeRequestTelemetry();
        using var current = new Activity("framework").Start();
        app.Run(_ =>
        {
            Assert.Same(current, Activity.Current);
            return Task.CompletedTask;
        });
        await app.Build()(new DefaultHttpContext());
        Assert.Same(current, Activity.Current);
    }

    [Fact]
    public async Task Missing_logger_does_not_replace_original_request_exception()
    {
        var exported = new ConcurrentBag<Activity>();
        using var provider = Provider(exported);
        var app = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
        app.UseSafeRequestTelemetry();
        var expected = new InvalidOperationException("private-secret");
        app.Run(_ => throw expected);
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => app.Build()(context));
        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task Foundry_run_exports_token_counts_but_never_content_or_credentials()
    {
        var exported = new ConcurrentBag<Activity>();
        using var provider = Provider(exported);
        var repository = InMemoryJournalRepository.CreateWithFutureLog(Guid.NewGuid());
        var clock = TimeProvider.System;
        var journal = new JournalApplicationService(new JournalService(repository, clock), clock);
        var runtime = new ChatRuntime(journal, new ReviewApplicationService(repository),
            Options.Create(new ChatOptions { Provider = "Foundry", Endpoint = "https://example.invalid", Deployment = "private-secret" }),
            new ClientFactory(), new FakeCredential());

        var result = await runtime.RunAsync(new ChatRunRequest("private-secret", "UTC", []), new ClaimsPrincipal());
        Assert.True(provider.ForceFlush());

        Assert.Equal("private-secret", result.Text);
        var run = Assert.Single(exported, a => a.DisplayName == "chat.run");
        var model = Assert.Single(exported, a => a.DisplayName == "chat.foundry.request" && a.TraceId == run.TraceId);
        Assert.Equal(17L, model.GetTagItem("gen_ai.usage.input_tokens"));
        Assert.Equal(9L, model.GetTagItem("gen_ai.usage.output_tokens"));
        AssertSafe(exported.Where(a => a.TraceId == run.TraceId).ToArray());
    }

    [Fact]
    public void Sensitive_capture_is_rejected_even_without_connection_string()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telemetry:CaptureSensitiveContent"] = "true",
            ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = null
        });
        var error = Assert.Throws<InvalidOperationException>(() => builder.AddSafeTelemetry());
        Assert.Contains("protected routing", error.Message);
    }

    [Fact]
    public void No_connection_string_registers_no_tracer_provider()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telemetry:CaptureSensitiveContent"] = "false",
            ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = ""
        });
        builder.AddSafeTelemetry();
        Assert.DoesNotContain(builder.Services, descriptor => descriptor.ServiceType == typeof(TracerProvider));
    }

    [Fact]
    public void Configured_pipeline_registers_no_opentelemetry_log_exporter()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Telemetry:CaptureSensitiveContent"] = "false",
            ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = "InstrumentationKey=00000000-0000-0000-0000-000000000001"
        });
        builder.AddSafeTelemetry();
        Assert.DoesNotContain(builder.Services, descriptor =>
            descriptor.ServiceType == typeof(ILoggerProvider) &&
            (descriptor.ImplementationType?.FullName?.Contains("OpenTelemetry", StringComparison.Ordinal) ?? false));
    }

    [Fact]
    public async Task Real_knowledge_search_query_is_not_in_the_exported_server_span()
    {
        using var overrides = new UnsafeEnvironmentOverrides();
        var exported = new ConcurrentBag<Activity>();
        using var provider = Provider(exported);
        using var factory = new JournalApiFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/knowledge/search?text=private-secret");
        request.Headers.Add("Authorization", "Test allow");
        request.Headers.Add("traceparent", "00-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-bbbbbbbbbbbbbbbb-01");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(provider.ForceFlush());

        var server = Assert.Single(exported, a => a.TraceId.ToString() == "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" &&
            a.DisplayName == "http.server");
        Assert.Equal("/api/knowledge/search", server.GetTagItem("http.route"));
        AssertSafe(server);
    }

    [Fact]
    public async Task Mcp_prompt_content_is_not_exported()
    {
        var exported = new ConcurrentBag<Activity>();
        using var provider = Provider(exported);
        using var factory = new JournalApiFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"prompts/get","params":{"name":"capture_item","arguments":{"itemText":"private-secret"}}}""")
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Headers.Add("Authorization", "Test allow");
        request.Headers.Add("MCP-Protocol-Version", "2025-03-26");
        request.Headers.Add("traceparent", "00-cccccccccccccccccccccccccccccccc-bbbbbbbbbbbbbbbb-01");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("private-secret", await response.Content.ReadAsStringAsync());
        Assert.True(provider.ForceFlush());
        var server = Assert.Single(exported, a => a.TraceId.ToString() == "cccccccccccccccccccccccccccccccc" &&
            a.DisplayName == "http.server");
        AssertSafe(server);
    }

    [Fact]
    public async Task Exception_handler_reexecutes_the_same_auth_error_route_with_or_without_tracing()
    {
        async Task<(HttpStatusCode Status, string Body)> Request()
        {
            using var factory = new JournalApiFactory();
            using var production = factory.WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IKnowledgeRepository>();
                    services.AddSingleton<IKnowledgeRepository, ThrowingKnowledgeRepository>();
                });
            });
            using var client = production.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/knowledge/tags");
            request.Headers.Add("Authorization", "Test allow");
            using var response = await client.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        var without = await Request();
        var exported = new ConcurrentBag<Activity>();
        using var provider = Provider(exported);
        var with = await Request();
        Assert.Equal(without, with);
        Assert.Equal(HttpStatusCode.InternalServerError, with.Status);
        Assert.Contains("Sign-in unavailable", with.Body);
        Assert.True(provider.ForceFlush());
        Assert.Contains(exported, span => span.OperationName == "http.server" &&
            span.Status == ActivityStatusCode.Error);
    }

    [Fact]
    public void Export_boundary_removes_unapproved_fields_and_drops_events_and_baggage()
    {
        var exported = new ConcurrentBag<Activity>();
        using var provider = Provider(exported);
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Microsoft.Data.SqlClient",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        using (var safe = SafeTelemetry.Source.StartActivity("chat.run")!)
        {
            safe.SetTag("url.full", "https://example.invalid/?q=private-secret");
            safe.SetTag("db.statement", "select 'private-secret'");
            safe.SetTag("bujo.chat.tool.name", "private-secret");
            safe.SetTag("http.request.method", "private-secret");
            safe.SetStatus(ActivityStatusCode.Error, "private-secret");
            safe.DisplayName = "private-secret";
        }
        using (var withEvent = SafeTelemetry.Source.StartActivity("chat.run")!)
            withEvent.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
                { { "exception.message", "private-secret" } }));
        using (var withBaggage = SafeTelemetry.Source.StartActivity("chat.run")!)
            withBaggage.AddBaggage("token", "private-secret");
        using (var foreign = new ActivitySource("Microsoft.Data.SqlClient").StartActivity("select private-secret"))
        {
            Assert.NotNull(foreign);
            foreign.SetTag("db.statement", "private-secret");
        }
        Assert.True(provider.ForceFlush());
        var span = Assert.Single(exported, a => a.DisplayName == "chat.run");
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Null(span.GetTagItem("db.statement"));
        Assert.Null(span.GetTagItem("url.full"));
        Assert.Null(span.GetTagItem("http.request.method"));
        Assert.Null(span.GetTagItem("bujo.chat.tool.name"));
        AssertSafe(span);
    }

    [Fact]
    public void Configured_logging_blocks_sensitive_categories_but_keeps_operational_errors()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = "InstrumentationKey=00000000-0000-0000-0000-000000000001",
            ["Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"] = "Trace",
            ["Logging:LogLevel:System.Net.Http.HttpClient.ChatProvider"] = "Trace",
            ["Logging:LogLevel:Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware"] = "Trace",
            ["Logging:Console:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"] = "Trace"
        });
        builder.AddSafeTelemetry();
        var records = new ConcurrentBag<string>();
        builder.Logging.AddProvider(new CaptureLogs(records));
        using var services = builder.Services.BuildServiceProvider();
        var logs = services.GetRequiredService<ILoggerFactory>();
        Assert.False(logs.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command").IsEnabled(LogLevel.Error));
        Assert.False(logs.CreateLogger("System.Net.Http.HttpClient.ChatProvider").IsEnabled(LogLevel.Error));
        Assert.False(logs.CreateLogger("Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware").IsEnabled(LogLevel.Error));
        Assert.True(logs.CreateLogger("KnowledgeEndpoints").IsEnabled(LogLevel.Error));
        Assert.True(logs.CreateLogger("Microsoft.Hosting.Lifetime").IsEnabled(LogLevel.Error));
        logs.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command").LogError("private-secret");
        logs.CreateLogger("System.Net.Http.HttpClient.ChatProvider").LogError("private-secret");
        logs.CreateLogger("Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware").LogError("private-secret");
        logs.CreateLogger("Microsoft.Hosting.Lifetime").LogError("operational error");
        Assert.Contains("operational error", records);
        Assert.DoesNotContain(records, message => message.Contains("private-secret", StringComparison.Ordinal));
    }

    [Fact]
    public void No_connection_string_preserves_existing_logging_rules()
    {
        static WebApplicationBuilder Create(bool configureTelemetry)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = "",
                ["Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"] = "Trace",
                ["Logging:LogLevel:Microsoft.Hosting.Lifetime"] = "Information",
                ["Logging:Console:LogLevel:Microsoft.EntityFrameworkCore.Database.Command"] = "Trace"
            });
            if (configureTelemetry) builder.AddSafeTelemetry();
            return builder;
        }
        using var baseline = Create(false).Services.BuildServiceProvider();
        using var actual = Create(true).Services.BuildServiceProvider();
        var baselineRules = baseline.GetRequiredService<IOptions<LoggerFilterOptions>>().Value;
        var actualRules = actual.GetRequiredService<IOptions<LoggerFilterOptions>>().Value;
        Assert.Equal(baselineRules.MinLevel, actualRules.MinLevel);
        Assert.Equal(baselineRules.Rules.Select(rule => (rule.ProviderName, rule.CategoryName, rule.LogLevel)),
            actualRules.Rules.Select(rule => (rule.ProviderName, rule.CategoryName, rule.LogLevel)));
        var a = baseline.GetRequiredService<ILoggerFactory>();
        var b = actual.GetRequiredService<ILoggerFactory>();
        foreach (var category in new[] { "Microsoft.EntityFrameworkCore.Database.Command", "Microsoft.Hosting.Lifetime",
                     "Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware", "Arbitrary.Application" })
            foreach (var level in new[] { LogLevel.Trace, LogLevel.Information, LogLevel.Error })
                Assert.Equal(a.CreateLogger(category).IsEnabled(level), b.CreateLogger(category).IsEnabled(level));
    }

    [Fact]
    public async Task Known_chat_tool_name_is_exported_without_its_arguments()
    {
        var exported = new ConcurrentBag<Activity>();
        using var provider = Provider(exported);
        var repository = InMemoryJournalRepository.CreateWithFutureLog(Guid.NewGuid());
        var clock = TimeProvider.System;
        var journal = new JournalApplicationService(new JournalService(repository, clock), clock);
        var runtime = new ChatRuntime(journal, new ReviewApplicationService(repository),
            Options.Create(new ChatOptions()), new ClientFactory());
        await runtime.RunAsync(new ChatRunRequest("find list private-secret", "UTC", []), new ClaimsPrincipal());
        Assert.True(provider.ForceFlush());
        var tool = Assert.Single(exported, span => span.OperationName == "chat.tool");
        Assert.Equal("find_lists", tool.GetTagItem("bujo.chat.tool.name"));
        AssertSafe(tool);
    }

    [Fact]
    public async Task Untrusted_model_tool_name_is_not_exported()
    {
        var exported = new ConcurrentBag<Activity>();
        using var provider = Provider(exported);
        var repository = InMemoryJournalRepository.CreateWithFutureLog(Guid.NewGuid());
        var clock = TimeProvider.System;
        var journal = new JournalApplicationService(new JournalService(repository, clock), clock);
        var runtime = new ChatRuntime(journal, new ReviewApplicationService(repository),
            Options.Create(new ChatOptions { Provider = "Foundry", Endpoint = "https://example.invalid", Deployment = "test" }),
            new MaliciousClientFactory(), new FakeCredential());
        await runtime.RunAsync(new ChatRunRequest("hello", "UTC", []), new ClaimsPrincipal());
        Assert.True(provider.ForceFlush());
        var tool = Assert.Single(exported, span => span.OperationName == "chat.tool");
        Assert.Null(tool.GetTagItem("bujo.chat.tool.name"));
        AssertSafe(exported.ToArray());
    }

    [Fact]
    public void Sql_interceptor_records_only_fixed_metadata_and_is_registered_on_real_context()
    {
        var exported = new ConcurrentBag<Activity>();
        using var provider = Provider(exported);
        var services = new ServiceCollection();
        services.AddJournalPersistence("Server=localhost;Database=journal;Integrated Security=true;TrustServerCertificate=true");
        using var serviceProvider = services.BuildServiceProvider();
        using var db = serviceProvider.GetRequiredService<IDbContextFactory<JournalDbContext>>().CreateDbContext();
        var options = db.GetService<IDbContextOptions>();
        Assert.Contains(options.Extensions.OfType<CoreOptionsExtension>().SelectMany(o => o.Interceptors ?? []),
            interceptor => interceptor is SafeSqlCommandTelemetry);

        using var connection = new SqlConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "select 'private-secret'";
        command.Parameters.AddWithValue("@p", "private-secret");
        var start = DateTimeOffset.UtcNow.AddMilliseconds(-25);
        var duration = TimeSpan.FromMilliseconds(20);
        var data = new CommandExecutedEventData(null!, (_, _) => "private-secret", connection, command,
            "private-secret", db, DbCommandMethod.ExecuteNonQuery, Guid.NewGuid(), Guid.NewGuid(), 1,
            false, false, start, duration, CommandSource.Unknown);
        using (var parent = SafeTelemetry.Source.StartActivity("chat.run")!)
        {
            Assert.Equal(1, SafeSqlCommandTelemetry.Instance.NonQueryExecuted(command, data, 1));
        }
        Assert.True(provider.ForceFlush());
        var sql = Assert.Single(exported, span => span.OperationName == "db.command");
        var run = Assert.Single(exported, span => span.OperationName == "chat.run");
        Assert.Equal(run.SpanId, sql.ParentSpanId);
        Assert.Equal("mssql", sql.GetTagItem("db.system.name"));
        Assert.Equal(duration, sql.Duration);
        AssertSafe(sql);
    }

    [Fact]
    public async Task Ef_sql_commands_emit_safe_success_and_failure_spans()
    {
        var exported = new ConcurrentBag<Activity>();
        using var provider = Provider(exported);
        using var connection = new FakeDbConnection();
        var options = new DbContextOptionsBuilder<JournalDbContext>()
            .UseSqlServer(connection)
            .AddInterceptors(SafeSqlCommandTelemetry.Instance)
            .Options;
        await using var db = new JournalDbContext(options);
        using (var parent = SafeTelemetry.Source.StartActivity("chat.run")!)
        {
            Assert.Equal(1, db.Database.ExecuteSqlRaw("update T set V=@p where X='private-secret'",
                new SqlParameter("@p", "private-secret")));
            connection.Fail = true;
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                db.Database.ExecuteSqlRawAsync("update T set V=@p where X='private-secret'",
                    new SqlParameter("@p", "private-secret")));
        }
        Assert.True(provider.ForceFlush());
        var spans = exported.Where(span => span.OperationName == "db.command").ToArray();
        Assert.Equal(2, spans.Length);
        Assert.Single(spans, span => span.Status == ActivityStatusCode.Error);
        Assert.All(spans, span => Assert.Equal("mssql", span.GetTagItem("db.system.name")));
        AssertSafe(spans);
    }

    [Fact]
    public async Task Azure_exporter_serializes_only_safe_fields_to_a_local_collector()
    {
        using var overrides = new UnsafeEnvironmentOverrides();
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var incoming = listener.GetContextAsync().WaitAsync(timeout.Token);
        var builder = Sdk.CreateTracerProviderBuilder();
        SafeTelemetry.ConfigureTracing(builder, new AzureMonitorTraceExporter(new AzureMonitorExporterOptions
        {
            ConnectionString = $"InstrumentationKey=00000000-0000-0000-0000-000000000001;IngestionEndpoint=http://127.0.0.1:{port}/",
            DisableOfflineStorage = true
        }));
        using var provider = builder.Build();
        var app = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
        app.UseSafeRequestTelemetry();
        app.Run(async context =>
        {
            Activity.Current!.SetTag("db.statement", "private-secret");
            Activity.Current.SetTag("url.full", "https://example.invalid/?q=private-secret");
            using var activity = SafeTelemetry.Source.StartActivity("chat.run")!;
            activity.SetTag("bujo.chat.tool_call_count", 3);
            activity.SetStatus(ActivityStatusCode.Error, "private-secret");
            using var client = new HttpClient(new SafeHttpTelemetryHandler { InnerHandler = new ReplyHandler() });
            using var response = await client.GetAsync("https://example.invalid/private-secret?q=private-secret");
            using var dbConnection = new FakeDbConnection();
            var dbOptions = new DbContextOptionsBuilder<JournalDbContext>()
                .UseSqlServer(dbConnection).AddInterceptors(SafeSqlCommandTelemetry.Instance).Options;
            await using var db = new JournalDbContext(dbOptions);
            await db.Database.ExecuteSqlRawAsync("update T set V='private-secret'");
            dbConnection.Fail = true;
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                db.Database.ExecuteSqlRawAsync("update T set V='private-secret'"));
        });
        var requestContext = new DefaultHttpContext();
        requestContext.Request.Method = "GET";
        requestContext.Request.Path = "/api/knowledge/search";
        requestContext.Request.QueryString = new QueryString("?text=private-secret");
        requestContext.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask,
            RoutePatternFactory.Parse("/api/knowledge/search"), 0, EndpointMetadataCollection.Empty, "route"));
        await app.Build()(requestContext);
        using (var exception = SafeTelemetry.Source.StartActivity("chat.run")!)
            exception.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
                { { "exception.message", "private-secret" } }));
        var flush = Task.Run(() => provider.ForceFlush(10000));
        var context = await incoming;
        using var reader = new StreamReader(context.Request.InputStream);
        var payload = await reader.ReadToEndAsync(timeout.Token);
        context.Response.StatusCode = 200;
        context.Response.Close();
        Assert.True(await flush);
        Assert.Contains("chat.run", payload);
        Assert.Contains("bujo.chat.tool_call_count", payload);
        Assert.Contains("/api/knowledge/search", payload);
        Assert.Contains("db.command", payload);
        Assert.Contains("mssql", payload);
        Assert.True(System.Text.RegularExpressions.Regex.Matches(payload, "db.command").Count >= 2);
        Assert.DoesNotContain("private-secret", payload);
        Assert.DoesNotContain("db.statement", payload);
        Assert.DoesNotContain("url.full", payload);
    }

    private static TracerProvider Provider(ConcurrentBag<Activity> exported) =>
        BuildProvider(exported);

    private static TracerProvider BuildProvider(ConcurrentBag<Activity> exported)
    {
        var builder = Sdk.CreateTracerProviderBuilder();
        SafeTelemetry.ConfigureTracing(builder, new MemoryExporter(exported));
        return builder.Build();
    }

    private static void AssertSafe(params Activity[] activities)
    {
        Assert.NotEmpty(activities);
        foreach (var activity in activities)
        {
            Assert.DoesNotContain("private-secret", activity.DisplayName);
            Assert.DoesNotContain("private-secret", activity.StatusDescription ?? "");
            Assert.Empty(activity.Events);
            Assert.Empty(activity.Baggage);
            Assert.All(activity.TagObjects, tag =>
                Assert.DoesNotContain("private-secret", $"{tag.Key}={tag.Value}"));
        }
    }

    private sealed class MemoryExporter(ConcurrentBag<Activity> exported) : BaseExporter<Activity>
    {
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch) exported.Add(activity);
            return ExportResult.Success;
        }
    }

    private sealed class CaptureLogs(ConcurrentBag<string> records) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(records);
        public void Dispose() { }
        private sealed class Logger(ConcurrentBag<string> records) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => records.Add(formatter(state, exception));
        }
    }

    private sealed class FakeDbConnection : DbConnection
    {
        private ConnectionState state;
        public bool Fail { get; set; }
        [AllowNull] public override string ConnectionString { get; set; } = "";
        public override string Database => "private-secret";
        public override string DataSource => "private-secret";
        public override string ServerVersion => "16.0";
        public override ConnectionState State => state;
        public override void Open() => state = ConnectionState.Open;
        public override void Close() => state = ConnectionState.Closed;
        public override void ChangeDatabase(string databaseName) { }
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => new FakeDbCommand(this);
    }

    private sealed class FakeDbCommand(FakeDbConnection connection) : DbCommand
    {
        private readonly SqlCommand parameters = new();
        [AllowNull] public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = connection;
        protected override DbParameterCollection DbParameterCollection => parameters.Parameters;
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override int ExecuteNonQuery() => connection.Fail ? throw new InvalidOperationException("private-secret") : 1;
        public override object? ExecuteScalar() => throw new NotSupportedException();
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new SqlParameter();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
    }

    private sealed class ThrowingKnowledgeRepository : IKnowledgeRepository
    {
        public DomainResult<KnowledgeEntrySnapshot> Create(CreateKnowledgeEntryCommand command) => throw new NotSupportedException();
        public DomainResult<KnowledgeEntrySnapshot> Get(Guid id) => throw new NotSupportedException();
        public DomainResult<KnowledgeEntrySnapshot> Update(UpdateKnowledgeEntryCommand command) => throw new NotSupportedException();
        public DomainResult<KnowledgeEntrySnapshot> SetArchived(Guid id, bool archived, long expectedVersion, DateTimeOffset updatedAt) => throw new NotSupportedException();
        public DomainResult<KnowledgeEntrySnapshot> Delete(Guid id, long expectedVersion) => throw new NotSupportedException();
        public DomainResult<KnowledgeSearchPage> Search(KnowledgeSearchCriteria criteria) => throw new NotSupportedException();
        public IReadOnlyList<KnowledgeTagInfo> GetTags() => throw new InvalidOperationException("private-secret");
        public KnowledgeIndexState GetIndexState() => throw new NotSupportedException();
        public KnowledgeBackupSnapshot ExportBackup() => throw new NotSupportedException();
        public DomainResult<KnowledgeBackupSnapshot> RestoreBackup(KnowledgeBackupSnapshot backup) => throw new NotSupportedException();
    }

    private sealed class ReplyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"message":{"content":"private-secret"}}],"usage":{"prompt_tokens":17,"completion_tokens":9}}""")
            });
    }

    private sealed class ClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new SafeHttpTelemetryHandler { InnerHandler = new ReplyHandler() });
    }

    private sealed class MaliciousClientFactory : IHttpClientFactory
    {
        private readonly MaliciousHandler handler = new();
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
        private sealed class MaliciousHandler : HttpMessageHandler
        {
            private int calls;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var content = calls++ == 0
                    ? """{"choices":[{"message":{"role":"assistant","tool_calls":[{"id":"one","type":"function","function":{"name":"private-secret","arguments":"{}"}}]}}]}"""
                    : """{"choices":[{"message":{"content":"done"}}]}""";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) });
            }
        }
    }

    private sealed class FakeCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("private-secret", DateTimeOffset.UtcNow.AddMinutes(5));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class UnsafeEnvironmentOverrides : IDisposable
    {
        private readonly Dictionary<string, string?> previous = new();
        public UnsafeEnvironmentOverrides()
        {
            foreach (var key in new[] {
                "OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION",
                "OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION" })
            {
                previous[key] = Environment.GetEnvironmentVariable(key);
                Environment.SetEnvironmentVariable(key, "true");
            }
        }

        public void Dispose()
        {
            foreach (var (key, value) in previous) Environment.SetEnvironmentVariable(key, value);
        }
    }
}
