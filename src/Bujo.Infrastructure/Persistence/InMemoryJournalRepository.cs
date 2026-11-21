using System.Text;
using Bujo.Domain;

namespace Bujo.Infrastructure.Persistence;

public sealed class InMemoryJournalRepository : IJournalRepository
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Item> _items = [];
    private readonly Dictionary<Guid, JournalList> _lists = [];
    private readonly Dictionary<Guid, Occurrence> _occurrences = [];
    private readonly List<JournalEvent> _history = [];
    private readonly Dictionary<Guid, (MoveFingerprint Fingerprint, DomainResult<MoveResult> Result)> _moveResults = [];
    private readonly Dictionary<Guid, ReviewProposalSnapshot> _reviewProposals = [];
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

    public InMemoryJournalRepository(
        JournalSnapshot snapshot,
        IEnumerable<ReviewProposalSnapshot>? reviewProposals = null)
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
        if (reviewProposals is not null)
        {
            foreach (var proposal in reviewProposals)
            {
                _reviewProposals.Add(proposal.Id, proposal);
            }
        }
    }

    public JournalSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return SnapshotUnsafe();
        }
    }

    public DomainResult<JournalSnapshot> RestoreSnapshot(JournalSnapshot snapshot)
    {
        lock (_gate)
        {
            try
            {
                ValidateSnapshot(snapshot);
                RestoreUnsafe(snapshot, []);
                _reviewProposals.Clear();
                return DomainResult<JournalSnapshot>.Success(SnapshotUnsafe());
            }
            catch (Exception exception) when (
                exception is ArgumentException or DomainException or InvalidOperationException)
            {
                return DomainResult<JournalSnapshot>.Failure(new DomainError(
                    DomainErrorCode.InvalidInput,
                    "The journal backup is invalid."));
            }
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
                    command.WeekKey,
                    command.PeriodKey);

                if (_lists.Values.Any(existing =>
                    string.Equals(NormalizeListName(existing.Name), list.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new DomainException(DomainErrorCode.DuplicateListName, "A list with this name already exists.");
                }

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
                    command.PlannedPeriod,
                    command.Type);
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

                if (item.Type == ItemType.Thought)
                {
                    throw new DomainException(DomainErrorCode.InvalidTransition, "Thoughts cannot change completion state.");
                }

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
                    .Where(occurrence => occurrence.ListId == list.Id &&
                        occurrence.State == OccurrenceState.Open &&
                        _items[occurrence.ItemId].Type == ItemType.Task)
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

    public DomainResult<ReviewProposalSnapshot> CreateReviewProposal(CreateReviewProposalCommand command)
    {
        lock (_gate)
        {
            if (command.Id == Guid.Empty ||
                (command.Changes.Count == 0 && !command.RequestCloseList) ||
                _reviewProposals.ContainsKey(command.Id))
            {
                return DomainResult<ReviewProposalSnapshot>.Failure(
                    new DomainError(DomainErrorCode.InvalidInput, "The review proposal is invalid."));
            }

            var proposal = new ReviewProposalSnapshot(
                command.Id,
                command.ListId,
                command.ExpectedListVersion,
                command.CreatedBy,
                command.Source,
                command.CreatedAt,
                command.ExpiresAt,
                ReviewProposalStatus.Pending,
                command.RequestCloseList,
                command.Changes.ToArray(),
                command.Digest,
                null,
                null,
                null,
                null);
            _reviewProposals.Add(proposal.Id, proposal);
            return DomainResult<ReviewProposalSnapshot>.Success(proposal);
        }
    }

    public DomainResult<ReviewProposalSnapshot> GetReviewProposal(Guid id)
    {
        lock (_gate)
        {
            return _reviewProposals.TryGetValue(id, out var proposal)
                ? DomainResult<ReviewProposalSnapshot>.Success(proposal)
                : DomainResult<ReviewProposalSnapshot>.Failure(
                    new DomainError(DomainErrorCode.NotFound, "The review proposal was not found."));
        }
    }

    internal IReadOnlyList<ReviewProposalSnapshot> GetReviewProposals()
    {
        lock (_gate)
        {
            return _reviewProposals.Values.OrderBy(proposal => proposal.CreatedAt).ToArray();
        }
    }

    public DomainResult<ReviewProposalSnapshot> SetReviewProposalStatus(ReviewProposalStatusCommand command)
    {
        lock (_gate)
        {
            if (!_reviewProposals.TryGetValue(command.Id, out var proposal))
            {
                return DomainResult<ReviewProposalSnapshot>.Failure(
                    new DomainError(DomainErrorCode.NotFound, "The review proposal was not found."));
            }

            if (!StringComparer.Ordinal.Equals(proposal.CreatedBy, command.UserId))
            {
                return DomainResult<ReviewProposalSnapshot>.Failure(
                    new DomainError(DomainErrorCode.ApprovalDenied, "The proposal is not available to this principal."));
            }

            if (command.Status == ReviewProposalStatus.Approved)
            {
                if (proposal.Status != ReviewProposalStatus.Pending ||
                    command.OccurredAt >= proposal.ExpiresAt)
                {
                    return DomainResult<ReviewProposalSnapshot>.Failure(
                        new DomainError(DomainErrorCode.ProposalTerminal, "The proposal is not pending or has expired."));
                }

                if (!StringComparer.Ordinal.Equals(proposal.Digest, command.Digest))
                {
                    return DomainResult<ReviewProposalSnapshot>.Failure(
                        new DomainError(DomainErrorCode.ProposalDigestMismatch, "The proposal digest does not match."));
                }

                proposal = proposal with
                {
                    Status = ReviewProposalStatus.Approved,
                    ApprovedBy = command.UserId,
                    ApprovedAt = command.OccurredAt,
                    ApprovedDigest = command.Digest
                };
            }
            else if (command.Status == ReviewProposalStatus.Expired)
            {
                if (proposal.Status is not (ReviewProposalStatus.Pending or ReviewProposalStatus.Approved))
                {
                    return DomainResult<ReviewProposalSnapshot>.Failure(
                        new DomainError(DomainErrorCode.ProposalTerminal, "The proposal is already terminal."));
                }

                proposal = proposal with { Status = ReviewProposalStatus.Expired };
            }
            else if (command.Status is ReviewProposalStatus.Cancelled or ReviewProposalStatus.Rejected)
            {
                if (proposal.Status is not (ReviewProposalStatus.Pending or ReviewProposalStatus.Approved))
                {
                    return DomainResult<ReviewProposalSnapshot>.Failure(
                        new DomainError(DomainErrorCode.ProposalTerminal, "The proposal is already terminal."));
                }

                proposal = proposal with { Status = command.Status };
            }
            else
            {
                return DomainResult<ReviewProposalSnapshot>.Failure(
                    new DomainError(DomainErrorCode.InvalidInput, "The proposal status transition is invalid."));
            }

            _reviewProposals[proposal.Id] = proposal;
            return DomainResult<ReviewProposalSnapshot>.Success(proposal);
        }
    }

    public DomainResult<ReviewProposalSnapshot> ApplyReviewProposal(ApplyReviewProposalCommand command)
    {
        lock (_gate)
        {
            if (!_reviewProposals.TryGetValue(command.Id, out var proposal))
            {
                return DomainResult<ReviewProposalSnapshot>.Failure(
                    new DomainError(DomainErrorCode.NotFound, "The review proposal was not found."));
            }

            if (proposal.Status == ReviewProposalStatus.Applied)
            {
                return DomainResult<ReviewProposalSnapshot>.Success(proposal);
            }

            if (!StringComparer.Ordinal.Equals(proposal.CreatedBy, command.UserId) ||
                !StringComparer.Ordinal.Equals(proposal.ApprovedBy, command.UserId))
            {
                return DomainResult<ReviewProposalSnapshot>.Failure(
                    new DomainError(DomainErrorCode.ApprovalDenied, "The proposal is not available to this principal."));
            }

            if (proposal.Status != ReviewProposalStatus.Approved ||
                !StringComparer.Ordinal.Equals(proposal.Digest, command.Digest) ||
                !StringComparer.Ordinal.Equals(proposal.ApprovedDigest, command.Digest))
            {
                return DomainResult<ReviewProposalSnapshot>.Failure(
                    new DomainError(DomainErrorCode.ApprovalRequired, "The proposal requires a valid approval."));
            }

            if (command.OccurredAt >= proposal.ExpiresAt)
            {
                proposal = proposal with { Status = ReviewProposalStatus.Expired };
                _reviewProposals[proposal.Id] = proposal;
                return DomainResult<ReviewProposalSnapshot>.Success(proposal);
            }

            var list = _lists.GetValueOrDefault(proposal.ListId);
            var stale = list is null || list.Version != proposal.ExpectedListVersion ||
                list.State != JournalListState.Open;
            foreach (var change in proposal.Changes)
            {
                var item = _items.GetValueOrDefault(change.ItemId);
                var occurrence = _occurrences.GetValueOrDefault(change.OccurrenceId);
                if (item is null || occurrence is null ||
                    item.Version != change.ExpectedItemVersion ||
                    item.CurrentOccurrenceId != occurrence.Id ||
                    occurrence.ItemId != item.Id ||
                    occurrence.ListId != proposal.ListId ||
                    occurrence.State != OccurrenceState.Open)
                {
                    stale = true;
                }

                if (change.Action == ReviewDisposition.Move)
                {
                    var destination = change.DestinationListId is { } destinationId
                        ? _lists.GetValueOrDefault(destinationId)
                        : null;
                    if (destination is null ||
                        destination.State != JournalListState.Open ||
                        destination.Version != change.ExpectedDestinationListVersion)
                    {
                        stale = true;
                    }
                }
            }

            if (stale)
            {
                proposal = proposal with { Status = ReviewProposalStatus.Conflicted };
                _reviewProposals[proposal.Id] = proposal;
                return DomainResult<ReviewProposalSnapshot>.Success(proposal);
            }

            var before = SnapshotUnsafe();
            var priorMoveResults = _moveResults.ToArray();
            foreach (var change in proposal.Changes)
            {
                DomainResult<object> result;
                if (change.Action == ReviewDisposition.Move)
                {
                    var item = RequireItem(change.ItemId);
                    var destination = RequireList(change.DestinationListId!.Value);
                    var moved = Move(new MoveCommand(
                        change.OperationId,
                        item.Id,
                        change.OccurrenceId,
                        destination.Id,
                        item.Version,
                        destination.Version,
                        command.OccurredAt));
                    result = moved.IsSuccess
                        ? DomainResult<object>.Success(moved.Value!)
                        : DomainResult<object>.Failure(moved.Error!);
                }
                else
                {
                    var occurrence = RequireOccurrence(change.OccurrenceId);
                    var item = RequireItem(change.ItemId);
                    var occurrenceList = RequireList(occurrence.ListId);
                    var transitioned = Transition(new TransitionCommand(
                        occurrence.Id,
                        change.Action == ReviewDisposition.Done ? OccurrenceAction.Done : OccurrenceAction.Abandon,
                        item.Version,
                        occurrenceList.Version,
                        command.OccurredAt));
                    result = transitioned.IsSuccess
                        ? DomainResult<object>.Success(transitioned.Value!)
                        : DomainResult<object>.Failure(transitioned.Error!);
                }

                if (!result.IsSuccess)
                {
                    RestoreUnsafe(before, priorMoveResults);
                    return DomainResult<ReviewProposalSnapshot>.Failure(result.Error!);
                }
            }

            if (proposal.RequestCloseList)
            {
                var currentList = RequireList(proposal.ListId);
                var closed = CloseList(new ListVersionCommand(currentList.Id, currentList.Version, command.OccurredAt));
                if (!closed.IsSuccess)
                {
                    RestoreUnsafe(before, priorMoveResults);
                    return DomainResult<ReviewProposalSnapshot>.Failure(closed.Error!);
                }
            }

            proposal = proposal with
            {
                Status = ReviewProposalStatus.Applied,
                AppliedAt = command.OccurredAt
            };
            _reviewProposals[proposal.Id] = proposal;
            return DomainResult<ReviewProposalSnapshot>.Success(proposal);
        }
    }

    private JournalSnapshot SnapshotUnsafe() => new(
        _items.Values.OrderBy(item => item.Id).Select(ToSnapshot).ToArray(),
        _lists.Values.OrderBy(list => list.Id).Select(ToSnapshot).ToArray(),
        _occurrences.Values.OrderBy(item => item.Sequence).ThenBy(item => item.Id).Select(ToSnapshot).ToArray(),
        _history.ToArray());

    private void RestoreUnsafe(
        JournalSnapshot snapshot,
        IReadOnlyList<KeyValuePair<Guid, (MoveFingerprint Fingerprint, DomainResult<MoveResult> Result)>> moveResults)
    {
        _items.Clear();
        foreach (var item in snapshot.Items)
        {
            _items.Add(item.Id, Item.FromSnapshot(item));
        }

        _lists.Clear();
        foreach (var list in snapshot.Lists)
        {
            _lists.Add(list.Id, JournalList.FromSnapshot(list));
        }

        _occurrences.Clear();
        foreach (var occurrence in snapshot.Occurrences)
        {
            _occurrences.Add(occurrence.Id, Occurrence.FromSnapshot(occurrence));
        }

        _history.Clear();
        _history.AddRange(snapshot.History);
        _nextHistorySequence = snapshot.History.Count == 0 ? 0 : snapshot.History.Max(item => item.Sequence);
        _moveResults.Clear();
        foreach (var moveResult in moveResults)
        {
            _moveResults.Add(moveResult.Key, moveResult.Value);
        }
    }

    internal static void ValidateSnapshot(JournalSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Items is null || snapshot.Lists is null ||
            snapshot.Occurrences is null || snapshot.History is null ||
            snapshot.Lists.Count == 0 ||
            snapshot.Lists.Count(list => list.Kind == JournalListKind.FutureLog) != 1 ||
            snapshot.Lists.Any(list =>
                list.Id == Guid.Empty ||
                string.IsNullOrWhiteSpace(list.Name) ||
                list.Name.Length > DomainLimits.ListName ||
                !Enum.IsDefined(list.Kind) ||
                !Enum.IsDefined(list.State) ||
                list.GoalText?.Length > DomainLimits.GoalText ||
                list.Version < 0) ||
            snapshot.Lists.Select(list => list.Id).Distinct().Count() != snapshot.Lists.Count ||
            snapshot.Lists.Select(list => NormalizeListName(list.Name))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != snapshot.Lists.Count ||
            snapshot.Items.Any(item =>
                item.Id == Guid.Empty ||
                string.IsNullOrWhiteSpace(item.Name) ||
                item.Name.Length > DomainLimits.ItemName ||
                item.Description?.Length > DomainLimits.ItemDescription ||
                item.Tags is null ||
                item.Tags.Count > DomainLimits.TagsPerItem ||
                item.CurrentOccurrenceId == Guid.Empty ||
                !Enum.IsDefined(item.Type) ||
                item.Version < 0) ||
            snapshot.Items.Select(item => item.Id).Distinct().Count() != snapshot.Items.Count ||
            snapshot.Occurrences.Any(occurrence =>
                occurrence.Id == Guid.Empty ||
                occurrence.ItemId == Guid.Empty ||
                occurrence.ListId == Guid.Empty ||
                occurrence.Sequence < 1 ||
                !Enum.IsDefined(occurrence.State) ||
                occurrence.Version < 0 ||
                !snapshot.Items.Any(item => item.Id == occurrence.ItemId) ||
                !snapshot.Lists.Any(list => list.Id == occurrence.ListId)) ||
            snapshot.Occurrences.Select(occurrence => occurrence.Id).Distinct().Count() != snapshot.Occurrences.Count ||
            snapshot.Occurrences.GroupBy(occurrence => new
            {
                occurrence.ItemId,
                occurrence.Sequence
            }).Any(group => group.Count() != 1) ||
            snapshot.History.Any(entry => entry.Sequence < 1 || !Enum.IsDefined(entry.Kind)) ||
            snapshot.History.Select(entry => entry.Sequence).Distinct().Count() != snapshot.History.Count)
        {
            throw DomainLimits.Invalid("The journal backup is invalid.");
        }

        var openByItem = snapshot.Occurrences
            .Where(occurrence => occurrence.State == OccurrenceState.Open)
            .GroupBy(occurrence => occurrence.ItemId)
            .Any(group => group.Count() != 1);
        if (openByItem)
        {
            throw DomainLimits.Invalid("The journal backup must contain one open occurrence per active item.");
        }

        var repository = new InMemoryJournalRepository(snapshot);
        foreach (var occurrence in snapshot.Occurrences)
        {
            var resolved = repository.GetSnapshot().Resolve(occurrence.Id);
            if (!resolved.IsSuccess)
            {
                throw new DomainException(resolved.Error!.Code, resolved.Error.Message);
            }
        }
    }

    private static string NormalizeListName(string name) => name.Trim().Normalize(NormalizationForm.FormKC);

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
        new(item.Id, item.Name, item.Description, item.Tags.ToArray(), item.PlannedPeriod, item.CurrentOccurrenceId, item.Version, item.Type);

    private static JournalListSnapshot ToSnapshot(JournalList list) =>
        new(list.Id, list.Name, list.Kind, list.State, list.GoalText, list.WeekKey, list.Version, list.PeriodKey);

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
