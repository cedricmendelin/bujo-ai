using Bujo.Domain;

namespace Bujo.Web.Components;

public sealed class ItemDraft
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Tags { get; set; }
    public string[]? TagNames => Tags?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static ItemDraft From(ItemSnapshot item) => new()
    {
        Name = item.Name,
        Description = item.Description,
        Tags = string.Join(", ", item.Tags.Select(tag => tag.Value))
    };
}

public sealed class PeriodDraft
{
    public PlannedPeriodKind Kind { get; set; }
    public DateOnly? Day { get; set; }
    public int? Year { get; set; }
    public int? Number { get; set; }

    public PlannedPeriod Build() => Kind switch
    {
        PlannedPeriodKind.Unplanned => PlannedPeriod.Unplanned,
        PlannedPeriodKind.Day when Day is { } day => PlannedPeriod.ForDay(day),
        PlannedPeriodKind.Week when Year is { } year && Number is { } week => PlannedPeriod.ForWeek(new(year, week)),
        PlannedPeriodKind.Month when Year is { } year && Number is { } month => PlannedPeriod.ForMonth(year, month),
        PlannedPeriodKind.Quarter when Year is { } year && Number is { } quarter => PlannedPeriod.ForQuarter(year, quarter),
        PlannedPeriodKind.Year when Year is { } year => PlannedPeriod.ForYear(year),
        _ => throw new DomainException(DomainErrorCode.InvalidInput, "Fill in the planned period fields.")
    };

    public static PeriodDraft From(PlannedPeriod period) => new()
    {
        Kind = period.Kind,
        Day = period.Day,
        Year = period.Week?.Year ?? period.Year,
        Number = period.Week?.Week ?? period.Month ?? period.Quarter
    };
}

internal static class JournalLabels
{
    public static string Period(PlannedPeriod period) => period.Kind switch
    {
        PlannedPeriodKind.Day => $"{period.Day:yyyy-MM-dd}",
        PlannedPeriodKind.Week => $"{period.Week}",
        PlannedPeriodKind.Month => $"{period.Year:D4}-{period.Month:D2}",
        PlannedPeriodKind.Quarter => $"{period.Year:D4}-Q{period.Quarter}",
        PlannedPeriodKind.Year => $"{period.Year:D4}",
        _ => "Unplanned"
    };
}
