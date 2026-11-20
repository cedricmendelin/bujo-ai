using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Bujo.Application.Chat;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Bujo.Host.Chat;

public sealed record ChatAgUiRequest(
    string Message,
    string? TimeZone,
    IReadOnlyList<ChatTurn>? History,
    string? ThreadId);

public sealed record ChatSpeechTokenResponse(
    string Token,
    string? Region,
    string? Endpoint,
    DateTimeOffset ExpiresAt);

public static class ChatEndpoints
{
    public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/chat/agui", async (
            ChatAgUiRequest request,
            HttpContext context,
            ClaimsPrincipal principal,
            IChatRuntime runtime,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            if (!configuration.GetValue<bool>("Chat:Enabled"))
            {
                return Results.NotFound();
            }

            if (string.IsNullOrWhiteSpace(request.Message))
            {
                return Results.BadRequest(new { error = "A non-empty message is required." });
            }

            var threadId = string.IsNullOrWhiteSpace(request.ThreadId) || request.ThreadId.Trim().Length > 128
                ? Guid.NewGuid().ToString("D")
                : request.ThreadId.Trim();
            var runId = Guid.NewGuid();
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-store";

            await WriteEventAsync(context, "run_started", new
            {
                runId,
                threadId
            }, cancellationToken);

            try
            {
                var result = await runtime.RunAsync(new ChatRunRequest(
                    request.Message,
                    request.TimeZone,
                    request.History ?? [],
                    threadId), principal, cancellationToken);

                foreach (var toolCall in result.ToolCalls)
                {
                    await WriteEventAsync(context, "tool_call_start", new
                    {
                        name = toolCall.Name
                    }, cancellationToken);
                    await WriteEventAsync(context, "tool_call_result", new
                    {
                        name = toolCall.Name,
                        succeeded = toolCall.Succeeded,
                        summary = toolCall.Summary
                    }, cancellationToken);
                }

                foreach (var proposal in result.Proposals)
                {
                    await WriteEventAsync(context, "approval_card", new
                    {
                        proposalId = proposal.ProposalId,
                        digest = proposal.Digest,
                        summary = proposal.Summary,
                        expiresAt = proposal.ExpiresAt
                    }, cancellationToken);
                }

                foreach (var line in SplitText(result.Text))
                {
                    await WriteEventAsync(context, "text_message_content", new
                    {
                        text = line
                    }, cancellationToken);
                }

                await WriteEventAsync(context, "run_finished", new
                {
                    runId,
                    cancelled = false
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await WriteEventAsync(context, "run_finished", new
                {
                    runId,
                    cancelled = true
                }, CancellationToken.None);
            }
            catch (Exception)
            {
                await WriteEventAsync(context, "run_error", new
                {
                    code = "ChatProviderUnavailable",
                    message = "The chat run could not be completed."
                }, CancellationToken.None);
            }

            return Results.Empty;
        }).RequireAuthorization("ApprovedPrincipal").RequireRateLimiting("chat");

        endpoints.MapPost("/api/chat/speech-token", async (
            HttpContext context,
            IConfiguration configuration,
            IHttpClientFactory httpClientFactory,
            CancellationToken cancellationToken) =>
        {
            if (!configuration.GetValue<bool>("Chat:Enabled"))
            {
                return Results.NotFound();
            }

            var region = configuration["Chat:SpeechRegion"];
            var endpoint = configuration["Chat:SpeechEndpoint"];
            if (string.IsNullOrWhiteSpace(region) && string.IsNullOrWhiteSpace(endpoint))
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            var credential = new DefaultAzureCredential();
            var accessToken = await credential.GetTokenAsync(
                new TokenRequestContext(["https://cognitiveservices.azure.com/.default"]),
                cancellationToken);

            var issuerEndpoint = !string.IsNullOrWhiteSpace(endpoint)
                ? $"{endpoint.TrimEnd('/')}/sts/v1.0/issueToken"
                : $"https://{region!.Trim().ToLowerInvariant()}.api.cognitive.microsoft.com/sts/v1.0/issueToken";

            using var http = httpClientFactory.CreateClient("ChatSpeech");
            using var request = new HttpRequestMessage(HttpMethod.Post, issuerEndpoint);
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", accessToken.Token);
            request.Content = new StringContent("", Encoding.UTF8, "application/json");

            using var response = await http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            var token = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(token))
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            var expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
            return Results.Ok(new ChatSpeechTokenResponse(
                token,
                region,
                endpoint,
                expiresAt));
        }).RequireAuthorization("ApprovedPrincipal").RequireRateLimiting("chat");

        return endpoints;
    }

    private static async Task WriteEventAsync(
        HttpContext context,
        string eventName,
        object payload,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes($"event: {eventName}\ndata: {json}\n\n");
        await context.Response.Body.WriteAsync(bytes, cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
    }

    private static IEnumerable<string> SplitText(string text)
    {
        if (text.Length == 0)
        {
            yield break;
        }

        const int size = 64;
        for (var index = 0; index < text.Length; index += size)
        {
            yield return text.Substring(index, Math.Min(size, text.Length - index));
        }
    }
}
