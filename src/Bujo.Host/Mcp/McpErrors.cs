using System.Text.Json;
using System.Text.Json.Serialization;
using Bujo.Domain;
using ModelContextProtocol.Protocol;

namespace Bujo.Host.Mcp;

internal static class McpErrors
{
    private static readonly JsonSerializerOptions SuccessJsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly JsonSerializerOptions ErrorJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static CallToolResult From<T>(DomainResult<T> result) =>
        result.IsSuccess
            ? Success(result.Value)
            : Error(result.Error!);

    public static CallToolResult Success<T>(T value) => Result(value, isError: false, SuccessJsonOptions);

    public static CallToolResult Error(DomainError error) =>
        Error(JournalContract.ErrorCode(error.Code), error.Message);

    public static CallToolResult Error(
        DomainErrorCode code,
        string message) =>
        Error(JournalContract.ErrorCode(code), message);

    public static CallToolResult Error(
        string code,
        string message,
        IReadOnlyList<JournalListSnapshot>? candidates = null) =>
        Result(new McpToolError(code, message, candidates), isError: true, ErrorJsonOptions);

    private static CallToolResult Result<T>(
        T value,
        bool isError,
        JsonSerializerOptions serializerOptions)
    {
        var structuredContent = JsonSerializer.SerializeToElement(value, serializerOptions);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = structuredContent.GetRawText() }],
            StructuredContent = structuredContent,
            IsError = isError
        };
    }

    private sealed record McpToolError(
        string Code,
        string Message,
        IReadOnlyList<JournalListSnapshot>? Candidates = null);
}
