using System.Text.Json;
using Bujo.Domain;

namespace Bujo.Domain.Tests;

public sealed class PlannedReviewQueryTests
{
    [Fact]
    public void DailyStartUsesLocalDateAndGroupsEveryMissedPrecisionWithoutMutatingTheSnapshot()
    {
        var fixture = new JournalTestFixture(
            new DateTimeOffset(2026, 11, 17, 0, 30, 0, TimeSpan.Zero),
            "Europe/Zurich");
        var missed = new[]
        {
            fixture.Capture("Missed day", PlannedPeriod.ForDay(new DateOnly(2026, 11, 16))),
            fixture.Capture("Missed week", PlannedPeriod.ForWeek(new IsoWeekKey(2026, 46))),
            fixture.Capture("Missed month", PlannedPeriod.ForMonth(2026, 10)),
            fixture.Capture("Missed quarter", PlannedPeriod.ForQuarter(2026, 3)),
            fixture.Capture("Missed year", PlannedPeriod.ForYear(2025))
        };
        var today = fixture.Capture("Today", PlannedPeriod.ForDay(new DateOnly(2026, 11, 17)), tags: ["Work"]);
        var thisWeek = fixture.Capture("This week", PlannedPeriod.ForWeek(new IsoWeekKey(2026, 47)));
        fixture.Capture("Future day", PlannedPeriod.ForDay(new DateOnly(2026, 11, 18)));
        fixture.Capture("Current month", PlannedPeriod.ForMonth(2026, 11));
        fixture.Capture("Unplanned");
        var done = fixture.Capture("Done today", PlannedPeriod.ForDay(new DateOnly(2026, 11, 17)));
        fixture.Transition(done.Occurrence.Id, OccurrenceAction.Done);
        var abandoned = fixture.Capture("Abandoned today", PlannedPeriod.ForDay(new DateOnly(2026, 11, 17)));
        fixture.Transition(abandoned.Occurrence.Id, OccurrenceAction.Abandon);

        var before = fixture.Repository.GetSnapshot();
        var review = PlannedReviewQueries.GetDailyStart(before, fixture.Clock.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich"));
        var repeated = PlannedReviewQueries.GetDailyStart(before, fixture.Clock.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich"));
        var after = fixture.Repository.GetSnapshot();

        Assert.True(review.IsSuccess, review.Error?.Message);
        Assert.True(repeated.IsSuccess, repeated.Error?.Message);
        Assert.Equal(new DateOnly(2026, 11, 17), review.Value!.Today);
        Assert.Equal(["Overdue", "Today", "ThisWeek"], review.Value.Groups.Select(group => group.Code));
        var overdue = review.Value.Groups[0].Items;
        Assert.Equal(
            ["MissedYear", "MissedQuarter", "MissedMonth", "MissedWeek", "MissedDay"],
            overdue.Select(item => item.ReasonCode));
        Assert.Equal(missed.Select(item => item.Item.Id).OrderBy(id => id), overdue.Select(item => item.ItemId).OrderBy(id => id));
        Assert.Equal(today.Item.Id, Assert.Single(review.Value.Groups[1].Items).ItemId);
        Assert.Equal(["Work"], review.Value.Groups[1].Items[0].Tags);
        Assert.Equal(thisWeek.Item.Id, Assert.Single(review.Value.Groups[2].Items).ItemId);
        Assert.Equal(10, review.Value.FutureLogCount);
        Assert.Equal(1, review.Value.UnplannedCount);
        Assert.DoesNotContain(review.Value.Groups.SelectMany(group => group.Items), item => item.ItemId == done.Item.Id);
        Assert.DoesNotContain(review.Value.Groups.SelectMany(group => group.Items), item => item.ItemId == abandoned.Item.Id);
        Assert.Equal(JsonSerializer.Serialize(review.Value), JsonSerializer.Serialize(repeated.Value));
        Assert.Equal(JsonSerializer.Serialize(before.Items), JsonSerializer.Serialize(after.Items));
        Assert.Equal(JsonSerializer.Serialize(before.Occurrences), JsonSerializer.Serialize(after.Occurrences));
        Assert.Equal(JsonSerializer.Serialize(before.History), JsonSerializer.Serialize(after.History));
    }

    [Fact]
    public void DailyStartResolvesRepeatedMovesToOneCurrentDestinationAndFlagsWeeklyConflict()
    {
        var fixture = new JournalTestFixture(
            new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero),
            "Europe/Zurich");
        var capture = fixture.Capture("Moved task", PlannedPeriod.ForDay(new DateOnly(2026, 11, 17)));
        var week47 = fixture.CreateWeekly(2026, 47);
        var week48 = fixture.CreateWeekly(2026, 48);
        fixture.Move(capture.Item, capture.Occurrence.Id, week47.Id);
        var current = fixture.Repository.GetSnapshot().Items.Single();
        var currentOccurrence = current.CurrentOccurrenceId;
        fixture.Move(current, currentOccurrence, week48.Id);

        var review = PlannedReviewQueries.GetDailyStart(
            fixture.Repository.GetSnapshot(),
            fixture.Clock.GetUtcNow(),
            TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich"));

        Assert.True(review.IsSuccess, review.Error?.Message);
        var item = Assert.Single(review.Value!.Groups[1].Items);
        Assert.Equal(capture.Item.Id, item.ItemId);
        Assert.True(item.Conflict);
        Assert.Equal(week48.Id, item.Destination.ListId);
        Assert.Equal("Week 48", item.Destination.ListName);
        Assert.Equal(OccurrenceState.Open.ToString(), item.Destination.State);
        Assert.Single(review.Value.Groups.SelectMany(group => group.Items));
        Assert.Equal(PlannedPeriod.ForDay(new DateOnly(2026, 11, 17)), fixture.Repository.GetSnapshot().Items.Single().PlannedPeriod);
    }

    [Fact]
    public void DailyStartHandlesIsoWeekAndCalendarYearBoundaries()
    {
        var fixture = new JournalTestFixture(
            new DateTimeOffset(2027, 1, 3, 23, 30, 0, TimeSpan.Zero),
            "Europe/Zurich");
        fixture.Capture("Week 52", PlannedPeriod.ForWeek(new IsoWeekKey(2026, 52)));
        fixture.Capture("Week 53", PlannedPeriod.ForWeek(new IsoWeekKey(2026, 53)));
        var currentWeek = fixture.Capture("Week 1", PlannedPeriod.ForWeek(new IsoWeekKey(2027, 1)));
        fixture.Capture("Last year", PlannedPeriod.ForYear(2026));
        fixture.Capture("Last quarter", PlannedPeriod.ForQuarter(2026, 4));
        fixture.Capture("Yesterday", PlannedPeriod.ForDay(new DateOnly(2027, 1, 3)));
        var today = fixture.Capture("Today", PlannedPeriod.ForDay(new DateOnly(2027, 1, 4)));
        fixture.Capture("This year", PlannedPeriod.ForYear(2027));

        var review = PlannedReviewQueries.GetDailyStart(
            fixture.Repository.GetSnapshot(),
            fixture.Clock.GetUtcNow(),
            TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich"));

        Assert.True(review.IsSuccess, review.Error?.Message);
        Assert.Equal(new DateOnly(2027, 1, 4), review.Value!.Today);
        Assert.Equal(
            ["MissedYear", "MissedQuarter", "MissedWeek", "MissedWeek", "MissedDay"],
            review.Value.Groups[0].Items.Select(item => item.ReasonCode));
        Assert.Equal(today.Item.Id, Assert.Single(review.Value.Groups[1].Items).ItemId);
        Assert.Equal(currentWeek.Item.Id, Assert.Single(review.Value.Groups[2].Items).ItemId);
    }

    [Fact]
    public void DailyStartOrderingUsesDestinationNameAfterPlannedPeriod()
    {
        var fixture = new JournalTestFixture(
            new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero),
            "Europe/Zurich");
        var zulu = fixture.CreateList("Zulu");
        var alpha = fixture.CreateList("Alpha");
        fixture.Capture("Zulu item", PlannedPeriod.ForDay(new DateOnly(2026, 11, 17)), zulu.Id);
        fixture.Capture("Alpha item", PlannedPeriod.ForDay(new DateOnly(2026, 11, 17)), alpha.Id);

        var review = PlannedReviewQueries.GetDailyStart(
            fixture.Repository.GetSnapshot(),
            fixture.Clock.GetUtcNow(),
            TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich"));

        Assert.True(review.IsSuccess, review.Error?.Message);
        Assert.Equal(["Alpha item", "Zulu item"], review.Value!.Groups[1].Items.Select(item => item.Text));
    }

    [Theory]
    [InlineData("2026-03-08T04:30:00+00:00", "2026-03-07")]
    [InlineData("2026-11-01T04:30:00+00:00", "2026-11-01")]
    public void DailyStartUsesLocalDateAcrossDaylightSavingTransitions(string instant, string expectedDate)
    {
        var now = DateTimeOffset.Parse(instant);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var today = DateOnly.Parse(expectedDate);
        var repository = new Bujo.Infrastructure.Persistence.InMemoryJournalRepository();
        var journal = new JournalService(repository, new FrozenTimeProvider(now));
        var futureLog = journal.CreateList("Future Log", JournalListKind.FutureLog);
        Assert.True(futureLog.IsSuccess);
        var capture = journal.Capture("Today", plannedPeriod: PlannedPeriod.ForDay(today));
        Assert.True(capture.IsSuccess);

        var review = PlannedReviewQueries.GetDailyStart(repository.GetSnapshot(), now, timeZone);

        Assert.True(review.IsSuccess, review.Error?.Message);
        Assert.Equal(today, review.Value!.Today);
        Assert.Equal(capture.Value!.Item.Id, Assert.Single(review.Value.Groups[1].Items).ItemId);
    }

    [Fact]
    public void UpcomingQueriesBoundHorizonsAndPageTheFutureLogSeparately()
    {
        var fixture = new JournalTestFixture(
            new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero),
            "Europe/Zurich");
        fixture.Capture("Current month", PlannedPeriod.ForMonth(2026, 11));
        fixture.Capture("Current quarter", PlannedPeriod.ForQuarter(2026, 4));
        fixture.Capture("Current year", PlannedPeriod.ForYear(2026));
        fixture.Capture("Near day", PlannedPeriod.ForDay(new DateOnly(2026, 11, 20)));
        fixture.Capture("Far year", PlannedPeriod.ForYear(2027));
        fixture.Capture("Unplanned one");
        fixture.Capture("Unplanned two");

        var upcoming = PlannedReviewQueries.GetUpcoming(
            fixture.Repository.GetSnapshot(),
            fixture.Clock.GetUtcNow(),
            TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich"));
        var firstPage = PlannedReviewQueries.GetUpcoming(
            fixture.Repository.GetSnapshot(),
            fixture.Clock.GetUtcNow(),
            TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich"),
            pageSize: 2,
            futureLog: true);
        var secondPage = PlannedReviewQueries.GetUpcoming(
            fixture.Repository.GetSnapshot(),
            fixture.Clock.GetUtcNow(),
            TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich"),
            page: 2,
            pageSize: 2,
            futureLog: true);
        var invalidHorizon = PlannedReviewQueries.GetUpcoming(
            fixture.Repository.GetSnapshot(),
            fixture.Clock.GetUtcNow(),
            TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich"),
            horizonDays: 91);

        Assert.True(upcoming.IsSuccess, upcoming.Error?.Message);
        Assert.Equal(4, upcoming.Value!.TotalCount);
        Assert.Contains(upcoming.Value.Groups, group => group.PlannedPeriod.Kind == PlannedPeriodKind.Month);
        Assert.Contains(upcoming.Value.Groups, group => group.PlannedPeriod.Kind == PlannedPeriodKind.Quarter);
        Assert.Contains(upcoming.Value.Groups, group => group.PlannedPeriod.Kind == PlannedPeriodKind.Year);
        Assert.DoesNotContain(upcoming.Value.Groups, group => group.Items.Any(item => item.Text == "Far year"));
        Assert.True(firstPage.IsSuccess, firstPage.Error?.Message);
        Assert.True(firstPage.Value!.Truncated);
        Assert.Equal(7, firstPage.Value.TotalCount);
        Assert.Equal(2, firstPage.Value.Groups.Sum(group => group.Items.Count));
        Assert.True(secondPage.IsSuccess, secondPage.Error?.Message);
        Assert.Equal(2, secondPage.Value!.Groups.Sum(group => group.Items.Count));
        Assert.Empty(invalidHorizon.Value?.Groups ?? []);
        Assert.Equal(DomainErrorCode.InvalidInput, invalidHorizon.Error!.Code);
    }

    [Fact]
    public void PlannedPeriodRangesUseInclusiveCalendarBoundaries()
    {
        Assert.Equal(
            new CalendarDateRange(new DateOnly(2026, 12, 28), new DateOnly(2027, 1, 3)),
            PlannedPeriodRanges.For(PlannedPeriod.ForWeek(new IsoWeekKey(2026, 53))));
        Assert.Equal(
            new CalendarDateRange(new DateOnly(2026, 10, 1), new DateOnly(2026, 12, 31)),
            PlannedPeriodRanges.For(PlannedPeriod.ForQuarter(2026, 4)));
        Assert.Null(PlannedPeriodRanges.For(PlannedPeriod.Unplanned));
    }
}
