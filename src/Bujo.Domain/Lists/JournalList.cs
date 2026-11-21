namespace Bujo.Domain;

public enum JournalListKind
{
    FutureLog,
    Weekly,
    Custom
}

public enum JournalListState
{
    Open,
    Closed
}

public sealed class JournalList
{
    private JournalList(
        Guid id,
        string name,
        JournalListKind kind,
        string? goalText,
        IsoWeekKey? weekKey)
    {
        Id = id;
        Name = name;
        Kind = kind;
        GoalText = goalText;
        WeekKey = weekKey;
    }

    public Guid Id { get; }

    public string Name { get; private set; }

    public JournalListKind Kind { get; }

    public JournalListState State { get; private set; } = JournalListState.Open;

    public string? GoalText { get; private set; }

    public IsoWeekKey? WeekKey { get; }

    public long Version { get; private set; }

    internal static JournalList Create(
        Guid id,
        string name,
        JournalListKind kind,
        string? goalText,
        IsoWeekKey? weekKey)
    {
        if (!Enum.IsDefined(kind))
        {
            throw DomainLimits.Invalid("Unknown journal list kind.");
        }

        if (kind != JournalListKind.Weekly && weekKey is not null)
        {
            throw DomainLimits.Invalid("Only weekly lists can have an ISO week key.");
        }

        if (weekKey is { } key && !key.IsValid)
        {
            throw DomainLimits.Invalid("A valid ISO week key is required.");
        }

        return new JournalList(
            id,
            DomainLimits.RequiredText(name, DomainLimits.ListName, "List name"),
            kind,
            DomainLimits.OptionalText(goalText, DomainLimits.GoalText, "Goal text"),
            weekKey);
    }

    internal static JournalList FromSnapshot(JournalListSnapshot snapshot)
    {
        var list = new JournalList(snapshot.Id, snapshot.Name, snapshot.Kind, snapshot.GoalText, snapshot.WeekKey)
        {
            State = snapshot.State,
            Version = snapshot.Version
        };
        return list;
    }

    public void Rename(string name)
    {
        Name = DomainLimits.RequiredText(name, DomainLimits.ListName, "List name");
        Version++;
    }

    public void SetGoalText(string? goalText)
    {
        GoalText = DomainLimits.OptionalText(goalText, DomainLimits.GoalText, "Goal text");
        Version++;
    }

    public void Close(IReadOnlyCollection<Guid> unresolvedOccurrenceIds, DateTimeOffset occurredAt)
    {
        if (Kind == JournalListKind.FutureLog)
        {
            throw new DomainException(DomainErrorCode.FutureLogCannotClose, "The Future Log cannot be closed.");
        }

        if (unresolvedOccurrenceIds.Count != 0)
        {
            throw new DomainException(
                DomainErrorCode.UnresolvedOccurrences,
                "Resolve all open tasks before closing this list.",
                unresolvedOccurrenceIds.ToArray());
        }

        if (State == JournalListState.Closed)
        {
            return;
        }

        State = JournalListState.Closed;
        Version++;
    }

    public void Reopen()
    {
        if (State == JournalListState.Open)
        {
            return;
        }

        State = JournalListState.Open;
        Version++;
    }

    internal void RecordOccurrenceChange() => Version++;
}
