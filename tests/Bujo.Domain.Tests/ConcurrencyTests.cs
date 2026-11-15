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
            Assert.All(
                outcomes.Where(result => !result.IsSuccess),
                result => Assert.Equal(DomainErrorCode.StaleOccurrence, result.Error!.Code));
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
        for (var iteration = 0; iteration < 25; iteration++)
        {
            var destination = fixture.CreateWeekly(2026, iteration + 1);
            var capture = fixture.Capture($"Close race {iteration}");
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
            if (moveResult.IsSuccess)
            {
                Assert.False(closeResult.IsSuccess);
                Assert.Contains(closeResult.Error!.Code, new[]
                {
                    DomainErrorCode.UnresolvedOccurrences,
                    DomainErrorCode.ConcurrencyConflict
                });
                Assert.Equal(JournalListState.Open, list.State);
                Assert.Single(openOccurrences);
            }
            else
            {
                Assert.True(closeResult.IsSuccess);
                Assert.Contains(moveResult.Error!.Code, new[]
                {
                    DomainErrorCode.ListClosed,
                    DomainErrorCode.ConcurrencyConflict
                });
                Assert.Equal(JournalListState.Closed, list.State);
                Assert.Empty(openOccurrences);
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Move_rejects_stale_item_or_destination_versions_without_mutation(bool changeItem)
    {
        var fixture = new JournalTestFixture();
        var destination = fixture.CreateWeekly(2026, 47);
        var capture = fixture.Capture("Version check");
        var command = new MoveCommand(
            Guid.NewGuid(), capture.Item.Id, capture.Occurrence.Id, destination.Id,
            capture.Item.Version, destination.Version, fixture.Clock.GetUtcNow());

        if (changeItem)
        {
            Assert.True(fixture.Journal.EditItem(capture.Item.Id, "Edited", null, null).IsSuccess);
        }
        else
        {
            Assert.True(fixture.Journal.SetListGoal(destination.Id, "Changed goal").IsSuccess);
        }

        var before = fixture.Journal.GetSnapshot();
        var rejected = fixture.Repository.Move(command);
        var after = fixture.Journal.GetSnapshot();

        Assert.False(rejected.IsSuccess);
        Assert.Equal(DomainErrorCode.ConcurrencyConflict, rejected.Error!.Code);
        Assert.Equal(before.Items, after.Items);
        Assert.Equal(before.Lists, after.Lists);
        Assert.Equal(before.Occurrences, after.Occurrences);
        Assert.Equal(before.History, after.History);
        Assert.Equal(before.MoveHistory, after.MoveHistory);
    }

    [Fact]
    public void Capture_rejects_a_list_closed_after_reading_its_version_without_mutation()
    {
        var fixture = new JournalTestFixture();
        var destination = fixture.CreateWeekly(2026, 47);
        var command = new CaptureCommand(
            "Stale capture", null, null, PlannedPeriod.Unplanned,
            destination.Id, destination.Version, fixture.Clock.GetUtcNow());
        Assert.True(fixture.Journal.CloseList(destination.Id).IsSuccess);
        var before = fixture.Journal.GetSnapshot();

        var rejected = fixture.Repository.Capture(command);
        var after = fixture.Journal.GetSnapshot();

        Assert.False(rejected.IsSuccess);
        Assert.Equal(DomainErrorCode.ConcurrencyConflict, rejected.Error!.Code);
        Assert.Equal(before.Items, after.Items);
        Assert.Equal(before.Lists, after.Lists);
        Assert.Equal(before.Occurrences, after.Occurrences);
        Assert.Equal(before.History, after.History);
    }
}
