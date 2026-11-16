using Bujo.Application;
using Bujo.Domain;
using Bujo.Infrastructure.Persistence;

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

    [Fact]
    public void ListsCaptureGoalsAndEditsAreAvailableThroughTheApplicationService()
    {
        var service = CreateService();
        var futureLog = service.CreateList("Future Log", JournalListKind.FutureLog);
        var custom = service.CreateList("Projects", JournalListKind.Custom);
        var weekly = service.CreateList(
            "Week 47",
            JournalListKind.Weekly,
            weekKey: new IsoWeekKey(2026, 47));

        Assert.True(futureLog.IsSuccess, futureLog.Error?.Message);
        Assert.True(custom.IsSuccess, custom.Error?.Message);
        Assert.True(weekly.IsSuccess, weekly.Error?.Message);

        var goal = service.SetListGoal(custom.Value!.Id, "Finish the project");
        var captured = service.Capture(
            "Review notes",
            "Add decisions",
            ["Work"],
            PlannedPeriod.ForWeek(new IsoWeekKey(2026, 48)),
            custom.Value.Id);

        Assert.True(goal.IsSuccess, goal.Error?.Message);
        Assert.Equal("Finish the project", goal.Value!.GoalText);
        Assert.True(captured.IsSuccess, captured.Error?.Message);
        Assert.Equal(custom.Value.Id, captured.Value!.Occurrence.ListId);
        Assert.Equal(PlannedPeriod.ForWeek(new IsoWeekKey(2026, 48)), captured.Value.Item.PlannedPeriod);

        var edited = service.EditItem(
            captured.Value.Item.Id,
            "Review meeting notes",
            "Keep the decisions",
            ["Work", "Planning"]);

        Assert.True(edited.IsSuccess, edited.Error?.Message);
        Assert.Equal("Review meeting notes", edited.Value!.Name);
        Assert.Equal("Keep the decisions", edited.Value.Description);
        Assert.Equal(["Work", "Planning"], edited.Value.Tags.Select(tag => tag.Value));
        Assert.Equal(3, service.GetSnapshot().Lists.Count);
    }

    [Fact]
    public void PlannedPeriodChangesDoNotCreateOccurrencesAndListsCanCloseAndReopen()
    {
        var service = CreateService();
        var list = service.CreateList("Custom", JournalListKind.Custom);
        Assert.True(list.IsSuccess, list.Error?.Message);

        var captured = service.Capture("Task", null, null, null, list.Value!.Id);
        Assert.True(captured.IsSuccess, captured.Error?.Message);

        var changed = service.ChangePlannedPeriod(
            captured.Value!.Item.Id,
            PlannedPeriod.ForDay(new DateOnly(2026, 11, 17)));
        var abandoned = service.Transition(captured.Value.Occurrence.Id, OccurrenceAction.Abandon);
        var closed = service.CloseList(list.Value.Id);

        Assert.True(changed.IsSuccess, changed.Error?.Message);
        Assert.Equal(PlannedPeriod.ForDay(new DateOnly(2026, 11, 17)), changed.Value!.PlannedPeriod);
        Assert.Single(service.GetSnapshot().Occurrences);
        Assert.True(abandoned.IsSuccess, abandoned.Error?.Message);
        Assert.Equal(OccurrenceState.Abandoned, abandoned.Value!.State);
        Assert.True(closed.IsSuccess, closed.Error?.Message);
        Assert.Equal(JournalListState.Closed, closed.Value!.State);

        var reopened = service.ReopenList(list.Value.Id);

        Assert.True(reopened.IsSuccess, reopened.Error?.Message);
        Assert.Equal(JournalListState.Open, reopened.Value!.State);
    }

    private static JournalApplicationService CreateService() =>
        new(new JournalService(new InMemoryJournalRepository()));
}
