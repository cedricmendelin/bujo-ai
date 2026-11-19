using Bujo.Application;
using Bujo.Domain;
using Bujo.Infrastructure.Persistence;
using System.Security.Claims;
using System.Text.Json;

namespace Bujo.Host;

public static class JournalEndpoints
{
    public static IEndpointRouteBuilder MapJournalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api");

        api.MapGet("/journal", (JournalApplicationService journal) => Results.Ok(journal.GetSnapshot()));
        api.MapGet("/journal/export", (
            JournalBackupApplicationService backupService,
            JournalBackupSerializer serializer) =>
        {
            try
            {
                return Results.Content(serializer.Serialize(backupService.ExportBackup()), "application/json");
            }
            catch (JsonException)
            {
                return Results.StatusCode(StatusCodes.Status413RequestEntityTooLarge);
            }
        });
        api.MapPost("/journal/restore", async (
            HttpRequest request,
            JournalBackupApplicationService backupService,
            JournalBackupSerializer serializer,
            CancellationToken cancellationToken) =>
        {
            if (request.ContentLength is > DomainLimits.JournalBackupBytes)
            {
                return Results.StatusCode(StatusCodes.Status413RequestEntityTooLarge);
            }

            try
            {
                using var reader = new StreamReader(request.Body);
                var backup = serializer.Deserialize(await reader.ReadToEndAsync(cancellationToken));
                return ToHttpResult(backupService.RestoreBackup(backup));
            }
            catch (JsonException)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "InvalidInput",
                    detail: "The journal backup is invalid.",
                    extensions: new Dictionary<string, object?> { ["code"] = "InvalidInput" });
            }
        });
        api.MapGet("/daily-start", (
            string? timeZone,
            JournalApplicationService journal,
            ILoggerFactory loggerFactory) =>
        {
            try
            {
                return ToHttpResult(journal.GetDailyStart(timeZone));
            }
            catch (Exception exception)
            {
                loggerFactory.CreateLogger("JournalEndpoints").LogError(exception, "Daily-start query failed.");
                return QueryFailure();
            }
        });
        api.MapGet("/planning/upcoming", (
            string? timeZone,
            int? horizon,
            int? page,
            int? limit,
            bool? futureLog,
            JournalApplicationService journal,
            ILoggerFactory loggerFactory) =>
        {
            try
            {
                return ToHttpResult(journal.GetUpcoming(
                    timeZone,
                    horizon ?? 14,
                    page ?? 1,
                    limit ?? 50,
                    futureLog ?? false));
            }
            catch (Exception exception)
            {
                loggerFactory.CreateLogger("JournalEndpoints").LogError(exception, "Upcoming-planning query failed.");
                return QueryFailure();
            }
        });
        api.MapPost("/lists", (CreateListRequest request, JournalApplicationService journal) =>
        {
            var weekKey = JournalContract.ParseWeekKey(request.WeekKey);
            return weekKey.IsSuccess
                ? ToHttpResult(journal.CreateList(request.Name, request.Kind, request.GoalText, weekKey.Value))
                : Error(weekKey.Error!);
        });
        api.MapPost("/items", (CaptureRequest request, JournalApplicationService journal) =>
        {
            var period = JournalContract.ParsePeriod(request.PlannedPeriod);
            return period.IsSuccess
                ? ToHttpResult(journal.Capture(request.Name, request.Description, request.Tags, period.Value, request.ListId))
                : Error(period.Error!);
        });
        api.MapPost("/items/{itemId:guid}/move", (Guid itemId, MoveRequest request, JournalApplicationService journal) =>
            ToHttpResult(journal.Move(request.OperationId, itemId, request.ExpectedOccurrenceId, request.DestinationListId)));
        api.MapPost("/occurrences/{occurrenceId:guid}/transition", (Guid occurrenceId, TransitionRequest request, JournalApplicationService journal) =>
            ToHttpResult(journal.Transition(occurrenceId, request.Action)));
        api.MapPost("/lists/{listId:guid}/close", (Guid listId, JournalApplicationService journal) =>
            ToHttpResult(journal.CloseList(listId)));
        api.MapPost("/lists/{listId:guid}/reopen", (Guid listId, JournalApplicationService journal) =>
            ToHttpResult(journal.ReopenList(listId)));
        api.MapPut("/items/{itemId:guid}", (Guid itemId, EditItemRequest request, JournalApplicationService journal) =>
            ToHttpResult(journal.EditItem(itemId, request.Name, request.Description, request.Tags)));
        api.MapPut("/items/{itemId:guid}/planned-period", (Guid itemId, PeriodRequest request, JournalApplicationService journal) =>
        {
            var period = JournalContract.ParsePeriod(request);
            return period.IsSuccess
                ? ToHttpResult(journal.ChangePlannedPeriod(itemId, period.Value!))
                : Error(period.Error!);
        });
        api.MapPut("/lists/{listId:guid}/goal", (Guid listId, GoalRequest request, JournalApplicationService journal) =>
            ToHttpResult(journal.SetListGoal(listId, request.GoalText)));
        api.MapGet("/occurrences/{occurrenceId:guid}/resolution", (Guid occurrenceId, JournalApplicationService journal) =>
            ToHttpResult(journal.Resolve(occurrenceId)));
        api.MapGet("/lists/{listId:guid}/review", (Guid listId, ReviewApplicationService review) =>
            ToHttpResult(review.GetListReview(listId)));
        api.MapPost("/review-proposals", (
            ReviewProposalRequest request,
            ClaimsPrincipal principal,
            ReviewApplicationService review) =>
            ToHttpResult(review.CreateProposal(
                request,
                ReviewActor.FromPrincipal(principal, ReviewProposalSource.Pwa, interactive: false))));
        api.MapGet("/review-proposals/{proposalId:guid}", (
            Guid proposalId,
            ClaimsPrincipal principal,
            ReviewApplicationService review) =>
            ToHttpResult(review.GetProposal(
                proposalId,
                ReviewActor.FromPrincipal(principal, ReviewProposalSource.Pwa, interactive: false))));
        api.MapPost("/review-proposals/{proposalId:guid}/cancel", (
            Guid proposalId,
            ClaimsPrincipal principal,
            ReviewApplicationService review) =>
            ToHttpResult(review.CancelProposal(
                proposalId,
                ReviewActor.FromPrincipal(principal, ReviewProposalSource.Pwa, interactive: false))));
        api.MapPost("/review-proposals/{proposalId:guid}/reject", (
            Guid proposalId,
            ClaimsPrincipal principal,
            ReviewApplicationService review) =>
            ToHttpResult(review.RejectProposal(
                proposalId,
                ReviewActor.FromPrincipal(principal, ReviewProposalSource.Pwa, interactive: false))));
        api.MapPost("/review-proposals/{proposalId:guid}/apply", (
            Guid proposalId,
            ClaimsPrincipal principal,
            ReviewApplicationService review) =>
            ToHttpResult(review.ApplyProposal(
                proposalId,
                ReviewActor.FromPrincipal(principal, ReviewProposalSource.Pwa, interactive: false))));

        return endpoints;
    }

    private static IResult ToHttpResult<T>(DomainResult<T> result) =>
        result.IsSuccess
            ? Results.Ok(result.Value)
            : Error(result.Error!);

    private static IResult Error(DomainError error)
    {
        return Results.Problem(
            statusCode: JournalContract.StatusCode(error.Code),
            title: JournalContract.ErrorCode(error.Code),
            detail: error.Message,
            extensions: new Dictionary<string, object?> { ["code"] = JournalContract.ErrorCode(error.Code) });
    }

    private static IResult QueryFailure() =>
        Results.Problem(
            statusCode: StatusCodes.Status500InternalServerError,
            title: "QueryFailed",
            detail: "The journal query could not be completed.",
            extensions: new Dictionary<string, object?> { ["code"] = "QueryFailed" });

    public sealed record CreateListRequest(string Name, JournalListKind Kind, string? GoalText, WeekKeyRequest? WeekKey);

    public sealed record WeekKeyRequest(int Year, int Week);

    public sealed record CaptureRequest(
        string Name,
        string? Description,
        IReadOnlyList<string>? Tags,
        PeriodRequest? PlannedPeriod,
        Guid? ListId);

    public sealed record MoveRequest(Guid OperationId, Guid ExpectedOccurrenceId, Guid DestinationListId);

    public sealed record TransitionRequest(OccurrenceAction Action);

    public sealed record EditItemRequest(string Name, string? Description, IReadOnlyList<string>? Tags);

    public sealed record GoalRequest(string? GoalText);

    public sealed record PeriodRequest(
        string? Kind,
        string? Day = null,
        int? WeekYear = null,
        int? Week = null,
        int? Year = null,
        int? Month = null,
        int? Quarter = null);
}
