namespace Bujo.Domain;

public sealed record ItemSnapshot(
    Guid Id,
    string Name,
    string? Description,
    IReadOnlyList<Tag> Tags,
    PlannedPeriod PlannedPeriod,
    Guid CurrentOccurrenceId,
    long Version,
    ItemType Type = ItemType.Task);

public sealed record JournalListSnapshot(
    Guid Id,
    string Name,
    JournalListKind Kind,
    JournalListState State,
    string? GoalText,
    IsoWeekKey? WeekKey,
    long Version,
    JournalPeriodKey? PeriodKey = null);

public sealed record OccurrenceSnapshot(
    Guid Id,
    Guid ItemId,
    Guid ListId,
    int Sequence,
    OccurrenceState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Guid? PreviousOccurrenceId,
    Guid? NextOccurrenceId,
    long Version);

public sealed record ItemResolution(
    ItemSnapshot Item,
    OccurrenceSnapshot SourceOccurrence,
    OccurrenceSnapshot CurrentOccurrence,
    JournalListSnapshot CurrentList,
    IReadOnlyList<OccurrenceSnapshot> Chain);

public sealed class JournalSnapshot
{
    internal JournalSnapshot(
        IReadOnlyList<ItemSnapshot> items,
        IReadOnlyList<JournalListSnapshot> lists,
        IReadOnlyList<OccurrenceSnapshot> occurrences,
        IReadOnlyList<JournalEvent> history)
    {
        Items = items;
        Lists = lists;
        Occurrences = occurrences;
        History = history;
        MoveHistory = history
            .Where(item => item.Kind == JournalEventKind.Moved)
            .Select(item => new MoveHistoryEntry(
                item.Sequence,
                item.OperationId!.Value,
                item.ItemId!.Value,
                item.PreviousOccurrenceId!.Value,
                item.NextOccurrenceId!.Value,
                Occurrences.Single(occurrence => occurrence.Id == item.PreviousOccurrenceId).ListId,
                Occurrences.Single(occurrence => occurrence.Id == item.NextOccurrenceId).ListId,
                item.OccurredAt))
            .ToArray();
    }

    public static JournalSnapshot Restore(
        IReadOnlyList<ItemSnapshot> items,
        IReadOnlyList<JournalListSnapshot> lists,
        IReadOnlyList<OccurrenceSnapshot> occurrences,
        IReadOnlyList<JournalEvent> history) =>
        new(items, lists, occurrences, history);

    public IReadOnlyList<ItemSnapshot> Items { get; }

    public IReadOnlyList<JournalListSnapshot> Lists { get; }

    public IReadOnlyList<OccurrenceSnapshot> Occurrences { get; }

    public IReadOnlyList<JournalEvent> History { get; }

    public IReadOnlyList<MoveHistoryEntry> MoveHistory { get; }

    public DomainResult<ItemResolution> Resolve(Guid occurrenceId)
    {
        var source = Occurrences.SingleOrDefault(item => item.Id == occurrenceId);
        if (source is null)
        {
            return DomainResult<ItemResolution>.Failure(
                new DomainError(DomainErrorCode.NotFound, "Occurrence was not found."));
        }

        var item = Items.SingleOrDefault(candidate => candidate.Id == source.ItemId);
        if (item is null)
        {
            return DomainResult<ItemResolution>.Failure(
                new DomainError(DomainErrorCode.InvariantViolation, "Occurrence has no canonical item."));
        }

        var chain = new List<OccurrenceSnapshot>();
        var visited = new HashSet<Guid>();
        var current = source;
        while (true)
        {
            if (!visited.Add(current.Id) || current.ItemId != item.Id)
            {
                return DomainResult<ItemResolution>.Failure(
                    new DomainError(DomainErrorCode.InvariantViolation, "Occurrence links contain a cycle or cross item boundary."));
            }

            chain.Add(current);
            if (current.NextOccurrenceId is null)
            {
                break;
            }

            var next = Occurrences.SingleOrDefault(candidate => candidate.Id == current.NextOccurrenceId);
            if (next is null || next.PreviousOccurrenceId != current.Id || next.Sequence <= current.Sequence)
            {
                return DomainResult<ItemResolution>.Failure(
                    new DomainError(DomainErrorCode.InvariantViolation, "Occurrence links are incomplete or out of order."));
            }

            current = next;
        }

        if (current.Id != item.CurrentOccurrenceId)
        {
            return DomainResult<ItemResolution>.Failure(
                new DomainError(DomainErrorCode.InvariantViolation, "The occurrence chain does not end at the item's current occurrence."));
        }

        var list = Lists.SingleOrDefault(candidate => candidate.Id == current.ListId);
        if (list is null)
        {
            return DomainResult<ItemResolution>.Failure(
                new DomainError(DomainErrorCode.InvariantViolation, "Current occurrence has no list."));
        }

        return DomainResult<ItemResolution>.Success(
            new ItemResolution(item, source, current, list, chain.AsReadOnly()));
    }

    public DomainResult<ListReview> GetListReview(Guid listId)
    {
        var list = Lists.SingleOrDefault(candidate => candidate.Id == listId);
        if (list is null)
        {
            return DomainResult<ListReview>.Failure(
                new DomainError(DomainErrorCode.NotFound, "The list was not found."));
        }

        var items = Items.ToDictionary(item => item.Id);
        var entries = new List<ListReviewEntry>();
        foreach (var occurrence in Occurrences
            .Where(occurrence => occurrence.ListId == listId)
            .OrderBy(occurrence => occurrence.Sequence)
            .ThenBy(occurrence => occurrence.Id))
        {
            ItemResolution? resolution = null;
            if (occurrence.State == OccurrenceState.Moved)
            {
                var resolved = Resolve(occurrence.Id);
                if (!resolved.IsSuccess)
                {
                    return DomainResult<ListReview>.Failure(resolved.Error!);
                }

                resolution = resolved.Value;
            }

            if (!items.TryGetValue(occurrence.ItemId, out var item))
            {
                return DomainResult<ListReview>.Failure(
                    new DomainError(DomainErrorCode.InvariantViolation, "Occurrence has no canonical item."));
            }

            entries.Add(new ListReviewEntry(item, occurrence, resolution));
        }

        return DomainResult<ListReview>.Success(new ListReview(
            list,
            entries.Where(entry => entry.Occurrence.State == OccurrenceState.Open).ToArray(),
            entries.Where(entry => entry.Occurrence.State == OccurrenceState.Moved).ToArray(),
            entries.Where(entry => entry.Occurrence.State is OccurrenceState.Done or OccurrenceState.Abandoned).ToArray()));
    }
}
