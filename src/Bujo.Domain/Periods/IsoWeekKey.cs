using System.Globalization;
using System.Text.Json.Serialization;

namespace Bujo.Domain;

public readonly record struct IsoWeekKey
{
    [JsonConstructor]
    public IsoWeekKey(int year, int week)
    {
        if (year is < 1 or > 9999)
        {
            throw DomainLimits.Invalid("ISO week-year must be between 1 and 9999.");
        }

        var weeksInYear = ISOWeek.GetWeeksInYear(year);
        if (week < 1 || week > weeksInYear)
        {
            throw DomainLimits.Invalid($"ISO week must be between 1 and {weeksInYear} for {year}.");
        }

        Year = year;
        Week = week;
    }

    public int Year { get; }

    public int Week { get; }

    public bool IsValid =>
        Year is >= 1 and <= 9999 &&
        Week >= 1 &&
        Week <= ISOWeek.GetWeeksInYear(Year);

    public DateOnly Monday
    {
        get
        {
            if (!IsValid)
            {
                throw DomainLimits.Invalid("The default ISO week key is not valid.");
            }

            return DateOnly.FromDateTime(ISOWeek.ToDateTime(Year, Week, DayOfWeek.Monday));
        }
    }

    public static IsoWeekKey FromDate(DateOnly date) =>
        new(ISOWeek.GetYear(date.ToDateTime(TimeOnly.MinValue)), ISOWeek.GetWeekOfYear(date.ToDateTime(TimeOnly.MinValue)));

    public bool Contains(DateOnly date) => FromDate(date) == this;

    public override string ToString() => $"{Year}-W{Week:00}";
}
