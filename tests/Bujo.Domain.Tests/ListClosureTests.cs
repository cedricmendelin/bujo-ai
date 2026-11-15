using Bujo.Domain;

namespace Bujo.Domain.Tests;

public sealed class ListClosureTests
{
    [Fact]
    public void Open_occurrences_block_closure_until_each_has_an_explicit_disposition()
    {
        var fixture = new JournalTestFixture();
        var list = fixture.CreateWeekly(2026, 47);
        var first = fixture.Capture("Move", listId: list.Id);
        var second = fixture.Capture("Done", listId: list.Id);
        var third = fixture.Capture("Abandon", listId: list.Id);

        var blocked = fixture.Journal.CloseList(list.Id);

        Assert.False(blocked.IsSuccess);
        Assert.Equal(DomainErrorCode.UnresolvedOccurrences, blocked.Error!.Code);
        Assert.Equal(
            new[] { first.Occurrence.Id, second.Occurrence.Id, third.Occurrence.Id }.Order(),
            blocked.Error.RelatedIds!.Order());

        var destination = fixture.CreateWeekly(2026, 48);
        fixture.Move(first.Item, first.Occurrence.Id, destination.Id);
        fixture.Transition(second.Occurrence.Id, OccurrenceAction.Done);
        fixture.Transition(third.Occurrence.Id, OccurrenceAction.Abandon);

        Assert.True(fixture.Journal.CloseList(list.Id).IsSuccess);
    }

    [Fact]
    public void Future_log_cannot_close_and_closed_lists_reject_new_occurrences_until_reopened()
    {
        var fixture = new JournalTestFixture();
        var list = fixture.CreateList("Custom");
        Assert.True(fixture.Journal.CloseList(list.Id).IsSuccess);

        var captureClosed = fixture.Journal.Capture("No", listId: list.Id);
        Assert.Equal(DomainErrorCode.ListClosed, captureClosed.Error!.Code);
        var source = fixture.Capture("Move into closed");
        var moveClosed = fixture.Journal.Move(Guid.NewGuid(), source.Item.Id, source.Occurrence.Id, list.Id);
        Assert.Equal(DomainErrorCode.ListClosed, moveClosed.Error!.Code);
        var futureLogClose = fixture.Journal.CloseList(fixture.FutureLog.Id);
        Assert.Equal(DomainErrorCode.FutureLogCannotClose, futureLogClose.Error!.Code);

        Assert.True(fixture.Journal.ReopenList(list.Id).IsSuccess);
        Assert.True(fixture.Journal.Capture("Yes", listId: list.Id).IsSuccess);
    }

    [Fact]
    public void Moved_historical_occurrences_do_not_block_closure()
    {
        var fixture = new JournalTestFixture();
        var source = fixture.CreateList("Source");
        var capture = fixture.Capture("Moved away", listId: source.Id);
        fixture.Move(capture.Item, capture.Occurrence.Id, fixture.FutureLog.Id);

        var close = fixture.Journal.CloseList(source.Id);

        Assert.True(close.IsSuccess, close.Error?.Message);
        Assert.Equal(OccurrenceState.Moved, fixture.Journal.GetSnapshot().Occurrences.Single(item => item.Id == capture.Occurrence.Id).State);
    }
}
