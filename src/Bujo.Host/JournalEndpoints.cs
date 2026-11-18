using Bujo.Application;
using Bujo.Domain;

namespace Bujo.Host;

public static class JournalEndpoints
{
    public static IEndpointRouteBuilder MapJournalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api");

        api.MapGet("/journal", (
            JournalApplicationService journal,
            ILoggerFactory loggerFactory) =>
        {
            try
            {
                return Results.Ok(journal.GetSnapshot());
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                loggerFactory.CreateLogger("JournalEndpoints").LogError("Journal snapshot query failed.");
                return QueryFailure();
            }
        });
        api.MapGet("/journal/export", (
            JournalApplicationService journal,
            IJournalExport export,
            ILoggerFactory loggerFactory) =>
        {
            try
            {
                return Results.Ok(export.Create(journal.GetSnapshot()));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                loggerFactory.CreateLogger("JournalEndpoints").LogError("Journal export query failed.");
                return QueryFailure();
            }
        }).Produces<JournalExportDocument>();
        api.MapGet("/daily-start", (
            string? timeZone,
            JournalApplicationService journal,
            ILoggerFactory loggerFactory) =>
        {
            try
            {
                return ToHttpResult(journal.GetDailyStart(timeZone));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                loggerFactory.CreateLogger("JournalEndpoints").LogError("Daily-start query failed.");
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
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                loggerFactory.CreateLogger("JournalEndpoints").LogError("Upcoming-planning query failed.");
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
