using Bujo.Domain;

namespace Bujo.Domain.Tests;

public sealed class PeriodConflictTests
{
    [Fact]
    public void Planned_week_conflicting_with_weekly_list_is_reported_without_rewriting_either()
    {
        var fixture = new JournalTestFixture();
        var week47 = fixture.CreateWeekly(2026, 47);
        var plannedWeek = PlannedPeriod.ForWeek(new IsoWeekKey(2026, 48));

        var capture = fixture.Capture("Week conflict", plannedWeek, week47.Id);

        Assert.Equal(PeriodConflictKind.PlannedWeekDoesNotMatchList, capture.PeriodConflict!.Kind);
        Assert.Equal(plannedWeek, capture.Item.PlannedPeriod);
        Assert.Equal(new IsoWeekKey(2026, 47), fixture.Journal.GetSnapshot().Lists.Single(item => item.Id == week47.Id).WeekKey);
    }

    [Fact]
    public void Planned_day_outside_week_is_reported_on_move_but_remains_unchanged()
    {
        var fixture = new JournalTestFixture();
        var week47 = fixture.CreateWeekly(2026, 47);
        var plannedDay = PlannedPeriod.ForDay(new DateOnly(2026, 11, 23));
        var capture = fixture.Capture("Day conflict", plannedDay);

        var moved = fixture.Journal.Move(
            Guid.NewGuid(),
            capture.Item.Id,
            capture.Occurrence.Id,
            week47.Id);

        Assert.True(moved.IsSuccess);
        Assert.Equal(PeriodConflictKind.PlannedDayOutsideListWeek, moved.Value!.PeriodConflict!.Kind);
        Assert.Equal(plannedDay, moved.Value.Resolution.Item.PlannedPeriod);
        Assert.Equal(new IsoWeekKey(2026, 47), moved.Value.Resolution.CurrentList.WeekKey);
    }

    [Fact]
    public void Matching_week_and_documented_undefined_cases_return_no_conflict()
    {
        var fixture = new JournalTestFixture();
        var week47 = fixture.CreateWeekly(2026, 47);
        var noKeyWeek = fixture.CreateList("Weekly without key", JournalListKind.Weekly);

        Assert.Null(fixture.Capture(
            "Matching week",
            PlannedPeriod.ForWeek(new IsoWeekKey(2026, 47)),
            week47.Id).PeriodConflict);

        foreach (var period in new[]
        {
            PlannedPeriod.Unplanned,
            PlannedPeriod.ForMonth(2026, 11),
            PlannedPeriod.ForQuarter(2026, 4),
            PlannedPeriod.ForYear(2026),
            PlannedPeriod.ForDay(new DateOnly(2026, 11, 23)),
            PlannedPeriod.ForWeek(new IsoWeekKey(2026, 48))
        })
        {
            Assert.Null(fixture.Capture($"No conflict {period.Kind}", period, noKeyWeek.Id).PeriodConflict);
        }

        foreach (var period in new[]
        {
            PlannedPeriod.Unplanned,
            PlannedPeriod.ForMonth(2026, 12),
            PlannedPeriod.ForQuarter(2027, 1),
            PlannedPeriod.ForYear(2027),
            PlannedPeriod.ForDay(new DateOnly(2026, 11, 16)),
            PlannedPeriod.ForDay(new DateOnly(2026, 11, 22))
        })
        {
            var capture = fixture.Capture($"Keyed week {period.Kind}", period, week47.Id);
            Assert.Null(capture.PeriodConflict);
            Assert.Equal(period, capture.Item.PlannedPeriod);
            Assert.Equal(week47.Id, capture.Occurrence.ListId);
        }

        Assert.Equal(
            week47.WeekKey,
            fixture.Journal.GetSnapshot().Lists.Single(list => list.Id == week47.Id).WeekKey);
    }
}
