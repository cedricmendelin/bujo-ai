using Bujo.Application;
using Bujo.Domain;
using Bujo.Infrastructure.Persistence;
using System.Text.Json;

namespace Bujo.Host;

public static class KnowledgeEndpoints
{
    public static IEndpointRouteBuilder MapKnowledgeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/knowledge");
        api.MapPost("", (CreateKnowledgeRequest request, KnowledgeApplicationService knowledge) =>
            ToHttpResult(knowledge.Create(request.Content, request.Title, request.SourceReference, request.Tags)));
        api.MapGet("/export", (KnowledgeApplicationService knowledge, KnowledgeBackupSerializer serializer) =>
        {
            try
            {
                return Results.Content(serializer.Serialize(knowledge.ExportBackup()), "application/json");
            }
            catch (JsonException)
            {
                return Results.StatusCode(StatusCodes.Status413RequestEntityTooLarge);
            }
        });
        api.MapPost("/restore", async (
            HttpRequest request,
            KnowledgeApplicationService knowledge,
            KnowledgeBackupSerializer serializer,
            CancellationToken cancellationToken) =>
        {
            if (request.ContentLength is > DomainLimits.KnowledgeBackupBytes)
            {
                return Results.StatusCode(StatusCodes.Status413RequestEntityTooLarge);
            }

            try
            {
                using var reader = new StreamReader(request.Body);
                var json = await reader.ReadToEndAsync(cancellationToken);
                var backup = serializer.Deserialize(json);
                return ToHttpResult(knowledge.RestoreBackup(backup));
            }
            catch (JsonException)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "InvalidInput",
                    detail: "The knowledge backup is invalid.",
                    extensions: new Dictionary<string, object?> { ["code"] = "InvalidInput" });
            }
        });
        api.MapGet("/{id:guid}", (Guid id, KnowledgeApplicationService knowledge) =>
            ToHttpResult(knowledge.Get(id)));
        api.MapPut("/{id:guid}", (Guid id, UpdateKnowledgeRequest request, KnowledgeApplicationService knowledge) =>
            ToHttpResult(knowledge.Update(
                id,
                request.Content,
                request.Title,
                request.SourceReference,
                request.Tags,
                request.ExpectedVersion)));
        api.MapPost("/{id:guid}/archive", (Guid id, ArchiveKnowledgeRequest request, KnowledgeApplicationService knowledge) =>
            ToHttpResult(knowledge.SetArchived(id, request.Archived, request.ExpectedVersion)));
        api.MapDelete("/{id:guid}", (Guid id, long expectedVersion, KnowledgeApplicationService knowledge) =>
            ToHttpResult(knowledge.Delete(id, expectedVersion)));
        api.MapGet("/search", (
            string? text,
            string[]? tags,
            bool? includeArchived,
            int? pageSize,
            string? cursor,
            KnowledgeApplicationService knowledge,
            ILoggerFactory loggerFactory) =>
        {
            try
            {
                return ToHttpResult(knowledge.Search(new KnowledgeSearchRequest(
                    text,
                    tags,
                    includeArchived ?? false,
                    pageSize ?? 20,
                    cursor)));
            }
            catch (Exception)
            {
                loggerFactory.CreateLogger("KnowledgeEndpoints")
                    .LogError("Knowledge search failed.");
                return Error(new DomainError(
                    DomainErrorCode.KnowledgeSearchUnavailable,
                    "Knowledge search is temporarily unavailable."));
            }
        });
        api.MapGet("/tags", (KnowledgeApplicationService knowledge) => Results.Ok(knowledge.GetTags()));
        api.MapGet("/search-status", (KnowledgeApplicationService knowledge) =>
            Results.Ok(new KnowledgeSearchStatus(knowledge.GetIndexState())));

        return endpoints;
    }

    private static IResult ToHttpResult<T>(DomainResult<T> result) =>
        result.IsSuccess
            ? Results.Ok(result.Value)
            : Error(result.Error!);

    private static IResult Error(DomainError error) =>
        Results.Problem(
            statusCode: JournalContract.StatusCode(error.Code),
            title: JournalContract.ErrorCode(error.Code),
            detail: error.Message,
            extensions: new Dictionary<string, object?> { ["code"] = JournalContract.ErrorCode(error.Code) });

    public sealed record CreateKnowledgeRequest(
        string Content,
        string? Title = null,
        string? SourceReference = null,
        IReadOnlyList<string>? Tags = null);

    public sealed record UpdateKnowledgeRequest(
        string Content,
        string? Title,
        string? SourceReference,
        IReadOnlyList<string>? Tags,
        long ExpectedVersion);

    public sealed record ArchiveKnowledgeRequest(bool Archived, long ExpectedVersion);

    public sealed record KnowledgeSearchStatus(KnowledgeIndexState IndexState);
}
