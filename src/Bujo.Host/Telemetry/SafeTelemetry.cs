using System.Diagnostics;
using Azure.Monitor.OpenTelemetry.Exporter;
using Bujo.Application.Chat;
using Bujo.Infrastructure.Persistence;
using Microsoft.AspNetCore.Routing;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Bujo.Host.Telemetry;

// Only application-owned sources are exported. Framework, MCP, SqlClient and AI
// SDK sources may carry user data and must not be subscribed to.
public static class SafeTelemetry
{
    public const string SourceName = "Bujo.Host.SafeTelemetry";
    public static readonly ActivitySource Source = new(SourceName);

    public static void AddSafeTelemetry(this WebApplicationBuilder builder)
    {
        if (builder.Configuration.GetValue<bool>("Telemetry:CaptureSensitiveContent"))
            throw new InvalidOperationException(
                "Sensitive telemetry capture is unavailable until protected routing and access controls are verified.");

        var connectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        // Preserve configured rules and levels, including provider-specific rules.
        // These particular framework categories can emit URLs, SQL or exceptions.
        // Apply the guard to every rule so a provider-specific override cannot
        // re-enable them. No log exporter is registered.
        builder.Services.PostConfigure<LoggerFilterOptions>(options =>
        {
            for (var index = 0; index < options.Rules.Count; index++)
            {
                var rule = options.Rules[index];
                var original = rule.Filter;
                options.Rules[index] = new LoggerFilterRule(rule.ProviderName, rule.CategoryName, rule.LogLevel,
                    (provider, category, level) => !SensitiveLogCategory(category) &&
                        (original?.Invoke(provider, category, level) ?? true));
            }
            options.Rules.Insert(0, new LoggerFilterRule(null, null, null,
                (_, category, _) => !SensitiveLogCategory(category)));
        });

        builder.Services.AddOpenTelemetry().WithTracing(tracing => ConfigureTracing(tracing,
            new AzureMonitorTraceExporter(new AzureMonitorExporterOptions
            {
                ConnectionString = connectionString,
                // Never replay cached data written by a different telemetry pipeline.
                DisableOfflineStorage = true
            })));
    }

    public static void ConfigureTracing(TracerProviderBuilder tracing, BaseExporter<Activity> exporter) =>
        tracing.SetResourceBuilder(ResourceBuilder.CreateEmpty().AddService("bujo-host"))
            // Metadata follows the incoming sampling decision; unsampled parents
            // suppress export. Root metadata spans are sampled. Content is never recorded.
            .SetSampler(new ParentBasedSampler(new AlwaysOnSampler()))
            .AddSource(SourceName, SafeSqlCommandTelemetry.SourceName).AddProcessor(
            new BatchActivityExportProcessor(new SafeTraceExporter(exporter)));

    public static IApplicationBuilder UseSafeRequestTelemetry(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            // Replace the unexported framework Activity with an exported server span.
            // Only a valid W3C parent is accepted; no baggage or request data is copied.
            var previous = Activity.Current;
            Activity.Current = null;
            ActivityContext.TryParse(context.Request.Headers.TraceParent.ToString(),
                null, isRemote: true, out var parent);
            using var activity = Source.StartActivity("http.server", ActivityKind.Server, parent);
            if (activity is null)
            {
                Activity.Current = previous;
                await next(context);
                return;
            }
            try
            {
                var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
                activity?.SetCustomProperty("bujo.safe.route", route ?? "unmatched");
                activity?.SetTag("http.route", route ?? "unmatched");
                activity?.SetTag("http.request.method", SafeMethod(context.Request.Method));
                await next(context);
                activity?.SetTag("http.response.status_code", context.Response.StatusCode);
                if (context.Response.StatusCode >= 500)
                    activity?.SetStatus(ActivityStatusCode.Error);
            }
            catch
            {
                activity?.SetStatus(ActivityStatusCode.Error);
                // A missing/failing provider must not replace the original exception
                // or alter exception-handler re-execution.
                try
                {
                    var loggerFactory = context.RequestServices?.GetService<ILoggerFactory>();
                    if (loggerFactory is null)
                        Console.Error.WriteLine("Request failed.");
                    else
                        loggerFactory.CreateLogger("Bujo.SafeRequestTelemetry").LogError("Request failed.");
                }
                catch (Exception)
                {
                    Console.Error.WriteLine("Request failed; structured logging failed.");
                }
                throw;
            }
            finally
            {
                activity?.Dispose();
                Activity.Current = previous;
            }
        });

    private static bool SensitiveLogCategory(string? category) => category is not null &&
        new[] { "System.Net.Http.HttpClient", "Microsoft.EntityFrameworkCore.Database.Command",
            "Microsoft.EntityFrameworkCore.Database.Connection", "Microsoft.EntityFrameworkCore.Query",
            "Microsoft.EntityFrameworkCore.Update",
            "Microsoft.AspNetCore.Hosting.Diagnostics", "Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware" }
        .Any(prefix => category.Equals(prefix, StringComparison.Ordinal) ||
            category.StartsWith(prefix + ".", StringComparison.Ordinal));

    internal static string SafeMethod(string method) => method switch
    {
        "GET" or "POST" or "PUT" or "DELETE" or "PATCH" or "HEAD" or "OPTIONS" => method,
        _ => "OTHER"
    };
}

// A final export boundary protects against accidental tags added to Activity.Current
// by libraries. Events, links and baggage cannot be reliably removed, so reject
// those spans rather than exporting their contents.
public sealed class SafeTraceExporter(BaseExporter<Activity> inner) : BaseExporter<Activity>
{
    private static readonly HashSet<string> AllowedTags =
    [
        "http.route", "http.request.method", "http.response.status_code",
        "bujo.chat.tool_call_count", "bujo.chat.proposal_count", "gen_ai.operation.name",
        "gen_ai.usage.input_tokens", "gen_ai.usage.output_tokens", "bujo.chat.tool.name", "db.system.name"
    ];

    public override ExportResult Export(in Batch<Activity> batch)
    {
        var accepted = new List<Activity>();
        var rejected = false;
        foreach (var activity in batch)
        {
            if (!((activity.Source.Name == SafeTelemetry.SourceName &&
                   activity.OperationName is "http.server" or "http.client" or "chat.run" or "chat.foundry" or "chat.foundry.request" or "chat.tool") ||
                  (activity.Source.Name == SafeSqlCommandTelemetry.SourceName && activity.OperationName == "db.command")) ||
                activity.Events.Any() || activity.Links.Any() || activity.Baggage.Any())
            {
                rejected = true;
                continue;
            }

            activity.DisplayName = activity.OperationName;
            activity.TraceStateString = null;
            activity.SetStatus(activity.Status);
            foreach (var tag in activity.TagObjects.ToArray())
                if (!AllowedTags.Contains(tag.Key) || !IsSafeValue(activity, tag.Key, tag.Value))
                    activity.SetTag(tag.Key, null);
            accepted.Add(activity);
        }

        if (rejected)
            Console.Error.WriteLine("A telemetry span was dropped by the privacy guard.");
        return accepted.Count == 0 ? ExportResult.Success :
            inner.Export(new Batch<Activity>(accepted.ToArray(), accepted.Count));
    }

    private static bool IsSafeValue(Activity activity, string key, object? value) => key switch
    {
        "http.route" => value is string route && route == activity.GetCustomProperty("bujo.safe.route") as string,
        "http.request.method" => value is string method && SafeTelemetry.SafeMethod(method) == method,
        "gen_ai.operation.name" => value is string operation && operation == "chat",
        "db.system.name" => value is string system && system == "mssql",
        "bujo.chat.tool.name" => value is string toolName &&
            ChatToolRegistry.All.Any(tool => string.Equals(tool.Name, toolName, StringComparison.Ordinal)),
        "http.response.status_code" => value is int status && status is >= 100 and <= 599,
        "bujo.chat.tool_call_count" or "bujo.chat.proposal_count" => value is int count && count >= 0,
        "gen_ai.usage.input_tokens" or "gen_ai.usage.output_tokens" => value is long count && count >= 0,
        _ => false
    };

    protected override bool OnForceFlush(int timeoutMilliseconds) => inner.ForceFlush(timeoutMilliseconds);
    protected override bool OnShutdown(int timeoutMilliseconds) => inner.Shutdown(timeoutMilliseconds);
    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}

public sealed class SafeHttpTelemetryHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var activity = SafeTelemetry.Source.StartActivity("http.client", ActivityKind.Client);
        activity?.SetTag("http.request.method", SafeTelemetry.SafeMethod(request.Method.Method));
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            activity?.SetTag("http.response.status_code", (int)response.StatusCode);
            if ((int)response.StatusCode >= 400)
                activity?.SetStatus(ActivityStatusCode.Error);
            return response;
        }
        catch
        {
            activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
    }
}
