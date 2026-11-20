using Bujo.Domain;

namespace Bujo.Domain.Tests;

public sealed class ItemTypeTests
{
    [Fact]
    public void CaptureDefaultsToTaskAndRejectsUndefinedTypeWithoutWriting()
    {
        var fixture = new JournalTestFixture();
        var task = fixture.Capture("Task");
        var before = fixture.Journal.GetSnapshot();

        var invalid = fixture.Journal.Capture("Invalid", type: (ItemType)42);

        Assert.Equal(ItemType.Task, task.Item.Type);
        Assert.Equal(DomainErrorCode.InvalidInput, invalid.Error!.Code);
        Assert.Equal(before.Items.Count, fixture.Journal.GetSnapshot().Items.Count);
        Assert.Equal(before.Occurrences.Count, fixture.Journal.GetSnapshot().Occurrences.Count);
    }

    [Theory]
    [InlineData(OccurrenceAction.Done)]
    [InlineData(OccurrenceAction.Abandon)]
    [InlineData(OccurrenceAction.Reopen)]
    public void ThoughtRejectsTaskTransitionsWithoutChangingHistory(OccurrenceAction action)
    {
        var fixture = new JournalTestFixture();
        var thought = fixture.Journal.Capture("A reflection", type: ItemType.Thought).Value!;
        var before = fixture.Journal.GetSnapshot();

        var result = fixture.Journal.Transition(thought.Occurrence.Id, action);
        var after = fixture.Journal.GetSnapshot();

        Assert.Equal(ItemType.Thought, thought.Item.Type);
        Assert.Equal(DomainErrorCode.InvalidTransition, result.Error!.Code);
        Assert.Equal(before.Occurrences.Single(), after.Occurrences.Single());
        Assert.Equal(before.History, after.History);
    }

    [Fact]
    public void ThoughtRetainsHistoryAcrossMoveAndListCloseReopenWhileOpenTaskStillBlocks()
    {
        var fixture = new JournalTestFixture();
        var list = fixture.CreateList("Journal");
        var thought = fixture.Journal.Capture("Remember this", type: ItemType.Thought).Value!;
        var moved = fixture.Move(thought.Item, thought.Occurrence.Id, list.Id);
        var task = fixture.Capture("Still to do", listId: list.Id);

        var blocked = fixture.Journal.CloseList(list.Id);
        Assert.Equal(DomainErrorCode.UnresolvedOccurrences, blocked.Error!.Code);
        Assert.Contains("open tasks", blocked.Error.Message);
        Assert.Contains(task.Occurrence.Id, blocked.Error.RelatedIds!);
        Assert.DoesNotContain(moved.Resolution.CurrentOccurrence.Id, blocked.Error.RelatedIds!);

        fixture.Transition(task.Occurrence.Id, OccurrenceAction.Done);
        var before = fixture.Journal.Resolve(thought.Occurrence.Id).Value!;
        Assert.True(fixture.Journal.CloseList(list.Id).IsSuccess);
        var closed = fixture.Journal.Resolve(thought.Occurrence.Id).Value!;
        Assert.Equal(JournalListState.Closed, closed.CurrentList.State);
        Assert.Equal(ItemType.Thought, closed.Item.Type);
        Assert.Equal(before.Chain, closed.Chain);
        Assert.Equal(OccurrenceState.Open, closed.CurrentOccurrence.State);
        Assert.Contains(fixture.Journal.GetSnapshot().GetListReview(list.Id).Value!.Unfinished,
            entry => entry.Item.Id == thought.Item.Id);

        Assert.True(fixture.Journal.ReopenList(list.Id).IsSuccess);
        var reopened = fixture.Journal.Resolve(thought.Occurrence.Id).Value!;
        Assert.Equal(JournalListState.Open, reopened.CurrentList.State);
        Assert.Equal(before.Chain, reopened.Chain);
        Assert.Equal(ItemType.Thought, reopened.Item.Type);
    }

    [Fact]
    public void ActionableDailyCountsExcludeThoughtsButFutureLogReadRetainsThem()
    {
        var fixture = new JournalTestFixture(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero), "UTC");
        var today = PlannedPeriod.ForDay(new DateOnly(2026, 11, 17));
        var task = fixture.Capture("Task today", today);
        var thought = fixture.Journal.Capture("Thought today", plannedPeriod: today, type: ItemType.Thought).Value!;
        fixture.Capture("Unplanned task");
        fixture.Journal.Capture("Unplanned thought", type: ItemType.Thought);

        var daily = PlannedReviewQueries.GetDailyStart(fixture.Journal.GetSnapshot(), fixture.Clock.GetUtcNow(), TimeZoneInfo.Utc).Value!;
        var future = PlannedReviewQueries.GetUpcoming(fixture.Journal.GetSnapshot(), fixture.Clock.GetUtcNow(), TimeZoneInfo.Utc, futureLog: true).Value!;

        Assert.Equal(2, daily.FutureLogCount);
        Assert.Equal(1, daily.UnplannedCount);
        Assert.Equal(task.Item.Id, Assert.Single(daily.Groups.Single(group => group.Code == "Today").Items).ItemId);
        Assert.Equal(4, future.TotalCount);
        var futureThought = future.Groups.SelectMany(group => group.Items).Single(item => item.ItemId == thought.Item.Id);
        Assert.Equal(ItemType.Thought, futureThought.Type);
    }
}
