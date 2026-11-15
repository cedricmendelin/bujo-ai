namespace Bujo.Domain;

public sealed record ItemSnapshot(
    Guid Id,
    string Name,
    string? Description,
    IReadOnlyList<Tag> Tags,
    PlannedPeriod PlannedPeriod,
    Guid CurrentOccurrenceId,
    long Version);

public sealed record JournalListSnapshot(
    Guid Id,
    string Name,
    JournalListKind Kind,
    JournalListState State,
    string? GoalText,
    IsoWeekKey? WeekKey,
    long Version);

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
}
