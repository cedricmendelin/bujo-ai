using Bujo.Domain;

namespace Bujo.Domain.Tests;

public sealed class MoveScenarioTests
{
    [Fact]
    public void Mvp_to_week_47_to_week_48_then_done_resolves_from_both_sources()
    {
        var fixture = new JournalTestFixture();
        var mvp = fixture.CreateList("MVP");
        var week47 = fixture.CreateWeekly(2026, 47);
        var week48 = fixture.CreateWeekly(2026, 48);
        var capture = fixture.Capture("Ship the journal", listId: mvp.Id);

        var firstMove = fixture.Move(capture.Item, capture.Occurrence.Id, week47.Id);
        var secondMove = fixture.Move(
            firstMove.Resolution.Item,
            firstMove.Resolution.CurrentOccurrence.Id,
            week48.Id);
        fixture.Transition(secondMove.Resolution.CurrentOccurrence.Id, OccurrenceAction.Done);

        foreach (var source in new[] { capture.Occurrence.Id, firstMove.Resolution.CurrentOccurrence.Id })
        {
            var resolution = fixture.Journal.Resolve(source);
            Assert.True(resolution.IsSuccess);
            Assert.Equal(week48.Id, resolution.Value!.CurrentList.Id);
            Assert.Equal(OccurrenceState.Done, resolution.Value.CurrentOccurrence.State);
        }

        var occurrences = fixture.Journal.GetSnapshot().Occurrences;
        Assert.Equal(OccurrenceState.Moved, occurrences.Single(item => item.Id == capture.Occurrence.Id).State);
        Assert.Equal(
            OccurrenceState.Moved,
            occurrences.Single(item => item.Id == firstMove.Resolution.CurrentOccurrence.Id).State);
        Assert.Equal(2, fixture.Journal.GetSnapshot().MoveHistory.Count);
    }

    [Fact]
    public void Moving_back_to_a_previously_used_list_creates_a_new_visit()
    {
        var fixture = new JournalTestFixture();
        var mvp = fixture.CreateList("MVP");
        var week47 = fixture.CreateWeekly(2026, 47);
        var capture = fixture.Capture("Return", listId: mvp.Id);
        var first = fixture.Move(capture.Item, capture.Occurrence.Id, week47.Id);
        var returned = fixture.Move(first.Resolution.Item, first.Resolution.CurrentOccurrence.Id, mvp.Id);

        Assert.NotEqual(capture.Occurrence.Id, returned.Resolution.CurrentOccurrence.Id);
        Assert.Equal(mvp.Id, returned.Resolution.CurrentList.Id);
        Assert.Equal(3, returned.Resolution.CurrentOccurrence.Sequence);
        Assert.Equal(first.Resolution.CurrentOccurrence.Id, returned.Resolution.CurrentOccurrence.PreviousOccurrenceId);
        var fullResolution = fixture.Journal.Resolve(capture.Occurrence.Id);
        Assert.Equal(3, fullResolution.Value!.Chain.Count);
        Assert.Equal(
            new[] { OccurrenceState.Moved, OccurrenceState.Moved, OccurrenceState.Open },
            fullResolution.Value.Chain.Select(item => item.State));
    }
}
