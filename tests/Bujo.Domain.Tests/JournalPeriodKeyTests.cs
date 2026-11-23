using Bujo.Domain;

namespace Bujo.Domain.Tests;

public sealed class JournalPeriodKeyTests
{
    [Fact]
    public void CalendarPeriodsHaveInclusiveBoundariesLabelsAndLeapDays()
    {
        var month = new JournalPeriodKey(JournalListKind.Monthly, 2024, 2);
        Assert.Equal("February 2024", month.Label);
        Assert.Equal(new CalendarDateRange(new(2024, 2, 1), new(2024, 2, 29)), month.Range);
        var quarter = new JournalPeriodKey(JournalListKind.Quarterly, 2026, quarter: 4);
        Assert.Equal("Q4 2026", quarter.Label);
        Assert.Equal(new CalendarDateRange(new(2026, 10, 1), new(2026, 12, 31)), quarter.Range);
        var year = new JournalPeriodKey(JournalListKind.Yearly, 2026);
        Assert.Equal("2026", year.Label);
        Assert.Equal(new CalendarDateRange(new(2026, 1, 1), new(2026, 12, 31)), year.Range);
    }

    [Fact]
    public void NavigationUsesCalendarYearRatherThanIsoWeekYear()
    {
        Assert.Equal(new JournalPeriodKey(JournalListKind.Monthly, 2027, 1),
            new JournalPeriodKey(JournalListKind.Monthly, 2026, 12).Next());
        Assert.Equal(new JournalPeriodKey(JournalListKind.Quarterly, 2027, quarter: 1),
            new JournalPeriodKey(JournalListKind.Quarterly, 2026, quarter: 4).Next());
        Assert.Equal(new JournalPeriodKey(JournalListKind.Quarterly, 2026, quarter: 4),
            new JournalPeriodKey(JournalListKind.Quarterly, 2027, quarter: 1).Previous());
        var day = new DateOnly(2021, 1, 1);
        Assert.Equal(2020, IsoWeekKey.FromDate(day).Year);
        Assert.Equal(2021, JournalPeriodKey.FromDate(JournalListKind.Yearly, day).Year);
        Assert.Null(new JournalPeriodKey(JournalListKind.Yearly, 1).Previous());
        Assert.Null(new JournalPeriodKey(JournalListKind.Monthly, 9999, 12).Next());
    }

    [Fact]
    public void InvalidKeysAndKindMismatchesAreRejectedWhilePeriodIsNotUnique()
    {
        Assert.Throws<DomainException>(() => new JournalPeriodKey(JournalListKind.Monthly, 2026, 13));
        Assert.Throws<DomainException>(() => new JournalPeriodKey(JournalListKind.Quarterly, 2026, quarter: 5));
        Assert.Throws<DomainException>(() => new JournalPeriodKey(JournalListKind.Yearly, 0));
        Assert.Throws<DomainException>(() => new JournalPeriodKey(JournalListKind.Weekly, 2026));
        var fixture = new JournalTestFixture();
        var key = new JournalPeriodKey(JournalListKind.Monthly, 2026, 11);
        Assert.True(fixture.Journal.CreateList("First", JournalListKind.Monthly, periodKey: key).IsSuccess);
        Assert.True(fixture.Journal.CreateList("Second", JournalListKind.Monthly, periodKey: key).IsSuccess);
        Assert.Equal(DomainErrorCode.DuplicateListName,
            fixture.Journal.CreateList("FIRST", JournalListKind.Monthly, periodKey: key).Error!.Code);
        Assert.Equal(DomainErrorCode.InvalidInput,
            fixture.Journal.CreateList("Bad", JournalListKind.Quarterly, periodKey: key).Error!.Code);
        Assert.Equal(DomainErrorCode.InvalidInput,
            fixture.Journal.CreateList("No key", JournalListKind.Monthly).Error!.Code);
        Assert.True(fixture.Journal.CreateList("Legacy", JournalListKind.Weekly).IsSuccess);
    }

    [Theory]
    [InlineData(JournalListKind.Monthly)]
    [InlineData(JournalListKind.Quarterly)]
    [InlineData(JournalListKind.Yearly)]
    public void ReviewRetainsOpenTasksThoughtsOutcomesAndMoveHistory(JournalListKind kind)
    {
        var fixture = new JournalTestFixture();
        var key = JournalPeriodKey.FromDate(kind, new DateOnly(2026, 11, 1));
        var list = fixture.Journal.CreateList("Period", kind, periodKey: key).Value!;
        var open = fixture.Capture("Open", listId: list.Id);
        var thought = fixture.Journal.Capture("Thought", listId: list.Id, type: ItemType.Thought).Value!;
        var done = fixture.Capture("Done", listId: list.Id);
        fixture.Transition(done.Occurrence.Id, OccurrenceAction.Done);
        var abandoned = fixture.Capture("Abandoned", listId: list.Id);
        fixture.Transition(abandoned.Occurrence.Id, OccurrenceAction.Abandon);
        var moved = fixture.Capture("Moved", listId: list.Id);
        fixture.Move(moved.Item, moved.Occurrence.Id, fixture.FutureLog.Id);

        var review = fixture.Journal.GetSnapshot().GetListReview(list.Id).Value!;
        Assert.Equal(new[] { open.Occurrence.Id, thought.Occurrence.Id }.Order(),
            review.Unfinished.Select(entry => entry.Occurrence.Id).Order());
        Assert.Equal(new[] { done.Occurrence.Id, abandoned.Occurrence.Id }.Order(),
            review.Resolved.Select(entry => entry.Occurrence.Id).Order());
        Assert.Equal(moved.Occurrence.Id, Assert.Single(review.Moved).Occurrence.Id);
        Assert.Equal(fixture.FutureLog.Id, review.Moved[0].Resolution!.CurrentList.Id);
    }
}
