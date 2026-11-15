using Bujo.Domain;

namespace Bujo.Domain.Tests;

public sealed class ConcurrencyTests
{
    [Fact]
    public async Task Concurrent_moves_from_one_current_occurrence_have_one_winner()
    {
        var fixture = new JournalTestFixture();
        var destinations = Enumerable.Range(1, 12).Select(week => fixture.CreateWeekly(2026, week)).ToArray();
        for (var iteration = 0; iteration < 10; iteration++)
        {
            var capture = fixture.Capture($"Concurrent {iteration}");
            using var start = new ManualResetEventSlim(false);
            var results = destinations.Select(destination => Task.Run(() =>
            {
                start.Wait();
                return fixture.Journal.Move(
                    Guid.NewGuid(),
                    capture.Item.Id,
                    capture.Occurrence.Id,
                    destination.Id);
            })).ToArray();
            start.Set();
            var outcomes = await Task.WhenAll(results);

            Assert.Single(outcomes, result => result.IsSuccess);
            var snapshot = fixture.Journal.GetSnapshot();
            Assert.Single(snapshot.Occurrences, item =>
                item.ItemId == capture.Item.Id && item.State == OccurrenceState.Open);
        }

        Assert.Equal(10, fixture.Journal.GetSnapshot().MoveHistory.Count);
    }

    [Fact]
    public async Task Concurrent_close_and_move_in_never_leave_an_open_occurrence_on_a_closed_list()
    {
        var fixture = new JournalTestFixture();
        var destination = fixture.CreateWeekly(2026, 47);
        var capture = fixture.Capture("Close race");
        using var start = new ManualResetEventSlim(false);

        var close = Task.Run(() =>
        {
            start.Wait();
            return fixture.Journal.CloseList(destination.Id);
        });
        var move = Task.Run(() =>
        {
            start.Wait();
            return fixture.Journal.Move(
                Guid.NewGuid(),
                capture.Item.Id,
                capture.Occurrence.Id,
                destination.Id);
        });
        start.Set();
        var closeResult = await close;
        var moveResult = await move;

        var snapshot = fixture.Journal.GetSnapshot();
        var list = snapshot.Lists.Single(item => item.Id == destination.Id);
        var openOccurrences = snapshot.Occurrences.Where(
            item => item.ListId == destination.Id && item.State == OccurrenceState.Open);
        Assert.False(list.State == JournalListState.Closed && openOccurrences.Any());
        Assert.True(closeResult.IsSuccess || moveResult.IsSuccess);
    }
}
