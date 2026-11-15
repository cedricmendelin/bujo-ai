using Bujo.Domain;

namespace Bujo.Domain.Tests;

public sealed class ResolutionTests
{
    [Fact]
    public void Any_historical_occurrence_resolves_through_three_or_more_moves()
    {
        var fixture = new JournalTestFixture();
        var lists = Enumerable.Range(1, 5)
            .Select(week => fixture.CreateWeekly(2026, week))
            .ToArray();
        var capture = fixture.Capture("Many hops");
        var current = capture.Occurrence;
        var item = capture.Item;
        foreach (var list in lists)
        {
            var moved = fixture.Move(item, current.Id, list.Id);
            item = moved.Resolution.Item;
            current = moved.Resolution.CurrentOccurrence;
        }

        fixture.Transition(current.Id, OccurrenceAction.Abandon);

        foreach (var source in fixture.Journal.GetSnapshot().Occurrences.Where(item => item.ItemId == capture.Item.Id))
        {
            var resolution = fixture.Journal.Resolve(source.Id);
            Assert.True(resolution.IsSuccess);
            Assert.Equal(current.Id, resolution.Value!.CurrentOccurrence.Id);
            Assert.Equal(OccurrenceState.Abandoned, resolution.Value.CurrentOccurrence.State);
            Assert.Equal(current.Sequence - source.Sequence + 1, resolution.Value.Chain.Count);
        }
    }

    [Fact]
    public void Missing_occurrence_returns_a_typed_not_found_result()
    {
        var fixture = new JournalTestFixture();

        var result = fixture.Journal.Resolve(Guid.NewGuid());

        Assert.False(result.IsSuccess);
        Assert.Equal(DomainErrorCode.NotFound, result.Error!.Code);
    }
}
