using Bujo.Application;
using Bujo.Domain;

namespace Bujo.Application.Tests;

public sealed class JournalApplicationServiceTests
{
    [Fact]
    public void CaptureMoveCompleteAndResolveUseTheSameDomainService()
    {
        var repository = new InMemoryJournalRepository();
        var service = new JournalApplicationService(new JournalService(repository));
        var futureLogResult = service.CreateList("Future Log", JournalListKind.FutureLog);
        Assert.True(futureLogResult.IsSuccess);
        var futureLog = futureLogResult.Value!;
        var captured = service.Capture("Review notes", null, ["Work"], null, null);
        Assert.True(captured.IsSuccess);
        var weekly = service.CreateList("Week 47", JournalListKind.Weekly, weekKey: new IsoWeekKey(2026, 47));
        Assert.True(weekly.IsSuccess);

        var moved = service.Move(
            Guid.NewGuid(),
            captured.Value!.Item.Id,
            captured.Value.Occurrence.Id,
            weekly.Value!.Id);
        Assert.True(moved.IsSuccess);
        var completed = service.Transition(moved.Value!.Resolution.CurrentOccurrence.Id, OccurrenceAction.Done);
        Assert.True(completed.IsSuccess);

        var history = service.Resolve(captured.Value.Occurrence.Id);
        Assert.True(history.IsSuccess);
        Assert.Equal(futureLog.Id, history.Value!.SourceOccurrence.ListId);
        Assert.Equal(weekly.Value.Id, history.Value.CurrentList.Id);
        Assert.Equal(OccurrenceState.Done, history.Value.CurrentOccurrence.State);
    }
}
