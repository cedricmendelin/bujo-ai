using System.Security.Claims;
using System.Text.Json;

namespace Bujo.Application.Chat;

public sealed record ChatTurn(ChatRole Role, string Text);

public enum ChatRole
{
    User,
    Assistant
}

public sealed record ChatRunRequest(
    string Message,
    string? TimeZone,
    IReadOnlyList<ChatTurn> History);

public sealed record ChatToolCall(
    string Name,
    bool Succeeded,
    string Summary);

public sealed record ChatProposalCard(
    Guid ProposalId,
    string Digest,
    string Summary,
    DateTimeOffset ExpiresAt);

public sealed record ChatRunResult(
    string Text,
    IReadOnlyList<ChatToolCall> ToolCalls,
    IReadOnlyList<ChatProposalCard> Proposals);

public interface IChatRuntime
{
    Task<ChatRunResult> RunAsync(
        ChatRunRequest request,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default);
}

public sealed record ChatToolRequest(
    string Name,
    JsonElement Arguments,
    ClaimsPrincipal Principal);

public sealed record ChatToolResult(
    bool Succeeded,
    string Summary,
    object? Value);
