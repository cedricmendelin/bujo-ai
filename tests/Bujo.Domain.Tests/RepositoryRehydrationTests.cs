using Bujo.Domain;
using Bujo.Infrastructure.Persistence;

namespace Bujo.Domain.Tests;

public sealed class RepositoryRehydrationTests
{
    [Fact]
    public void RehydratedRepositoryPreservesMoveChainAndCanContinueTransitions()
    {
        var fixture = new JournalTestFixture();
        var item = fixture.Capture("Prepare release");
        var weekly = fixture.CreateWeekly(2026, 47);
        var moved = fixture.Move(item.Item, item.Occurrence.Id, weekly.Id);
        var restoredRepository = new InMemoryJournalRepository(fixture.Repository.GetSnapshot());
        var restoredJournal = new JournalService(restoredRepository, fixture.Clock);

        var resolved = restoredJournal.Resolve(item.Occurrence.Id);
        Assert.True(resolved.IsSuccess);
        Assert.Equal(moved.Resolution.CurrentOccurrence.Id, resolved.Value!.CurrentOccurrence.Id);
        Assert.Equal(OccurrenceState.Open, resolved.Value.CurrentOccurrence.State);

        var completed = restoredJournal.Transition(resolved.Value.CurrentOccurrence.Id, OccurrenceAction.Done);
        Assert.True(completed.IsSuccess);
        Assert.Equal(OccurrenceState.Done, completed.Value!.State);
        Assert.Equal(weekly.Id, restoredJournal.Resolve(item.Occurrence.Id).Value!.CurrentList.Id);
    }
}
