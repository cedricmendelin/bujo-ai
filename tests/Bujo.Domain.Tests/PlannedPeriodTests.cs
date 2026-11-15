using Bujo.Domain;

namespace Bujo.Domain.Tests;

public sealed class PlannedPeriodTests
{
    [Fact]
    public void Period_anchors_validate_calendar_values()
    {
        Assert.Equal(new DateOnly(2024, 2, 29), PlannedPeriod.ForDay(new DateOnly(2024, 2, 29)).Day);
        Assert.Equal(2, PlannedPeriod.ForMonth(2024, 2).Month);
        Assert.Equal(4, PlannedPeriod.ForQuarter(2024, 4).Quarter);
        Assert.Equal(2024, PlannedPeriod.ForYear(2024).Year);
        Assert.Throws<DomainException>(() => PlannedPeriod.ForMonth(2024, 13));
        Assert.Throws<DomainException>(() => PlannedPeriod.ForQuarter(2024, 5));
        Assert.Throws<DomainException>(() => PlannedPeriod.ForYear(0));
        Assert.Throws<DomainException>(() => PlannedPeriod.ForWeek(default));
        Assert.Throws<DomainException>(() => new IsoWeekKey(0, 0));
        Assert.Throws<DomainException>(() => new IsoWeekKey(2021, 53));
    }

    [Fact]
    public void Changing_period_does_not_change_list_state_occurrences_or_history()
    {
        var fixture = new JournalTestFixture();
        var weekly = fixture.CreateWeekly(2026, 47);
        var capture = fixture.Capture("Independent", listId: weekly.Id);
        var before = fixture.Journal.GetSnapshot();
        var nextPeriod = PlannedPeriod.ForWeek(new IsoWeekKey(2026, 48));

        Assert.True(fixture.Journal.ChangePlannedPeriod(capture.Item.Id, nextPeriod).IsSuccess);

        var after = fixture.Journal.GetSnapshot();
        Assert.Equal(before.Lists, after.Lists);
        Assert.Equal(before.Occurrences, after.Occurrences);
        Assert.Equal(before.History, after.History);
        Assert.Equal(before.MoveHistory, after.MoveHistory);
        Assert.Equal(nextPeriod, after.Items.Single().PlannedPeriod);
    }

    [Fact]
    public void Capture_defaults_to_future_log_and_unplanned_but_honors_an_explicit_period()
    {
        var fixture = new JournalTestFixture();
        var unplanned = fixture.Capture("Default");
        var planned = fixture.Capture(
            "Planned",
            plannedPeriod: PlannedPeriod.ForDay(new DateOnly(2026, 11, 17)));

        Assert.Equal(fixture.FutureLog.Id, unplanned.Occurrence.ListId);
        Assert.Equal(PlannedPeriod.Unplanned, unplanned.Item.PlannedPeriod);
        Assert.Equal(PlannedPeriodKind.Day, planned.Item.PlannedPeriod.Kind);
    }
}
