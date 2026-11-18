using Bujo.Domain;

namespace Bujo.Domain.Tests;

public sealed class ListReviewTests
{
    [Fact]
    public void ReviewSeparatesOpenOccurrencesFromMovedLinksAndResolvesRepeatedMoves()
    {
        var fixture = new JournalTestFixture();
        var source = fixture.CreateList("Review source");
        var middle = fixture.CreateList("Review middle");
        var destination = fixture.CreateList("Review destination");
        var movedItem = fixture.Capture("Moved twice", listId: source.Id);
        fixture.Move(movedItem.Item, movedItem.Occurrence.Id, middle.Id);
        var middleOccurrence = fixture.Journal.GetSnapshot().Items.Single(item => item.Id == movedItem.Item.Id).CurrentOccurrenceId;
        fixture.Move(movedItem.Item, middleOccurrence, destination.Id);
        var current = fixture.Journal.GetSnapshot().Items.Single(item => item.Id == movedItem.Item.Id).CurrentOccurrenceId;
        fixture.Transition(current, OccurrenceAction.Done);
        var unfinished = fixture.Capture("Still open", listId: source.Id);

        var review = fixture.Journal.GetSnapshot().GetListReview(source.Id);

        Assert.True(review.IsSuccess, review.Error?.Message);
        Assert.Equal(unfinished.Occurrence.Id, Assert.Single(review.Value!.Unfinished).Occurrence.Id);
        var moved = Assert.Single(review.Value.Moved);
        Assert.Equal(movedItem.Occurrence.Id, moved.Occurrence.Id);
        Assert.Equal(OccurrenceState.Moved, moved.Occurrence.State);
        Assert.Equal(destination.Id, moved.Resolution!.CurrentList.Id);
        Assert.Equal(OccurrenceState.Done, moved.Resolution.CurrentOccurrence.State);
        Assert.Empty(review.Value.Resolved);
    }

    [Fact]
    public void HistoricalMovedLinksDoNotBlockListClosure()
    {
        var fixture = new JournalTestFixture();
        var source = fixture.CreateList("Historical links");
        var item = fixture.Capture("Moved away", listId: source.Id);
        fixture.Move(item.Item, item.Occurrence.Id, fixture.FutureLog.Id);

        var review = fixture.Journal.GetSnapshot().GetListReview(source.Id);
        var close = fixture.Journal.CloseList(source.Id);

        Assert.True(review.IsSuccess, review.Error?.Message);
        Assert.Empty(review.Value!.Unfinished);
        Assert.Single(review.Value.Moved);
        Assert.True(close.IsSuccess, close.Error?.Message);
    }
}
