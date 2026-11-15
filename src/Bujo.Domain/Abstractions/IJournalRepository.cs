namespace Bujo.Domain;

public interface IJournalRepository
{
    JournalSnapshot GetSnapshot();

    DomainResult<JournalListSnapshot> CreateList(CreateListCommand command);

    DomainResult<CaptureResult> Capture(CaptureCommand command);

    DomainResult<MoveResult> Move(MoveCommand command);

    DomainResult<OccurrenceSnapshot> Transition(TransitionCommand command);

    DomainResult<JournalListSnapshot> CloseList(ListVersionCommand command);

    DomainResult<JournalListSnapshot> ReopenList(ListVersionCommand command);

    DomainResult<ItemSnapshot> EditItem(EditItemCommand command);

    DomainResult<ItemSnapshot> ChangePlannedPeriod(ChangePeriodCommand command);

    DomainResult<JournalListSnapshot> SetListGoal(SetListGoalCommand command);
}
