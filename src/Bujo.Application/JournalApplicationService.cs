using Bujo.Domain;

namespace Bujo.Application;

public sealed class JournalApplicationService
{
    private readonly JournalService _journal;
    private readonly TimeProvider _clock;

    public JournalApplicationService(JournalService journal, TimeProvider? clock = null)
    {
        _journal = journal;
        _clock = clock ?? TimeProvider.System;
    }

    public JournalSnapshot GetSnapshot() => _journal.GetSnapshot();

    public DomainResult<DailyStartReview> GetDailyStart(string? timeZoneId)
    {
        var timeZone = FindTimeZone(timeZoneId);
        return timeZone.IsSuccess
            ? PlannedReviewQueries.GetDailyStart(_journal.GetSnapshot(), _clock.GetUtcNow(), timeZone.Value!)
            : DomainResult<DailyStartReview>.Failure(timeZone.Error!);
    }

    public DomainResult<UpcomingPlan> GetUpcoming(
        string? timeZoneId,
        int horizonDays = 14,
        int page = 1,
        int pageSize = 50,
        bool futureLog = false)
    {
        var timeZone = FindTimeZone(timeZoneId);
        return timeZone.IsSuccess
            ? PlannedReviewQueries.GetUpcoming(
                _journal.GetSnapshot(),
                _clock.GetUtcNow(),
                timeZone.Value!,
                horizonDays,
                page,
                pageSize,
                futureLog)
            : DomainResult<UpcomingPlan>.Failure(timeZone.Error!);
    }

    public DomainResult<IsoWeekKey> GetNextWeek(string? timeZoneId)
    {
        var timeZone = FindTimeZone(timeZoneId);
        if (!timeZone.IsSuccess)
        {
            return DomainResult<IsoWeekKey>.Failure(timeZone.Error!);
        }

        try
        {
            var localToday = DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), timeZone.Value!).DateTime);
            var nextMonday = IsoWeekKey.FromDate(localToday).Monday.AddDays(7);
            return DomainResult<IsoWeekKey>.Success(IsoWeekKey.FromDate(nextMonday));
        }
        catch (ArgumentOutOfRangeException)
        {
            return DomainResult<IsoWeekKey>.Failure(new DomainError(
                DomainErrorCode.InvalidInput,
                "The next ISO week is outside the supported calendar range."));
        }
    }

    public DomainResult<JournalListSnapshot> CreateList(
        string name,
        JournalListKind kind,
        string? goalText = null,
        IsoWeekKey? weekKey = null,
        JournalPeriodKey? periodKey = null) =>
        _journal.CreateList(name, kind, goalText, weekKey, periodKey);

    public DomainResult<JournalPeriodNavigation> NavigatePeriod(
        JournalListKind kind, int? year, int? month, int? quarter, string? direction, string? timeZone)
    {
        try
        {
            JournalPeriodKey key;
            if (string.Equals(direction, "current", StringComparison.OrdinalIgnoreCase))
            {
                var zone = FindTimeZone(timeZone);
                if (!zone.IsSuccess)
                    return DomainResult<JournalPeriodNavigation>.Failure(zone.Error!);
                var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_clock.GetUtcNow(), zone.Value!).DateTime);
                key = JournalPeriodKey.FromDate(kind, today);
            }
            else
            {
                key = new JournalPeriodKey(kind, year ?? 0, month, quarter);
                key = direction?.ToLowerInvariant() switch
                {
                    "previous" => key.Previous() ?? throw new DomainException(DomainErrorCode.InvalidInput, "No previous period is in the supported calendar."),
                    "next" => key.Next() ?? throw new DomainException(DomainErrorCode.InvalidInput, "No next period is in the supported calendar."),
                    "selected" => key,
                    _ => throw new DomainException(DomainErrorCode.InvalidInput, "Direction must be selected, previous, next, or current.")
                };
            }

            var lists = _journal.GetSnapshot().Lists.Where(list => list.PeriodKey == key)
                .OrderBy(list => list.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            return DomainResult<JournalPeriodNavigation>.Success(new(key, key.Label, key.Range, lists));
        }
        catch (DomainException exception)
        {
            return DomainResult<JournalPeriodNavigation>.Failure(exception.ToError());
        }
    }

    public DomainResult<CaptureResult> Capture(
        string name,
        string? description,
        IReadOnlyList<string>? tags,
        PlannedPeriod? plannedPeriod,
        Guid? listId,
        ItemType type = ItemType.Task) =>
        _journal.Capture(name, description, tags, plannedPeriod, listId, type);

    public DomainResult<MoveResult> Move(
        Guid operationId,
        Guid itemId,
        Guid expectedCurrentOccurrenceId,
        Guid destinationListId) =>
        _journal.Move(operationId, itemId, expectedCurrentOccurrenceId, destinationListId);

    public DomainResult<OccurrenceSnapshot> Transition(Guid occurrenceId, OccurrenceAction action) =>
        _journal.Transition(occurrenceId, action);

    public DomainResult<JournalListSnapshot> CloseList(Guid listId) => _journal.CloseList(listId);

    public DomainResult<JournalListSnapshot> ReopenList(Guid listId) => _journal.ReopenList(listId);

    public DomainResult<ItemSnapshot> EditItem(
        Guid itemId,
        string name,
        string? description,
        IReadOnlyList<string>? tags) =>
        _journal.EditItem(itemId, name, description, tags);

    public DomainResult<ItemSnapshot> ChangePlannedPeriod(Guid itemId, PlannedPeriod period) =>
        _journal.ChangePlannedPeriod(itemId, period);

    public DomainResult<JournalListSnapshot> SetListGoal(Guid listId, string? goalText) =>
        _journal.SetListGoal(listId, goalText);

    public DomainResult<ItemResolution> Resolve(Guid occurrenceId) => _journal.Resolve(occurrenceId);

    private static DomainResult<TimeZoneInfo> FindTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return InvalidTimeZone(timeZoneId);
        }

        try
        {
            return DomainResult<TimeZoneInfo>.Success(TimeZoneInfo.FindSystemTimeZoneById(timeZoneId));
        }

        catch (TimeZoneNotFoundException)
        {
            return InvalidTimeZone(timeZoneId);
        }
        catch (InvalidTimeZoneException)
        {
            return InvalidTimeZone(timeZoneId);
        }
        catch (ArgumentException)
        {
            return InvalidTimeZone(timeZoneId);
        }
    }

    private static DomainResult<TimeZoneInfo> InvalidTimeZone(string? timeZoneId) =>
        DomainResult<TimeZoneInfo>.Failure(new DomainError(
            DomainErrorCode.InvalidTimeZone,
            string.IsNullOrWhiteSpace(timeZoneId)
                ? "An IANA time zone is required."
                : $"Unknown or invalid IANA time zone: {timeZoneId}."));
}

public sealed record JournalPeriodNavigation(
    JournalPeriodKey Key,
    string Label,
    CalendarDateRange Range,
    IReadOnlyList<JournalListSnapshot> Lists);
