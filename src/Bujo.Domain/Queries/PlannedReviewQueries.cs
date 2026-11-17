namespace Bujo.Domain;

public sealed record ReviewDestination(
    Guid ListId,
    string ListName,
    Guid OccurrenceId,
    string State);

public sealed record PlannedReviewItem(
    Guid ItemId,
    string Text,
    IReadOnlyList<string> Tags,
    PlannedPeriod PlannedPeriod,
    string ReasonCode,
    string Reason,
    ReviewDestination Destination,
    bool Conflict);

public sealed record DailyStartGroup(
    string Code,
    string Label,
    IReadOnlyList<PlannedReviewItem> Items);

public sealed record DailyStartReview(
    DateOnly Today,
    IReadOnlyList<DailyStartGroup> Groups,
    int FutureLogCount,
    int UnplannedCount);

public sealed record UpcomingPlanGroup(
    PlannedPeriod PlannedPeriod,
    IReadOnlyList<PlannedReviewItem> Items);

public sealed record UpcomingPlan(
    DateOnly Today,
    int HorizonDays,
    bool FutureLog,
    int Page,
    int PageSize,
    int TotalCount,
    bool Truncated,
    IReadOnlyList<UpcomingPlanGroup> Groups);

public static class PlannedReviewQueries
{
    private const int DefaultHorizonDays = 14;
    private const int MaximumHorizonDays = 90;
    private const int DefaultPageSize = 50;
    private const int MaximumPageSize = 50;

    public static DomainResult<DailyStartReview> GetDailyStart(
        JournalSnapshot snapshot,
        DateTimeOffset now,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(timeZone);

        var today = LocalDate(now, timeZone);
        var currentWeek = IsoWeekKey.FromDate(today);
        var currentWeekStart = currentWeek.Monday;
        var resolutions = ResolveCurrentItems(snapshot);
        if (!resolutions.IsSuccess)
        {
            return DomainResult<DailyStartReview>.Failure(resolutions.Error!);
        }

        var active = resolutions.Value!.Where(item => item.CurrentOccurrence.State == OccurrenceState.Open).ToArray();
        var overdue = new List<OrderedReviewItem>();
        var todayItems = new List<OrderedReviewItem>();
        var thisWeek = new List<OrderedReviewItem>();

        foreach (var resolved in active)
        {
            var period = resolved.Item.PlannedPeriod;
            var range = PlannedPeriodRanges.For(period);
            if (range is null)
            {
                continue;
            }

            List<OrderedReviewItem>? target = null;
            string? reasonCode = null;
            string? reason = null;
            switch (period.Kind)
            {
                case PlannedPeriodKind.Day when period.Day is { } plannedDay && plannedDay < today:
                    target = overdue;
                    reasonCode = "MissedDay";
                    reason = $"Planned day {plannedDay:yyyy-MM-dd} has passed.";
                    break;
                case PlannedPeriodKind.Day when period.Day == today:
                    target = todayItems;
                    reasonCode = "PlannedForToday";
                    reason = $"Planned for today ({today:yyyy-MM-dd}).";
                    break;
                case PlannedPeriodKind.Week when period.Week == currentWeek:
                    target = thisWeek;
                    reasonCode = "PlannedForThisWeek";
                    reason = $"Planned for this ISO week ({currentWeek}).";
                    break;
                case PlannedPeriodKind.Week when range.Value.End < currentWeekStart:
                    target = overdue;
                    reasonCode = "MissedWeek";
                    reason = $"Planned ISO week {period.Week} has ended.";
                    break;
                case PlannedPeriodKind.Month when range.Value.End < today:
                    target = overdue;
                    reasonCode = "MissedMonth";
                    reason = $"Planned month {period.Year:D4}-{period.Month:D2} has ended.";
                    break;
                case PlannedPeriodKind.Quarter when range.Value.End < today:
                    target = overdue;
                    reasonCode = "MissedQuarter";
                    reason = $"Planned quarter {period.Year}-Q{period.Quarter} has ended.";
                    break;
                case PlannedPeriodKind.Year when range.Value.End < today:
                    target = overdue;
                    reasonCode = "MissedYear";
                    reason = $"Planned year {period.Year} has ended.";
                    break;
            }

            if (target is not null)
            {
                target.Add(new OrderedReviewItem(
                    resolved,
                    range.Value.Start,
                    reasonCode!,
                    reason!));
            }
        }

        return DomainResult<DailyStartReview>.Success(new DailyStartReview(
            today,
            [
                CreateGroup("Overdue", "Overdue", overdue),
                CreateGroup("Today", "Today", todayItems),
                CreateGroup("ThisWeek", "This week", thisWeek)
            ],
            active.Count(item => item.CurrentList.Kind == JournalListKind.FutureLog),
            active.Count(item => item.Item.PlannedPeriod.Kind == PlannedPeriodKind.Unplanned)));
    }

    public static DomainResult<UpcomingPlan> GetUpcoming(
        JournalSnapshot snapshot,
        DateTimeOffset now,
        TimeZoneInfo timeZone,
        int horizonDays = DefaultHorizonDays,
        int page = 1,
        int pageSize = DefaultPageSize,
        bool futureLog = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(timeZone);
        if (horizonDays is < 1 or > MaximumHorizonDays)
        {
            return InvalidUpcoming("The horizon must be between 1 and 90 days.");
        }

        if (page < 1)
        {
            return InvalidUpcoming("The page must be at least 1.");
        }

        if (pageSize is < 1 or > MaximumPageSize)
        {
            return InvalidUpcoming("The page size must be between 1 and 50.");
        }

        var today = LocalDate(now, timeZone);
        var horizon = new CalendarDateRange(
            today,
            PlannedPeriodRanges.AddDaysClamped(today, horizonDays - 1));
        var resolutions = ResolveCurrentItems(snapshot);
        if (!resolutions.IsSuccess)
        {
            return DomainResult<UpcomingPlan>.Failure(resolutions.Error!);
        }

        var matching = new List<OrderedReviewItem>();
        foreach (var resolved in resolutions.Value!)
        {
            if (resolved.CurrentOccurrence.State != OccurrenceState.Open)
            {
                continue;
            }

            var period = resolved.Item.PlannedPeriod;
            if (futureLog)
            {
                if (resolved.CurrentList.Kind != JournalListKind.FutureLog)
                {
                    continue;
                }

                var range = PlannedPeriodRanges.For(period);
                matching.Add(new OrderedReviewItem(
                    resolved,
                    range?.Start ?? DateOnly.MaxValue,
                    "FutureLog",
                    "Open item in the Future Log."));
            }
            else
            {
                var range = PlannedPeriodRanges.For(period);
                if (range is null || !range.Value.Overlaps(horizon))
                {
                    continue;
                }

                matching.Add(new OrderedReviewItem(
                    resolved,
                    range.Value.Start,
                    UpcomingReasonCode(period.Kind),
                    $"Planned {PeriodDescription(period)} overlaps the next {horizonDays} days."));
            }
        }

        var ordered = matching.OrderBy(item => item, OrderedReviewItemComparer.Instance).ToArray();
        var totalCount = ordered.Length;
        var offset = (long)(page - 1) * pageSize;
        var pageItems = offset >= totalCount
            ? []
            : ordered.Skip((int)offset).Take(pageSize).ToArray();
        var groups = pageItems
            .GroupBy(item => item.Resolved.Item.PlannedPeriod)
            .OrderBy(group => PlannedPeriodRanges.For(group.Key)?.Start ?? DateOnly.MaxValue)
            .ThenBy(group => group.Key.Kind)
            .Select(group => new UpcomingPlanGroup(
                group.Key,
                group.Select(item => ToReviewItem(item.Resolved, item.ReasonCode, item.Reason)).ToArray()))
            .ToArray();

        return DomainResult<UpcomingPlan>.Success(new UpcomingPlan(
            today,
            horizonDays,
            futureLog,
            page,
            pageSize,
            totalCount,
            offset + pageSize < totalCount,
            groups));
    }

    private static DomainResult<UpcomingPlan> InvalidUpcoming(string message) =>
        DomainResult<UpcomingPlan>.Failure(new DomainError(DomainErrorCode.InvalidInput, message));

    private static PlannedReviewItem ToReviewItem(
        ItemResolution resolved,
        string reasonCode,
        string reason) =>
        new(
            resolved.Item.Id,
            resolved.Item.Name,
            resolved.Item.Tags.Select(tag => tag.Value).ToArray(),
            resolved.Item.PlannedPeriod,
            reasonCode,
            reason,
            new ReviewDestination(
                resolved.CurrentList.Id,
                resolved.CurrentList.Name,
                resolved.CurrentOccurrence.Id,
                resolved.CurrentOccurrence.State.ToString()),
            PeriodConflictRules.Find(resolved.Item.PlannedPeriod, resolved.CurrentList) is not null);

    private static DomainResult<IReadOnlyList<ItemResolution>> ResolveCurrentItems(JournalSnapshot snapshot)
    {
        var resolutions = new List<ItemResolution>(snapshot.Items.Count);
        foreach (var item in snapshot.Items)
        {
            var resolution = snapshot.Resolve(item.CurrentOccurrenceId);
            if (!resolution.IsSuccess)
            {
                return DomainResult<IReadOnlyList<ItemResolution>>.Failure(resolution.Error!);
            }

            resolutions.Add(resolution.Value!);
        }

        return DomainResult<IReadOnlyList<ItemResolution>>.Success(resolutions);
    }

    private static DailyStartGroup CreateGroup(string code, string label, IEnumerable<OrderedReviewItem> items)
    {
        var ordered = items.OrderBy(item => item, OrderedReviewItemComparer.Instance)
            .Select(item => ToReviewItem(item.Resolved, item.ReasonCode, item.Reason))
            .ToArray();
        return new DailyStartGroup(code, label, ordered);
    }

    private static DateOnly LocalDate(DateTimeOffset now, TimeZoneInfo timeZone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, timeZone).DateTime);

    private static string UpcomingReasonCode(PlannedPeriodKind kind) => kind switch
    {
        PlannedPeriodKind.Day => "UpcomingDay",
        PlannedPeriodKind.Week => "UpcomingWeek",
        PlannedPeriodKind.Month => "UpcomingMonth",
        PlannedPeriodKind.Quarter => "UpcomingQuarter",
        PlannedPeriodKind.Year => "UpcomingYear",
        _ => "FutureLog"
    };

    private static string PeriodDescription(PlannedPeriod period) => period.Kind switch
    {
        PlannedPeriodKind.Day => $"day {period.Day:yyyy-MM-dd}",
        PlannedPeriodKind.Week => $"ISO week {period.Week}",
        PlannedPeriodKind.Month => $"month {period.Year:D4}-{period.Month:D2}",
        PlannedPeriodKind.Quarter => $"quarter {period.Year}-Q{period.Quarter}",
        PlannedPeriodKind.Year => $"year {period.Year}",
        _ => "period"
    };

    private sealed record OrderedReviewItem(
        ItemResolution Resolved,
        DateOnly PeriodStart,
        string ReasonCode,
        string Reason);

    private sealed class OrderedReviewItemComparer : IComparer<OrderedReviewItem>
    {
        public static OrderedReviewItemComparer Instance { get; } = new();

        public int Compare(OrderedReviewItem? left, OrderedReviewItem? right)
        {
            if (ReferenceEquals(left, right))
            {
                return 0;
            }

            if (left is null)
            {
                return -1;
            }

            if (right is null)
            {
                return 1;
            }

            var comparison = left.PeriodStart.CompareTo(right.PeriodStart);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = StringComparer.Ordinal.Compare(left.Resolved.CurrentList.Name, right.Resolved.CurrentList.Name);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = left.Resolved.CurrentOccurrence.CreatedAt.CompareTo(right.Resolved.CurrentOccurrence.CreatedAt);
            return comparison != 0
                ? comparison
                : left.Resolved.Item.Id.CompareTo(right.Resolved.Item.Id);
        }
    }
}
