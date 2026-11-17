namespace Bujo.Domain;

public readonly record struct CalendarDateRange(DateOnly Start, DateOnly End)
{
    public bool Contains(DateOnly date) => date >= Start && date <= End;

    public bool Overlaps(CalendarDateRange other) => Start <= other.End && other.Start <= End;
}

public static class PlannedPeriodRanges
{
    public static CalendarDateRange? For(PlannedPeriod period) =>
        period.Kind switch
        {
            PlannedPeriodKind.Day when period.Day is { } day => new CalendarDateRange(day, day),
            PlannedPeriodKind.Week when period.Week is { } week =>
                new CalendarDateRange(week.Monday, AddDaysClamped(week.Monday, 6)),
            PlannedPeriodKind.Month when period.Year is { } year && period.Month is { } month =>
                MonthRange(year, month),
            PlannedPeriodKind.Quarter when period.Year is { } year && period.Quarter is { } quarter =>
                QuarterRange(year, quarter),
            PlannedPeriodKind.Year when period.Year is { } year =>
                new CalendarDateRange(new DateOnly(year, 1, 1), new DateOnly(year, 12, 31)),
            PlannedPeriodKind.Unplanned => null,
            _ => throw new DomainException(DomainErrorCode.InvalidInput, "The planned period is invalid.")
        };

    public static DateOnly AddDaysClamped(DateOnly date, int days) =>
        DateOnly.FromDayNumber(Math.Min(DateOnly.MaxValue.DayNumber, date.DayNumber + days));

    private static CalendarDateRange MonthRange(int year, int month)
    {
        var start = new DateOnly(year, month, 1);
        return new CalendarDateRange(start, new DateOnly(year, month, DateTime.DaysInMonth(year, month)));
    }

    private static CalendarDateRange QuarterRange(int year, int quarter)
    {
        var firstMonth = (quarter - 1) * 3 + 1;
        var start = new DateOnly(year, firstMonth, 1);
        var lastMonth = firstMonth + 2;
        return new CalendarDateRange(start, new DateOnly(year, lastMonth, DateTime.DaysInMonth(year, lastMonth)));
    }
}
