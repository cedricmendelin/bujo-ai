using System.Globalization;
using System.Text.Json.Serialization;

namespace Bujo.Domain;

/// <summary>A calendar period identifying a monthly, quarterly, or yearly list.</summary>
public readonly record struct JournalPeriodKey
{
    [JsonConstructor]
    public JournalPeriodKey(JournalListKind kind, int year, int? month = null, int? quarter = null)
    {
        if (year is < 1 or > 9999 ||
            !(kind == JournalListKind.Monthly && month is >= 1 and <= 12 && quarter is null ||
              kind == JournalListKind.Quarterly && quarter is >= 1 and <= 4 && month is null ||
              kind == JournalListKind.Yearly && month is null && quarter is null))
            throw new DomainException(DomainErrorCode.InvalidInput, "The journal period key is invalid.");

        Kind = kind;
        Year = year;
        Month = month;
        Quarter = quarter;
    }

    public JournalListKind Kind { get; }
    public int Year { get; }
    public int? Month { get; }
    public int? Quarter { get; }

    [JsonIgnore]
    public bool IsValid => Year is >= 1 and <= 9999 &&
        (Kind == JournalListKind.Monthly && Month is >= 1 and <= 12 && Quarter is null ||
         Kind == JournalListKind.Quarterly && Quarter is >= 1 and <= 4 && Month is null ||
         Kind == JournalListKind.Yearly && Month is null && Quarter is null);

    public CalendarDateRange Range => Kind switch
    {
        JournalListKind.Monthly when IsValid => PlannedPeriodRanges.For(PlannedPeriod.ForMonth(Year, Month!.Value))!.Value,
        JournalListKind.Quarterly when IsValid => PlannedPeriodRanges.For(PlannedPeriod.ForQuarter(Year, Quarter!.Value))!.Value,
        JournalListKind.Yearly when IsValid => PlannedPeriodRanges.For(PlannedPeriod.ForYear(Year))!.Value,
        _ => throw new DomainException(DomainErrorCode.InvalidInput, "The journal period key is invalid.")
    };

    public string Label => Kind switch
    {
        JournalListKind.Monthly when IsValid => new DateOnly(Year, Month!.Value, 1).ToString("MMMM yyyy", CultureInfo.InvariantCulture),
        JournalListKind.Quarterly when IsValid => $"Q{Quarter} {Year}",
        JournalListKind.Yearly when IsValid => Year.ToString("0000", CultureInfo.InvariantCulture),
        _ => throw new DomainException(DomainErrorCode.InvalidInput, "The journal period key is invalid.")
    };

    public static JournalPeriodKey FromDate(JournalListKind kind, DateOnly date) => kind switch
    {
        JournalListKind.Monthly => new(kind, date.Year, date.Month),
        JournalListKind.Quarterly => new(kind, date.Year, quarter: (date.Month - 1) / 3 + 1),
        JournalListKind.Yearly => new(kind, date.Year),
        _ => throw new DomainException(DomainErrorCode.InvalidInput, "A calendar list kind is required.")
    };

    public JournalPeriodKey? Previous() => Range.Start == DateOnly.MinValue ? null : FromDate(Kind, Range.Start.AddDays(-1));
    public JournalPeriodKey? Next() => Range.End == DateOnly.MaxValue ? null : FromDate(Kind, Range.End.AddDays(1));
}
