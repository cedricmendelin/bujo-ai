using Bujo.Application;
using Bujo.Domain;
using System.Globalization;

namespace Bujo.Host;

public static class JournalEndpoints
{
    public static IEndpointRouteBuilder MapJournalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api");

        api.MapGet("/journal", (JournalApplicationService journal) => Results.Ok(journal.GetSnapshot()));
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
            var weekKey = ParseWeekKey(request.WeekKey);
            return weekKey.IsSuccess
                ? ToHttpResult(journal.CreateList(request.Name, request.Kind, request.GoalText, weekKey.Value))
                : Error(weekKey.Error!);
        });
        api.MapPost("/items", (CaptureRequest request, JournalApplicationService journal) =>
        {
            var period = ParsePeriod(request.PlannedPeriod);
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
            var period = ParsePeriod(request);
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
        var statusCode = error.Code switch
        {
            DomainErrorCode.NotFound => StatusCodes.Status404NotFound,
            DomainErrorCode.InvalidInput or DomainErrorCode.InvalidTimeZone => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status409Conflict
        };

        return Results.Problem(
            statusCode: statusCode,
            title: error.Code.ToString(),
            detail: error.Message,
            extensions: new Dictionary<string, object?> { ["code"] = error.Code.ToString() });
    }

    private static IResult QueryFailure() =>
        Results.Problem(
            statusCode: StatusCodes.Status500InternalServerError,
            title: "QueryFailed",
            detail: "The journal query could not be completed.",
            extensions: new Dictionary<string, object?> { ["code"] = "QueryFailed" });

    private static DomainResult<PlannedPeriod> ParsePeriod(PeriodRequest? request)
    {
        if (request is null || string.Equals(request.Kind, "unplanned", StringComparison.OrdinalIgnoreCase))
        {
            return DomainResult<PlannedPeriod>.Success(PlannedPeriod.Unplanned);
        }

        try
        {
            var period = request.Kind?.ToLowerInvariant() switch
            {
                "day" when DateOnly.TryParseExact(
                    request.Day,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var day) => PlannedPeriod.ForDay(day),
                "week" when request.WeekYear.HasValue && request.Week.HasValue =>
                    PlannedPeriod.ForWeek(new IsoWeekKey(request.WeekYear.Value, request.Week.Value)),
                "month" when request.Year.HasValue && request.Month.HasValue =>
                    PlannedPeriod.ForMonth(request.Year.Value, request.Month.Value),
                "quarter" when request.Year.HasValue && request.Quarter.HasValue =>
                    PlannedPeriod.ForQuarter(request.Year.Value, request.Quarter.Value),
                "year" when request.Year.HasValue => PlannedPeriod.ForYear(request.Year.Value),
                _ => throw new DomainException(DomainErrorCode.InvalidInput, "The planned period is invalid.")
            };
            return DomainResult<PlannedPeriod>.Success(period);
        }
        catch (DomainException exception)
        {
            return DomainResult<PlannedPeriod>.Failure(exception.ToError());
        }
    }

    private static DomainResult<IsoWeekKey?> ParseWeekKey(WeekKeyRequest? request)
    {
        if (request is null)
        {
            return DomainResult<IsoWeekKey?>.Success(null);
        }

        try
        {
            return DomainResult<IsoWeekKey?>.Success(new IsoWeekKey(request.Year, request.Week));
        }
        catch (DomainException exception)
        {
            return DomainResult<IsoWeekKey?>.Failure(exception.ToError());
        }
    }

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
