using Bujo.Domain;

namespace Bujo.Application;

public sealed class JournalApplicationService(JournalService journal)
{
    public JournalSnapshot GetSnapshot() => journal.GetSnapshot();

    public DomainResult<JournalListSnapshot> CreateList(
        string name,
        JournalListKind kind,
        string? goalText = null,
        IsoWeekKey? weekKey = null) =>
        journal.CreateList(name, kind, goalText, weekKey);

    public DomainResult<CaptureResult> Capture(
        string name,
        string? description,
        IReadOnlyList<string>? tags,
        PlannedPeriod? plannedPeriod,
        Guid? listId) =>
        journal.Capture(name, description, tags, plannedPeriod, listId);

    public DomainResult<MoveResult> Move(
        Guid operationId,
        Guid itemId,
        Guid expectedCurrentOccurrenceId,
        Guid destinationListId) =>
        journal.Move(operationId, itemId, expectedCurrentOccurrenceId, destinationListId);

    public DomainResult<OccurrenceSnapshot> Transition(Guid occurrenceId, OccurrenceAction action) =>
        journal.Transition(occurrenceId, action);

    public DomainResult<JournalListSnapshot> CloseList(Guid listId) => journal.CloseList(listId);

    public DomainResult<JournalListSnapshot> ReopenList(Guid listId) => journal.ReopenList(listId);

    public DomainResult<ItemSnapshot> EditItem(
        Guid itemId,
        string name,
        string? description,
        IReadOnlyList<string>? tags) =>
        journal.EditItem(itemId, name, description, tags);

    public DomainResult<ItemSnapshot> ChangePlannedPeriod(Guid itemId, PlannedPeriod period) =>
        journal.ChangePlannedPeriod(itemId, period);

    public DomainResult<JournalListSnapshot> SetListGoal(Guid listId, string? goalText) =>
        journal.SetListGoal(listId, goalText);

    public DomainResult<ItemResolution> Resolve(Guid occurrenceId) => journal.Resolve(occurrenceId);
}
