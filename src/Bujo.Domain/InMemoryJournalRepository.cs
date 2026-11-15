using Bujo.Domain;

namespace Bujo.Domain;

public sealed class InMemoryJournalRepository : IJournalRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Item> _items = [];
    private readonly Dictionary<Guid, JournalList> _lists = [];
    private readonly Dictionary<Guid, Occurrence> _occurrences = [];
    private readonly List<JournalEvent> _history = [];
    private readonly Dictionary<Guid, (MoveFingerprint Fingerprint, DomainResult<MoveResult> Result)> _moveResults = [];
    private long _nextHistorySequence;

    public InMemoryJournalRepository()
    {
    }

    public static InMemoryJournalRepository CreateWithFutureLog(Guid listId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(listId, Guid.Empty);
        var repository = new InMemoryJournalRepository();
        repository._lists.Add(
            listId,
            JournalList.Create(listId, "Future Log", JournalListKind.FutureLog, null, null));
        return repository;
    }

    public InMemoryJournalRepository(JournalSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        foreach (var item in snapshot.Items)
        {
            _items.Add(item.Id, Item.FromSnapshot(item));
        }

        foreach (var list in snapshot.Lists)
        {
            _lists.Add(list.Id, JournalList.FromSnapshot(list));
        }

        foreach (var occurrence in snapshot.Occurrences)
        {
            _occurrences.Add(occurrence.Id, Occurrence.FromSnapshot(occurrence));
        }

        _history.AddRange(snapshot.History);
        _nextHistorySequence = snapshot.History.Count == 0 ? 0 : snapshot.History.Max(item => item.Sequence);
    }

    public JournalSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return SnapshotUnsafe();
        }
    }

    public DomainResult<JournalListSnapshot> CreateList(CreateListCommand command)
    {
        lock (_gate)
        {
            return Execute(() =>
            {
                if (command.Kind == JournalListKind.FutureLog &&
                    _lists.Values.Any(list => list.Kind == JournalListKind.FutureLog))
                {
                    throw new DomainException(DomainErrorCode.DuplicateFutureLog, "Only one Future Log can exist.");
                }

                var list = JournalList.Create(
                    Guid.NewGuid(),
                    command.Name,
                    command.Kind,
                    command.GoalText,
                    command.WeekKey);
                _lists.Add(list.Id, list);
                return ToSnapshot(list);
            });
        }
    }

    public DomainResult<CaptureResult> Capture(CaptureCommand command)
    {
        lock (_gate)
        {
            return Execute(() =>
            {
                var list = RequireList(command.ListId);
                RequireVersion(list.Version, command.ExpectedListVersion, "Destination list");
                EnsureListOpen(list);

                var item = Item.Create(
                    Guid.NewGuid(),
                    command.Name,
                    command.Description,
                    command.Tags,
                    command.PlannedPeriod);
                var occurrence = Occurrence.Create(
                    Guid.NewGuid(),
                    item.Id,
                    list.Id,
                    1,
                    null,
                    command.OccurredAt);
                item.SetCurrentOccurrence(occurrence.Id);
                _items.Add(item.Id, item);
                _occurrences.Add(occurrence.Id, occurrence);
                list.RecordOccurrenceChange();
                AddEvent(JournalEventKind.Captured, command.OccurredAt, item.Id, occurrence.Id, listId: list.Id);

                return new CaptureResult(
                    ToSnapshot(item),
                    ToSnapshot(occurrence),
                    PeriodConflictRules.Find(item.PlannedPeriod, ToSnapshot(list)));
            });
        }
    }

    public DomainResult<MoveResult> Move(MoveCommand command)
    {
        lock (_gate)
        {
            var fingerprint = new MoveFingerprint(
                command.ItemId,
                command.ExpectedCurrentOccurrenceId,
                command.DestinationListId);
            if (_moveResults.TryGetValue(command.OperationId, out var previous))
            {
                return previous.Fingerprint == fingerprint
                    ? previous.Result
                    : DomainResult<MoveResult>.Failure(new DomainError(
                        DomainErrorCode.IdempotencyKeyReuse,
                        "The operation id was already used for a different move."));
            }

            var execution = Execute(() =>
            {
                if (command.OperationId == Guid.Empty)
                {
                    throw DomainLimits.Invalid("A move operation id is required.");
                }

                var item = RequireItem(command.ItemId);
                if (item.CurrentOccurrenceId != command.ExpectedCurrentOccurrenceId)
                {
                    throw new DomainException(
                        DomainErrorCode.StaleOccurrence,
                        "The expected occurrence is no longer current.");
                }

                RequireVersion(item.Version, command.ExpectedItemVersion, "Item");
                var source = RequireOccurrence(item.CurrentOccurrenceId);
                var sourceList = RequireList(source.ListId);
                var destination = RequireList(command.DestinationListId);
                RequireVersion(destination.Version, command.ExpectedDestinationListVersion, "Destination list");
                EnsureListOpen(sourceList);
                EnsureListOpen(destination);

                if (source.ListId == destination.Id)
                {
                    throw DomainLimits.Invalid("An occurrence must move to a different list.");
                }

                source.Move(command.OccurredAt);
                var next = Occurrence.Create(
                    Guid.NewGuid(),
                    item.Id,
                    destination.Id,
                    source.Sequence + 1,
                    source.Id,
                    command.OccurredAt);
                source.LinkNext(next);
                item.SetCurrentOccurrence(next.Id);
                _occurrences.Add(next.Id, next);
                sourceList.RecordOccurrenceChange();
                destination.RecordOccurrenceChange();
                var moveEvent = AddEvent(
                    JournalEventKind.Moved,
                    command.OccurredAt,
                    item.Id,
                    source.Id,
                    source.Id,
                    next.Id,
                    destination.Id,
                    command.OperationId);

                var snapshot = SnapshotUnsafe();
                var resolution = snapshot.Resolve(source.Id);
                if (!resolution.IsSuccess)
                {
                    throw new DomainException(
                        resolution.Error!.Code,
                        resolution.Error.Message,
                        resolution.Error.RelatedIds);
                }

                var historyEntry = snapshot.MoveHistory.Single(entry => entry.Sequence == moveEvent.Sequence);
                var moveResult = new MoveResult(
                    resolution.Value!,
                    historyEntry,
                    PeriodConflictRules.Find(item.PlannedPeriod, ToSnapshot(destination)));
                return moveResult;
            });

            if (!execution.IsSuccess)
            {
                return DomainResult<MoveResult>.Failure(execution.Error!);
            }

            var result = DomainResult<MoveResult>.Success(execution.Value!);
            _moveResults[command.OperationId] = (fingerprint, result);
            return result;
        }
    }

    public DomainResult<OccurrenceSnapshot> Transition(TransitionCommand command)
    {
        lock (_gate)
        {
            return Execute(() =>
            {
                var occurrence = RequireOccurrence(command.OccurrenceId);
                var item = RequireItem(occurrence.ItemId);
                if (item.CurrentOccurrenceId != occurrence.Id)
                {
                    throw new DomainException(DomainErrorCode.StaleOccurrence, "Only the current occurrence can change state.");
                }

                RequireVersion(item.Version, command.ExpectedItemVersion, "Item");
                var list = RequireList(occurrence.ListId);
                RequireVersion(list.Version, command.ExpectedListVersion, "List");

                switch (command.Action)
                {
                    case OccurrenceAction.Done:
                        occurrence.Complete(command.OccurredAt);
                        AddEvent(JournalEventKind.Done, command.OccurredAt, item.Id, occurrence.Id, listId: list.Id);
                        break;
                    case OccurrenceAction.Abandon:
                        occurrence.Abandon(command.OccurredAt);
                        AddEvent(JournalEventKind.Abandoned, command.OccurredAt, item.Id, occurrence.Id, listId: list.Id);
                        break;
                    case OccurrenceAction.Reopen:
                        occurrence.Reopen(true, list.State == JournalListState.Open, command.OccurredAt);
                        AddEvent(JournalEventKind.Reopened, command.OccurredAt, item.Id, occurrence.Id, listId: list.Id);
                        break;
                    default:
                        throw DomainLimits.Invalid("Unknown occurrence action.");
                }

                item.RecordChange();
                list.RecordOccurrenceChange();
                return ToSnapshot(occurrence);
            });
        }
    }

    public DomainResult<JournalListSnapshot> CloseList(ListVersionCommand command)
    {
        lock (_gate)
        {
            return Execute(() =>
            {
                var list = RequireList(command.ListId);
                RequireVersion(list.Version, command.ExpectedVersion, "List");
                var unresolved = _occurrences.Values
                    .Where(occurrence => occurrence.ListId == list.Id && occurrence.State == OccurrenceState.Open)
                    .Select(occurrence => occurrence.Id)
                    .ToArray();
                var wasOpen = list.State == JournalListState.Open;
                list.Close(unresolved, command.OccurredAt);
                if (wasOpen)
                {
                    AddEvent(JournalEventKind.ListClosed, command.OccurredAt, listId: list.Id);
                }

                return ToSnapshot(list);
            });
        }
    }

    public DomainResult<JournalListSnapshot> ReopenList(ListVersionCommand command)
    {
        lock (_gate)
        {
            return Execute(() =>
            {
                var list = RequireList(command.ListId);
                RequireVersion(list.Version, command.ExpectedVersion, "List");
                var wasClosed = list.State == JournalListState.Closed;
                list.Reopen();
                if (wasClosed)
                {
                    AddEvent(JournalEventKind.ListReopened, command.OccurredAt, listId: list.Id);
                }

                return ToSnapshot(list);
            });
        }
    }

    public DomainResult<ItemSnapshot> EditItem(EditItemCommand command)
    {
        lock (_gate)
        {
            return Execute(() =>
            {
                var item = RequireItem(command.ItemId);
                RequireVersion(item.Version, command.ExpectedVersion, "Item");
                item.Edit(command.Name, command.Description, command.Tags);
                return ToSnapshot(item);
            });
        }
    }

    public DomainResult<ItemSnapshot> ChangePlannedPeriod(ChangePeriodCommand command)
    {
        lock (_gate)
        {
            return Execute(() =>
            {
                var item = RequireItem(command.ItemId);
                RequireVersion(item.Version, command.ExpectedVersion, "Item");
                item.ChangePlannedPeriod(command.PlannedPeriod);
                return ToSnapshot(item);
            });
        }
    }

    public DomainResult<JournalListSnapshot> SetListGoal(SetListGoalCommand command)
    {
        lock (_gate)
        {
            return Execute(() =>
            {
                var list = RequireList(command.ListId);
                RequireVersion(list.Version, command.ExpectedVersion, "List");
                list.SetGoalText(command.GoalText);
                return ToSnapshot(list);
            });
        }
    }

    private JournalSnapshot SnapshotUnsafe() => new(
        _items.Values.OrderBy(item => item.Id).Select(ToSnapshot).ToArray(),
        _lists.Values.OrderBy(list => list.Id).Select(ToSnapshot).ToArray(),
        _occurrences.Values.OrderBy(item => item.Sequence).ThenBy(item => item.Id).Select(ToSnapshot).ToArray(),
        _history.ToArray());

    private JournalEvent AddEvent(
        JournalEventKind kind,
        DateTimeOffset occurredAt,
        Guid? itemId = null,
        Guid? occurrenceId = null,
        Guid? previousOccurrenceId = null,
        Guid? nextOccurrenceId = null,
        Guid? listId = null,
        Guid? operationId = null)
    {
        var entry = new JournalEvent(
            ++_nextHistorySequence,
            kind,
            occurredAt.ToUniversalTime(),
            itemId,
            occurrenceId,
            previousOccurrenceId,
            nextOccurrenceId,
            listId,
            operationId);
        _history.Add(entry);
        return entry;
    }

    private Item RequireItem(Guid id) =>
        _items.TryGetValue(id, out var item)
            ? item
            : throw new DomainException(DomainErrorCode.NotFound, "The item was not found.");

    private JournalList RequireList(Guid id) =>
        _lists.TryGetValue(id, out var list)
            ? list
            : throw new DomainException(DomainErrorCode.NotFound, "The list was not found.");

    private Occurrence RequireOccurrence(Guid id) =>
        _occurrences.TryGetValue(id, out var occurrence)
            ? occurrence
            : throw new DomainException(DomainErrorCode.NotFound, "The occurrence was not found.");

    private static void EnsureListOpen(JournalList list)
    {
        if (list.State != JournalListState.Open)
        {
            throw new DomainException(DomainErrorCode.ListClosed, "The list is closed.");
        }
    }

    private static void RequireVersion(long actual, long expected, string subject)
    {
        if (actual != expected)
        {
            throw new DomainException(DomainErrorCode.ConcurrencyConflict, $"{subject} changed since it was read.");
        }
    }

    private static DomainResult<T> Execute<T>(Func<T> action)
    {
        try
        {
            return DomainResult<T>.Success(action());
        }
        catch (DomainException exception)
        {
            return DomainResult<T>.Failure(exception.ToError());
        }
    }

    private static ItemSnapshot ToSnapshot(Item item) =>
        new(item.Id, item.Name, item.Description, item.Tags.ToArray(), item.PlannedPeriod, item.CurrentOccurrenceId, item.Version);

    private static JournalListSnapshot ToSnapshot(JournalList list) =>
        new(list.Id, list.Name, list.Kind, list.State, list.GoalText, list.WeekKey, list.Version);

    private static OccurrenceSnapshot ToSnapshot(Occurrence occurrence) =>
        new(
            occurrence.Id,
            occurrence.ItemId,
            occurrence.ListId,
            occurrence.Sequence,
            occurrence.State,
            occurrence.CreatedAt,
            occurrence.UpdatedAt,
            occurrence.PreviousOccurrenceId,
            occurrence.NextOccurrenceId,
            occurrence.Version);

    private sealed record MoveFingerprint(Guid ItemId, Guid ExpectedCurrentOccurrenceId, Guid DestinationListId);

}
