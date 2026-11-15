namespace Bujo.Domain;

public enum PeriodConflictKind
{
    PlannedWeekDoesNotMatchList,
    PlannedDayOutsideListWeek
}

public sealed record PeriodConflict(
    PeriodConflictKind Kind,
    PlannedPeriod PlannedPeriod,
    Guid WeeklyListId,
    IsoWeekKey ListWeek);

public static class PeriodConflictRules
{
    public static PeriodConflict? Find(PlannedPeriod period, JournalListSnapshot list)
    {
        if (list.Kind != JournalListKind.Weekly || list.WeekKey is not { } week)
        {
            return null;
        }

        return period.Kind switch
        {
            PlannedPeriodKind.Week when period.Week is { } plannedWeek && plannedWeek != week =>
                new PeriodConflict(PeriodConflictKind.PlannedWeekDoesNotMatchList, period, list.Id, week),
            PlannedPeriodKind.Day when period.Day is { } day && !week.Contains(day) =>
                new PeriodConflict(PeriodConflictKind.PlannedDayOutsideListWeek, period, list.Id, week),
            _ => null
        };
    }
}
