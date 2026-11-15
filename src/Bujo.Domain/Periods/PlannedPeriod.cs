namespace Bujo.Domain;

public enum PlannedPeriodKind
{
    Unplanned,
    Day,
    Week,
    Month,
    Quarter,
    Year
}

public readonly record struct PlannedPeriod
{
    private PlannedPeriod(
        PlannedPeriodKind kind,
        DateOnly? day,
        IsoWeekKey? week,
        int? year,
        int? month,
        int? quarter)
    {
        Kind = kind;
        Day = day;
        Week = week;
        Year = year;
        Month = month;
        Quarter = quarter;
    }

    public PlannedPeriodKind Kind { get; }

    public DateOnly? Day { get; }

    public IsoWeekKey? Week { get; }

    public int? Year { get; }

    public int? Month { get; }

    public int? Quarter { get; }

    public static PlannedPeriod Unplanned => new(PlannedPeriodKind.Unplanned, null, null, null, null, null);

    public static PlannedPeriod ForDay(DateOnly day) =>
        new(PlannedPeriodKind.Day, day, null, null, null, null);

    public static PlannedPeriod ForWeek(IsoWeekKey week)
    {
        if (!week.IsValid)
        {
            throw DomainLimits.Invalid("A valid ISO week key is required.");
        }

        return new(PlannedPeriodKind.Week, null, week, null, null, null);
    }

    public static PlannedPeriod ForMonth(int year, int month)
    {
        ValidateYear(year);
        if (month is < 1 or > 12)
        {
            throw DomainLimits.Invalid("Month must be between 1 and 12.");
        }

        return new(PlannedPeriodKind.Month, null, null, year, month, null);
    }

    public static PlannedPeriod ForQuarter(int year, int quarter)
    {
        ValidateYear(year);
        if (quarter is < 1 or > 4)
        {
            throw DomainLimits.Invalid("Quarter must be between 1 and 4.");
        }

        return new(PlannedPeriodKind.Quarter, null, null, year, null, quarter);
    }

    public static PlannedPeriod ForYear(int year)
    {
        ValidateYear(year);
        return new(PlannedPeriodKind.Year, null, null, year, null, null);
    }

    private static void ValidateYear(int year)
    {
        if (year is < 1 or > 9999)
        {
            throw DomainLimits.Invalid("Year must be between 1 and 9999.");
        }
    }
}
