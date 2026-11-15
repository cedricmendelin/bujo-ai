using Bujo.Domain;

namespace Bujo.Domain.Tests;

public sealed class TransitionTableTests
{
    [Theory]
    [InlineData(OccurrenceState.Open, OccurrenceAction.Done, true)]
    [InlineData(OccurrenceState.Open, OccurrenceAction.Abandon, true)]
    [InlineData(OccurrenceState.Open, OccurrenceAction.Reopen, false)]
    [InlineData(OccurrenceState.Done, OccurrenceAction.Done, false)]
    [InlineData(OccurrenceState.Done, OccurrenceAction.Abandon, false)]
    [InlineData(OccurrenceState.Done, OccurrenceAction.Reopen, true)]
    [InlineData(OccurrenceState.Abandoned, OccurrenceAction.Done, false)]
    [InlineData(OccurrenceState.Abandoned, OccurrenceAction.Abandon, false)]
    [InlineData(OccurrenceState.Abandoned, OccurrenceAction.Reopen, true)]
    [InlineData(OccurrenceState.Moved, OccurrenceAction.Done, false)]
    [InlineData(OccurrenceState.Moved, OccurrenceAction.Abandon, false)]
    [InlineData(OccurrenceState.Moved, OccurrenceAction.Reopen, false)]
    public void Transition_table_matches_the_domain_contract(
        OccurrenceState initialState,
        OccurrenceAction action,
        bool expectedSuccess)
    {
        var fixture = new JournalTestFixture();
        var destination = fixture.CreateWeekly(2026, 47);
        var item = fixture.Capture("transition case").Item;
        var occurrence = fixture.Repository.GetSnapshot().Occurrences.Single();

        if (initialState == OccurrenceState.Done)
        {
            fixture.Transition(occurrence.Id, OccurrenceAction.Done);
        }
        else if (initialState == OccurrenceState.Abandoned)
        {
            fixture.Transition(occurrence.Id, OccurrenceAction.Abandon);
        }
        else if (initialState == OccurrenceState.Moved)
        {
            fixture.Move(item, occurrence.Id, destination.Id);
        }

        var result = fixture.Journal.Transition(occurrence.Id, action);

        Assert.Equal(expectedSuccess, result.IsSuccess);
        if (expectedSuccess)
        {
            Assert.Equal(
                action == OccurrenceAction.Reopen ? OccurrenceState.Open : action == OccurrenceAction.Done
                    ? OccurrenceState.Done
                    : OccurrenceState.Abandoned,
                result.Value!.State);
        }
        else
        {
            Assert.NotNull(result.Error);
        }
    }

    [Fact]
    public void Moving_is_allowed_only_from_open_occurrences()
    {
        foreach (var state in Enum.GetValues<OccurrenceState>())
        {
            var fixture = new JournalTestFixture();
            var item = fixture.Capture("move transition").Item;
            var source = fixture.Repository.GetSnapshot().Occurrences.Single();
            var firstDestination = fixture.CreateWeekly(2026, 47);

            if (state == OccurrenceState.Done)
            {
                fixture.Transition(source.Id, OccurrenceAction.Done);
            }
            else if (state == OccurrenceState.Abandoned)
            {
                fixture.Transition(source.Id, OccurrenceAction.Abandon);
            }
            else if (state == OccurrenceState.Moved)
            {
                fixture.Move(item, source.Id, firstDestination.Id);
            }

            var target = fixture.CreateWeekly(2026, 48);
            var expectedOccurrenceId = state == OccurrenceState.Moved ? source.Id : item.CurrentOccurrenceId;
            var result = fixture.Journal.Move(Guid.NewGuid(), item.Id, expectedOccurrenceId, target.Id);
            Assert.Equal(state == OccurrenceState.Open, result.IsSuccess);
        }
    }

    [Fact]
    public void Reopen_requires_a_current_occurrence_on_an_open_list()
    {
        var fixture = new JournalTestFixture();
        var weekly = fixture.CreateWeekly(2026, 47);
        var capture = fixture.Capture("reopen", listId: weekly.Id);
        fixture.Transition(capture.Occurrence.Id, OccurrenceAction.Done);
        Assert.True(fixture.Journal.CloseList(weekly.Id).IsSuccess);

        var closedListResult = fixture.Journal.Transition(capture.Occurrence.Id, OccurrenceAction.Reopen);
        Assert.Equal(DomainErrorCode.ListClosed, closedListResult.Error!.Code);

        var other = fixture.CreateWeekly(2026, 48);
        var moved = fixture.Journal.Move(Guid.NewGuid(), capture.Item.Id, capture.Occurrence.Id, other.Id);
        Assert.False(moved.IsSuccess);

        var secondFixture = new JournalTestFixture();
        var original = secondFixture.Capture("moved occurrence");
        var firstList = secondFixture.CreateWeekly(2026, 47);
        secondFixture.Move(original.Item, original.Occurrence.Id, firstList.Id);
        var notCurrent = secondFixture.Journal.Transition(original.Occurrence.Id, OccurrenceAction.Reopen);
        Assert.Equal(DomainErrorCode.StaleOccurrence, notCurrent.Error!.Code);
    }
}
