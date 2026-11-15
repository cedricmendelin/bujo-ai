using Bujo.Domain;
using Bujo.Domain.Tests.InMemory;

namespace Bujo.Domain.Tests;

internal sealed class JournalTestFixture
{
    public JournalTestFixture(DateTimeOffset? now = null, string timeZone = "America/Los_Angeles")
    {
        Clock = new FrozenTimeProvider(now ?? new DateTimeOffset(2026, 11, 15, 13, 0, 0, TimeSpan.Zero));
        Repository = new InMemoryJournalRepository();
        Journal = new JournalService(Repository, Clock, timeZone);
        FutureLog = CreateList("Future Log", JournalListKind.FutureLog);
    }

    public FrozenTimeProvider Clock { get; }

    public InMemoryJournalRepository Repository { get; }

    public JournalService Journal { get; }

    public JournalListSnapshot FutureLog { get; }

    public JournalListSnapshot CreateList(
        string name,
        JournalListKind kind = JournalListKind.Custom,
        string? goalText = null,
        IsoWeekKey? weekKey = null)
    {
        var result = Journal.CreateList(name, kind, goalText, weekKey);
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value!;
    }

    public JournalListSnapshot CreateWeekly(int year, int week) =>
        CreateList($"Week {week}", JournalListKind.Weekly, weekKey: new IsoWeekKey(year, week));

    public CaptureResult Capture(
        string name,
        PlannedPeriod? plannedPeriod = null,
        Guid? listId = null,
        IReadOnlyList<string>? tags = null)
    {
        var result = Journal.Capture(name, tags: tags, plannedPeriod: plannedPeriod, listId: listId);
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value!;
    }

    public MoveResult Move(ItemSnapshot item, Guid occurrenceId, Guid destinationListId, Guid? operationId = null)
    {
        var result = Journal.Move(operationId ?? Guid.NewGuid(), item.Id, occurrenceId, destinationListId);
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value!;
    }

    public OccurrenceSnapshot Transition(Guid occurrenceId, OccurrenceAction action)
    {
        var result = Journal.Transition(occurrenceId, action);
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value!;
    }
}

internal sealed class FrozenTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
