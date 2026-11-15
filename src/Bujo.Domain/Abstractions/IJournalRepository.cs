namespace Bujo.Domain;

/// <summary>
/// Commits each command atomically after checking its expected versions.
/// Failed commands leave journal state unchanged. Occurrence creation and list
/// closure must be coordinated so closed lists never acquire open occurrences.
/// Implementations must enforce at most one open occurrence per item.
/// </summary>
public interface IJournalRepository
{
    JournalSnapshot GetSnapshot();

    DomainResult<JournalListSnapshot> CreateList(CreateListCommand command);

    DomainResult<CaptureResult> Capture(CaptureCommand command);

    /// <summary>
    /// Atomically commits source state, destination occurrence, links, current
    /// reference, history and the operation result. Retrying the same operation
    /// returns its original result even after later changes; reusing its id for
    /// a different move is rejected. A stale current-occurrence id is a conflict.
    /// </summary>
    DomainResult<MoveResult> Move(MoveCommand command);

    DomainResult<OccurrenceSnapshot> Transition(TransitionCommand command);

    DomainResult<JournalListSnapshot> CloseList(ListVersionCommand command);

    DomainResult<JournalListSnapshot> ReopenList(ListVersionCommand command);

    DomainResult<ItemSnapshot> EditItem(EditItemCommand command);

    DomainResult<ItemSnapshot> ChangePlannedPeriod(ChangePeriodCommand command);

    DomainResult<JournalListSnapshot> SetListGoal(SetListGoalCommand command);
}
